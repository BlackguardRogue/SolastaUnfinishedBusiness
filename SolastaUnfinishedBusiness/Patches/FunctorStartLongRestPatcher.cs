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
    //first, and a campfire set to rest at once still asks for approval (CampfireRestContext)
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
}
