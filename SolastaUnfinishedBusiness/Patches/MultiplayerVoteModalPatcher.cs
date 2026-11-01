using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using Photon.Pun;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

// The game's multiplayer vote (fast travel, and the mod's approval of time-passing actions), made to work whoever asks
// and shown to every player. The behaviour is described in Models/MultiplayerVoteContext.cs.
[UsedImplicitly]
public static class MultiplayerVoteModalPatcher
{
    //PATCH: records the vote this computer starts and adds its ID to the request; refuses it while another vote runs
    //or a player has another build (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.ShowVote))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ShowVote_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ref string parameter, MultiplayerVoteModal.VoteCanceledHandler voteCanceled)
        {
            return MultiplayerVoteContext.OnShowVote(ref parameter, voteCanceled);
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

    //PATCH: the vote screen preview's made-up players instead of the room's (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.CollectPlayerInfo))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CollectPlayerInfo_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(MultiplayerVoteModal __instance)
        {
            return MultiplayerVoteContext.OnCollectPlayerInfo(__instance);
        }
    }

    //PATCH: the vote screen preview binds its made-up players' rows (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.BindUnreadyPlayerInfoGroup))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindUnreadyPlayerInfoGroup_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(PlayerInfoGroup playerInfoGroup, int playerId)
        {
            return MultiplayerVoteContext.OnBindRow(playerInfoGroup, playerId);
        }
    }

    //PATCH: the vote screen preview's made-up players get the game's default avatars (MULTIPLAYER)
    [HarmonyPatch(typeof(PlayerInfoGroup), nameof(PlayerInfoGroup.RefreshPlayerAvatar))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshPlayerAvatar_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(int playerId, RawImage playerImage,
            List<AssetReferenceSprite> defaultPlayerSpriteReferences, ref Sprite defaultSprite, ref bool __result)
        {
            return MultiplayerVoteContext.OnRefreshAvatar(playerId, playerImage, defaultPlayerSpriteReferences,
                ref defaultSprite, ref __result);
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

    //PATCH: an approval request goes straight to the request handling, without the game's travel request step, which
    //stops every character's action and drops the request unanswered where a script or a save-blocking action runs
    //(MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.RequestVoteRPC))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RequestVoteRPC_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(NetworkingManager __instance, int voteType, string caller, string voteParameter,
            PhotonMessageInfo info)
        {
            MultiplayerVoteContext.TryTakeVoteId(voteParameter, out var original, out _);

            if (!MultiplayerApprovalContext.IsApprovalParameter(original))
            {
                return true;
            }

            var senderId = info.Sender?.ActorNumber ?? 0;

            if (senderId != __instance.LocalPlayerNumber)
            {
                __instance.localCommandManager.RequestVote((NetworkingDefinitions.MultiplayerVoteType)voteType,
                    caller, voteParameter, senderId);
            }

            return false;
        }
    }

    //PATCH: records a request from another player (taking the vote ID out of its parameter), refuses it while another
    //vote runs, and shows an approval's request with its own texts (MULTIPLAYER)
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.RequestVote))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RequestVote_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            LocalCommandManager __instance, string caller, ref string voteParameter, int actorNumber)
        {
            var hasVoteId = MultiplayerVoteContext.TryTakeVoteId(voteParameter, out var original, out var voteId);

            // the game's travel request then shows the parameter without the ID
            voteParameter = original;

            return MultiplayerVoteContext.OnRequest(caller, original, voteId, hasVoteId, actorNumber) &&
                   !MultiplayerApprovalContext.ShowRequest(__instance, caller, original);
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

    //PATCH: this player's answer is sent with the vote's ID (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.AnswerVote))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AnswerVote_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(bool accepted)
        {
            MultiplayerVoteContext.SendLocalAnswer(accepted);

            return false;
        }
    }

    //PATCH: the asking computer's "vote passed" is sent with the vote's ID (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.NotifyVoteAccepted))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class NotifyVoteAccepted_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            MultiplayerVoteContext.SendOutcome(true, 0);

            return false;
        }
    }

    //PATCH: the asking computer's "vote refused" is sent with the vote's ID (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.NotifyVoteRejected))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class NotifyVoteRejected_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(int actorNumber)
        {
            MultiplayerVoteContext.SendOutcome(false, actorNumber);

            return false;
        }
    }

    //PATCH: the asking player's cancel is sent with the vote's ID (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.CancelVote))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CancelVote_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            MultiplayerVoteContext.SendCancel();

            return false;
        }
    }

    //PATCH: the game's answer, outcome and cancel messages carry no vote ID; this build no longer sends them, and ignores
    //them when another build does (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.AnswerVoteRPC))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AnswerVoteRPC_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            return false;
        }
    }

    //PATCH: ignores the game's ID-less "vote passed" message (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.NotifyVoteAcceptedRPC))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class NotifyVoteAcceptedRPC_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            return false;
        }
    }

    //PATCH: ignores the game's ID-less "vote refused" message (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.NotifyVoteRejectedRPC))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class NotifyVoteRejectedRPC_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            return false;
        }
    }

    //PATCH: ignores the game's ID-less cancel message (MULTIPLAYER)
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.CancelVoteRPC))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CancelVoteRPC_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            return false;
        }
    }
}
