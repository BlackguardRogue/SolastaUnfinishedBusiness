using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Races;

/// <summary>
///     Light Sensitivity of Dark Kobolds, Dark Elves and Gray Dwarves, in two rules chosen by the
///     "outdoors only" setting (<c>RaceLightSensitivityApplyOutdoorsOnly</c>, Races menu):
///     <list type="bullet">
///         <item>
///             Off: the original mod rule. Each racial light affinity gives the condition while the hero's own
///             illumination is Bright, indoors or out, a torch or a light spell included. The game's light
///             affinity code (<c>GameLocationCharacter.CheckLightingAffinityEffects</c>, patched in
///             GameLocationCharacterPatcher) applies it in the shared light category.
///         </item>
///         <item>
///             On: the daylight rule here. The condition applies while the hero stands in an exterior sector whose
///             hourly outdoor light is Bright (08:00 to 18:00 in Solasta 1.5.97), whatever the hero's own light: a
///             torch at night doesn't trigger it, magical darkness at noon doesn't stop it. The light affinity code
///             then skips these three features, so the two rules never both apply.
///         </item>
///     </list>
///     The condition itself (-1d4 on attack rolls, disadvantage on Wisdom (Perception)) is the same in both.
/// </summary>
[HarmonyPatch(typeof(GameLocationVisibilityManager), nameof(GameLocationVisibilityManager.ComputeIllumination))]
internal static class RacialSunlightSensitivity
{
    // The daylight rule keeps its condition in a category of its own, so the light affinity code, which owns the
    // shared light category, never sees it as surplus and removes it, and the two rules can't remove each other's.
    private const string ConditionCategory = "RacialSunlightSensitivity";
    private static readonly HashSet<FeatureDefinition> RacialFeatures = [];

    /// <summary>
    ///     True when the daylight rule is in use. Read at every check, so switching the setting takes effect at each
    ///     hero's next light update (a move, an hour change, a refresh). In a multiplayer room the host's settings are
    ///     in use on every computer.
    /// </summary>
    internal static bool Enabled => Main.Settings.RaceLightSensitivityApplyOutdoorsOnly;

    internal static bool IsRacialFeature(FeatureDefinition feature)
    {
        return RacialFeatures.Contains(feature);
    }

    /// <summary>
    ///     Builds one race's light affinity. It keeps its original name, so its ID in saves and characters is
    ///     unchanged, and its original table (Bright: Light Sensitivity), which the original rule uses while the
    ///     setting is off. The feature is registered so the daylight rule can recognise the race and the light
    ///     affinity code can skip it while the setting is on.
    /// </summary>
    internal static FeatureDefinitionLightAffinity Build(string name)
    {
        var feature = FeatureDefinitionLightAffinityBuilder.Create(name)
            .SetGuiPresentation(CustomConditionsContext.LightSensitivity.Name, Category.Condition)
            .AddLightingEffectAndCondition(
                new FeatureDefinitionLightAffinity.LightingEffectAndCondition
                {
                    lightingState = LocationDefinitions.LightingState.Bright,
                    condition = CustomConditionsContext.LightSensitivity
                })
            .AddToDB();

        RacialFeatures.Add(feature);

        return feature;
    }

    [UsedImplicitly]
    public static void Postfix(IIlluminable illuminable, GameLocationCharacter previewCharacter)
    {
        // Previews (attack previews, hovered cells, character labels) run on one computer only and must not change
        // a character or use an object ID; the game's real update passes no preview character. Only a hero whose
        // subrace has one of the three racial features is handled. A hero in a shape change (wild shape,
        // polymorph) is a monster here and is skipped until back in the hero's form.
        if (previewCharacter != null ||
            illuminable is not GameLocationCharacter { RulesetCharacter: RulesetCharacterHero hero } character ||
            hero.SubRaceDefinition?.FeatureUnlocks.Exists(x => RacialFeatures.Contains(x.FeatureDefinition)) != true)
        {
            return;
        }

        var sensitivity = CustomConditionsContext.LightSensitivity;
        var active = hero.ConditionsByCategory.TryGetValue(ConditionCategory, out var conditions)
            ? conditions.FirstOrDefault(x => x.ConditionDefinition == sensitivity)
            : null;

        // Setting off: the original rule applies through the light affinity code. Only a daylight-rule condition left
        // from while the setting was on (or in a save made then) is removed; nothing else is touched.
        if (!Enabled)
        {
            if (active != null)
            {
                hero.RemoveConditionOfCategory(ConditionCategory, active, true, false, true);
            }

            return;
        }

        var location = ServiceRepository.GetService<IGameLocationService>()?.GameLocation;

        if (location?.SectorBounds == null)
        {
            return;
        }

        // Exterior sectors receive their global light from the campaign hour
        // (GameLocationVisibilityManager.UpdateGlobalIlluminationState, which also marks every character for a light
        // update when it changes). Torches, magical darkness and other personal illumination don't enter this test.
        // A position outside every sector counts as not exposed.
        var sector = new GridAccessor(character.LocationPosition).sector;
        var exposed = sector is { IsExterior: true, GlobalLightingState: LocationDefinitions.LightingState.Bright };

        // A copy given by the original rule (an older save, or a check made while the setting was off) sits in the
        // shared light category. Remove that one condition only, leaving other light affinities' conditions alone.
        if (hero.ConditionsByCategory.TryGetValue(AttributeDefinitions.TagLightSensitivity, out var previous) &&
            previous.FirstOrDefault(x => x.ConditionDefinition == sensitivity) is { } legacy)
        {
            hero.RemoveConditionOfCategory(AttributeDefinitions.TagLightSensitivity, legacy, true, false, true);
        }

        // Change only what differs, so repeated checks use no new object ID (object-ID desyncs in multiplayer).
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
