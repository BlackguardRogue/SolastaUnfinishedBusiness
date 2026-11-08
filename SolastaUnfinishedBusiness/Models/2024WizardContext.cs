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
    internal static bool TryPrepareMemorizeSpell(SpellRepertoirePanel panel)
    {
        var character = panel.GuiCharacter?.RulesetCharacter;

        if (character is not RulesetCharacterHero hero || !TryGetMemorizeSpellCondition(hero, out var condition))
        {
            return false;
        }

        var session = GetMemorizeSession(condition);
        var argument = string.Join("|",
            panel.SpellRepertoire.SpellCastingFeature.Name,
            $"{session.Amount},{session.SpellIndex}",
            string.Join(";", panel.preparedSpells.Select(x => x.Name)));

        panel.StartCoroutine(RestCommandContext.Send(hero, nameof(MemorizeSpellPrepare), argument, outcome =>
        {
            if (outcome != RestCommandContext.Outcome.Ran || !panel)
            {
                return;
            }

            _memorizeSession = null;
            panel.SpellPreparationValidated();
        }));

        return true;
    }

    // runs on every computer at the same tick (RestCommandContext): the screen's chosen spells, and the swap made on it
    // as the record. Nothing changes unless every part is found, alike everywhere.
    private static bool MemorizeSpellPrepare(RulesetCharacterHero hero, string argument)
    {
        var parts = argument.Split('|');
        var values = parts.Length == 3 ? parts[1].Split(',') : [];
        var spells = new List<SpellDefinition>();
        var database = DatabaseRepository.GetDatabase<SpellDefinition>();

        if (parts.Length != 3 || values.Length != 2 ||
            !int.TryParse(values[0], out var amount) ||
            !int.TryParse(values[1], out var spellIndex) ||
            !TryGetMemorizeSpellCondition(hero, out var condition) ||
            hero.SpellRepertoires.FirstOrDefault(x => x.SpellCastingFeature.Name == parts[0]) is not { } repertoire)
        {
            Main.Error($"[RestCommand] Memorize Spell preparation for {hero.Name} not applied ({argument})");

            return false;
        }

        foreach (var name in parts[2].Split([';'], StringSplitOptions.RemoveEmptyEntries))
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
