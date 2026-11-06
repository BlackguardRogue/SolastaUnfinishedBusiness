using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NarrativeStateTeleportCharacterBasePatcher
{
    //PATCH: heroes without a role follow when a scripted teleport moves the whole party (PARTYSIZE)
    [HarmonyPatch(typeof(NarrativeStateTeleportCharacterBase), nameof(NarrativeStateTeleportCharacterBase.Begin))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Begin_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NarrativeStateTeleportCharacterBase __instance)
        {
            MultiplayerContext.Guarded("a cutscene party teleport",
                () => MultiplayerContext.PlaceHeroesLeftByTeleport(__instance));
        }
    }
}
