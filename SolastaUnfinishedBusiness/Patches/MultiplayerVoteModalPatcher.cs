using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

// The game's vote screen also shows the approval of time-passing actions (MultiplayerApprovalVote). These patches only
// act while it does; a fast travel vote runs unchanged.
[UsedImplicitly]
public static class MultiplayerVoteModalPatcher
{
    //PATCH: Cancel withdraws an approval request through the mod's own message, not the game's cancel vote (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.OnCancelVoteCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnCancelVoteCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(MultiplayerVoteModal __instance)
        {
            if (!MultiplayerApprovalVote.IsShowingApproval(__instance))
            {
                return true;
            }

            MultiplayerApprovalVote.Cancel();

            return false;
        }
    }

    //PATCH: a refresh binds every player as pending again; the approvals received so far are marked again (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerVoteModal), nameof(MultiplayerVoteModal.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(MultiplayerVoteModal __instance)
        {
            MultiplayerApprovalVote.ApplyVotes(__instance);
        }
    }

    //PATCH: if the game closes the screen itself (a change of game state), the approval request is withdrawn and the
    //screen's travel texts are put back (MULTIPLAYER)
    [HarmonyPatch(typeof(MultiplayerStatusModal), nameof(MultiplayerStatusModal.OnBeginHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginHide_Patch
    {
        [UsedImplicitly]
        public static void Postfix(MultiplayerStatusModal __instance)
        {
            if (__instance is MultiplayerVoteModal voteModal && MultiplayerApprovalVote.IsShowingApproval(voteModal))
            {
                MultiplayerApprovalVote.Cancel();
            }
        }
    }
}
