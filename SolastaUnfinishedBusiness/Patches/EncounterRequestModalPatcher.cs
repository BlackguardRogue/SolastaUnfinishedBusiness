using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

// The travel encounter popup outliving its game (issue 11): see EncounterPopupContext.
[UsedImplicitly]
public static class EncounterRequestModalPatcher
{
    // a click with no game would throw before anything happens; close the popup instead, so it can't be clicked again
    private static bool AllowClick(EncounterRequestModal modal, string button)
    {
        if (Gui.Game != null)
        {
            EncounterPopupContext.Log($"{button} clicked");

            return true;
        }

        EncounterPopupContext.Log($"{button} clicked with no game: popup closed, nothing sent");
        modal.Hide(true);

        return false;
    }

    [HarmonyPatch(typeof(EncounterRequestModal), nameof(EncounterRequestModal.GameEncounterRequested))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameEncounterRequested_Patch
    {
        [UsedImplicitly]
        public static void Postfix(EncounterRequestModal __instance)
        {
            EncounterPopupContext.Visible = __instance;
            EncounterPopupContext.Log("shown");
        }
    }

    [HarmonyPatch(typeof(EncounterRequestModal), nameof(EncounterRequestModal.OnEndHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnEndHide_Patch
    {
        [UsedImplicitly]
        public static void Postfix(EncounterRequestModal __instance)
        {
            if (EncounterPopupContext.Visible != __instance)
            {
                return;
            }

            EncounterPopupContext.Log("hidden");
            EncounterPopupContext.Visible = null;
        }
    }

    [HarmonyPatch(typeof(EncounterRequestModal), nameof(EncounterRequestModal.OnProceedCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnProceedCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(EncounterRequestModal __instance)
        {
            return AllowClick(__instance, "Proceed");
        }
    }

    [HarmonyPatch(typeof(EncounterRequestModal), nameof(EncounterRequestModal.OnFightCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnFightCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(EncounterRequestModal __instance)
        {
            return AllowClick(__instance, "Fight");
        }
    }

    [HarmonyPatch(typeof(EncounterRequestModal), nameof(EncounterRequestModal.OnAvoidCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnAvoidCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(EncounterRequestModal __instance)
        {
            return AllowClick(__instance, "Avoid");
        }
    }

    // GuiGameScreen.GameDestroying runs for every game screen when the game is being destroyed and only unhooks; the
    // encounter popup is one of them, and the game never hides it, so it's closed here (as MessageModal closes itself)
    [HarmonyPatch(typeof(GuiGameScreen), nameof(GuiGameScreen.GameDestroying))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameDestroying_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiGameScreen __instance)
        {
            if (__instance is not EncounterRequestModal modal || !modal.Visible || modal.Hiding)
            {
                return;
            }

            EncounterPopupContext.Log("game destroyed while shown: popup closed");
            modal.Hide(true);
        }
    }

    // the events that end or replace a game, logged only while the popup is up: they're the candidates for issue 11
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.DestroyGame))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DestroyGame_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            if (EncounterPopupContext.Visible != null)
            {
                EncounterPopupContext.Log("game being destroyed (load or exit)");
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.Shutdown))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Shutdown_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            if (EncounterPopupContext.Visible != null)
            {
                EncounterPopupContext.Log("game service shutting down");
            }
        }
    }

    [HarmonyPatch(typeof(SessionState_ExitingGame), nameof(SessionState_ExitingGame.Begin))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExitingGame_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            if (EncounterPopupContext.Visible != null)
            {
                EncounterPopupContext.Log("session exiting the game");
            }
        }
    }

    [HarmonyPatch(typeof(Session), nameof(Session.OnUserDisconnected))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnUserDisconnected_Patch
    {
        [UsedImplicitly]
        public static void Prefix(string cause, bool fromLogic)
        {
            if (EncounterPopupContext.Visible != null)
            {
                EncounterPopupContext.Log($"user disconnected: cause={cause}, fromLogic={fromLogic}");
            }
        }
    }
}
