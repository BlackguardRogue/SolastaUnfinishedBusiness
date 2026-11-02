using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

// The campfire gather (CampfireFastTravelContext) follows the game's fast travel arrival from its start to its end, so
// it knows when the party was really placed, and where to; its temporary fast travel waypoint leaves the map again at
// points that run on every computer: when the fast travel to it ends and when a long rest starts. The travel itself is
// the game's own.
[UsedImplicitly]
public static class FastTravelPatcher
{
    // a coroutine's state machine; AccessTools.EnumeratorMoveNext isn't in the Harmony the build uses
    private static MethodBase CoroutineMoveNext(Type type, string coroutine)
    {
        return AccessTools.Method(
            type.GetNestedTypes(BindingFlags.NonPublic).First(x => x.Name.StartsWith($"<{coroutine}>")),
            "MoveNext");
    }

    //PATCH: where each fast travel goes is noted (on every computer), so the campfire gather only takes the arrival of
    //its own travel (CampfireFastTravelContext.OnFastTravelStarting)
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FastTravel_Patch
    {
        // the explicit interface implementation that MapWaypointItem.ExecuteTravel calls
        [UsedImplicitly]
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(GameLocationMapManager),
                $"{nameof(IGameLocationMapService)}.{nameof(IGameLocationMapService.FastTravel)}");
        }

        [UsedImplicitly]
        public static void Prefix(TA.int3 gridPosition)
        {
            CampfireFastTravelContext.OnFastTravelStarting(gridPosition);
        }
    }

    //PATCH: a fast travel's arrival is followed from start to end (the campfire gather waits for it), and the one to
    //the campfire's temporary waypoint removes it
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

        // a step of the arrival runs: until its end, the travel isn't over
        [UsedImplicitly]
        public static void Prefix()
        {
            CampfireFastTravelContext.OnArrivalStep();
        }

        // the coroutine's last step ran: the party is placed
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
