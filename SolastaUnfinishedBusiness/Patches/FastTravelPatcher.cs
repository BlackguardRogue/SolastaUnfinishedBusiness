using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

// The campfire gather (CampfireFastTravelContext) follows the game's fast travel arrival from its start to its end, so
// it knows when the party was really placed; its temporary fast travel waypoint leaves the map again at points that
// run on every computer: when a fast travel ends and when a long rest starts. The travel itself is the game's own.
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

    //PATCH: a fast travel's arrival is followed from start to end (the campfire gather waits for it), and one that
    //placed the party removes the campfire's temporary waypoint
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
