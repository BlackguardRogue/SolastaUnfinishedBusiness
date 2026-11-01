using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Multiplayer approval of actions that pass in-game time (rests, learning or scribing spells, Mark Deity, rituals and
// spells or powers that take a minute or more). The action waits on the computer that asked for it until every other
// player approves, then goes through the lockstep as usual.
//
// The approval is the game's own fast travel vote, with only its texts changed (user direction, 2026-11-01): the
// asking computer calls MultiplayerVoteModal.ShowVote with an encoded parameter (EncodeVoteParameter) and the action
// as the function to run when the vote passes; the game sends the request, counts the answers and reports the outcome.
// MultiplayerVoteContext makes that vote work whoever asks, and shows it to every player; this class supplies the
// approval's texts (DescribeVoteParameter, ShowRequest) and the parts the game's vote doesn't have:
// - nobody is asked while a character is in combat or in a cutscene: the asking player is told which character is
//   busy instead (before asking, and when another computer is busy as the request arrives, through Photon event 172);
// - a fight or cutscene starting while the vote runs cancels it.
internal static class MultiplayerApprovalContext
{
    internal enum ApprovalAction : byte
    {
        ShortRest = 1,
        LongRest = 2,
        LearnSpell = 3,
        ScribeScroll = 4,
        MarkDeity = 5,
        CastSpell = 6,
        CastRitual = 7,
        UsePower = 8,
        WaitAndLongRest = 9
    }

    // the busy reply's reasons; the values are those the earlier request/reply messages used
    private enum Answer : byte
    {
        None = 0,
        InCombat = 2,
        InCutscene = 3
    }

    private const byte EventCode = 172;
    // a computer that was asked can't take part (a fight or a cutscene): [BusyKind, (byte)Answer, character names]
    private const byte BusyKind = 2;
    // marks a vote parameter as an approval (a travel vote's parameter is the destination's name); the parts are
    // separated by PartSeparator: marker, action, character name, subject definition name, seconds
    private const string ApprovalMarker = "\u0001FinishingBusinessApproval";
    private const char PartSeparator = '\u0002';
    // GameCampActivityMarkDeity's duration
    internal const int MarkDeitySeconds = 600;
    // actions up to this long can skip approval (rituals, 1 and 10 minute castings, Mark Deity)
    private const int ShortActionSeconds = 600;

    private static bool _eventHandlerRegistered;
    // the approval this computer asked for, while its vote runs
    private static PendingRequest _pending;

    // set while an approved action runs again, so the patches let it through
    internal static bool IsRunningApprovedAction { get; private set; }

    private static bool IsOnlineRoom =>
        Global.IsMultiplayer && PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    // leaving or joining a room: forgets the approval and the vote, closing their screens
    internal static void Reset()
    {
        _pending = null;
        MultiplayerVoteContext.Reset();
    }

    #region Asking

    // true when the action must wait: it then runs through proceed once every other player approves
    internal static bool MustWaitForApproval(
        ApprovalAction action, string characterName, BaseDefinition subject, int seconds, Action proceed)
    {
        if (IsRunningApprovedAction ||
            !Main.Settings.MultiplayerRequireApprovalForTimeConsumingActions ||
            !IsOnlineRoom ||
            seconds <= 0 ||
            (Main.Settings.MultiplayerApprovalSkipActionsOfTenMinutesOrLess && seconds <= ShortActionSeconds) ||
            PhotonNetwork.PlayerListOthers.Length == 0)
        {
            return false;
        }

        RegisterEventHandler();

        if (_pending != null || MultiplayerVoteContext.IsVoteRunning)
        {
            Gui.GuiService.ShowAlert(Gui.Localize("Message/&MultiplayerVoteAlreadyRunning"), Gui.ColorFailure, 5f);

            return true;
        }

        var busy = DescribeLocalBusy(out _);

        if (busy != null)
        {
            ShowBusy(busy);

            return true;
        }

        var screen = Gui.GuiService.GetScreen<MultiplayerFastTravelVoteModal>();

        // the game checks this when the request arrives on each computer (NetworkingManager.RequestVoteAsync, "nothing
        // running that blocks saving") and would tell the asking player that fast travel isn't possible; checked here
        // first so an approval doesn't get the travel message
        if (!screen || screen.Visible ||
            ServiceRepository.GetService<IFastTravelService>() is not { CanFastTravel: true })
        {
            Gui.GuiService.ShowAlert(Gui.Localize("Failure/&FailureGenericTryAgain"), Gui.ColorFailure, 5f);

            return true;
        }

        var pending = new PendingRequest { Proceed = proceed };

        _pending = pending;
        // the game's vote: sends the request to everyone (MultiplayerVoteContext swaps in the approval's texts), counts
        // the answers on this computer and runs one of the two functions
        screen.ShowVote(NetworkingDefinitions.MultiplayerVoteType.MapWaypointNavigate,
            EncodeVoteParameter(action, characterName, subject?.Name ?? string.Empty, seconds),
            () => OnVotePassed(pending),
            () => OnVoteCanceled(pending));

        return true;
    }

    // everyone approved (the game shows its own "everyone accepted" alert): the action runs
    private static void OnVotePassed(PendingRequest pending)
    {
        if (_pending != pending)
        {
            return;
        }

        _pending = null;
        IsRunningApprovedAction = true;

        try
        {
            pending.Proceed();
        }
        finally
        {
            IsRunningApprovedAction = false;
        }
    }

    // refused, cancelled, or the game closed the vote: the action doesn't run. The game already said who refused;
    // a refusal for being busy also says which character is busy.
    private static void OnVoteCanceled(PendingRequest pending)
    {
        if (_pending != pending)
        {
            return;
        }

        _pending = null;

        if (pending.BusyReason != null)
        {
            ShowBusy(pending.BusyReason);
        }
    }

    // another computer can't take part right now: that counts as its refusal, with the reason shown afterwards
    private static void OnBusyReply(Player sender, Answer answer, string names)
    {
        var screen = Gui.GuiService.GetScreen<MultiplayerFastTravelVoteModal>();

        if (_pending == null || sender == null || !screen || !screen.Visible || screen.Hiding)
        {
            return;
        }

        _pending.BusyReason = Gui.Format(answer == Answer.InCombat
            ? "Message/&MultiplayerApprovalInCombat"
            : "Message/&MultiplayerApprovalInCutscene", names);
        // the game's refusal: tells everyone who refused, closes the screens and cancels the vote
        screen.PlayerVoteReceived(sender.ActorNumber, false);
    }

    // called every network frame: a fight or cutscene starting while the vote runs ends it
    internal static void Update()
    {
        MultiplayerVoteContext.Update();

        if (!IsOnlineRoom)
        {
            return;
        }

        if (_pending != null)
        {
            var busy = DescribeLocalBusy(out _);
            var screen = Gui.GuiService.GetScreen<MultiplayerFastTravelVoteModal>();

            if (busy != null && screen && screen.Visible && !screen.Hiding)
            {
                _pending.BusyReason = busy;
                // the vote screen's own Cancel: tells the others, closes it and cancels the approval
                screen.OnCancelVoteCb();
            }

            return;
        }

        // a request on screen here, not answered yet, is refused with the reason if this computer gets busy
        if (MultiplayerVoteContext.IsAwaitingLocalAnswer &&
            DescribeVoteParameter(MultiplayerVoteContext.RequestParameter) != null &&
            DescribeLocalBusy(out var answer) != null)
        {
            ReplyBusy(MultiplayerVoteContext.RequestAskerId, answer);
            HideAnswerModal();
            MultiplayerVoteContext.Forget();
        }
    }

    private static void ShowBusy(string reason)
    {
        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2, "Message/&MultiplayerApprovalBusyTitle",
            Gui.Format("Message/&MultiplayerApprovalBusyDescription", reason),
            "Message/&MessageOkTitle", string.Empty, null, null);
    }

    #endregion

    #region Answering

    // LocalCommandManager.RequestVote for an approval (true; false for a travel vote, which the game shows as usual):
    // the game's request window with the approval's texts and the game's own Accept and Refuse, or the busy reply
    internal static bool ShowRequest(LocalCommandManager commandManager, string caller, string parameter, int askerId)
    {
        if (!TryDecodeVoteParameter(parameter, out var action, out var characterName, out var subjectName,
                out var seconds))
        {
            return false;
        }

        RegisterEventHandler();

        if (DescribeLocalBusy(out var answer) != null)
        {
            ReplyBusy(askerId, answer);
            MultiplayerVoteContext.Forget();

            return true;
        }

        var title = Gui.Localize("Message/&MultiplayerApprovalRequestTitle");
        var text = Gui.Format("Message/&MultiplayerApprovalRequestDescription", caller,
            DescribeAction(action, characterName, subjectName), FormatDuration(seconds));

        // as the game's LocalCommandManager.RequestVote does for travel, with these texts
        Gui.GuiService.ShowMultiplayerAnswer(title, text, commandManager.VoteAccepted, commandManager.VoteRejected,
            true);

        return true;
    }

    private static void ReplyBusy(int askerId, Answer answer)
    {
        PhotonNetwork.RaiseEvent(EventCode,
            new object[] { BusyKind, (byte)answer, DescribeLocalBusyNames(answer) },
            new RaiseEventOptions { TargetActors = [askerId] }, SendOptions.SendReliable);
    }

    private static void HideAnswerModal()
    {
        var answerModal = Gui.GuiService.GetScreen<MultiplayerAnswerModal>();

        if (answerModal && answerModal.Visible && !answerModal.Hiding)
        {
            answerModal.Hide();
        }
    }

    #endregion

    #region Vote parameter

    private static string EncodeVoteParameter(ApprovalAction action, string characterName, string subjectName,
        int seconds)
    {
        return string.Join(PartSeparator.ToString(), ApprovalMarker, ((byte)action).ToString(),
            characterName ?? string.Empty, subjectName ?? string.Empty, seconds.ToString());
    }

    private static bool TryDecodeVoteParameter(string parameter, out ApprovalAction action, out string characterName,
        out string subjectName, out int seconds)
    {
        action = default;
        characterName = subjectName = string.Empty;
        seconds = 0;

        if (parameter == null || !parameter.StartsWith(ApprovalMarker + PartSeparator))
        {
            return false;
        }

        var parts = parameter.Split(PartSeparator);

        if (parts.Length != 5 || !byte.TryParse(parts[1], out var actionValue) ||
            !int.TryParse(parts[4], out seconds))
        {
            return false;
        }

        action = (ApprovalAction)actionValue;
        characterName = parts[2];
        subjectName = parts[3];

        return true;
    }

    // the approval's action with its in-game time, as the vote screen shows it, or null for a travel vote
    internal static string DescribeVoteParameter(string parameter)
    {
        return TryDecodeVoteParameter(parameter, out var action, out var characterName, out var subjectName,
            out var seconds)
            ? Gui.Format("Message/&MultiplayerApprovalVoteAction", DescribeAction(action, characterName, subjectName),
                FormatDuration(seconds))
            : null;
    }

    #endregion

    #region Busy

    // the reason this computer can't be asked right now (a fight or a cutscene), or null
    private static string DescribeLocalBusy(out Answer answer)
    {
        if (Gui.Battle != null)
        {
            answer = Answer.InCombat;

            return Gui.Format("Message/&MultiplayerApprovalInCombat", DescribeLocalBusyNames(answer));
        }

        if (ServiceRepository.GetService<INarrativeDirectionService>() is { IsSequenceInProgress: true })
        {
            answer = Answer.InCutscene;

            return Gui.Format("Message/&MultiplayerApprovalInCutscene", DescribeLocalBusyNames(answer));
        }

        answer = Answer.None;

        return null;
    }

    // this player's characters taking part, or every party character when none of them is
    private static string DescribeLocalBusyNames(Answer answer)
    {
        var party = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters ?? [];
        var involved = answer == Answer.InCombat && Gui.Battle != null
            ? party.Where(x => Gui.Battle.PlayerContenders.Contains(x)).ToList()
            : party.ToList();
        var mine = Gui.ActivePlayerController?.ControlledCharacters ?? [];
        var names = involved.Where(x => mine.Contains(x)).ToList();

        if (names.Count == 0)
        {
            names = involved;
        }

        return names.Count == 0
            ? PhotonNetwork.LocalPlayer?.NickName ?? "?"
            : string.Join(", ", names.Select(x => x.Name));
    }

    #endregion

    #region Text

    private static string DescribeAction(ApprovalAction action, string characterName, string subjectName)
    {
        var subject = subjectName;

        // the wait is sent in hours and worded on each computer
        if (action == ApprovalAction.WaitAndLongRest && int.TryParse(characterName, out var hours))
        {
            characterName = FormatDuration(hours * 3600);
        }

        if (DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(subjectName, out var spell))
        {
            subject = spell.FormatTitle();
        }
        else if (DatabaseRepository.GetDatabase<FeatureDefinitionPower>().TryGetElement(subjectName, out var power))
        {
            subject = power.FormatTitle();
        }

        return Gui.Format($"Message/&MultiplayerApprovalAction{action}", characterName, subject);
    }

    internal static string FormatDuration(int seconds)
    {
        var hours = seconds / 3600;
        var minutes = (seconds % 3600 + 59) / 60;
        var parts = new List<string>();

        if (hours > 0)
        {
            parts.Add(Gui.Format(hours == 1 ? "Message/&MultiplayerApprovalHour" : "Message/&MultiplayerApprovalHours",
                hours.ToString()));
        }

        if (minutes > 0 || hours == 0)
        {
            parts.Add(Gui.Format(
                minutes == 1 ? "Message/&MultiplayerApprovalMinute" : "Message/&MultiplayerApprovalMinutes",
                minutes.ToString()));
        }

        return string.Join(" ", parts);
    }

    #endregion

    #region Durations

    internal static int RestSeconds(RestType restType)
    {
        return RestDurationInSeconds.TryGetValue(restType, out var seconds) ? seconds : 0;
    }

    // in-game time an action passes, as the game counts it when the action runs (0 for none)
    internal static int ActionSeconds(CharacterActionParams actionParams, out ApprovalAction action,
        out BaseDefinition subject)
    {
        action = ApprovalAction.CastSpell;
        subject = null;

        int seconds;

        switch (actionParams?.RulesetEffect)
        {
            case RulesetEffectSpell spell:
                subject = spell.SpellDefinition;

                if (spell.SlotLevel < 0 && spell.SpellRepertoire == null && spell.SpellDefinition.Ritual)
                {
                    action = ApprovalAction.CastRitual;
                    seconds = ComputeActivationTimeSeconds(spell.SpellDefinition.RitualCastingTime);
                }
                else
                {
                    seconds = ComputeActivationTimeSeconds(spell.SpellDefinition.ActivationTime);
                }

                break;
            case RulesetEffectPower power:
                action = ApprovalAction.UsePower;
                subject = power.PowerDefinition;
                seconds = ComputeActivationTimeSeconds(power.PowerDefinition.ActivationTime);
                break;
            default:
                return 0;
        }

        // a feast (Heroes' Feast) passes its hours after the casting
        seconds += actionParams.RulesetEffect.EffectDescription.EffectForms
            .Where(x => x.FormType == EffectForm.EffectFormType.Alteration &&
                        x.AlterationForm.AlterationType == AlterationForm.Type.Feast)
            .Sum(x => x.AlterationForm.FeastDurationHours * 3600);

        return seconds;
    }

    #endregion

    #region Network

    private static void RegisterEventHandler()
    {
        if (_eventHandlerRegistered || PhotonNetwork.NetworkingClient == null)
        {
            return;
        }

        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _eventHandlerRegistered = true;
    }

    // clients register as soon as they join, so a busy reply isn't missed before they ask for anything themselves
    internal static void OnJoinedRoom()
    {
        Reset();
        RegisterEventHandler();
    }

    private static void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != EventCode ||
            photonEvent.CustomData is not object[] { Length: >= 3 } data ||
            (byte)data[0] != BusyKind)
        {
            return;
        }

        OnBusyReply(PhotonNetwork.CurrentRoom?.GetPlayer(photonEvent.Sender), (Answer)(byte)data[1],
            (string)data[2]);
    }

    #endregion

    private sealed class PendingRequest
    {
        internal Action Proceed;
        // set when the vote ends because a character is busy, shown once it is cancelled
        internal string BusyReason;
    }
}
