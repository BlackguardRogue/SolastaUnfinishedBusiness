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

// Multiplayer battle start: the host alone decides where characters who shared a cell stand and sends that once; the
// other computers wait for it before initiative, apply it, check it, and report a desync if it doesn't match.
//
// Why: the separation (MultiplayerContext.SeparateFromSharedCell) picks a cell no enemy can see for a sneaking
// character, and asks the game's visibility service which cells enemies perceive. That service is each computer's own
// view, not lockstep state, so two computers could separate the same characters to different cells, and nothing told
// them apart; from then on the battle ran on different positions (Codex's trace for desync issue 04, 2026-11-09:
// verified that the separation ran on every computer from local grid, stealth and visibility and was never sent; its
// part in the WizardTower desync is plausible, not proven).
//
// How (simplified on the user's direction, 2026-11-09: "simply send out a desync notification if the battle formation
// is detected to be incorrect after the first broadcast"; with a short wait kept, the user's choice):
// 1. Host: when a battle starts (GameLocationBattleManager.TriggerBattleStart), it separates the characters, runs the
//    game's own unstack (the one BattleState_Initiative.Begin would run on each computer from its own grid) and
//    applies its cells, so nothing is left stacked for each computer's initiative to resolve from its surroundings.
//    It then sends the formation once: a random battle ID, the map, every placed character's ID, cell and facing
//    (contenders, moved or not, then any non-contender the unstack moved), and a signature of the cells they occupy,
//    who is registered there, who is surprised and the contender order initiative rolls in (OccupancySignature).
//    The host doesn't wait for anyone.
// 2. Every other computer holds its initiative state (before its Begin and before the roll) from its battle start
//    until the formation has arrived and been applied, or ClientWaitSeconds have passed. A formation that arrives
//    before its own battle start is kept until then. Only one battle runs at a time (user, 2026-11-09), so one
//    pending battle and one early formation are all there is to keep.
// 3. It applies the formation through the game's positioning service and checks every character's cell and facing,
//    the contenders and the signature. If anything differs, the player gets an alert and the host is told once, which
//    gives it the usual Resync offer (MultiplayerDesyncContext). Nothing is retried, answered or confirmed otherwise.
// 4. If no formation comes in time (or the host changes, or the room or map goes), initiative goes on as it stands, the
//    log says so, and the regular desync check reports any difference. A formation for a battle already under way is
//    never applied, so it can't move characters mid-battle.
// 5. When each battle's first turn begins, every computer logs the signature again ("first turn") for comparing logs.
// Single player is unchanged: the separation never ran there. The sync is its own host-shared setting (Multiplayer >
// Movement, on by default): off, none of this runs and each computer separates characters itself as before.
internal static class MultiplayerBattlePlacementContext
{
    private const byte EventCode = 175;
    private const byte FormationKind = 1, MismatchKind = 4;

    private const float ClientWaitSeconds = 10f;
    private const string SurprisedConditionName = "ConditionSurprised";

    private static bool _eventHandlerRegistered;

    private static Pending _pending;

    // client: the formation that came before this computer started the battle, and when it arrived; only one battle
    // runs at a time, so there is only ever one, and a newer one replaces it. Used only within ClientWaitSeconds
    private static object[] _earlyFormation;
    private static float _earlyFormationReceived;

    // the initiative state's Begin is held while waiting, then run once released
    private static BattleState_Initiative _heldInitiative;
    private static object[] _heldParameters;
    private static bool _runningHeldBegin;

    // the battle whose first turn was logged
    private static GameLocationBattle _firstTurnLogged;

    private static bool IsOnlineRoom =>
        Global.IsMultiplayer && PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    // Multiplayer > "Synchronize battle starting positions with the host" (host-shared, on by default). Off, nothing
    // here runs and nothing waits; each computer separates characters itself at their battle start if "move characters
    // sharing a cell" is on (OnContenderStartedBattle), as before the sync existed.
    private static bool IsActive => IsOnlineRoom && Main.Settings.MultiplayerSyncBattleStartPositions;

    private static string LocationName => Gui.GameLocation?.LocationDefinition?.Name ?? string.Empty;

    private static void Log(string message)
    {
        Main.Info($"[BattlePlacement] {message}");
    }

    private static string Short(string id)
    {
        return string.IsNullOrEmpty(id) ? "?" : id.Substring(0, Math.Min(8, id.Length));
    }

    // GameLocationCharacter.StartBattle postfix: the separation without the sync, on each computer
    internal static void OnContenderStartedBattle(GameLocationCharacter character)
    {
        if (IsOnlineRoom && !Main.Settings.MultiplayerSyncBattleStartPositions &&
            Main.Settings.MultiplayerResolveOverlapsOnBattleStart)
        {
            MultiplayerContext.SeparateFromSharedCell(character);
        }
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
        Release("the host changed before its formation arrived");
    }

    private static void Reset(string why)
    {
        Release(why);

        // a held initiative state still in play runs its Begin at its next Update, as nothing holds it now
        _pending = null;
        _earlyFormation = null;
    }

    #endregion

    #region Battle start

    // TriggerBattleStart prefix: a client opens its wait before the battle's states can run
    internal static void OnBattleStarting(bool battleInProgress)
    {
        if (battleInProgress || !IsActive)
        {
            return;
        }

        _pending = new Pending(Time.realtimeSinceStartup, PhotonNetwork.IsMasterClient);
    }

    // TriggerBattleStart postfix: every contender has started; the host places and sends, a client looks for a
    // formation that came first
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

        if (_pending.Released)
        {
            PlaceAsHost(battle);

            return;
        }

        var early = _earlyFormation;

        _earlyFormation = null;

        if (early != null && Time.realtimeSinceStartup - _earlyFormationReceived <= ClientWaitSeconds)
        {
            ApplyFormation(early);
        }
    }

    private static List<GameLocationCharacter> Contenders(GameLocationBattle battle)
    {
        return battle.AllContenders.Where(x => x != null).OrderBy(x => x.Guid).ToList();
    }

    private static void PlaceAsHost(GameLocationBattle battle)
    {
        // one contender after the other: each separation teleports at once, so the next sees that cell taken. With
        // "move characters sharing a cell" off, only the game's own unstack below runs, here on the host
        if (Main.Settings.MultiplayerResolveOverlapsOnBattleStart)
        {
            foreach (var contender in battle.AllContenders.Where(x => x != null).ToList())
            {
                MultiplayerContext.SeparateFromSharedCell(contender);
            }
        }

        var extras = UnstackAsHost(battle);
        var contenders = Contenders(battle);

        // contenders first, then the non-contenders the unstack moved (by ID each)
        var placed = contenders.Concat(extras.OrderBy(x => x.Guid)).ToList();
        var guids = placed.Select(x => (long)x.Guid).ToArray();
        var cells = placed
            .SelectMany(x => new[] { x.LocationPosition.x, x.LocationPosition.y, x.LocationPosition.z })
            .ToArray();
        var facings = placed.Select(x => (int)x.Orientation).ToArray();
        var signature = OccupancySignature(placed, battle, out var shared);

        _pending.PlacedOthers = extras;
        _pending.HostBattleId = Guid.NewGuid().ToString("N");

        if (shared.Count > 0)
        {
            // not even the game's unstack found cells for them; initiative's own unstack tries again on every
            // computer, and the regular desync check reports it if they end up apart
            Log($"still sharing cells after separation and unstack: {string.Join("; ", shared)}");
        }

        Log($"battle {Short(_pending.HostBattleId)}: sending the formation of {guids.Length} characters " +
            $"(occupancy {signature:X16})");

        Send(new object[]
        {
            FormationKind, _pending.HostBattleId, LocationName, guids, cells, facings, signature, contenders.Count
        }, ReceiverGroup.Others);
    }

    // host: the game's own unstack, which BattleState_Initiative.Begin runs on every computer from that computer's
    // grid, run here once, its cells applied, so the formation sent leaves nothing for it to do anywhere. Its search
    // also takes in other movable characters standing in the stacked cells, contenders or not, so it returns the
    // non-contenders it moved: they go into the formation too (Codex's review of e2402bae).
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

    // client: the host's formation for the battle this computer is waiting on, kept for one it hasn't started yet, or
    // ignored (a battle already under way here)
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

        if (_pending is not { Guids: not null })
        {
            // this computer hasn't started the battle yet
            _earlyFormation = data;
            _earlyFormationReceived = Time.realtimeSinceStartup;

            return;
        }

        if (_pending.Released || _pending.HostBattleId != null)
        {
            // the battle here is already under way (it stopped waiting) or placed: moving characters now would only
            // make things worse; the regular desync check covers it
            Log($"ignored the formation of battle {Short(id)}: the battle here is already under way or placed");

            return;
        }

        _pending.HostBattleId = id;

        string problem;

        if (!guids.Take(contenderCount).OrderBy(x => x).SequenceEqual(_pending.Guids))
        {
            problem = $"the host's contenders ({string.Join(",", guids.Take(contenderCount).OrderBy(x => x))}) " +
                      $"aren't this battle's ({string.Join(",", _pending.Guids)})";
        }
        else
        {
            Apply(guids, cells, facings, signature, out problem);
        }

        if (string.IsNullOrEmpty(problem))
        {
            Log($"battle {Short(id)}: formation applied and checked (occupancy {signature:X16})");
        }
        else
        {
            // detected incorrect: one notice, here and to the host, and nothing more
            Log($"battle {Short(id)}: formation doesn't match: {problem}");
            Gui.GuiService?.ShowAlert(
                Gui.Localize("Message/&MultiplayerBattlePlacementUnconfirmed"), Gui.ColorFailure, 8f);
            Send(new object[] { MismatchKind, id }, ReceiverGroup.MasterClient);
        }

        Release(null);
    }

    // moves every character to the host's cell and facing, then checks them; problem is empty when all match
    private static void Apply(long[] guids, int[] cells, int[] facings, long signature, out string problem)
    {
        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioningService == null)
        {
            problem = "no positioning service";

            return;
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

                return;
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

        // the whole footprints and who else stands in them, surprise and initiative order
        var local = OccupancySignature(placed, _pending.Battle, out var shared);

        if (local != signature)
        {
            wrong.Add($"occupancy {local:X16}, host {signature:X16}" +
                      (shared.Count > 0 ? $" (sharing here: {string.Join("; ", shared)})" : string.Empty));
        }

        problem = string.Join("; ", wrong);
    }

    // FNV-1a over every placed character (by ID): its Surprised condition, cell, facing, and each grid cell of its
    // footprint with the IDs of all the characters registered there; then the living contenders in the order
    // initiative rolls for them, per side (the same characters in another order would get other dice), and the enemies
    // the battle start found surprised. Surprise was rolled inside TriggerBattleStart from each computer's positions
    // before any placement, so it's compared here too (Codex's reviews of PR #28). Cells holding more than one living
    // character are listed in shared.
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

        // RollInitiative skips the dead (RulesetActor.IsDead) on both sides
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

    #region Messages and waiting

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

                // a player's game didn't match the formation: the host's Resync offer names them
                case MismatchKind when PhotonNetwork.IsMasterClient:
                    var name = PhotonNetwork.CurrentRoom?.GetPlayer(photonEvent.Sender)?.NickName ??
                               $"player {photonEvent.Sender}";

                    Log($"battle {Short(id)}: {name}'s game doesn't match the formation");
                    MultiplayerDesyncContext.OfferResyncForBattlePlacement(name);
                    break;
            }
        });
    }

    // ends this computer's wait; a reason is logged when it ends without the formation
    private static void Release(string why)
    {
        if (_pending is not { Released: false })
        {
            return;
        }

        _pending.Released = true;

        if (why != null)
        {
            Log($"starting initiative without the host's formation: {why}");
        }
    }

    #endregion

    #region Initiative hold

    private static bool Holds => _pending is { Released: false };

    // a battle start held for the host's formation: positions may differ between computers until it's applied, so
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
        Log("initiative held until the host's formation arrives");

        return false;
    }

    // BattleState_Initiative.Update prefix: false skips the frame while held; the held Begin runs first once released
    internal static bool BeforeInitiativeUpdate(BattleState_Initiative state)
    {
        if (Holds && (!IsOnlineRoom || Time.realtimeSinceStartup - _pending.Started >= ClientWaitSeconds))
        {
            Release(IsOnlineRoom ? $"it didn't arrive within {ClientWaitSeconds} s" : "not in an online room any more");
        }

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

    // a battle start: the host's never waits (Released from the start); a client's waits for the formation
    private sealed class Pending(float started, bool isHost)
    {
        internal readonly float Started = started;
        internal GameLocationBattle Battle;
        internal long[] Guids;

        // the host's ID for this battle: the host's own, or (client) the one of the formation applied
        internal string HostBattleId;

        // non-contenders the host's unstack moved, which the formation placed too
        internal List<GameLocationCharacter> PlacedOthers;

        internal bool Released = isHost;
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
