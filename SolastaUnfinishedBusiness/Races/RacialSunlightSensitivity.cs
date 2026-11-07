using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Races;

[HarmonyPatch(typeof(GameLocationVisibilityManager), nameof(GameLocationVisibilityManager.ComputeIllumination))]
internal static class RacialSunlightSensitivity
{
    private const string ConditionCategory = "RacialSunlightSensitivity";
    private static readonly HashSet<FeatureDefinition> RacialFeatures = [];

    internal static FeatureDefinitionLightAffinity Build(string name)
    {
        // Keep each saved racial feature's original ID and type. An empty personal-light
        // affinity table leaves the ordinary creature lighting system nothing to apply.
        var feature = FeatureDefinitionLightAffinityBuilder.Create(name)
            .SetGuiPresentation(CustomConditionsContext.LightSensitivity.Name, Category.Condition)
            .AddToDB();
        RacialFeatures.Add(feature);
        return feature;
    }

    [UsedImplicitly]
    public static void Postfix(IIlluminable illuminable, GameLocationCharacter previewCharacter)
    {
        // Previews run independently on each client. Only real updates of a hero with one
        // of the three racial unlocks may change conditions or allocate a ruleset ID.
        if (previewCharacter != null ||
            illuminable is not GameLocationCharacter { RulesetCharacter: RulesetCharacterHero hero } character ||
            hero.SubRaceDefinition?.FeatureUnlocks.Exists(x => RacialFeatures.Contains(x.FeatureDefinition)) != true)
        {
            return;
        }

        var location = ServiceRepository.GetService<IGameLocationService>()?.GameLocation;
        if (location?.SectorBounds == null)
        {
            return;
        }

        // Exterior sectors receive their global light from the campaign hour. Torches,
        // magical darkness and other effects on personal illumination do not enter this test.
        var sector = new GridAccessor(character.LocationPosition).sector;
        var exposed = sector is { IsExterior: true, GlobalLightingState: LocationDefinitions.LightingState.Bright };
        var sensitivity = CustomConditionsContext.LightSensitivity;

        // A save from the previous rule can still hold the racial condition in the shared
        // lighting category. Remove only that condition, leaving other affinities alone.
        if (hero.ConditionsByCategory.TryGetValue(AttributeDefinitions.TagLightSensitivity, out var previous) &&
            previous.FirstOrDefault(x => x.ConditionDefinition == sensitivity) is { } legacy)
        {
            hero.RemoveConditionOfCategory(AttributeDefinitions.TagLightSensitivity, legacy, true, false, true);
        }

        var active = hero.ConditionsByCategory.TryGetValue(ConditionCategory, out var conditions)
            ? conditions.FirstOrDefault(x => x.ConditionDefinition == sensitivity)
            : null;

        if (exposed && active == null)
        {
            hero.AddConditionOfCategory(ConditionCategory, RulesetCondition.CreateActiveCondition(
                hero.Guid, sensitivity, DurationType.Irrelevant, 0, TurnOccurenceType.StartOfTurn,
                character.Guid, hero.CurrentFaction.Name), true);
        }
        else if (!exposed && active != null)
        {
            hero.RemoveConditionOfCategory(ConditionCategory, active, true, false, true);
        }
    }
}
