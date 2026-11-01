using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class FunctorStartLongRestPatcher
{
    //PATCH: a campfire's long rest: any player can start it in multiplayer, a spread-out party walks to the campfire
    //first, and a campfire set to rest at once rests without an approval (CampfireRestContext)
    [HarmonyPatch(typeof(FunctorStartLongRest), nameof(FunctorStartLongRest.Execute))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Execute_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ref IEnumerator __result, FunctorParametersDescription functorParameters)
        {
            __result = CampfireRestContext.WrapFunctor(__result, functorParameters);
        }
    }

    //PATCH: the campfire's "Waiting to Perform a Long Rest" (the party already rested today): characters too far away
    //walk to the campfire before the wait, so the long rest after it isn't refused for a spread-out party
    //(CampfireRestContext)
    [HarmonyPatch(typeof(FunctorStartLongRest), nameof(FunctorStartLongRest.DoWaitLongRest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DoWaitLongRest_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            return !CampfireRestContext.TryGatherBeforeWait();
        }
    }
}
