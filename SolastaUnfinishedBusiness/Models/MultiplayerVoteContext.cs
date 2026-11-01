using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// The game's multiplayer vote as every computer sees it. The game has one vote, the fast travel vote
// (MultiplayerFastTravelVoteModal, NetworkingManager's RequestVote / AnswerVote / CancelVote / NotifyVote* messages);
// the mod also runs the approval of time-passing actions on it (MultiplayerApprovalContext), with only its texts
// changed. User request, 2026-11-01: use the game's vote as it is, and show the vote screen to every player.
//
// What the game does by itself: the asking player gets the vote screen (the other players with their votes, and
// Cancel); everyone else gets the request window (MultiplayerAnswerModal: Accept, Refuse, a 20 second timer that
// refuses when it runs out). When the vote passes, only the asking computer runs the function it gave the vote screen
// (for travel, MapWaypointItem.FastTravelVotePassed sends the travel command); the others just close their window.
//
// What this class changes, for travel and approval votes alike:
// - Answers are counted by whoever asked. The game counts them only on the host's screen (LocalCommandManager.
//   AnswerVote checks IsMasterClient), so a vote started by another player never received its answers.
// - A player who accepted gets the vote screen too, showing who proposed what and everyone's votes so far, instead of
//   the request window saying "waiting for players". Its Cancel refuses the vote, which ends it for everyone, as a
//   refusal does.
// - When the asking player cancels, the others are told who cancelled; the game always named the host.
// - The vote screen gets more player rows when the room has more than 4 players (the prefab has 3; the mod allows 6).
// - A player who leaves no longer blocks the vote: the game reset every row to "waiting" on a redraw, so the asking
//   computer waited for answers that could not come. The vote passes once every player still in the room accepted.
// - Only one vote at a time: a request that arrives while another vote is running here is refused. Two votes started
//   at the same moment therefore end each other, because an answer doesn't say which vote it is for.
//
// Every computer keeps one record of the running vote (_vote): who asked, what, and who accepted so far. The answers
// reach every computer (AnswerVote is sent to all the others), which is what lets each one show everyone's votes.
internal static class MultiplayerVoteContext
{
    private static Vote _vote;

    // the vote screen's title and proposal labels with the prefab's own texts, captured on first use
    private static GuiLabel _titleLabel;
    private static GuiLabel _descriptionLabel;
    private static string _titleText;
    private static string _descriptionText;

    internal static bool IsVoteRunning => _vote != null;

    // the running vote's parameter on a computer that was asked (null when there is none or this computer asked)
    internal static string RequestParameter => _vote is { LocalIsAsker: false } ? _vote.Parameter : null;

    // the player who asked for the running vote
    internal static int RequestAskerId => _vote?.AskerId ?? 0;

    // true while this computer was asked and hasn't answered yet
    internal static bool IsAwaitingLocalAnswer => _vote is { LocalIsAsker: false, Answered: false };

    private static int LocalPlayerNumber =>
        ServiceRepository.GetService<INetworkingService>()?.LocalPlayerNumber ?? PhotonNetwork.LocalPlayer.ActorNumber;

    private static MultiplayerFastTravelVoteModal VoteScreen =>
        Gui.GuiService.GetScreen<MultiplayerFastTravelVoteModal>();

    // leaving or joining a room: forgets the vote and closes its screens without sending anything
    internal static void Reset()
    {
        if (_vote == null)
        {
            return;
        }

        var shownHere = _vote.LocalIsAsker || _vote.ShownToLocal;

        _vote = null;
        HideLocalScreens(shownHere);
    }

    // this computer drops out of the vote it was asked to join (it answered for a reason of its own, e.g. busy)
    internal static void Forget()
    {
        if (_vote is { LocalIsAsker: false })
        {
            _vote = null;
        }
    }

    #region Asking player

    // MultiplayerVoteModal.ShowVote: records the vote this computer starts; false refuses it while another vote runs
    internal static bool OnShowVote(string parameter, MultiplayerVoteModal.VoteCanceledHandler voteCanceled)
    {
        if (_vote != null)
        {
            Gui.GuiService.ShowAlert(Gui.Localize("Message/&MultiplayerVoteAlreadyRunning"), Gui.ColorFailure, 5f);
            // the caller undoes what it prepared (fast travel clears its "preparing" state), as for a cancel
            voteCanceled?.Invoke();

            return false;
        }

        _vote = new Vote
        {
            AskerId = LocalPlayerNumber,
            AskerName = PhotonNetwork.LocalPlayer?.NickName ?? string.Empty,
            Parameter = parameter,
            LocalIsAsker = true
        };

        return true;
    }

    // ProcessVoteAccepted, ProcessVoteRefused and the asking player's Cancel end the vote here before the game closes
    // the screen, so closing it isn't taken for the game dropping the vote (OnScreenHiding)
    internal static void OnAskerEndedVote()
    {
        if (_vote is { LocalIsAsker: true })
        {
            _vote = null;
        }
    }

    // called every network frame on the asking computer: the vote passes once every player still in the room accepted,
    // so a player who leaves doesn't hold it up
    private static void UpdateAsker()
    {
        var screen = VoteScreen;

        if (!screen || !screen.Visible || screen.Hiding)
        {
            return;
        }

        if (PhotonNetwork.PlayerListOthers.All(x => _vote.Accepted.Contains(x.ActorNumber)))
        {
            screen.ProcessVoteAccepted();
        }
    }

    #endregion

    #region Other players

    // LocalCommandManager.RequestVote, on every computer but the asking one: records the vote; false when this
    // computer refused it because another vote is running here
    internal static bool OnRequest(string caller, string parameter, int askerId)
    {
        if (_vote != null)
        {
            if (_vote.AskerId != askerId)
            {
                ServiceRepository.GetService<IFeedbackCommandService>()?.AnswerVote(false, 0);
            }

            return false;
        }

        _vote = new Vote { AskerId = askerId, AskerName = caller ?? string.Empty, Parameter = parameter };

        return true;
    }

    // LocalCommandManager.VoteAccepted / VoteRejected: this player answered the request window
    internal static void OnLocalAnswer(bool accepted)
    {
        if (_vote is not { LocalIsAsker: false })
        {
            return;
        }

        _vote.Answered = true;

        if (accepted)
        {
            ShowVoteScreenToOther();
        }
    }

    // the request window is replaced by the vote screen, which shows who proposed what and everyone's votes so far
    private static void ShowVoteScreenToOther()
    {
        var answerModal = Gui.GuiService.GetScreen<MultiplayerAnswerModal>();

        if (answerModal && answerModal.Visible && !answerModal.Hiding)
        {
            answerModal.Hide();
        }

        var screen = VoteScreen;

        if (!screen || screen.Visible)
        {
            return;
        }

        _vote.ShownToLocal = true;
        // never run here: the vote passes or ends on the asking computer, and Cancel is handled by OnCancelButton
        screen.VotePassed = null;
        screen.VoteCanceled = null;
        ApplyTexts(screen);
        screen.CollectPlayerInfo();
        screen.Show(false);
    }

    // called every network frame on a computer that was asked: the vote is over if the asking player left
    private static void UpdateOther()
    {
        if (PhotonNetwork.CurrentRoom?.GetPlayer(_vote.AskerId) != null)
        {
            return;
        }

        Reset();
    }

    #endregion

    #region Messages

    // LocalCommandManager.AnswerVote, on every computer but the one that answered (replaces the game's handling)
    internal static void OnAnswer(bool accepted, int actorNumber)
    {
        if (_vote == null || actorNumber == _vote.AskerId)
        {
            return;
        }

        if (_vote.LocalIsAsker)
        {
            if (accepted)
            {
                _vote.Accepted.Add(actorNumber);
            }

            var screen = VoteScreen;

            // the game's own counting: marks the row, and passes or refuses the vote
            if (screen && screen.Visible && !screen.Hiding)
            {
                screen.PlayerVoteReceived(actorNumber, accepted);
            }

            return;
        }

        if (accepted)
        {
            _vote.Accepted.Add(actorNumber);

            var screen = VoteScreen;

            if (_vote.ShownToLocal && screen && screen.Visible)
            {
                ApplyVotes(screen);
            }

            return;
        }

        // refused: the windows close here now; the asking computer's notice (NotifyVoteRejected) then names who
        // refused and ends the vote. The game also showed that name from here, so it appeared twice.
        var shownHere = _vote.ShownToLocal;

        _vote.ShownToLocal = false;
        HideLocalScreens(shownHere);
    }

    // LocalCommandManager.NotifyVoteAccepted / NotifyVoteRejected: the asking computer reports the outcome
    internal static void OnOutcome()
    {
        if (_vote is not { LocalIsAsker: false })
        {
            return;
        }

        Reset();
    }

    // NetworkingManager.CancelVoteRPC (replaces the game's handling, which named the host as the one who cancelled)
    internal static void OnCancelled(int senderId)
    {
        if (_vote is not { LocalIsAsker: false } || _vote.AskerId != senderId)
        {
            return;
        }

        var name = _vote.AskerName;

        Reset();
        Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerVoteCancelledBy", name), Gui.ColorAlert, 5f);
    }

    internal static void Update()
    {
        if (_vote == null)
        {
            return;
        }

        if (_vote.LocalIsAsker)
        {
            UpdateAsker();
        }
        else
        {
            UpdateOther();
        }
    }

    #endregion

    #region Vote screen

    // MultiplayerVoteModal.OnCancelVoteCb: false when the button refuses another player's vote instead of cancelling
    internal static bool OnCancelButton(MultiplayerVoteModal screen)
    {
        if (_vote == null)
        {
            return true;
        }

        if (_vote.LocalIsAsker)
        {
            // the game's cancel: tells the others, closes the screen and runs the vote's cancel function
            OnAskerEndedVote();

            return true;
        }

        _vote.ShownToLocal = false;
        screen.Hide();
        ServiceRepository.GetService<IFeedbackCommandService>()?.AnswerVote(false, 0);

        return false;
    }

    // MultiplayerStatusModal.OnBeginHide: the game closed the vote screen without the vote ending (a load or a change
    // of game state). On the asking computer the vote is cancelled for everyone, as with Cancel; elsewhere only the
    // screen is gone, which isn't an answer.
    internal static void OnScreenHiding(MultiplayerStatusModal screen)
    {
        if (_vote == null || screen != VoteScreen)
        {
            return;
        }

        if (!_vote.LocalIsAsker)
        {
            _vote.ShownToLocal = false;

            return;
        }

        var voteScreen = (MultiplayerVoteModal)screen;
        var voteCanceled = voteScreen.VoteCanceled;

        _vote = null;
        ServiceRepository.GetService<IFeedbackCommandService>()?.CancelVote();
        voteCanceled?.Invoke();
    }

    // MultiplayerVoteModal.Refresh binds one row per other player by index; the prefab has 3 rows (4 players), and
    // the mod allows up to 6 players, so rows are added by copying the last one
    internal static void EnsurePlayerRows(MultiplayerVoteModal screen)
    {
        var groups = screen.playerInfoGroups;
        var needed = (ServiceRepository.GetService<INetworkingService>()?.GetPlayersInRoom()?.Count ?? 1) - 1;

        while (groups.Count > 0 && groups.Count < needed)
        {
            var last = groups[groups.Count - 1];
            var copy = Object.Instantiate(last, last.transform.parent);

            copy.name = $"{last.name} (FB {groups.Count})";
            groups.Add(copy);
        }
    }

    // MultiplayerVoteModal.Refresh binds every row as "waiting"; the votes received so far are marked again. On a
    // computer that was asked, the asking player's row shows accepted.
    internal static void ApplyVotes(MultiplayerVoteModal screen)
    {
        if (_vote == null || screen != VoteScreen || !(_vote.LocalIsAsker || _vote.ShownToLocal))
        {
            return;
        }

        foreach (var group in screen.playerInfoGroups.Where(x =>
                     x.gameObject.activeSelf && x.PlayerSlot != null &&
                     x.PlayerStatus != PlayerInfoGroup.PlayerAbstractStatus.Valid &&
                     (x.PlayerSlot.PlayerId == _vote.AskerId || _vote.Accepted.Contains(x.PlayerSlot.PlayerId))))
        {
            screen.SetPlayerVoteAccepted(group);
        }
    }

    // MultiplayerFastTravelVoteModal.ShowVoteHook and the vote screen of a player who accepted: the title, the proposal
    // line ("You propose a fast travel to" or "<name> proposes a fast travel to") and the destination, or for an
    // approval "Approval Vote", "You propose:" / "<name> proposes:" and the action with its in-game time
    internal static void ApplyTexts(MultiplayerFastTravelVoteModal screen)
    {
        if (_vote == null)
        {
            return;
        }

        CaptureLabels(screen);

        var approval = MultiplayerApprovalContext.DescribeVoteParameter(_vote.Parameter);
        var mine = _vote.LocalIsAsker;
        string title;
        string description;

        if (approval != null)
        {
            title = Gui.Localize("Message/&MultiplayerApprovalVoteTitle");
            description = mine
                ? Gui.Localize("Message/&MultiplayerApprovalVoteDescription")
                : Gui.Format("Message/&MultiplayerApprovalVoteOthersDescription", _vote.AskerName);
        }
        else
        {
            title = _titleText;
            description = mine
                ? _descriptionText
                : Gui.Format("Message/&MultiplayerVoteOthersTravelDescription", _vote.AskerName);
        }

        if (_titleLabel)
        {
            _titleLabel.Text = title;
        }

        if (_descriptionLabel)
        {
            _descriptionLabel.Text = description;
        }

        screen.travelDestinationLabel.Text = approval ?? _vote.Parameter;
    }

    private static void CaptureLabels(MultiplayerFastTravelVoteModal screen)
    {
        if (_titleLabel && _descriptionLabel)
        {
            return;
        }

        var labels = screen.GetComponentsInChildren<GuiLabel>(true);

        _titleLabel = labels.FirstOrDefault(x => x.name == "VoteTitle");
        _descriptionLabel = labels.FirstOrDefault(x => x.name == "VoteDescription");
        _titleText = _titleLabel?.Text;
        _descriptionText = _descriptionLabel?.Text;
    }

    // closes the request window, and the vote screen when it showed this vote here
    private static void HideLocalScreens(bool voteScreen)
    {
        var answerModal = Gui.GuiService.GetScreen<MultiplayerAnswerModal>();

        if (answerModal && answerModal.Visible && !answerModal.Hiding)
        {
            answerModal.Hide();
        }

        var screen = VoteScreen;

        if (voteScreen && screen && screen.Visible && !screen.Hiding)
        {
            screen.Hide();
        }
    }

    #endregion

    private sealed class Vote
    {
        // players who accepted so far (not the asking player, who proposed it)
        internal readonly HashSet<int> Accepted = [];
        internal int AskerId;
        internal string AskerName;
        // the travel destination, or an approval's encoded action (MultiplayerApprovalContext.DescribeVoteParameter)
        internal string Parameter;
        internal bool LocalIsAsker;
        // a computer that was asked: it answered the request window
        internal bool Answered;
        // a computer that was asked: the vote screen shows this vote (after Accept)
        internal bool ShownToLocal;
    }
}
