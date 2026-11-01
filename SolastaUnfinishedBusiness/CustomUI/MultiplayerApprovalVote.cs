using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.CustomUI;

// The approval of time-passing actions (MultiplayerApprovalContext) shown with the game's own fast travel vote screens,
// so it looks like the game's travel vote (user request, 2026-11-01). The asking player gets
// MultiplayerFastTravelVoteModal: their proposal, every player in the room with their vote, and Cancel. The other
// players get MultiplayerAnswerModal: the request with Accept and Refuse and the game's 20 second timer, which refuses
// when it runs out, as it does for travel.
//
// Only the screens are borrowed. The requests, answers and cancels stay on the mod's own messages (Photon event 172):
// the game's vote messages would start a fast travel when they pass, so ShowVote, the vote modal's Cancel and its
// "everyone accepted" step are never used for an approval. The vote screen's title and description are the travel
// texts in the prefab; they are replaced while an approval is shown and put back afterwards.
internal static class MultiplayerApprovalVote
{
    private static readonly HashSet<int> Approved = [];
    private static MultiplayerFastTravelVoteModal _voteModal;
    private static Action _cancel;
    private static GuiLabel _titleLabel;
    private static GuiLabel _descriptionLabel;
    private static string _titleBefore;
    private static string _descriptionBefore;
    private static string _destinationBefore;
    private static MultiplayerAnswerModal _answerModal;

    // true while the vote screen shows an approval rather than a travel vote
    internal static bool IsShowingApproval(MultiplayerVoteModal modal)
    {
        return modal != null && modal == _voteModal;
    }

    #region Asking player

    // shows the asking player's vote screen; false when it isn't available (the caller then uses a message box)
    internal static bool ShowWaiting(string action, string duration, Action cancel)
    {
        var modal = Gui.GuiService.GetScreen<MultiplayerFastTravelVoteModal>();

        if (!modal || modal.Visible)
        {
            return false;
        }

        HideWaiting();

        _voteModal = modal;
        _cancel = cancel;
        Approved.Clear();

        // the asking player is the proposer, so their own row shows accepted
        Approved.Add(ServiceRepository.GetService<INetworkingService>().LocalPlayerNumber);

        var labels = modal.GetComponentsInChildren<GuiLabel>(true);

        _titleLabel = labels.FirstOrDefault(x => x.name == "VoteTitle");
        _descriptionLabel = labels.FirstOrDefault(x => x.name == "VoteDescription");
        _titleBefore = _titleLabel?.Text;
        _descriptionBefore = _descriptionLabel?.Text;
        _destinationBefore = modal.travelDestinationLabel.Text;

        if (_titleLabel)
        {
            _titleLabel.Text = Gui.Localize("Message/&MultiplayerApprovalVoteTitle");
        }

        if (_descriptionLabel)
        {
            _descriptionLabel.Text = Gui.Localize("Message/&MultiplayerApprovalVoteDescription");
        }

        modal.travelDestinationLabel.Text = Gui.Format("Message/&MultiplayerApprovalVoteAction", action, duration);
        modal.CollectPlayerInfo();
        // never called for an approval (the vote passes or ends through the mod's messages), but never null either
        modal.VotePassed = () => { };
        modal.VoteCanceled = () => { };
        modal.Show(false);

        return true;
    }

    // a player approved: their row shows accepted
    internal static void MarkApproved(int actorNumber)
    {
        if (!_voteModal)
        {
            return;
        }

        Approved.Add(actorNumber);
        ApplyVotes(_voteModal);
    }

    // closes the asking player's vote screen and puts the travel texts back
    internal static void HideWaiting()
    {
        var modal = _voteModal;

        if (!modal)
        {
            _voteModal = null;

            return;
        }

        _voteModal = null;
        _cancel = null;
        Approved.Clear();

        // not when the game is already closing it (the screen's OnBeginHide calls Cancel)
        if (modal.Visible && !modal.Hiding)
        {
            modal.Hide();
        }

        if (_titleLabel)
        {
            _titleLabel.Text = _titleBefore;
        }

        if (_descriptionLabel)
        {
            _descriptionLabel.Text = _descriptionBefore;
        }

        modal.travelDestinationLabel.Text = _destinationBefore;
        _titleLabel = null;
        _descriptionLabel = null;
    }

    // the vote screen's Cancel button, or the game closing the screen itself (a change of game state): withdraws the
    // request instead of sending the game's cancel vote message
    internal static void Cancel()
    {
        var cancel = _cancel;

        HideWaiting();
        cancel?.Invoke();
    }

    // the vote screen binds every row as pending whenever it refreshes; the approvals so far are marked again
    internal static void ApplyVotes(MultiplayerVoteModal modal)
    {
        if (!IsShowingApproval(modal))
        {
            return;
        }

        foreach (var group in modal.playerInfoGroups.Where(x =>
                     x.gameObject.activeSelf && x.PlayerSlot != null && Approved.Contains(x.PlayerSlot.PlayerId) &&
                     x.PlayerStatus != PlayerInfoGroup.PlayerAbstractStatus.Valid))
        {
            modal.SetPlayerVoteAccepted(group);
        }
    }

    #endregion

    #region Other players

    // shows the request to a player who is asked; false when the screen isn't available
    internal static bool ShowRequest(string title, string description, Action accept, Action refuse)
    {
        var modal = Gui.GuiService.GetScreen<MultiplayerAnswerModal>();

        if (!modal)
        {
            return false;
        }

        HideRequest();
        _answerModal = modal;
        modal.Show(title, description, () => accept(), () => refuse(), false);

        return true;
    }

    // closes the request screen: answered elsewhere, withdrawn, or done. After Accept it stays open showing that the
    // other players are awaited, as for a travel vote, until the asking player reports the outcome.
    internal static void HideRequest()
    {
        var modal = _answerModal;

        _answerModal = null;

        if (modal && modal.Visible && !modal.Hiding)
        {
            modal.Hide();
        }
    }

    #endregion
}
