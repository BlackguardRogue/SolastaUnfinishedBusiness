using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using Photon.Pun;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

// The game's multiplayer vote (fast travel, and the mod's approval of time-passing actions), made to work whoever asks
// and shown to every player. The behaviour is described in Models/MultiplayerVoteContext.cs.
[UsedImplicitly]
public static class MultiplayerVoteModalPatcher
{
    //PATCH: records the vote this computer starts; refuses a second vote while one runs (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.ShowVote))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ShowVote_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(string parameter, MultiplayerVoteModal.VoteCanceledHandler voteCanceled)
        {
            return MultiplayerVoteContext.OnShowVote(parameter, voteCanceled);
        }
    }

    //PATCH: the vote screen's title and proposal texts for an approval, or for a vote another player proposed (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerFastTravelVoteModal), nameof(MultiplayerFastTravelVoteModal.ShowVoteHook))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ShowVoteHook_Patch
    {
        [UsedImplicitly]
        public static void Postfix(MultiplayerFastTravelVoteModal __instance)
        {
            MultiplayerVoteContext.ApplyTexts(__instance);
        }
    }

    //PATCH: enough player rows for up to 6 players, and the votes received so far marked again after a redraw (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Prefix(MultiplayerVoteModal __instance)
        {
            MultiplayerVoteContext.EnsurePlayerRows(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(MultiplayerVoteModal __instance)
        {
            MultiplayerVoteContext.ApplyVotes(__instance);
        }
    }

    //PATCH: Cancel on another player's vote screen refuses that vote (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.OnCancelVoteCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnCancelVoteCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(MultiplayerVoteModal __instance)
        {
            return MultiplayerVoteContext.OnCancelButton(__instance);
        }
    }

    //PATCH: the asking computer's vote ends when it passes (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.ProcessVoteAccepted))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ProcessVoteAccepted_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            MultiplayerVoteContext.OnAskerEndedVote();
        }
    }

    //PATCH: the asking computer's vote ends when someone refuses it (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.ProcessVoteRefused))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ProcessVoteRefused_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            MultiplayerVoteContext.OnAskerEndedVote();
        }
    }

    //PATCH: the game closing the vote screen by itself cancels the vote on the asking computer (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerStatusModal), nameof(MultiplayerStatusModal.OnBeginHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginHide_Patch
    {
        [UsedImplicitly]
        public static void Postfix(MultiplayerStatusModal __instance)
        {
            MultiplayerVoteContext.OnScreenHiding(__instance);
        }
    }

    //PATCH: records a request from another player, refuses it while another vote runs, and shows an approval's
    //request with its own texts (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.RequestVote))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RequestVote_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            LocalCommandManager __instance, string caller, string voteParameter, int actorNumber)
        {
            return MultiplayerVoteContext.OnRequest(caller, voteParameter, actorNumber) &&
                   !MultiplayerApprovalContext.ShowRequest(__instance, caller, voteParameter, actorNumber);
        }
    }

    //PATCH: an answer is counted by whoever asked (the game counted it on the host only) and shown on every other
    //player's vote screen (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.AnswerVote))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AnswerVote_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(bool accepted, int actorNumber)
        {
            MultiplayerVoteContext.OnAnswer(accepted, actorNumber);

            return false;
        }
    }

    //PATCH: after Accept the request window gives way to the vote screen (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.VoteAccepted))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class VoteAccepted_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            MultiplayerVoteContext.OnLocalAnswer(true);
        }
    }

    //PATCH: records that this player refused (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.VoteRejected))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class VoteRejected_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            MultiplayerVoteContext.OnLocalAnswer(false);
        }
    }

    //PATCH: the vote passed: the other players' vote screens close (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.NotifyVoteAccepted))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class NotifyVoteAccepted_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            MultiplayerVoteContext.OnOutcome();
        }
    }

    //PATCH: the vote was refused: the other players' vote screens close (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.NotifyVoteRejected))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class NotifyVoteRejected_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            MultiplayerVoteContext.OnOutcome();
        }
    }

    //PATCH: the asking player cancelled: the others are told who, and their screens close (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.CancelVoteRPC))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CancelVoteRPC_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(PhotonMessageInfo info)
        {
            MultiplayerVoteContext.OnCancelled(info.Sender?.ActorNumber ?? 0);

            return false;
        }
    }
}
