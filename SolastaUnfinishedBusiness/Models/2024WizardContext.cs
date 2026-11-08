using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
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

    // The one swap Memorize Spell allows is chosen on the clicking player's preparation screen, so it's tracked on that
    // computer only, by the condition's object ID: (whether a spell was unselected, the index of the spell last
    // unselected or selected, -1 before either). A new condition (after the next rest) starts a new record. This used to
    // be kept in the condition's Amount and SourceProficiencyBonus, which changed a shared game object on one computer.
    private static readonly Dictionary<ulong, (int Amount, int SpellIndex)> MemorizeSwaps = new();

    internal static bool IsInvalidMemorizeSelectedSpell(
        SpellRepertoirePanel spellRepertoirePanel, RulesetCharacter rulesetCharacter, SpellDefinition spell)
    {
        if (!TryGetMemorizeSpellCondition(rulesetCharacter, out var activeCondition))
        {
            return false;
        }

        if (!MemorizeSwaps.TryGetValue(activeCondition.Guid, out var swap))
        {
            // how many spells can be prepared starting at zero as need an unselect event first, and the index of the
            // unselected spell starting at -1 to allow any spell to be unselected on first take
            swap = (0, -1);
        }

        var spellIndex = SpellsContext.Spells.IndexOf(spell);
        var isUncheck = spellRepertoirePanel.preparedSpells.Contains(spell);

        if (isUncheck)
        {
            if (swap.SpellIndex != -1 && swap.SpellIndex != spellIndex)
            {
                return true;
            }

            MemorizeSwaps[activeCondition.Guid] = (1, spellIndex);

            return false;
        }

        MemorizeSwaps[activeCondition.Guid] = (0, spellIndex);

        return false;
    }

    // runs on every computer at the same tick (RestCommandContext): the condition that puts the hero's preparation
    // screen in Memorize Spell mode until the next rest, so every game creates it, with the same object ID
    private static void BeginMemorizeSpell(RulesetCharacterHero hero, string argument)
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
    }

    private static void LoadWizardMemorizeSpell()
    {
        ServiceRepository.GetService<IFunctorService>()
            .RegisterFunctor(nameof(FunctorMemorizeSpell), new FunctorMemorizeSpell());
        RestCommandContext.Register(nameof(BeginMemorizeSpell), BeginMemorizeSpell);
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
            // preparation screen only opens once this computer has it too
            var begun = false;

            yield return RestCommandContext.Send(hero, nameof(BeginMemorizeSpell), string.Empty, x => begun = x);

            if (!begun)
            {
                yield break;
            }

            // the chosen spells reach every computer through the game's own PrepareSpells command
            partyStatusScreen.SetupDisplayPreferences(false, false, false);
            inspectionScreen.ShowSpellPreparation(
                functorParameters.RestingHero, Gui.GuiService.GetScreen<RestModal>(), spellRepertoire);

            while (context.Async && inspectionScreen.Visible)
            {
                yield return null;
            }

            partyStatusScreen.SetupDisplayPreferences(true, true, true);
        }
    }
}
