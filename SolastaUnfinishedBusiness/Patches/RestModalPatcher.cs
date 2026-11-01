using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RestModalPatcher
{
    // a rest the game allows waits for every other player's approval first
    private static bool MustWaitForApproval(RestType restType, Action proceed)
    {
        var failure = string.Empty;

        if (ServiceRepository.GetService<IGameRestingService>()?.CanExecuteLocationRest(restType, ref failure, true) !=
            true)
        {
            return false;
        }

        return MultiplayerApprovalContext.MustWaitForApproval(
            restType == RestType.ShortRest
                ? MultiplayerApprovalContext.ApprovalAction.ShortRest
                : MultiplayerApprovalContext.ApprovalAction.LongRest,
            string.Empty, null, MultiplayerApprovalContext.RestSeconds(restType), proceed);
    }

    //PATCH: asks every player to approve a short rest (MULTIPLAYER)
    [HarmonyPatch(typeof(RestModal), nameof(RestModal.OnStartShortRestCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnStartShortRestCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RestModal __instance)
        {
            GameRestingManagerPatcher.InRestScreen = true;

            return !MustWaitForApproval(RestType.ShortRest, __instance.OnStartShortRestCb);
        }

        [UsedImplicitly]
        public static void Finalizer()
        {
            GameRestingManagerPatcher.InRestScreen = false;
        }
    }

    //PATCH: asks every player to approve a long rest (MULTIPLAYER)
    [HarmonyPatch(typeof(RestModal), nameof(RestModal.OnStartLongRestCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnStartLongRestCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RestModal __instance)
        {
            GameRestingManagerPatcher.InRestScreen = true;

            // a campfire set to rest at once pressing Start itself: the game's own Start, with no rest checks, no wait
            // and no approval (user direction, 2026-11-02), as the story puts the party to rest there
            if (CampfireRestContext.IsForcedStart)
            {
                return true;
            }

            // the campfire's walk is over and the rest checks passed (CampfireRestContext): the game's own Start
            if (CampfireRestContext.IsReplayingStart)
            {
                return true;
            }

            // Start pressed again while the party walks to the campfire
            if (CampfireRestContext.IsGathering)
            {
                return false;
            }

            //PATCH: when the party already rested today, waits until it can rest again, then rests
            if (LongRestWaitContext.TryWaitThenRest(__instance))
            {
                return false;
            }

            //PATCH: at a campfire, characters too far away walk to it first, then the party rests; in multiplayer the
            //walk and the rest are approved together
            var walkers = CampfireRestContext.FindWalkers();

            if (walkers.Count == 0)
            {
                return !MustWaitForApproval(RestType.LongRest, __instance.OnStartLongRestCb);
            }

            var failure = string.Empty;

            // every other rest rule must pass before anyone walks (the walk only fixes the distance); the button is
            // only enabled when they do, so this is a rule that stopped passing since, shown as the game shows it
            if (ServiceRepository.GetService<IGameRestingService>()
                    ?.CanExecuteLocationRest(RestType.LongRest, ref failure, true) != true)
            {
                Gui.GuiService.ShowAlert(failure, Gui.ColorFailure, 2.5f);

                return false;
            }

            if (!MultiplayerApprovalContext.MustWaitForApproval(
                    MultiplayerApprovalContext.ApprovalAction.CampfireLongRest,
                    string.Join(", ", walkers.ConvertAll(x => x.Name)), null,
                    MultiplayerApprovalContext.RestSeconds(RestType.LongRest),
                    () => CampfireRestContext.StartGather(__instance, walkers)))
            {
                CampfireRestContext.StartGather(__instance, walkers);
            }

            return false;
        }

        [UsedImplicitly]
        public static void Finalizer()
        {
            GameRestingManagerPatcher.InRestScreen = false;
        }
    }

    //PATCH: a rest screen that isn't a campfire's own forgets the campfire (CampfireRestContext)
    [HarmonyPatch(typeof(RestModal), nameof(RestModal.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            CampfireRestContext.OnRestScreenShown();
        }
    }

    //PATCH: the rest screen's start buttons are enabled for every player, not only the host (MULTIPLAYER)
    [HarmonyPatch(typeof(RestBeforePanel), nameof(RestBeforePanel.SetRestType))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetRestType_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            GameRestingManagerPatcher.InRestScreen = true;
        }

        [UsedImplicitly]
        public static void Finalizer()
        {
            GameRestingManagerPatcher.InRestScreen = false;
        }
    }
}
