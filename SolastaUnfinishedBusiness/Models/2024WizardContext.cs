using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSubclassChoices;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly FeatureDefinitionPointPool PointPoolWizardScholar = FeatureDefinitionPointPoolBuilder
        .Create("PointPoolWizardScholar")
        .SetGuiPresentation(Category.Feature)
        .SetPool(HeroDefinitions.PointsPoolType.Expertise, 1)
        .RestrictChoices(
            SkillDefinitions.Arcana,
            SkillDefinitions.History,
            SkillDefinitions.Investigation,
            SkillDefinitions.Medecine,
            SkillDefinitions.Nature,
            SkillDefinitions.Religion)
        .AddToDB();

    internal static readonly FeatureDefinition FeatureMemorizeSpell = FeatureDefinitionBuilder
        .Create("FeatureWizardMemorizeSpell")
        .SetGuiPresentation(Category.Feature)
        .AddToDB();

    private static readonly RestActivityDefinition RestActivityMemorizeSpell = RestActivityDefinitionBuilder
        .Create("RestActivityMemorizeSpell")
        .SetGuiPresentation("FeatureWizardMemorizeSpell", Category.Feature)
        .SetRestData(
            RestDefinitions.RestStage.AfterRest,
            RestType.ShortRest,
            RestActivityDefinition.ActivityCondition.CanPrepareSpells,
            nameof(FunctorMemorizeSpell),
            string.Empty)
        .AddToDB();

    private static readonly ConditionDefinition ConditionMemorizeSpell = ConditionDefinitionBuilder
        .Create("ConditionMemorizeSpell")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .SetFixedAmount(0)
        .AddToDB();


    internal static void SwitchWizardSchoolOfMagicLearningLevel()
    {
        var schools = DatabaseRepository.GetDatabase<CharacterSubclassDefinition>()
            .Where(x =>
                SubclassChoiceWizardArcaneTraditions.Subclasses.Contains(x.Name) ||
                x.Name.StartsWith(WizardClass))
            .ToList();

        var fromLevel = 3;
        var toLevel = 2;

        if (Main.Settings.EnableWizardToLearnSchoolAtLevel3)
        {
            fromLevel = 2;
            toLevel = 3;
        }

        foreach (var featureUnlock in schools
                     .SelectMany(school => school.FeatureUnlocks
                         .Where(featureUnlock => featureUnlock.level == fromLevel)))
        {
            featureUnlock.level = toLevel;
        }

        // change spell casting level on Wizard itself
        Wizard.FeatureUnlocks
                .FirstOrDefault(x =>
                    x.FeatureDefinition == SubclassChoiceWizardArcaneTraditions)!
                .level =
            toLevel;

        foreach (var school in schools)
        {
            school.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        }

        Wizard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchWizardScholar()
    {
        Wizard.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == PointPoolWizardScholar);

        if (Main.Settings.EnableWizardToLearnScholarAtLevel2)
        {
            Wizard.FeatureUnlocks.Add(new FeatureUnlockByLevel(PointPoolWizardScholar, 2));
        }

        Wizard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static bool IsRestActivityMemorizeSpellAvailable(
        RestActivityDefinition activity, RulesetCharacterHero hero)
    {
        return activity != RestActivityMemorizeSpell ||
               (Main.Settings.EnableWizardMemorizeSpell2024 && hero.GetClassLevel(Wizard) >= 5);
    }

    private static bool TryGetMemorizeSpellCondition(RulesetCharacter character, out RulesetCondition condition)
    {
        return character.TryGetConditionOfCategoryAndType(
            AttributeDefinitions.TagEffect, ConditionMemorizeSpell.Name, out condition);
    }

    internal static bool IsMemorizeSpellPreparation(RulesetCharacter character)
    {
        return TryGetMemorizeSpellCondition(character, out _);
    }

    // The one swap Memorize Spell allows is recorded in the condition, as upstream did: Amount (1 once a spell was
    // unselected) and SourceProficiencyBonus (the index of the spell last unselected or selected, -1 before either).
    // The condition lasts until the next rest and is saved with the game, so reopening Memorize Spell or loading a
    // save can't grant a second swap. Upstream changed those fields on every click on the clicker's computer only; now
    // a click changes only this computer's screen session below, and the record is written on every computer together
    // with the chosen spells when Prepare is pressed (TryPrepareMemorizeSpell); closing without it changes nothing.
    private static MemorizeSession _memorizeSession;

    private sealed class MemorizeSession(ulong conditionGuid, int amount, int spellIndex)
    {
        internal readonly ulong ConditionGuid = conditionGuid;
        internal int Amount = amount;
        internal int SpellIndex = spellIndex;
    }

    // the screen session for this condition, started from the committed record
    private static MemorizeSession GetMemorizeSession(RulesetCondition condition)
    {
        if (_memorizeSession == null || _memorizeSession.ConditionGuid != condition.Guid)
        {
            _memorizeSession = new MemorizeSession(condition.Guid, condition.Amount, condition.SourceProficiencyBonus);
        }

        return _memorizeSession;
    }

    internal static bool IsInvalidMemorizeSelectedSpell(
        SpellRepertoirePanel spellRepertoirePanel, RulesetCharacter rulesetCharacter, SpellDefinition spell)
    {
        if (!TryGetMemorizeSpellCondition(rulesetCharacter, out var activeCondition))
        {
            return false;
        }

        var session = GetMemorizeSession(activeCondition);
        var spellIndex = SpellsContext.Spells.IndexOf(spell);
        var isUncheck = spellRepertoirePanel.preparedSpells.Contains(spell);

        if (isUncheck)
        {
            if (session.SpellIndex != -1 && session.SpellIndex != spellIndex)
            {
                return true;
            }

            session.Amount = 1;
            session.SpellIndex = spellIndex;

            return false;
        }

        session.Amount = 0;
        session.SpellIndex = spellIndex;

        return false;
    }

    // The preparation screen's Prepare button in Memorize Spell mode (SpellRepertoirePanel.DoValidate prefix): the
    // chosen spells and the swap record go to every computer in one command, MemorizeSpellPrepare, in place of the
    // game's PrepareSpells, so no computer can get one without the other (a disconnect between two commands could
    // otherwise change the spells but not the record). The screen closes as the game's own would, once the command has
    // run here; if it doesn't run, the screen stays open with nothing changed. False: not Memorize Spell mode.
    //
    // Each press is a session, tied to the condition it was made for, so a press that arrives after the condition was
    // replaced (a rest) changes nothing. Codex's reviews of PR #28 asked for these guards:
    // - one press at a time: another click while one is out does nothing, so presses can't overlap;
    // - a press that hasn't come back after the command's wait may still arrive later, after the player has given up
    //   on it or closed the screen: a revert with the spells and record from before it is sent at once, and runs
    //   after it everywhere (one sender's commands keep their order). It only undoes the press if that press is still
    //   the latest one applied to the condition, so it can never undo a newer one;
    // - a press from a player who has left, that runs after the host's departure command, is refused everywhere, so
    //   a press still queued when its player left can't change the spells with nobody to reconcile it.
    // when the press now out was sent; a press older than the command's wait (and a little) no longer blocks, in case
    // its screen closed and stopped the coroutine that would have cleared it
    private static float _memorizePressOutSince = -1f;

    internal static bool TryPrepareMemorizeSpell(SpellRepertoirePanel panel)
    {
        var character = panel.GuiCharacter?.RulesetCharacter;

        if (character is not RulesetCharacterHero hero || !TryGetMemorizeSpellCondition(hero, out var condition))
        {
            return false;
        }

        var now = UnityEngine.Time.realtimeSinceStartup;

        if (_memorizePressOutSince >= 0f && now - _memorizePressOutSince < 35f)
        {
            // handled: the earlier press is still on its way
            return true;
        }

        _memorizePressOutSince = now;

        var session = GetMemorizeSession(condition);
        var sessionId = RestCommandContext.NewTransactionId();
        var feature = panel.SpellRepertoire.SpellCastingFeature.Name;
        var argument = string.Join("|", sessionId, condition.Guid, feature,
            $"{session.Amount},{session.SpellIndex}",
            string.Join(";", panel.preparedSpells.Select(x => x.Name)));
        var revert = string.Join("|", sessionId, condition.Guid, feature,
            $"{condition.Amount},{condition.SourceProficiencyBonus}",
            string.Join(";", panel.SpellRepertoire.PreparedSpells.Select(x => x.Name)));

        panel.StartCoroutine(RestCommandContext.Send(hero, nameof(MemorizeSpellPrepare), argument, outcome =>
        {
            _memorizePressOutSince = -1f;

            if (outcome == RestCommandContext.Outcome.TimedOut)
            {
                RestCommandContext.Post(hero, nameof(MemorizeSpellRevert), revert);

                return;
            }

            if (outcome != RestCommandContext.Outcome.Ran || !panel)
            {
                return;
            }

            _memorizeSession = null;
            panel.SpellPreparationValidated();
        }));

        return true;
    }

    // the latest press applied to each Memorize Spell condition, by the condition's ID; written by the commands only,
    // so the same on every computer
    private static readonly Dictionary<ulong, string> LatestMemorizePress = new();

    // runs on every computer at the same tick (RestCommandContext): the screen's chosen spells, and the swap made on it
    // as the record. Nothing changes unless every part is found, alike everywhere.
    private static bool MemorizeSpellPrepare(RulesetCharacterHero hero, string argument)
    {
        var sessionId = argument.Split('|')[0];

        if (RestCommandContext.HasDeparted(sessionId))
        {
            Main.Info($"[RestCommand] Memorize Spell preparation for {hero.Name} not applied: its player has left");

            return false;
        }

        if (!ApplyMemorizeSpells(hero, argument, out var conditionGuid))
        {
            return false;
        }

        LatestMemorizePress[conditionGuid] = sessionId;

        return true;
    }

    // runs on every computer at the same tick: undoes a press with the spells and record from before it, only while
    // that press is the latest one applied to its condition
    private static bool MemorizeSpellRevert(RulesetCharacterHero hero, string argument)
    {
        var parts = argument.Split('|');

        if (parts.Length < 2 || !ulong.TryParse(parts[1], out var conditionGuid) ||
            !LatestMemorizePress.TryGetValue(conditionGuid, out var latest) || latest != parts[0])
        {
            // the press never ran, was refused, or a newer one has replaced it: nothing to undo
            return false;
        }

        LatestMemorizePress.Remove(conditionGuid);
        Main.Info($"[RestCommand] Memorize Spell preparation for {hero.Name} came too late; undone");

        return ApplyMemorizeSpells(hero, argument, out _);
    }

    // session|condition ID|spellcasting feature|amount,spell index|spell;spell...
    private static bool ApplyMemorizeSpells(RulesetCharacterHero hero, string argument, out ulong conditionGuid)
    {
        var parts = argument.Split('|');
        var values = parts.Length == 5 ? parts[3].Split(',') : [];
        var spells = new List<SpellDefinition>();
        var database = DatabaseRepository.GetDatabase<SpellDefinition>();

        conditionGuid = 0;

        if (parts.Length != 5 || values.Length != 2 ||
            !ulong.TryParse(parts[1], out conditionGuid) ||
            !int.TryParse(values[0], out var amount) ||
            !int.TryParse(values[1], out var spellIndex) ||
            !TryGetMemorizeSpellCondition(hero, out var condition) ||
            condition.Guid != conditionGuid ||
            hero.SpellRepertoires.FirstOrDefault(x => x.SpellCastingFeature.Name == parts[2]) is not { } repertoire)
        {
            Main.Error($"[RestCommand] Memorize Spell preparation for {hero.Name} not applied ({argument})");

            return false;
        }

        foreach (var name in parts[4].Split([';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!database.TryGetElement(name, out var spell))
            {
                Main.Error($"[RestCommand] Memorize Spell preparation for {hero.Name}: no spell {name}");

                return false;
            }

            spells.Add(spell);
        }

        repertoire.PreparedSpells.SetRange(spells);
        condition.Amount = amount;
        condition.SourceProficiencyBonus = spellIndex;

        return true;
    }

    // runs on every computer at the same tick (RestCommandContext): the condition that puts the hero's preparation
    // screen in Memorize Spell mode until the next rest, so every game creates it, with the same object ID
    private static bool BeginMemorizeSpell(RulesetCharacterHero hero, string argument)
    {
        // make this until any rest to ensure users cannot cheat by reopening the prep screen
        // as conditions on refresh won't update source amount nor source ability bonus used for tracking
        hero.InflictCondition(
            ConditionMemorizeSpell.Name,
            DurationType.UntilAnyRest,
            0,
            TurnOccurenceType.EndOfTurn,
            AttributeDefinitions.TagEffect,
            hero.guid,
            hero.CurrentFaction.Name,
            1,
            ConditionMemorizeSpell.Name,
            0,
            0,
            -1);

        return true;
    }

    private static void LoadWizardMemorizeSpell()
    {
        ServiceRepository.GetService<IFunctorService>()
            .RegisterFunctor(nameof(FunctorMemorizeSpell), new FunctorMemorizeSpell());
        RestCommandContext.Register(nameof(BeginMemorizeSpell), BeginMemorizeSpell);
        RestCommandContext.Register(nameof(MemorizeSpellPrepare), MemorizeSpellPrepare);
        RestCommandContext.Register(nameof(MemorizeSpellRevert), MemorizeSpellRevert);
    }

    internal static void SwitchWizardMemorizeSpell()
    {
        Wizard.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == FeatureMemorizeSpell);

        if (Main.Settings.EnableWizardMemorizeSpell2024)
        {
            Wizard.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureMemorizeSpell, 5));
        }

        Wizard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private class FunctorMemorizeSpell : Functor
    {
        public override IEnumerator Execute(
            FunctorParametersDescription functorParameters,
            FunctorExecutionContext context)
        {
            var inspectionScreen = Gui.GuiService.GetScreen<CharacterInspectionScreen>();
            var partyStatusScreen = Gui.GuiService.GetScreen<GamePartyStatusScreen>();
            var hero = functorParameters.RestingHero;

            Gui.GuiService.GetScreen<RestModal>().KeepCurrentState = true;

            var spellRepertoire = hero.SpellRepertoires.FirstOrDefault(x =>
                x.SpellCastingFeature.SpellReadyness == SpellReadyness.Prepared);

            if (spellRepertoire == null)
            {
                yield break;
            }

            // the condition is added on every computer (it used to be added on the clicker's only, a desync); the
            // preparation screen only opens once this computer has it too. A late Begin (TimedOut) adds the condition
            // everywhere at once, which is what a successful one does, so there's nothing to undo.
            var outcome = RestCommandContext.Outcome.Failed;

            yield return RestCommandContext.Send(hero, nameof(BeginMemorizeSpell), string.Empty, x => outcome = x);

            if (outcome != RestCommandContext.Outcome.Ran)
            {
                yield break;
            }

            // a fresh screen session from the committed record
            _memorizeSession = null;

            // the screen's choice and the swap record reach every computer together: TryPrepareMemorizeSpell
            partyStatusScreen.SetupDisplayPreferences(false, false, false);
            inspectionScreen.ShowSpellPreparation(
                functorParameters.RestingHero, Gui.GuiService.GetScreen<RestModal>(), spellRepertoire);

            while (context.Async && inspectionScreen.Visible)
            {
                yield return null;
            }

            _memorizeSession = null;

            partyStatusScreen.SetupDisplayPreferences(true, true, true);
        }
    }
}
