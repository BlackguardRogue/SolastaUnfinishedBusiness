using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Multiplayer battle start: the host alone decides where characters who shared a cell stand, and every computer waits
// before initiative until all of them stand there.
//
// Why: the separation (MultiplayerContext.SeparateFromSharedCell) picks a cell no enemy can see for a sneaking
// character, and asks the game's visibility service which cells enemies perceive. That service is each computer's own
// view, not lockstep state, so two computers could separate the same characters to different cells, and nothing told
// them apart; from then on the battle ran on different positions (Codex's trace for desync issue 04, 2026-11-09:
// verified that the separation ran on every computer from local grid, stealth and visibility and was never sent; its
// part in the WizardTower desync is plausible, not proven).
//
// How:
// 1. Every computer: when a battle starts (GameLocationBattleManager.TriggerBattleStart), a pending placement opens
//    with the contenders' IDs.
// 2. The host separates the characters, then runs the game's own unstack (the one BattleState_Initiative.Begin would
//    run) for anyone still stacked and moves them to its cells, so nothing is left stacked for each computer's
//    initiative to resolve from its own surroundings (Codex's re-review: the native unstack searches cells beyond the
//    contenders, which a signature of their own cells doesn't cover). It names the battle with a random ID and sends
//    the whole formation: the ID, the map, every contender's ID, cell and facing (moved or not), and a signature of the
//    grid cells every contender occupies with everyone registered in them (OccupancySignature).
// 3. A client never separates anyone itself. It takes a formation for its pending battle when the map and contenders
//    match, applies it through the game's positioning service, checks every contender's cell and facing and the
//    signature, and reports to the host, under the host's ID, whether all match. A formation that comes before this
//    computer's battle start is kept until then; once the host has released that battle it is never applied, so a
//    late formation can't move characters in a battle under way. One for other
//    contenders doesn't use up the placement: after MismatchGraceSeconds without the right one, the client reports the
//    mismatch, and the right one is still applied if it comes. Stale formations from before a map change can't arrive
//    afterwards: Photon delivers each sender's reliable messages in order, and the host's map change comes after them.
// 4. Every computer holds the battle's initiative state (BattleState_Initiative, which the intro state requests a
//    frame or more after the battle starts) before its Begin and before the roll, until the host releases it: when
//    every player in the room has reported, or after HostWaitSeconds. A client that hears nothing releases itself after
//    ClientWaitSeconds.
// 5. The release says whether every player matched. If not (a failed check, a mismatch, no answer), nobody falls back
//    to a local placement: the log says why, every player gets an alert that positions may differ, and the host gets
//    the Resync offer (MultiplayerDesyncContext), which reloads every computer from the host's game. A host change or
//    leaving the room ends the hold the same way, without the offer.
// 6. When each battle's first turn begins, every computer logs the occupancy signature again ("first turn"), so
//    players' logs can be compared after initiative.
// Single player is unchanged: the separation never ran there.
internal static class MultiplayerBattlePlacementContext
{
    private const byte EventCode = 175;
    private const byte FormationKind = 1, ReportKind = 2, ReleaseKind = 3;

    private const float HostWaitSeconds = 10f;
    private const float ClientWaitSeconds = 20f;
    private const float MismatchGraceSeconds = 2f;
    private const int RememberedBattles = 8;
    private const string SurprisedConditionName = "ConditionSurprised";

    private static bool _eventHandlerRegistered;

    private static Pending _pending;

    // client: formations that came before this computer started their battle, by the host's battle ID, with the time
    // they arrived; dropped when the host releases that battle or after ClientWaitSeconds
    private static readonly Dictionary<string, (object[] Data, float Received)> EarlyFormations = new();

    // client: the host's releases for battles this computer hadn't placed, so a late start doesn't wait for one again
    private static readonly Dictionary<string, bool> ReleasedBattles = new();
    private static readonly Queue<string> ReleasedOrder = new();

    // the initiative state's Begin is held while placing, then run once released
    private static BattleState_Initiative _heldInitiative;
    private static object[] _heldParameters;
    private static bool _runningHeldBegin;

    // the battle whose first turn was logged
    private static GameLocationBattle _firstTurnLogged;

    private static bool IsOnlineRoom =>
        Global.IsMultiplayer && PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    private static bool IsActive => IsOnlineRoom && Main.Settings.MultiplayerResolveOverlapsOnBattleStart;

    private static string LocationName => Gui.GameLocation?.LocationDefinition?.Name ?? string.Empty;

    private static void Log(string message)
    {
        Main.Info($"[BattlePlacement] {message}");
    }

    private static string Short(string id)
    {
        return string.IsNullOrEmpty(id) ? "?" : id.Substring(0, Math.Min(8, id.Length));
    }

    #region Lifecycle

    internal static void OnJoinedRoom()
    {
        Reset("joined a room");

        if (_eventHandlerRegistered || PhotonNetwork.NetworkingClient == null)
        {
            return;
        }

        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _eventHandlerRegistered = true;
    }

    internal static void OnLeftRoom()
    {
        Reset("left the room");
    }

    internal static void OnLocationUnloaded()
    {
        Reset("location unloaded");

        // the held state belongs to the map being unloaded
        _heldInitiative = null;
        _heldParameters = null;
        _firstTurnLogged = null;
    }

    internal static void OnMasterClientSwitched()
    {
        if (_pending is not { Released: false })
        {
            return;
        }

        Release(false, "the host changed before every player confirmed the formation");

        // the new host offers the Resync, as the old one would have for an unconfirmed placement
        if (PhotonNetwork.IsMasterClient)
        {
            MultiplayerDesyncContext.OfferResyncForBattlePlacement(string.Join(", ", PhotonNetwork.PlayerList
                .Where(x => x.ActorNumber != PhotonNetwork.LocalPlayer.ActorNumber)
                .Select(x => x.NickName)));
        }
    }

    private static void Reset(string why)
    {
        if (_pending is { Released: false })
        {
            Log($"battle {Short(_pending.HostBattleId)} placement dropped: {why}");
        }

        // a held initiative state still in play runs its Begin at its next Update, as nothing holds it now
        _pending = null;
        EarlyFormations.Clear();
        ReleasedBattles.Clear();
        ReleasedOrder.Clear();
    }

    #endregion

    #region Battle start

    // TriggerBattleStart prefix: opens the placement before the battle's states can run
    internal static void OnBattleStarting(bool battleInProgress)
    {
        if (battleInProgress || !IsActive)
        {
            return;
        }

        _pending = new Pending(Time.realtimeSinceStartup);
    }

    // TriggerBattleStart postfix: every contender has started; the host places, a client waits for the host
    internal static void OnBattleStarted()
    {
        if (_pending == null || _pending.Guids != null)
        {
            return;
        }

        var battle = ServiceRepository.GetService<IGameLocationBattleService>()?.Battle;

        if (battle == null || !IsActive)
        {
            // no battle came of it
            _pending = null;

            return;
        }

        _pending.Battle = battle;
        _pending.Guids = battle.AllContenders.Where(x => x != null).Select(x => (long)x.Guid).OrderBy(x => x)
            .ToArray();

        if (PhotonNetwork.IsMasterClient)
        {
            PlaceAsHost(battle);

            return;
        }

        // a formation that came first: the freshest one for this map and these contenders
        var now = Time.realtimeSinceStartup;

        foreach (var stale in EarlyFormations.Where(x => now - x.Value.Received > ClientWaitSeconds).ToList())
        {
            EarlyFormations.Remove(stale.Key);
        }

        var early = EarlyFormations.Values
            .Where(x => x.Data[2] is string location && location == LocationName &&
                        x.Data[3] is long[] guids && x.Data[7] is int count &&
                        guids.Take(count).OrderBy(g => g).SequenceEqual(_pending.Guids))
            .OrderByDescending(x => x.Received)
            .Select(x => x.Data)
            .FirstOrDefault();

        if (early != null)
        {
            EarlyFormations.Remove((string)early[1]);
            ApplyFormation(early);
        }
    }

    private static List<GameLocationCharacter> Contenders(GameLocationBattle battle)
    {
        return battle.AllContenders.Where(x => x != null).OrderBy(x => x.Guid).ToList();
    }

    private static void PlaceAsHost(GameLocationBattle battle)
    {
        // one contender after the other: each separation teleports at once, so the next sees that cell taken
        foreach (var contender in battle.AllContenders.Where(x => x != null).ToList())
        {
            MultiplayerContext.SeparateFromSharedCell(contender);
        }

        var extras = UnstackAsHost(battle);
        var contenders = Contenders(battle);
        // contenders first, then the non-contenders the unstack moved (by ID each)
        var placed = contenders.Concat(extras.OrderBy(x => x.Guid)).ToList();

        _pending.PlacedOthers = extras;
        var guids = placed.Select(x => (long)x.Guid).ToArray();
        var cells = placed
            .SelectMany(x => new[] { x.LocationPosition.x, x.LocationPosition.y, x.LocationPosition.z })
            .ToArray();
        var facings = placed.Select(x => (int)x.Orientation).ToArray();
        var signature = OccupancySignature(placed, battle, out var shared);

        if (shared.Count > 0)
        {
            // not even the game's unstack found cells for them here. Initiative's own unstack will try again on every
            // computer, searching cells outside the contenders' that the signature doesn't cover, so the boards may
            // part there: the placement counts as unconfirmed (alert and Resync offer), whatever the players report
            _pending.HostUnresolved = string.Join("; ", shared);
            Log($"still sharing cells after separation and unstack: {_pending.HostUnresolved}");
        }

        _pending.HostBattleId = Guid.NewGuid().ToString("N");
        _pending.Roster = PhotonNetwork.PlayerList.Select(x => x.ActorNumber).ToArray();
        _pending.Reported.Add(PhotonNetwork.LocalPlayer.ActorNumber);

        Log($"battle {Short(_pending.HostBattleId)}: sending the formation of {guids.Length} contenders " +
            $"(occupancy {signature:X16}) to {_pending.Roster.Length - 1} players");

        Send(new object[]
        {
            FormationKind, _pending.HostBattleId, LocationName, guids, cells, facings, signature, contenders.Count
        }, ReceiverGroup.Others);
        PumpHost();
    }

    // host: the game's own unstack, which BattleState_Initiative.Begin runs on every computer from that computer's
    // grid, run here once, its cells applied, so the formation sent leaves nothing for it to do anywhere
    // Its search also takes in other movable characters standing in the stacked cells, contenders or not, so it
    // returns the non-contenders it moved: they go into the formation too (Codex's review of e2402bae).
    private static List<GameLocationCharacter> UnstackAsHost(GameLocationBattle battle)
    {
        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();
        var movedOthers = new List<GameLocationCharacter>();

        if (positioningService == null)
        {
            return movedOthers;
        }

        var found = new Dictionary<GameLocationCharacter, int3>();

        positioningService.ComputeUnstackedPlacementPositions(
            battle.AllContenders.Where(x => x != null).ToList(), found, false);

        foreach (var pair in found.OrderBy(x => x.Key.Guid))
        {
            if (pair.Key.LocationPosition == pair.Value)
            {
                continue;
            }

            Log($"unstacking {pair.Key.Name} from {pair.Key.LocationPosition} to {pair.Value} (the game's own rule)");
            positioningService.TeleportCharacter(pair.Key, pair.Value, pair.Key.Orientation);

            if (!battle.AllContenders.Contains(pair.Key))
            {
                movedOthers.Add(pair.Key);
            }
        }

        return movedOthers;
    }

    // client: the host's formation for the pending battle, kept for a battle this computer hasn't started yet, or
    // ignored
    private static void ApplyFormation(object[] data)
    {
        if (data.Length != 8 || data[1] is not string id || data[2] is not string location ||
            data[3] is not long[] guids || data[4] is not int[] cells || data[5] is not int[] facings ||
            data[6] is not long signature || data[7] is not int contenderCount ||
            cells.Length != guids.Length * 3 || facings.Length != guids.Length ||
            contenderCount < 0 || contenderCount > guids.Length)
        {
            Log("ignored a malformed formation");

            return;
        }

        if (location != LocationName)
        {
            Log($"ignored the formation of battle {Short(id)}: it's for {location}, not {LocationName}");

            return;
        }

        if (_pending?.HostBattleId == id || ReleasedBattles.ContainsKey(id))
        {
            // a duplicate, or a battle the host already released
            return;
        }

        if (_pending is not { Guids: not null, Released: false, Applied: false })
        {
            // no battle here waiting for one: it may be for a battle this computer hasn't started yet
            EarlyFormations[id] = (data, Time.realtimeSinceStartup);

            return;
        }

        if (!guids.Take(contenderCount).OrderBy(x => x).SequenceEqual(_pending.Guids))
        {
            // other contenders: boards that already differ, or a battle that starts later here. It doesn't use up
            // this battle's placement; Pump reports the mismatch if nothing better comes
            if (_pending.MismatchId == null)
            {
                _pending.MismatchId = id;
                _pending.MismatchSeen = Time.realtimeSinceStartup;
                Log($"battle {Short(id)}: the host's contenders " +
                    $"({string.Join(",", guids.Take(contenderCount).OrderBy(x => x))}) " +
                    $"aren't this battle's ({string.Join(",", _pending.Guids)})");
            }

            EarlyFormations[id] = (data, Time.realtimeSinceStartup);

            return;
        }

        _pending.Applied = true;
        _pending.HostBattleId = id;

        var ok = Apply(guids, cells, facings, signature, out var problem);

        Log(ok
            ? $"battle {Short(id)}: formation applied and checked (occupancy {signature:X16})"
            : $"battle {Short(id)}: formation not applied as sent: {problem}");

        Send(new object[] { ReportKind, id, ok }, ReceiverGroup.MasterClient);
    }

    private static bool Apply(long[] guids, int[] cells, int[] facings, long signature, out string problem)
    {
        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioningService == null)
        {
            problem = "no positioning service";

            return false;
        }

        // contenders, and any other character the host's unstack moved
        var byGuid = _pending.Battle.AllContenders.Where(x => x != null).ToDictionary(x => (long)x.Guid);

        foreach (var guid in guids.Where(x => !byGuid.ContainsKey(x)))
        {
            var other = (ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters ?? [])
                .FirstOrDefault(x => (long)x.Guid == guid);

            if (other == null)
            {
                problem = $"no character {guid} here, which the host moved";

                return false;
            }

            byGuid[guid] = other;
        }

        var placed = guids.Select(x => byGuid[x]).ToList();

        _pending.PlacedOthers = placed.Where(x => !_pending.Battle.AllContenders.Contains(x)).ToList();

        for (var i = 0; i < guids.Length; i++)
        {
            var character = byGuid[guids[i]];
            var cell = new int3(cells[i * 3], cells[(i * 3) + 1], cells[(i * 3) + 2]);
            var facing = (LocationDefinitions.Orientation)facings[i];

            if (character.LocationPosition != cell || character.Orientation != facing)
            {
                Log($"moving {character.Name} from {character.LocationPosition} to {cell} as the host placed them");
                positioningService.TeleportCharacter(character, cell, facing);
            }
        }

        // checked after every move, as a later teleport could have displaced an earlier one
        var wrong = new List<string>();

        for (var i = 0; i < guids.Length; i++)
        {
            var character = byGuid[guids[i]];
            var cell = new int3(cells[i * 3], cells[(i * 3) + 1], cells[(i * 3) + 2]);

            if (character.LocationPosition != cell || (int)character.Orientation != facings[i])
            {
                wrong.Add($"{character.Name} at {character.LocationPosition} facing {character.Orientation}, " +
                          $"host {cell} facing {(LocationDefinitions.Orientation)facings[i]}");
            }
        }

        // the whole footprints and who else stands in them, which anchors and facings alone don't show
        var local = OccupancySignature(placed, _pending.Battle, out var shared);

        if (local != signature)
        {
            wrong.Add($"occupancy {local:X16}, host {signature:X16}" +
                      (shared.Count > 0 ? $" (sharing here: {string.Join("; ", shared)})" : string.Empty));
        }

        problem = string.Join("; ", wrong);

        return wrong.Count == 0;
    }

    // FNV-1a over every placed character (by ID): its cell, facing, and each grid cell of its footprint with the IDs of
    // all the characters registered there; then the enemies the battle start found surprised. That was rolled inside
    // TriggerBattleStart from each computer's positions before any placement, so it's compared here too (Codex's
    // review of e2402bae). Equal on two computers when characters stand alike, the grid holds the same occupants in
    // their cells and the same enemies are surprised. Cells holding more than one living character are listed in shared.
    private static long OccupancySignature(
        List<GameLocationCharacter> contenders, GameLocationBattle battle, out List<string> shared)
    {
        shared = [];

        var hash = 14695981039346656037UL;

        void Mix(long value)
        {
            for (var i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= 1099511628211UL;
            }
        }

        var reported = new HashSet<int3>();

        foreach (var contender in contenders)
        {
            Mix((long)contender.Guid);

            // the Surprised condition itself, which the battle start gives party members and enemies alike
            Mix(contender.RulesetCharacter?.HasConditionOfType(SurprisedConditionName) == true ? 1 : 0);
            Mix(contender.LocationPosition.x);
            Mix(contender.LocationPosition.y);
            Mix(contender.LocationPosition.z);
            Mix((int)contender.Orientation);

            foreach (var cell in contender.LocationPathfindBoundingBox.EnumerateAllPositionsWithin())
            {
                var occupants = GridAccessor.Default.Occupants_TryGet(cell, out var list) && list != null
                    ? list.Where(x => x != null).OrderBy(x => x.Guid).ToList()
                    : [];

                Mix(cell.x);
                Mix(cell.y);
                Mix(cell.z);
                Mix(occupants.Count);

                foreach (var occupant in occupants)
                {
                    Mix((long)occupant.Guid);
                }

                var living = occupants.Where(x => x.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false })
                    .ToList();

                if (living.Count > 1 && reported.Add(cell))
                {
                    shared.Add($"{cell}: {string.Join(", ", living.Select(x => x.Name))}");
                }
            }
        }

        // the order of the contender lists, which initiative rolls in before it sorts: the same characters listed in
        // another order would get other dice (Codex's review of 66bfef30). Only the ones it rolls for: RollInitiative
        // skips the dead (RulesetActor.IsDead) on both sides
        foreach (var list in new[] { battle?.PlayerContenders, battle?.EnemyContenders })
        {
            var order = list?.Where(x => x?.RulesetCharacter is { IsDead: false }).Select(x => (long)x.Guid).ToList() ??
                        [];

            Mix(order.Count);

            foreach (var guid in order)
            {
                Mix(guid);
            }
        }

        var surprised = battle?.surprisedEnemyContenders?.Where(x => x != null).Select(x => (long)x.Guid)
            .OrderBy(x => x).ToList() ?? [];

        Mix(surprised.Count);

        foreach (var guid in surprised)
        {
            Mix(guid);
        }

        return (long)hash;
    }

    #endregion

    #region Messages and release

    private static void Send(object[] data, ReceiverGroup receivers)
    {
        PhotonNetwork.RaiseEvent(EventCode, data, new RaiseEventOptions { Receivers = receivers },
            SendOptions.SendReliable);
        PhotonNetwork.SendAllOutgoingCommands();
    }

    private static void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != EventCode || photonEvent.CustomData is not object[] { Length: >= 2 } data ||
            data[0] is not byte kind || data[1] is not string id || !IsOnlineRoom)
        {
            return;
        }

        MultiplayerContext.Guarded("a battle placement message", () =>
        {
            var host = PhotonNetwork.MasterClient?.ActorNumber ?? 0;

            switch (kind)
            {
                case FormationKind when photonEvent.Sender == host && !PhotonNetwork.IsMasterClient:
                    ApplyFormation(data);
                    break;

                case ReportKind when PhotonNetwork.IsMasterClient && data.Length == 3 && data[2] is bool ok &&
                                     _pending is { Released: false, Roster: not null } &&
                                     _pending.HostBattleId == id:
                    _pending.Reported.Add(photonEvent.Sender);

                    // a later report replaces an earlier one (a mismatch, then the right formation applied)
                    if (ok)
                    {
                        _pending.Failed.Remove(photonEvent.Sender);
                    }
                    else
                    {
                        _pending.Failed.Add(photonEvent.Sender);
                        Log($"battle {Short(id)}: {PlayerName(photonEvent.Sender)} couldn't apply the formation");
                    }

                    PumpHost();
                    break;

                case ReleaseKind when photonEvent.Sender == host && data.Length == 3 && data[2] is bool matched:
                    OnRelease(id, matched);
                    break;
            }
        });
    }

    // client: the host released a battle
    private static void OnRelease(string id, bool matched)
    {
        EarlyFormations.Remove(id);

        if (_pending is { Released: false, Applied: true } && _pending.HostBattleId == id)
        {
            Release(matched, matched ? null : "the host found that not every player has its formation");

            return;
        }

        if (_pending is { Released: false, Applied: false } && _pending.MismatchId == id)
        {
            Release(false, "the host's formation was for other contenders");

            return;
        }

        // not placed here: its formation, if it comes again or was kept, is never applied. A battle that starts here
        // only after the host gave up waiting (HostWaitSeconds) can't tell its own released formation from an older
        // battle's, so it gets none and releases itself after ClientWaitSeconds with the alert; the host has already
        // offered the Resync for it
        ReleasedBattles[id] = matched;
        ReleasedOrder.Enqueue(id);

        while (ReleasedOrder.Count > RememberedBattles)
        {
            ReleasedBattles.Remove(ReleasedOrder.Dequeue());
        }
    }

    private static string PlayerName(int actor)
    {
        return PhotonNetwork.CurrentRoom?.GetPlayer(actor)?.NickName ?? $"player {actor}";
    }

    // host: release once every player in the room when the battle started has reported, or at the deadline
    private static void PumpHost()
    {
        if (_pending is not { Released: false, Roster: not null } || !PhotonNetwork.IsMasterClient)
        {
            return;
        }

        // players who left can't report; anyone who joined since isn't in this battle's start
        var present = new HashSet<int>(PhotonNetwork.PlayerList.Select(x => x.ActorNumber));
        var missing = _pending.Roster.Where(x => present.Contains(x) && !_pending.Reported.Contains(x)).ToList();
        var timedOut = Time.realtimeSinceStartup - _pending.Started >= HostWaitSeconds;

        if (missing.Count > 0 && !timedOut)
        {
            return;
        }

        var differing = missing.Concat(_pending.Failed.Where(present.Contains)).Distinct().ToList();
        var others = _pending.Roster.Count(x => present.Contains(x) && x != PhotonNetwork.LocalPlayer.ActorNumber);
        var matched = differing.Count == 0 && (_pending.HostUnresolved == null || others == 0);

        Send(new object[] { ReleaseKind, _pending.HostBattleId, matched }, ReceiverGroup.Others);

        var problems = new List<string>();

        if (missing.Count > 0)
        {
            problems.Add($"no answer from {string.Join(", ", missing.Select(PlayerName))}");
        }

        if (_pending.Failed.Count > 0)
        {
            problems.Add($"not applied by {string.Join(", ", _pending.Failed.Select(PlayerName))}");
        }

        if (_pending.HostUnresolved != null)
        {
            problems.Add($"characters still sharing cells, left to each computer's initiative: {_pending.HostUnresolved}");
        }

        Release(matched, matched ? null : string.Join("; ", problems));

        if (!matched)
        {
            // with only the host's unresolved cells, every other player may differ
            var named = differing.Count > 0
                ? differing
                : _pending.Roster.Where(x => present.Contains(x) && x != PhotonNetwork.LocalPlayer.ActorNumber)
                    .ToList();

            MultiplayerDesyncContext.OfferResyncForBattlePlacement(string.Join(", ", named.Select(PlayerName)));
        }
    }

    private static void Release(bool matched, string problem)
    {
        if (_pending == null || _pending.Released)
        {
            return;
        }

        _pending.Released = true;

        if (matched)
        {
            Log($"battle {Short(_pending.HostBattleId)}: every player has the formation; starting initiative");

            return;
        }

        Log($"battle {Short(_pending.HostBattleId)}: starting initiative with positions that may differ: {problem}");
        Gui.GuiService?.ShowAlert(
            Gui.Localize("Message/&MultiplayerBattlePlacementUnconfirmed"), Gui.ColorFailure, 8f);
    }

    // called every frame while a battle start is held, from the held initiative state
    private static void Pump()
    {
        if (_pending is not { Released: false })
        {
            return;
        }

        if (!IsOnlineRoom)
        {
            // no other computers any more, so nothing can differ
            Release(true, null);

            return;
        }

        if (PhotonNetwork.IsMasterClient)
        {
            if (_pending.Roster == null)
            {
                // the battle started here before this computer became host, so it has no formation to wait for
                Release(false, "this computer became host during the battle start");

                return;
            }

            PumpHost();

            return;
        }

        // a formation for other contenders and nothing better since: tell the host, without giving up on the right
        // one (a report for it later replaces this one)
        if (!_pending.Applied && !_pending.ReportedMismatch && _pending.MismatchId != null &&
            Time.realtimeSinceStartup - _pending.MismatchSeen >= MismatchGraceSeconds)
        {
            _pending.ReportedMismatch = true;
            Log($"battle {Short(_pending.MismatchId)}: no formation for this battle's contenders came; " +
                "reporting the mismatch");
            Send(new object[] { ReportKind, _pending.MismatchId, false }, ReceiverGroup.MasterClient);
        }

        if (Time.realtimeSinceStartup - _pending.Started >= ClientWaitSeconds)
        {
            Release(false, _pending.Applied
                ? "the host's release didn't arrive"
                : "the host's formation didn't arrive; positions left as they are");
        }
    }

    #endregion

    #region Initiative hold

    private static bool Holds => _pending is { Released: false };

    // a battle start held for the host's formation: positions may differ between computers until it's released, so
    // the desync check doesn't compare them meanwhile (Codex's review of 66bfef30)
    internal static bool IsPlacing => Holds;

    // BattleState_Initiative.Begin prefix: false skips Begin for now; it runs from Update once released
    internal static bool BeforeInitiativeBegin(BattleState_Initiative state, object[] parameters)
    {
        if (_runningHeldBegin || !Holds)
        {
            return true;
        }

        _heldInitiative = state;
        _heldParameters = parameters;
        Log("initiative held until every player has the host's formation");

        return false;
    }

    // BattleState_Initiative.Update prefix: false skips the frame while held; the held Begin runs first once released
    internal static bool BeforeInitiativeUpdate(BattleState_Initiative state)
    {
        Pump();

        if (Holds)
        {
            return false;
        }

        if (_heldInitiative != state)
        {
            return true;
        }

        var parameters = _heldParameters;

        _heldInitiative = null;
        _heldParameters = null;
        _runningHeldBegin = true;

        try
        {
            state.Begin(parameters);
        }
        finally
        {
            _runningHeldBegin = false;
        }

        return true;
    }

    // BattleState_TurnInitialize.Begin postfix: the board every computer starts the first turn from, for comparing logs
    internal static void OnTurnInitialize()
    {
        if (!IsActive || ServiceRepository.GetService<IGameLocationBattleService>()?.Battle is not { } battle ||
            battle == _firstTurnLogged)
        {
            return;
        }

        _firstTurnLogged = battle;

        // the non-contenders placed with this battle too, so their final cells are compared as well
        var others = _pending?.Battle == battle ? _pending.PlacedOthers ?? [] : [];
        var signature = OccupancySignature(
            Contenders(battle).Concat(others.Where(x => x != null).OrderBy(x => x.Guid)).ToList(), battle,
            out var shared);
        var id = _pending?.Battle == battle ? Short(_pending.HostBattleId) : "?";

        Log($"battle {id} first turn: occupancy {signature:X16}" +
            (shared.Count > 0 ? $", sharing: {string.Join("; ", shared)}" : string.Empty));
    }

    #endregion

    private sealed class Pending(float started)
    {
        internal readonly HashSet<int> Failed = [];
        internal readonly HashSet<int> Reported = [];
        internal readonly float Started = started;
        internal bool Applied;
        internal GameLocationBattle Battle;
        internal long[] Guids;

        // the host's ID for this battle: the host's own, or (client) the one of the formation applied
        internal string HostBattleId;

        // host: cells still shared after its separation and unstack, which make the placement unconfirmed
        internal string HostUnresolved;

        // non-contenders the host's unstack moved, which the formation placed too
        internal List<GameLocationCharacter> PlacedOthers;

        // client: the first formation for other contenders, and whether its mismatch was reported
        internal string MismatchId;
        internal float MismatchSeen;
        internal bool ReportedMismatch;

        internal bool Released;
        internal int[] Roster;
    }

    [HarmonyPatch(typeof(GameLocationBattleManager), nameof(GameLocationBattleManager.TriggerBattleStart))]
    private static class TriggerBattleStartPatch
    {
        private static void Prefix(GameLocationBattleManager __instance)
        {
            OnBattleStarting(__instance.IsBattleInProgress);
        }

        private static void Postfix()
        {
            OnBattleStarted();
        }
    }

    // a map change ends any battle start in progress
    [HarmonyPatch(typeof(GameLocationManager), nameof(GameLocationManager.UnloadLocationAsync))]
    private static class UnloadLocationPatch
    {
        private static void Prefix()
        {
            MultiplayerContext.Guarded("unloading a location", OnLocationUnloaded);
        }
    }

    [HarmonyPatch(typeof(BattleState_Initiative), nameof(BattleState_Initiative.Begin))]
    private static class InitiativeBeginPatch
    {
        private static bool Prefix(BattleState_Initiative __instance, object[] parameters)
        {
            return BeforeInitiativeBegin(__instance, parameters);
        }
    }

    [HarmonyPatch(typeof(BattleState_Initiative), nameof(BattleState_Initiative.Update))]
    private static class InitiativeUpdatePatch
    {
        private static bool Prefix(BattleState_Initiative __instance)
        {
            return BeforeInitiativeUpdate(__instance);
        }
    }

    [HarmonyPatch(typeof(BattleState_TurnInitialize), nameof(BattleState_TurnInitialize.Begin))]
    private static class TurnInitializePatch
    {
        private static void Postfix()
        {
            MultiplayerContext.Guarded("logging the first turn's board", OnTurnInitialize);
        }
    }
}
