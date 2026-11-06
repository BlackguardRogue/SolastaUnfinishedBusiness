using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Game-state fingerprint for multiplayer desyncs. Every few seconds each computer sends, for the same tick, each
// character's HP, temporary HP, position, conditions and spell slots plus the position in the shared dice stream.
// Every computer compares itself with the host. A difference seen twice in a row is a desync: it names the character
// and what differs, once per desync rather than at every check. Out of combat the host can quick-correct simple
// differences (HP, position, slots, dice) at a lockstep tick, one correction per player, each with its own ID: it first
// makes a recovery save and only corrects once the game confirms that save was written. The corrected player reports
// whether the correction could be applied, and the next check after it tells whether it held: only then is it called
// verified. A correction that was refused, didn't hold or couldn't be checked in time offers the host a resync: a new
// recovery save that every client is brought back into, loaded by its exact file, never an older save in its place.
internal static class MultiplayerDesyncContext
{
    private const byte EventCode = 171;
    private const byte FingerprintKind = 1;
    private const byte CorrectionKind = 2;
    private const byte ResyncKind = 3;
    private const byte StatusKind = 4;
    private const byte AppliedKind = 5;
    private const byte ResyncCancelledKind = 6;
    private const ulong FingerprintTickInterval = 100;
    private const int ValuesPerCharacter = 11;

    // a correction not verified this long after it was sent is reported as unverified
    private const float CorrectionTimeout = 60f;

    // A correction that a command got in front of changes nothing, so it can safely be tried again at a later check. After
    // this many, players who keep acting get the host's reload offer rather than corrections that never land.
    private const int MaxCorrectionRetries = 3;

    // the game ignores a command whose tick is further than this from the local tick (NetworkingManager.AddDelayedCommand)
    private const long CommandTickWindow = 12;

    // how far back queued commands are remembered; far more than a correction's wait for its tick
    private const long CommandTicksKept = 256;

    // recovery saves take turns in a few slots of their own, so they never replace a player's save
    private const string RecoverySavePrefix = "FB Recovery ";
    private const int RecoverySaveSlots = 3;

    private static readonly Dictionary<ulong, Fingerprint> LocalByTick = [];

    // the host's reports on a client: kept by tick within a window around the local tick, however they arrive
    private static readonly BoundedTickReports<Fingerprint> HostReports = new(8, FingerprintTickInterval * 8);
    private static readonly Dictionary<int, HashSet<string>> PreviousDifferencesByActor = [];
    private static readonly Dictionary<int, Correction> CorrectionsByActor = [];

    // the desync last reported for each player, so the same one isn't reported (or corrected) again
    private static readonly Dictionary<int, string> ReportedByActor = [];

    // players whose correction failed: not corrected automatically again until a check finds their game matching
    private static readonly HashSet<int> FailedActors = [];

    // how many times each player's correction was given up because a command got in front of it
    private static readonly Dictionary<int, int> CorrectionRetriesByActor = [];

    // players whose condition IDs were already logged as differing, until a check finds them equal
    private static readonly HashSet<int> ConditionIdsReported = [];

    // the ticks of commands queued recently, which ApplyCorrection reads to see whether one came after its capture
    private static readonly List<long> CommandTicks = [];
    private static readonly FieldInfo[] RandomStateFields =
        typeof(UnityEngine.Random.State).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(x => x.FieldType == typeof(int))
            .OrderBy(x => x.Name)
            .ToArray();

    private static bool _eventHandlerRegistered;
    private static ulong _lastFingerprintTick = ulong.MaxValue;
    private static bool _resyncOffered;
    private static string _lastRecoverySave;
    private static bool _queueingCorrection;

    // on a client: the correction the host is applying to this computer, until its outcome arrives
    private static string _clientCorrection;
    private static float _clientCorrectionUntil;
    private static string _lastStatus;

    private enum CorrectionStatus : byte
    {
        Verified = 1,
        Failed = 2,
        TimedOut = 3,
        Rejected = 4,
        Cancelled = 5,

        // nothing was changed and the host will try again: not an outcome, so the player sees no message
        Retrying = 6
    }

    internal static void Reset()
    {
        LocalByTick.Clear();
        HostReports.Prune(ulong.MaxValue);
        PreviousDifferencesByActor.Clear();
        CorrectionsByActor.Clear();
        ReportedByActor.Clear();
        FailedActors.Clear();
        CorrectionRetriesByActor.Clear();
        ConditionIdsReported.Clear();
        CommandTicks.Clear();
        _lastFingerprintTick = ulong.MaxValue;
        _resyncOffered = false;
        _clientCorrection = null;
    }

    #region Sending

    internal static void SendFingerprintIfNeeded(NetworkingManager networking)
    {
        if (!Main.Settings.MultiplayerEnableDesyncDetection ||
            !Main.Settings.MultiplayerEnableStateFingerprint ||
            !Global.IsMultiplayer ||
            !PhotonNetwork.InRoom ||
            PhotonNetwork.OfflineMode ||
            networking.frozenGameplay ||
            !networking.ValidGameplayContext())
        {
            return;
        }

        RegisterEventHandler();

        if (PhotonNetwork.IsMasterClient)
        {
            CheckCorrectionTimeouts();
        }

        var tick = networking.tick.LocalTick;

        if (tick == 0 || tick % FingerprintTickInterval != 0 || tick == _lastFingerprintTick)
        {
            return;
        }

        _lastFingerprintTick = tick;

        var fingerprint = Fingerprint.Capture(tick);

        LocalByTick[tick] = fingerprint;
        Forget(LocalByTick, tick);

        PhotonNetwork.RaiseEvent(EventCode, fingerprint.ToEvent(),
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);

        // a client compares itself as soon as the host's fingerprint for this tick is known
        if (!PhotonNetwork.IsMasterClient && HostReports.Take(tick, out var host))
        {
            Compare(host, fingerprint, PhotonNetwork.MasterClient?.ActorNumber ?? 0);
        }
    }

    private static void Forget(Dictionary<ulong, Fingerprint> byTick, ulong tick)
    {
        foreach (var old in byTick.Keys.Where(x => x + (FingerprintTickInterval * 4) < tick).ToList())
        {
            byTick.Remove(old);
        }
    }

    private static void RegisterEventHandler()
    {
        if (_eventHandlerRegistered || PhotonNetwork.NetworkingClient == null)
        {
            return;
        }

        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _eventHandlerRegistered = true;
    }

    #endregion

    #region Receiving

    private static void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != EventCode || photonEvent.CustomData is not object[] { Length: > 0 } data ||
            data[0] is not byte kind)
        {
            return;
        }

        var sender = PhotonNetwork.CurrentRoom?.GetPlayer(photonEvent.Sender);

        switch (kind)
        {
            case FingerprintKind:
                OnFingerprint(Fingerprint.FromEvent(data), sender);
                break;
            case CorrectionKind when sender is { IsMasterClient: true }:
                OnCorrection(data);
                break;
            case ResyncKind when sender is { IsMasterClient: true }:
                Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncHostResyncing", Gui.ColorAlert, 10f);
                break;
            case ResyncCancelledKind when sender is { IsMasterClient: true }:
                Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncHostResyncCancelled", Gui.ColorFailure, 10f);
                break;
            case StatusKind when sender is { IsMasterClient: true } && data.Length >= 3 &&
                                 data[1] is string id && data[2] is byte status:
                OnStatus(id, (CorrectionStatus)status);
                break;
            case AppliedKind when sender != null && PhotonNetwork.IsMasterClient && data.Length >= 3 &&
                                  data[1] is string id && data[2] is bool applied:
                OnApplied(sender.ActorNumber, id, applied);
                break;
        }
    }

    private static void OnFingerprint(Fingerprint received, Player sender)
    {
        if (received == null || sender == null)
        {
            return;
        }

        if (sender.IsMasterClient)
        {
            if (LocalByTick.TryGetValue(received.Tick, out var local))
            {
                Compare(received, local, sender.ActorNumber);
            }
            else if (ServiceRepository.GetService<INetworkingService>() is NetworkingManager networking)
            {
                HostReports.Add(received.Tick, networking.tick.LocalTick, received);
            }

            return;
        }

        if (PhotonNetwork.IsMasterClient && LocalByTick.TryGetValue(received.Tick, out var hostPrint))
        {
            Compare(hostPrint, received, sender.ActorNumber);
        }
    }

    #endregion

    #region Comparing

    // host is the host's fingerprint, other is this computer's (on a client) or a client's (on the host)
    private static void Compare(Fingerprint host, Fingerprint other, int otherActor)
    {
        // a check is only meaningful when nothing was moving or acting on either computer at that tick
        if (!host.Quiet || !other.Quiet)
        {
            return;
        }

        var allDifferences = Differences(host, other);

        // Condition IDs are only logged: they don't change what a player sees or do anything to a correction's verdict,
        // and a reload offer for them would come again with each new condition once the numbering has drifted.
        var differences = allDifferences.Where(x => x.Type != DifferenceType.ConditionIds).ToList();

        PreviousDifferencesByActor.TryGetValue(otherActor, out var previous);
        PreviousDifferencesByActor[otherActor] = new HashSet<string>(differences.Select(x => x.Key));

        var playerName = PhotonNetwork.IsMasterClient
            ? PhotonNetwork.CurrentRoom?.GetPlayer(otherActor)?.NickName ?? "?"
            : Gui.Localize("ModUi/&MultiplayerHostRole").Trim();

        ReportConditionIds(allDifferences.Where(x => x.Type == DifferenceType.ConditionIds).ToList(),
            otherActor, playerName, host.Tick);

        // the first check after a correction's tick tells whether it held
        if (PhotonNetwork.IsMasterClient && CorrectionsByActor.TryGetValue(otherActor, out var correction) &&
            host.Tick > correction.ApplyTick)
        {
            // Matching isn't verified until the player has said it applied. Its answer was sent before this fingerprint on
            // the same reliable channel, so a missing one isn't merely late: the timeout then reports it as unverified.
            if (differences.Count == 0 && !correction.Applied)
            {
                return;
            }

            CorrectionsByActor.Remove(otherActor);

            if (differences.Count == 0)
            {
                ReportedByActor.Remove(otherActor);
                CorrectionRetriesByActor.Remove(otherActor);
                Gui.GuiService.ShowAlert(
                    Gui.Format("Message/&MultiplayerDesyncCorrectionVerified", correction.PlayerName),
                    Gui.ColorAlmostWhite, 6f);
                SendStatus(correction, CorrectionStatus.Verified);
            }
            else
            {
                FailCorrection(correction, CorrectionStatus.Failed,
                    string.Join("\n", differences.Select(x => x.Description).Take(6)));
            }

            return;
        }

        // a difference must show up in two checks in a row, so a value changing between frames isn't reported
        var confirmed = differences.Where(x => previous != null && previous.Contains(x.Key)).ToList();

        if (confirmed.Count == 0)
        {
            if (differences.Count == 0)
            {
                ReportedByActor.Remove(otherActor);
                FailedActors.Remove(otherActor);
                CorrectionRetriesByActor.Remove(otherActor);
            }

            return;
        }

        var signature = string.Join("|", confirmed.Select(x => x.Key).OrderBy(x => x));

        // reported once: a correction on its way, or a desync already reported, waits for its outcome
        if (CorrectionsByActor.ContainsKey(otherActor) ||
            (!PhotonNetwork.IsMasterClient && ClientCorrectionPending()) ||
            (ReportedByActor.TryGetValue(otherActor, out var reported) && reported == signature))
        {
            return;
        }

        // A correction sends the host's state as it is when it's sent, not as it was at this check, so that state has to be
        // a settled one. Nothing is reported yet: the next quiet check still finds this difference and comes back here.
        var hostNow = PhotonNetwork.IsMasterClient ? Fingerprint.Capture(host.Tick) : null;

        if (hostNow is { Quiet: false } && CanQuickCorrect(host, confirmed) && !FailedActors.Contains(otherActor))
        {
            return;
        }

        ReportedByActor[otherActor] = signature;

        var details = string.Join("\n", confirmed.Select(x => x.Description).Take(6));

        Main.Error($"Multiplayer state desync with {playerName} at tick {host.Tick}:\n{details}");
        // TEMPORARY: each computer saves its recent object-ID records for comparison
        ObjectIdDiagnosticsContext.DesyncDetected("state-desync", (ulong)host.Tick);

        if (!PhotonNetwork.IsMasterClient)
        {
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncDetails", playerName, details),
                Gui.ColorFailure, 10f);

            return;
        }

        // hostNow, because a battle may have started since the check
        if (CanQuickCorrect(hostNow, confirmed) && !FailedActors.Contains(otherActor))
        {
            StartCorrection(otherActor, playerName, confirmed, details);

            return;
        }

        OfferResync(playerName, details);
    }

    private static List<Difference> Differences(Fingerprint host, Fingerprint other)
    {
        var differences = new List<Difference>();
        if (host.Perception != other.Perception)
        {
            differences.Add(new Difference("perception", DifferenceType.Perception,
                Gui.Localize("Message/&MultiplayerDesyncPerception")));
        }

        if (host.Dice != other.Dice)
        {
            differences.Add(new Difference("dice", DifferenceType.Dice,
                Gui.Localize("Message/&MultiplayerDesyncDice")));
        }

        foreach (var hostCharacter in host.Characters)
        {
            var name = CharacterName(hostCharacter.Guid);

            if (!other.TryGet(hostCharacter.Guid, out var otherCharacter))
            {
                differences.Add(new Difference($"{hostCharacter.Guid}:missing", DifferenceType.Other,
                    Gui.Format("Message/&MultiplayerDesyncMissing", name)));

                continue;
            }

            void Add(string field, DifferenceType type, string hostValue, string otherValue)
            {
                differences.Add(new Difference($"{hostCharacter.Guid}:{field}", type,
                    Gui.Format("Message/&MultiplayerDesyncField", name, Gui.Localize($"Message/&MultiplayerDesync{field}"),
                        hostValue, otherValue)));
            }

            if (hostCharacter.HitPoints != otherCharacter.HitPoints)
            {
                Add("HitPoints", DifferenceType.Correctable,
                    hostCharacter.HitPoints.ToString(), otherCharacter.HitPoints.ToString());
            }

            if (hostCharacter.TemporaryHitPoints != otherCharacter.TemporaryHitPoints)
            {
                Add("TemporaryHitPoints", DifferenceType.Correctable,
                    hostCharacter.TemporaryHitPoints.ToString(), otherCharacter.TemporaryHitPoints.ToString());
            }

            if (!hostCharacter.Position.Equals(otherCharacter.Position))
            {
                Add("Position", DifferenceType.Correctable,
                    hostCharacter.Position.ToString(), otherCharacter.Position.ToString());
            }

            if (hostCharacter.SpellSlots != otherCharacter.SpellSlots)
            {
                Add("SpellSlots", DifferenceType.Correctable, "-", "-");
            }

            if (hostCharacter.Conditions != otherCharacter.Conditions)
            {
                Add("Conditions", DifferenceType.Other, "-", "-");
            }
            // The same conditions under different object IDs or sources, which is what a numbering gap looks like. This
            // text only goes to the log (see Compare), so it has no translations.
            else if (hostCharacter.ConditionIds != otherCharacter.ConditionIds)
            {
                differences.Add(new Difference($"{hostCharacter.Guid}:ConditionIds", DifferenceType.ConditionIds,
                    name));
            }
        }

        foreach (var otherCharacter in other.Characters.Where(x => !host.TryGet(x.Guid, out _)))
        {
            differences.Add(new Difference($"{otherCharacter.Guid}:extra", DifferenceType.Other,
                Gui.Format("Message/&MultiplayerDesyncExtra", CharacterName(otherCharacter.Guid))));
        }

        return differences;
    }

    private static void ReportConditionIds(List<Difference> differences, int actor, string playerName, ulong tick)
    {
        if (differences.Count == 0)
        {
            ConditionIdsReported.Remove(actor);

            return;
        }

        if (!ConditionIdsReported.Add(actor))
        {
            return;
        }

        Main.Error($"Multiplayer condition IDs or sources differ with {playerName} at tick {tick}: " +
                   string.Join(", ", differences.Select(x => x.Description)));
        // TEMPORARY: each computer saves its recent object-ID records for comparison
        ObjectIdDiagnosticsContext.DesyncDetected("condition-ids", tick);
    }

    private static string CharacterName(ulong guid)
    {
        var character = ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters
            .FirstOrDefault(x => x.Guid == guid);

        return character?.Name ?? $"#{guid}";
    }

    #endregion

    #region Quick Correct

    private static bool CanQuickCorrect(Fingerprint host, List<Difference> differences)
    {
        return Main.Settings.MultiplayerQuickCorrectDesync &&
               !host.InBattle &&
               differences.All(x => x.Type is DifferenceType.Correctable or DifferenceType.Dice or DifferenceType.Perception);
    }

    // host only: a recovery save first, then the correction; without a confirmed save nothing is corrected
    private static void StartCorrection(
        int actor, string playerName, List<Difference> confirmed, string details)
    {
        var correction = new Correction(Guid.NewGuid().ToString("N"), actor, playerName);
        var save = MakeRecoverySave(out var reason);

        if (save == null)
        {
            FailedActors.Add(actor);
            Main.Error($"Multiplayer quick correct for {playerName} cancelled: no recovery save ({reason})");
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncCorrectionNoSave", playerName, reason),
                Gui.ColorFailure, 12f);
            SendStatus(correction, CorrectionStatus.Cancelled);

            return;
        }

        correction.SaveName = save;
        correction.ApplyTick = SendCorrection(confirmed, correction);
        correction.Deadline = Time.realtimeSinceStartup + CorrectionTimeout;
        CorrectionsByActor[actor] = correction;
        Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncCorrecting", playerName, details, save),
            Gui.ColorAlert, 10f);
    }

    // The host's values are applied at a lockstep tick on every computer (the host included), like a command. They're all
    // read here, in one moment: hit points and position used to come from the earlier check while dice and spell slots were
    // read now, so a correction could put together a state that never existed.
    private static ulong SendCorrection(List<Difference> differences, Correction correction)
    {
        var networking = (NetworkingManager)ServiceRepository.GetService<INetworkingService>();
        var host = Fingerprint.Capture(networking.tick.LocalTick);
        var guids = differences
            .Select(x => x.Key.Split(':')[0])
            .Where(x => ulong.TryParse(x, out _))
            .Select(ulong.Parse)
            .Distinct()
            .ToList();
        var characters = guids
            .Select(x => host.TryGet(x, out var character) ? character : null)
            .Where(x => x != null)
            .ToList();
        var slots = new List<int>();

        for (var i = 0; i < characters.Count; i++)
        {
            foreach (var entry in SpellSlotEntries(FindCharacter(characters[i].Guid)))
            {
                slots.AddRange([i, entry.repertoire, entry.level, entry.used]);
            }
        }

        var tick = networking.CommandTick;

        PerceptionContext.HoldRequests((ulong)tick);

        var data = new object[]
        {
            CorrectionKind, tick, CaptureRandomState(),
            characters.Select(x => (long)x.Guid).ToArray(),
            characters.SelectMany(x => new[]
            {
                x.HitPoints, x.TemporaryHitPoints, x.Position.x, x.Position.y, x.Position.z
            }).ToArray(),
            slots.ToArray(),
            // which correction this is, the player it is for, and the recovery save made before it
            correction.Id, new[] { correction.Actor }, correction.SaveName, PerceptionContext.Capture(),
            // the tick it was captured at: a computer whose perception results changed at or after it rejects it
            (long)networking.tick.LocalTick
        };

        PhotonNetwork.RaiseEvent(EventCode, data,
            new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        PhotonNetwork.SendAllOutgoingCommands();

        return (ulong)tick;
    }

    private static void OnCorrection(object[] data)
    {
        if (data.Length < 6 || ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking)
        {
            return;
        }

        if (IsForThisComputer(data, out var id))
        {
            _clientCorrection = id;
            _clientCorrectionUntil = Time.realtimeSinceStartup + CorrectionTimeout + 10f;
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncBeingCorrected", (string)data[8]),
                Gui.ColorAlert, 8f);
        }

        PerceptionContext.HoldRequests((ulong)(long)data[1]);

        // queued like a command, but it mustn't count as one that got in front of itself
        _queueingCorrection = true;

        try
        {
            networking.AddDelayedCommand(ApplyCorrection(data), (long)data[1]);
        }
        finally
        {
            _queueingCorrection = false;
        }
    }

    // Every lockstep command goes through NetworkingManager.AddDelayedCommand with the tick it runs at, so this sees the same
    // commands on every computer, and all of those for a tick have arrived by the time that tick runs.
    internal static void CommandQueued(NetworkingManager networking, long tick)
    {
        if (_queueingCorrection || !Global.IsMultiplayer)
        {
            return;
        }

        var localTick = (long)networking.tick.LocalTick;

        // the game drops these, so they never run
        if (localTick > CommandTickWindow && Math.Abs(tick - localTick) > CommandTickWindow)
        {
            return;
        }

        CommandTicks.RemoveAll(x => x + CommandTicksKept < localTick);
        CommandTicks.Add(tick);
    }

    // Commands after the applying tick are left out: they may not have arrived everywhere yet, so counting them could split
    // computers, and they run after the correction anyway.
    private static bool CommandBetween(long fromTick, long toTick)
    {
        return CommandTicks.Any(x => x >= fromTick && x <= toTick);
    }

    // Host only: every computer in step gave this correction up with nothing changed. The difference is still there, so the
    // next quiet check starts a new one, with a new capture and recovery save.
    private static void RetryCorrection(string id)
    {
        var correction = CorrectionsByActor.Values.FirstOrDefault(x => x.Id == id);

        if (correction == null)
        {
            return;
        }

        CorrectionsByActor.Remove(correction.Actor);
        CorrectionRetriesByActor.TryGetValue(correction.Actor, out var retries);

        if (retries >= MaxCorrectionRetries)
        {
            CorrectionRetriesByActor.Remove(correction.Actor);
            FailCorrection(correction, CorrectionStatus.Rejected, null);

            return;
        }

        CorrectionRetriesByActor[correction.Actor] = retries + 1;
        ReportedByActor.Remove(correction.Actor);
        SendStatus(correction, CorrectionStatus.Retrying);
    }

    // The corrected player always answers, so the host knows it applied. Anyone else only speaks up on failure: a computer
    // that didn't apply it now differs from the ones that did, and the host has to hear that from it.
    private static void ReportApplied(object[] data, bool applied)
    {
        if (data.Length < 8 || data[6] is not string id)
        {
            return;
        }

        if (PhotonNetwork.IsMasterClient)
        {
            if (!applied)
            {
                OnApplied(PhotonNetwork.LocalPlayer?.ActorNumber ?? 0, id, false);
            }

            return;
        }

        if (!applied || IsForThisComputer(data, out _))
        {
            PhotonNetwork.RaiseEvent(EventCode, new object[] { AppliedKind, id, applied },
                new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
        }
    }

    // slot records are (character index, repertoire index, slot level, used slots); indices must be in range
    private static bool ValidSlotRecords(int[] slots, int characterCount)
    {
        if (slots.Length % 4 != 0)
        {
            return false;
        }

        for (var i = 0; i < slots.Length; i += 4)
        {
            if (slots[i] < 0 || slots[i] >= characterCount || slots[i + 1] < 0 || slots[i + 2] < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsForThisComputer(object[] data, out string id)
    {
        id = data.Length >= 9 && data[6] is string correctionId && data[7] is int[] actors && data[8] is string &&
             !PhotonNetwork.IsMasterClient && actors.Contains(PhotonNetwork.LocalPlayer?.ActorNumber ?? 0)
            ? correctionId
            : null;

        return id != null;
    }

    private static IEnumerator ApplyCorrection(object[] data)
    {
        // The whole correction is validated before anything changes, and a rejection is reported to the host like any
        // other incomplete correction (the host then offers its resync). Every computer applies it, the host too, so a
        // perception check rolled after the host captured it would be rolled back everywhere while the conditions it
        // ended stayed ended, unseen by verification. Results change only at synchronized ticks, so every computer in
        // step finds the same last change tick and makes the same choice: one that changed at or after the capture tick
        // rejects it (a change in the capture tick itself may have come before the capture: rejecting is the safe side).
        if (data.Length < 11 ||
            data[2] is not int[] correctionRandom || correctionRandom.Length != RandomStateFields.Length ||
            data[3] is not long[] correctionGuids ||
            data[4] is not int[] correctionValues || correctionValues.Length != correctionGuids.Length * 5 ||
            data[5] is not int[] correctionSlots || !ValidSlotRecords(correctionSlots, correctionGuids.Length) ||
            data[9] is not long[] perception || !PerceptionContext.CanRestore(perception) ||
            data[10] is not long captureTick || PerceptionContext.LastChangeTick >= (ulong)captureTick)
        {
            Main.Error("Multiplayer correction rejected: invalid data, or a perception check changed before it applied");
            ReportApplied(data, false);

            yield break;
        }

        // A command queued for a tick from the capture up to now (a move, a spell, a rest) has run or is due, and the host's
        // captured values don't include it: applying them would undo it on every computer. Computers in step find the same
        // commands, so all give up together, and only the host needs to act. One that isn't in step shows up at the next
        // check, as it does when it applied alone. A capture-tick command may have come before the capture: rejecting is the
        // safe side, as above.
        if (CommandBetween(captureTick, (long)data[1]))
        {
            Main.Info("Multiplayer quick correct given up: a command came after its capture");

            if (PhotonNetwork.IsMasterClient && data.Length >= 7 && data[6] is string retryId)
            {
                RetryCorrection(retryId);
            }

            yield break;
        }

        var randomState = (int[])data[2];
        var guids = (long[])data[3];
        var values = (int[])data[4];
        var slots = (int[])data[5];
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        var applied = true;

        for (var i = 0; i < guids.Length; i++)
        {
            var character = FindCharacter((ulong)guids[i]);

            if (character?.RulesetCharacter == null)
            {
                applied = false;

                continue;
            }

            var offset = i * 5;

            character.RulesetCharacter.currentHitPoints = values[offset];
            character.RulesetCharacter.temporaryHitPoints = values[offset + 1];

            var position = new int3(values[offset + 2], values[offset + 3], values[offset + 4]);

            if (!character.LocationPosition.Equals(position))
            {
                positioning?.TeleportCharacter(character, position, character.Orientation);
            }

            character.RulesetCharacter.RefreshAll();
        }

        // The records are all the host has for these characters, so a slot level only this computer has must go too, or the
        // next check would still differ and the correction would fail on it.
        foreach (var guid in guids)
        {
            var characterRepertoires = FindCharacter((ulong)guid)?.RulesetCharacter?.SpellRepertoires;

            if (characterRepertoires == null)
            {
                continue;
            }

            foreach (var repertoire in characterRepertoires)
            {
                repertoire.usedSpellsSlots.Clear();
            }
        }

        for (var i = 0; i + 3 < slots.Length; i += 4)
        {
            var character = guids.Length > slots[i] ? FindCharacter((ulong)guids[slots[i]]) : null;
            var repertoires = character?.RulesetCharacter?.SpellRepertoires;

            if (repertoires == null || slots[i + 1] >= repertoires.Count)
            {
                applied = false;

                continue;
            }

            repertoires[slots[i + 1]].usedSpellsSlots[slots[i + 2]] = slots[i + 3];
        }

        RestoreRandomState(randomState);
        PerceptionContext.Restore(perception);
        Main.Info($"Multiplayer quick correct applied for {guids.Length} character(s), complete: {applied}");

        // whether every value found its character; applied isn't verified yet
        ReportApplied(data, applied);

        yield break;
    }

    // actor is who answered: the corrected player, or any computer reporting a failure (see ReportApplied)
    private static void OnApplied(int actor, string id, bool applied)
    {
        var correction = CorrectionsByActor.Values.FirstOrDefault(x => x.Id == id);

        if (correction == null)
        {
            return;
        }

        if (applied)
        {
            if (actor == correction.Actor)
            {
                correction.Applied = true;
            }

            return;
        }

        Main.Error($"Multiplayer quick correct {id} for {correction.PlayerName} not applied by player {actor}");
        CorrectionsByActor.Remove(correction.Actor);
        FailCorrection(correction, CorrectionStatus.Rejected, null);
    }

    private static void CheckCorrectionTimeouts()
    {
        if (CorrectionsByActor.Count == 0)
        {
            return;
        }

        foreach (var correction in CorrectionsByActor.Values
                     .Where(x => Time.realtimeSinceStartup >= x.Deadline).ToList())
        {
            CorrectionsByActor.Remove(correction.Actor);
            FailCorrection(correction, CorrectionStatus.TimedOut, null);
        }
    }

    // the host is told, the player too, and a host-led reload is offered; the desync stays reported, so the same one
    // isn't corrected again in a loop
    private static void FailCorrection(Correction correction, CorrectionStatus status, string details)
    {
        FailedActors.Add(correction.Actor);
        Main.Error($"Multiplayer quick correct {correction.Id} for {correction.PlayerName}: {status}" +
                   (correction.Applied ? " (applied)" : string.Empty));
        Gui.GuiService.ShowAlert(
            Gui.Format($"Message/&MultiplayerDesyncCorrection{status}", correction.PlayerName, correction.SaveName),
            Gui.ColorFailure, 12f);
        SendStatus(correction, status);
        OfferResync(correction.PlayerName, details ?? Gui.Localize($"Message/&MultiplayerDesyncCorrection{status}Short"));
    }

    private static void SendStatus(Correction correction, CorrectionStatus status)
    {
        PhotonNetwork.RaiseEvent(EventCode, new object[] { StatusKind, correction.Id, (byte)status },
            new RaiseEventOptions { TargetActors = [correction.Actor] }, SendOptions.SendReliable);
    }

    private static bool ClientCorrectionPending()
    {
        return _clientCorrection != null && Time.realtimeSinceStartup < _clientCorrectionUntil;
    }

    // on the corrected player's computer; a cancelled correction arrives without one being announced first
    private static void OnStatus(string id, CorrectionStatus status)
    {
        if (status == CorrectionStatus.Retrying)
        {
            if (id == _clientCorrection)
            {
                _clientCorrection = null;
            }

            return;
        }

        if (id == _lastStatus || (status != CorrectionStatus.Cancelled && id != _clientCorrection))
        {
            return;
        }

        _lastStatus = id;
        _clientCorrection = null;
        Gui.GuiService.ShowAlert(Gui.Localize($"Message/&MultiplayerDesyncClient{status}"),
            status == CorrectionStatus.Verified ? Gui.ColorAlmostWhite : Gui.ColorFailure, 10f);
    }

    // a save in one of the recovery slots, named only once the game confirms it was written; null with the reason
    // when it can't be made
    private static string MakeRecoverySave(out string reason)
    {
        reason = null;

        if (ServiceRepository.GetService<IGameSerializationService>() is not GameSerializationManager serialization)
        {
            reason = Gui.Localize("Message/&MultiplayerDesyncSaveUnavailable");

            return null;
        }

        if (ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign is { IronManMode: true })
        {
            reason = Gui.Localize("Message/&MultiplayerDesyncSaveIronMan");

            return null;
        }

        if (!serialization.CanSave)
        {
            reason = Gui.Localize("Message/&MultiplayerDesyncSaveBusy");

            return null;
        }

        var name = NextRecoverySlot();
        var path = Path.Combine(TacticalAdventuresApplication.SaveGameDirectory, name) + ".sav";
        var saved = false;

        // the game raises GameSaved at the end of SaveGame, after writing; it returns without it when the disk is full
        serialization.GameSaved += OnSaved;

        try
        {
            serialization.SaveGame(path, GameSerializationDefinitions.SaveType.Manual);
        }
        catch (Exception e)
        {
            // SaveGame clears its saving flag only at its end
            serialization.saving = false;
            Main.Error($"Multiplayer recovery save failed: {e.Message}");
        }
        finally
        {
            serialization.GameSaved -= OnSaved;
        }

        if (!saved || !File.Exists(path))
        {
            reason = Gui.Localize("Message/&MultiplayerDesyncSaveFailed");

            return null;
        }

        Main.Info($"Multiplayer recovery save written: {path}");
        _lastRecoverySave = name;

        return name;

        void OnSaved(GameSerializationDefinitions.SaveType saveType, string savedGameName)
        {
            saved = true;
        }
    }

    // an unused slot first, then the oldest
    private static string NextRecoverySlot()
    {
        return Enumerable.Range(1, RecoverySaveSlots)
            .Select(x => RecoverySavePrefix + x)
            .OrderBy(x =>
            {
                var path = Path.Combine(TacticalAdventuresApplication.SaveGameDirectory, x) + ".sav";

                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            })
            .First();
    }

    private static IEnumerable<(int repertoire, int level, int used)> SpellSlotEntries(GameLocationCharacter character)
    {
        var repertoires = character?.RulesetCharacter?.SpellRepertoires;

        if (repertoires == null)
        {
            yield break;
        }

        for (var r = 0; r < repertoires.Count; r++)
        {
            foreach (var slot in repertoires[r].usedSpellsSlots.OrderBy(x => x.Key))
            {
                yield return (r, slot.Key, slot.Value);
            }
        }
    }

    private sealed class Correction(string id, int actor, string playerName)
    {
        internal string Id { get; } = id;
        internal int Actor { get; } = actor;
        internal string PlayerName { get; } = playerName;
        internal string SaveName { get; set; }
        internal ulong ApplyTick { get; set; }
        internal float Deadline { get; set; }
        internal bool Applied { get; set; }
    }

    #endregion

    #region Resync

    private static void OfferResync(string playerName, string details)
    {
        if (!Main.Settings.MultiplayerOfferHostResync || _resyncOffered)
        {
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncDetails", playerName, details),
                Gui.ColorFailure, 10f);

            return;
        }

        _resyncOffered = true;

        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2,
            "Message/&MultiplayerDesyncResyncTitle",
            Gui.Format("Message/&MultiplayerDesyncResyncDescription", playerName, details),
            "Message/&MultiplayerDesyncResyncAccept", "Message/&MultiplayerDesyncResyncLater",
            StartResync, () => _resyncOffered = false);
    }

    private static void StartResync()
    {
        if (ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking ||
            ServiceRepository.GetService<IGameSerializationService>() is not GameSerializationManager serialization)
        {
            return;
        }

        PhotonNetwork.RaiseEvent(EventCode, new object[] { ResyncKind },
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
        networking.StartCoroutine(Resync(serialization));
    }

    // the host's new recovery save is the state everyone continues from: loading it as host brings every client in.
    // If it can't be written, nothing is loaded: an older save isn't loaded in its place
    private static IEnumerator Resync(GameSerializationManager serialization)
    {
        // lets the message box close first
        yield return null;

        var previous = _lastRecoverySave;
        var save = MakeRecoverySave(out var reason);

        if (save == null)
        {
            Main.Error($"Multiplayer resync cancelled: no save ({reason}), nothing was loaded");
            _resyncOffered = false;
            PhotonNetwork.RaiseEvent(EventCode, new object[] { ResyncCancelledKind },
                new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
            Gui.GuiService.ShowMessage(MessageModal.Severity.Serious3,
                "Message/&MultiplayerDesyncResyncTitle",
                previous == null
                    ? Gui.Format("Message/&MultiplayerDesyncResyncNoSave", reason)
                    : Gui.Format("Message/&MultiplayerDesyncResyncNoSaveRecovery", reason, previous),
                "Message/&MessageOkTitle", string.Empty, null, null);

            yield break;
        }

        Main.Info($"Multiplayer resync: loading {save}");
        Reset();
        serialization.LoadGame(Path.Combine(TacticalAdventuresApplication.SaveGameDirectory, save) + ".sav");
    }

    #endregion

    #region Fingerprint

    private static GameLocationCharacter FindCharacter(ulong guid)
    {
        return ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters
            .FirstOrDefault(x => x.Guid == guid);
    }

    private static int[] CaptureRandomState()
    {
        if (ServiceRepository.GetService<IGameService>()?.Game is not { } game)
        {
            return [];
        }

        object state = game.randomState;

        return RandomStateFields.Select(x => (int)x.GetValue(state)).ToArray();
    }

    private static void RestoreRandomState(int[] values)
    {
        if (values == null || values.Length != RandomStateFields.Length ||
            ServiceRepository.GetService<IGameService>()?.Game is not { } game)
        {
            return;
        }

        object state = game.randomState;

        for (var i = 0; i < values.Length; i++)
        {
            RandomStateFields[i].SetValue(state, values[i]);
        }

        game.randomState = (UnityEngine.Random.State)state;
    }

    private static long Hash(IEnumerable<int> values)
    {
        // FNV-1a, 64 bits
        var hash = unchecked((long)14695981039346656037UL);

        foreach (var value in values)
        {
            hash = unchecked((hash ^ value) * 1099511628211L);
        }

        return hash;
    }

    private static IEnumerable<int> TextValues(string text)
    {
        return (text ?? string.Empty).Select(x => (int)x);
    }

    private enum DifferenceType
    {
        Correctable,
        Dice,
        Perception,
        Other,
        ConditionIds
    }

    private sealed class Difference(string key, DifferenceType type, string description)
    {
        internal string Key { get; } = key;
        internal DifferenceType Type { get; } = type;
        internal string Description { get; } = description;
    }

    private sealed class CharacterPrint
    {
        internal long ConditionIds;
        internal long Conditions;
        internal ulong Guid;
        internal int HitPoints;
        internal int3 Position;
        internal long SpellSlots;
        internal int TemporaryHitPoints;
    }

    private sealed class Fingerprint
    {
        private readonly Dictionary<ulong, CharacterPrint> _byGuid = [];
        internal readonly List<CharacterPrint> Characters = [];
        internal long Dice;
        internal long Perception;
        internal bool InBattle;
        internal bool Quiet;
        internal ulong Tick;

        internal bool TryGet(ulong guid, out CharacterPrint character)
        {
            return _byGuid.TryGetValue(guid, out character);
        }

        private void Add(CharacterPrint character)
        {
            Characters.Add(character);
            _byGuid[character.Guid] = character;
        }

        internal static Fingerprint Capture(ulong tick)
        {
            var characters = ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters ?? [];
            var actions = ServiceRepository.GetService<IGameLocationActionService>();
            var fingerprint = new Fingerprint
            {
                Tick = tick,
                Dice = Hash(CaptureRandomState()),
                Perception = PerceptionContext.Digest(),
                InBattle = ServiceRepository.GetService<IGameLocationBattleService>() is { IsBattleInProgress: true },
                // nothing moving or acting, and no perception check requested or waiting for its command
                Quiet = characters.All(x => !x.MoveInProgress && actions?.IsCharacterActing(x) != true) &&
                        PerceptionContext.IsIdle
            };

            foreach (var character in characters.Where(x => x.RulesetCharacter != null).OrderBy(x => x.Guid))
            {
                var ruleset = character.RulesetCharacter;

                fingerprint.Add(new CharacterPrint
                {
                    Guid = character.Guid,
                    HitPoints = ruleset.CurrentHitPoints,
                    TemporaryHitPoints = ruleset.TemporaryHitPoints,
                    Position = character.LocationPosition,
                    Conditions = Hash(ruleset.ConditionsByCategory
                        .OrderBy(x => x.Key)
                        .SelectMany(x => x.Value)
                        .Select(x => $"{x.ConditionDefinition?.Name}:{x.RemainingRounds}")
                        .OrderBy(x => x)
                        .SelectMany(TextValues)),
                    // apart from Conditions, so it can be told from a real difference in what a character has
                    ConditionIds = Hash(ruleset.ConditionsByCategory
                        .SelectMany(x => x.Value)
                        .Select(x => $"{x.ConditionDefinition?.Name}:{x.Guid}:{x.SourceGuid}")
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .SelectMany(TextValues)),
                    SpellSlots = Hash(SpellSlotEntries(character)
                        .SelectMany(x => new[] { x.repertoire, x.level, x.used }))
                });
            }

            return fingerprint;
        }

        internal object[] ToEvent()
        {
            var values = new int[Characters.Count * ValuesPerCharacter];

            for (var i = 0; i < Characters.Count; i++)
            {
                var c = Characters[i];
                var o = i * ValuesPerCharacter;

                values[o] = c.HitPoints;
                values[o + 1] = c.TemporaryHitPoints;
                values[o + 2] = c.Position.x;
                values[o + 3] = c.Position.y;
                values[o + 4] = c.Position.z;
                values[o + 5] = (int)(c.Conditions >> 32);
                values[o + 6] = (int)c.Conditions;
                values[o + 7] = (int)(c.SpellSlots >> 32);
                values[o + 8] = (int)c.SpellSlots;
                values[o + 9] = (int)(c.ConditionIds >> 32);
                values[o + 10] = (int)c.ConditionIds;
            }

            return
            [
                FingerprintKind, (long)Tick, Dice, InBattle, Quiet,
                Characters.Select(x => (long)x.Guid).ToArray(), values, Perception
            ];
        }

        internal static Fingerprint FromEvent(object[] data)
        {
            if (data.Length < 8 || data[7] is not long || data[5] is not long[] guids || data[6] is not int[] values ||
                values.Length != guids.Length * ValuesPerCharacter)
            {
                return null;
            }

            var fingerprint = new Fingerprint
            {
                Tick = (ulong)(long)data[1], Dice = (long)data[2], Perception = (long)data[7], InBattle = (bool)data[3], Quiet = (bool)data[4]
            };

            for (var i = 0; i < guids.Length; i++)
            {
                var o = i * ValuesPerCharacter;

                fingerprint.Add(new CharacterPrint
                {
                    Guid = (ulong)guids[i],
                    HitPoints = values[o],
                    TemporaryHitPoints = values[o + 1],
                    Position = new int3(values[o + 2], values[o + 3], values[o + 4]),
                    Conditions = ((long)values[o + 5] << 32) | (uint)values[o + 6],
                    SpellSlots = ((long)values[o + 7] << 32) | (uint)values[o + 8],
                    ConditionIds = ((long)values[o + 9] << 32) | (uint)values[o + 10]
                });
            }

            return fingerprint;
        }
    }

    #endregion
}
