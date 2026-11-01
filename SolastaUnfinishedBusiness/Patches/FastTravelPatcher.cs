using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using TA;

namespace SolastaUnfinishedBusiness.Patches;

// The game's fast travel during a campfire gather moves only the characters beyond the long rest distance
// (CampfireFastTravelContext): its steps that read the party list get the travellers instead, and its guests stay.
[UsedImplicitly]
public static class FastTravelPatcher
{
    private static readonly MethodInfo PartyCharacters =
        typeof(IGameLocationCharacterService).GetProperty(nameof(IGameLocationCharacterService.PartyCharacters))!
            .GetGetMethod();

    private static readonly MethodInfo GuestCharacters =
        typeof(IGameLocationCharacterService).GetProperty(nameof(IGameLocationCharacterService.GuestCharacters))!
            .GetGetMethod();

    private static readonly MethodInfo PartyForFastTravel =
        new Func<IGameLocationCharacterService, List<GameLocationCharacter>>(
            CampfireFastTravelContext.PartyForFastTravel).Method;

    private static readonly MethodInfo GuestsForFastTravel =
        new Func<IGameLocationCharacterService, List<GameLocationCharacter>>(
            CampfireFastTravelContext.GuestsForFastTravel).Method;

    // a coroutine's state machine; AccessTools.EnumeratorMoveNext isn't in the Harmony the build uses
    private static MethodBase CoroutineMoveNext(Type type, string coroutine)
    {
        return AccessTools.Method(
            type.GetNestedTypes(BindingFlags.NonPublic).First(x => x.Name.StartsWith($"<{coroutine}>")),
            "MoveNext");
    }

    // each read of the party list becomes CampfireFastTravelContext.PartyForFastTravel(service), same stack shape
    private static IEnumerable<CodeInstruction> SwapPartyLists(
        IEnumerable<CodeInstruction> instructions, string context, bool guests)
    {
        var result = instructions.ReplaceCalls(PartyCharacters, context,
            new CodeInstruction(OpCodes.Call, PartyForFastTravel));

        return guests
            ? result.ReplaceCalls(GuestCharacters, context, new CodeInstruction(OpCodes.Call, GuestsForFastTravel))
            : result;
    }

    //PATCH: a fast travel to the campfire's waypoint moves only the characters beyond the long rest distance; worked
    //out here, where the travel command runs on every computer (CampfireFastTravelContext)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.RequestFastTravel))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RequestFastTravel_Patch
    {
        [UsedImplicitly]
        public static void Prefix(int3 mapWaypointPos)
        {
            CampfireFastTravelContext.OnFastTravelRequested(mapWaypointPos);
        }
    }

    //PATCH: the fast travel's leader and its path start from the first traveller during a campfire fast travel
    [HarmonyPatch(typeof(MapWaypointItem), nameof(MapWaypointItem.PrepareFastTravel))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PrepareFastTravel_Patch
    {
        [UsedImplicitly]
        public static void Prefix(MapWaypointItem __instance)
        {
            CampfireFastTravelContext.OnPrepareFastTravel(__instance.GameMapWaypoint);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            return SwapPartyLists(instructions, "MapWaypointItem.PrepareFastTravel", false);
        }
    }

    //PATCH: only the travellers follow the path (the first leads) during a campfire fast travel
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecutePath_Patch
    {
        [UsedImplicitly]
        public static MethodBase TargetMethod()
        {
            return CoroutineMoveNext(typeof(GameLocationMapManager), nameof(GameLocationMapManager.ExecutePath));
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            return SwapPartyLists(instructions, "GameLocationMapManager.ExecutePath", true);
        }
    }

    //PATCH: only the travellers are placed at the destination during a campfire fast travel, which ends there
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetupPartyOnDestination_Patch
    {
        [UsedImplicitly]
        public static MethodBase TargetMethod()
        {
            return CoroutineMoveNext(typeof(GameLocationMapManager),
                nameof(GameLocationMapManager.SetupPartyOnDestination));
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            return SwapPartyLists(instructions, "GameLocationMapManager.SetupPartyOnDestination", true);
        }

        // the coroutine's last step ran: the travellers are placed
        [UsedImplicitly]
        public static void Postfix(bool __result)
        {
            if (!__result)
            {
                CampfireFastTravelContext.OnFastTravelEnded();
            }
        }
    }

    //PATCH: a long rest starting (on every computer) removes the campfire's temporary waypoint
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.StartRest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StartRest_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RuleDefinitions.RestType restType)
        {
            if (restType == RuleDefinitions.RestType.LongRest)
            {
                CampfireFastTravelContext.RemoveDestination();
            }
        }
    }
}
