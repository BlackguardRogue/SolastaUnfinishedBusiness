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
// 1. Every computer: when a battle starts (GameLocationBattleManager.TriggerBattleStart), a pending placement opens,
//    numbered by the battles started since joining the room or loading the map, with the contenders' IDs.
// 2. The host separates the characters, then sends the whole formation: every contender's ID, cell and facing,
//    moved or not, so a client that had them anywhere else is brought to the host's positions too.
// 3. A client never separates anyone itself. It applies the host's formation through the game's positioning service,
//    checks that every contender now stands where the host said, and reports to the host whether they do.
// 4. Every computer holds the battle's initiative state (BattleState_Initiative, which the intro state requests a
//    frame or more after the battle starts) before its Begin, which also moves stacked characters apart, and before
//    the roll, until the host releases it: when every player in the room has
//    reported, or after HostWaitSeconds. A client that hears nothing releases itself after ClientWaitSeconds.
// 5. A failed check, a formation for other contenders, a timeout, a host change or leaving the room never fall back
//    to a local placement: the battle goes on as it stands, the mod's log says why, and an alert tells the player that
//    positions may differ (a Resync puts every computer back on the host's game).
// Single player is unchanged: the placement runs at once and nothing waits.
internal static class MultiplayerBattlePlacementContext
{
    private const byte EventCode = 175;
    private const byte FormationKind = 1, ReportKind = 2, ReleaseKind = 3;

    private const float HostWaitSeconds = 10f;
    private const float ClientWaitSeconds = 20f;

    private static bool _eventHandlerRegistered;

    // battles started since joining the room or loading the map; counted on every computer, so it names the battle
    private static int _battleCount;

    private static Pending _pending;

    // formations that arrived before this computer started the battle (the host's message can outrun its own start)
    private static readonly Dictionary<int, object[]> EarlyFormations = new();

    // the initiative state's Begin is held while placing, then run once released
    private static BattleState_Initiative _heldInitiative;
    private static object[] _heldParameters;
    private static bool _runningHeldBegin;

    private static bool IsOnlineRoom =>
        Global.IsMultiplayer && PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    private static bool IsActive => IsOnlineRoom && Main.Settings.MultiplayerResolveOverlapsOnBattleStart;

    private static void Log(string message)
    {
        Main.Info($"[BattlePlacement] {message}");
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
    }

    internal static void OnMasterClientSwitched()
    {
        if (_pending is { Released: false })
        {
            Release("the host changed before the placement was confirmed", true);
        }
    }

    private static void Reset(string why)
    {
        if (_pending is { Released: false })
        {
            Log($"battle {_pending.Number} placement dropped: {why}");
        }

        // a held initiative state still in play runs its Begin at its next Update, as nothing holds it now
        _battleCount = 0;
        _pending = null;
        EarlyFormations.Clear();
    }

    #endregion

    #region Battle start

    // TriggerBattleStart prefix: opens the placement before the battle's states can run (the initiative state is
    // requested inside TriggerBattleStart, so its Begin must already find the placement pending)
    internal static void OnBattleStarting(bool battleInProgress)
    {
        if (battleInProgress || !IsActive)
        {
            return;
        }

        _pending = new Pending(++_battleCount, Time.realtimeSinceStartup);
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
            _battleCount--;

            return;
        }

        _pending.Battle = battle;
        _pending.Guids = battle.AllContenders.Where(x => x != null).Select(x => (long)x.Guid).OrderBy(x => x)
            .ToArray();

        if (PhotonNetwork.IsMasterClient)
        {
            PlaceAsHost(battle);
        }
        else
        {
            // a formation that came before this computer started the battle: by number, else by contenders
            var early = EarlyFormations.TryGetValue(_pending.Number, out var byNumber)
                ? byNumber
                : EarlyFormations.Values.FirstOrDefault(x =>
                    x[2] is long[] guids && guids.OrderBy(g => g).SequenceEqual(_pending.Guids));

            if (early != null)
            {
                EarlyFormations.Remove((int)early[1]);
                ApplyFormation(early);
            }
        }
    }

    private static void PlaceAsHost(GameLocationBattle battle)
    {
        // one contender after the other: each separation teleports at once, so the next sees that cell taken
        foreach (var contender in battle.AllContenders.Where(x => x != null).ToList())
        {
            MultiplayerContext.SeparateFromSharedCell(contender);
        }

        var contenders = battle.AllContenders.Where(x => x != null).OrderBy(x => x.Guid).ToList();
        var guids = contenders.Select(x => (long)x.Guid).ToArray();
        var cells = contenders
            .SelectMany(x => new[] { x.LocationPosition.x, x.LocationPosition.y, x.LocationPosition.z })
            .ToArray();
        var facings = contenders.Select(x => (int)x.Orientation).ToArray();

        _pending.Roster = PhotonNetwork.PlayerList.Select(x => x.ActorNumber).ToArray();
        _pending.Reported.Add(PhotonNetwork.LocalPlayer.ActorNumber);

        Log($"battle {_pending.Number}: sending the formation of {guids.Length} contenders to " +
            $"{_pending.Roster.Length - 1} players");

        Send(new object[] { FormationKind, _pending.Number, guids, cells, facings }, ReceiverGroup.Others);
        PumpHost();
    }

    // client: the host's formation for the pending battle, or kept for a battle this computer hasn't started yet.
    // It belongs to the pending battle when it has the same number or the same contenders: the contenders decide, so
    // a count that drifted (a computer that joined the map differently) still pairs the right battle.
    private static void ApplyFormation(object[] data)
    {
        if (data.Length != 5 || data[1] is not int number || data[2] is not long[] guids ||
            data[3] is not int[] cells || data[4] is not int[] facings ||
            cells.Length != guids.Length * 3 || facings.Length != guids.Length)
        {
            Log("ignored a malformed formation");

            return;
        }

        var sameContenders = _pending?.Guids != null && guids.OrderBy(x => x).SequenceEqual(_pending.Guids);

        if (_pending?.Guids == null || _pending.Applied || (_pending.Number != number && !sameContenders))
        {
            if (_pending is { Applied: true } && _pending.HostNumber == number)
            {
                // a duplicate
                return;
            }

            // a battle this computer hasn't started yet; keep the latest few
            EarlyFormations[number] = data;

            foreach (var old in EarlyFormations.Keys.Where(x => x < number - 4).ToList())
            {
                EarlyFormations.Remove(old);
            }

            return;
        }

        if (_pending.Number != number)
        {
            Log($"battle {_pending.Number} is the host's battle {number}: same contenders");
        }

        _pending.Applied = true;
        _pending.HostNumber = number;

        var ok = Apply(guids, cells, facings, out var problem);

        Log(ok
            ? $"battle {_pending.Number}: formation applied and checked"
            : $"battle {_pending.Number}: formation not applied as sent: {problem}");

        Send(new object[] { ReportKind, number, ok }, ReceiverGroup.MasterClient);
    }

    private static bool Apply(long[] guids, int[] cells, int[] facings, out string problem)
    {
        var battle = _pending.Battle;

        if (!guids.OrderBy(x => x).SequenceEqual(_pending.Guids))
        {
            problem = $"the host's contenders ({string.Join(",", guids)}) aren't this battle's " +
                      $"({string.Join(",", _pending.Guids)})";

            return false;
        }

        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioningService == null)
        {
            problem = "no positioning service";

            return false;
        }

        var byGuid = battle.AllContenders.Where(x => x != null).ToDictionary(x => (long)x.Guid);

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

        problem = string.Join("; ", wrong);

        return wrong.Count == 0;
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
            data[0] is not byte kind || data[1] is not int number || !IsOnlineRoom)
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
                                     _pending?.Number == number && _pending.Roster != null:
                    _pending.Reported.Add(photonEvent.Sender);

                    if (!ok)
                    {
                        _pending.Failed.Add(photonEvent.Sender);
                        Log($"battle {number}: {PlayerName(photonEvent.Sender)} couldn't apply the formation");
                    }

                    PumpHost();
                    break;

                // the host's number, which a client records when it applies the formation
                case ReleaseKind when photonEvent.Sender == host && _pending != null &&
                                      (_pending.Applied ? _pending.HostNumber == number : _pending.Number == number):
                    Release(_pending.Applied ? null : "released by the host before its formation arrived",
                        !_pending.Applied);
                    break;
            }
        });
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

        Send(new object[] { ReleaseKind, _pending.Number }, ReceiverGroup.Others);

        var problems = new List<string>();

        if (missing.Count > 0)
        {
            problems.Add($"no answer from {string.Join(", ", missing.Select(PlayerName))}");
        }

        if (_pending.Failed.Count > 0)
        {
            problems.Add($"not applied by {string.Join(", ", _pending.Failed.Select(PlayerName))}");
        }

        Release(problems.Count == 0 ? null : string.Join("; ", problems), problems.Count > 0);
    }

    private static void Release(string problem, bool alert)
    {
        if (_pending == null || _pending.Released)
        {
            return;
        }

        _pending.Released = true;

        Log(problem == null
            ? $"battle {_pending.Number}: every player has the formation; starting initiative"
            : $"battle {_pending.Number}: starting initiative without every player's confirmation: {problem}");

        if (alert)
        {
            Gui.GuiService?.ShowAlert(
                Gui.Localize("Message/&MultiplayerBattlePlacementUnconfirmed"), Gui.ColorFailure, 8f);
        }
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
            Release("not in an online room any more", false);

            return;
        }

        if (PhotonNetwork.IsMasterClient)
        {
            if (_pending.Roster == null)
            {
                // the battle started here before this computer became host, so it has no formation to wait for
                Release("this computer became host during the battle start", true);

                return;
            }

            PumpHost();

            return;
        }

        if (Time.realtimeSinceStartup - _pending.Started >= ClientWaitSeconds)
        {
            Release(_pending.Applied
                ? "the host's release didn't arrive"
                : "the host's formation didn't arrive; positions left as they are", true);
        }
    }

    #endregion

    #region Initiative hold

    private static bool Holds => _pending is { Released: false };

    // BattleState_Initiative.Begin prefix: false skips Begin for now; it runs from Update once released
    internal static bool BeforeInitiativeBegin(BattleState_Initiative state, object[] parameters)
    {
        if (_runningHeldBegin || !Holds)
        {
            return true;
        }

        _heldInitiative = state;
        _heldParameters = parameters;
        Log($"battle {_pending.Number}: initiative held until every player has the formation");

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

    #endregion

    private sealed class Pending(int number, float started)
    {
        internal readonly HashSet<int> Failed = [];
        internal readonly HashSet<int> Reported = [];
        internal readonly int Number = number;
        internal readonly float Started = started;
        internal bool Applied;
        internal int HostNumber;
        internal GameLocationBattle Battle;
        internal long[] Guids;
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
}
