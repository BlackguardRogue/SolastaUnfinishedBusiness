using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Builders;
using TA;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

// Fast travel for the campfire gather (user direction, 2026-11-02: "ensure that there is no fast travel point within 5
// cells of the campfire before creating one. Then, if necessary, create one and fast travel people who are further
// than the distance required for long rest"). CampfireRestContext uses it before its walk: the characters further
// than the long rest distance fast travel to the campfire, and the walk covers whatever is left.
//
// The destination is a map waypoint, as the game's fast travel only goes to one:
// - a fast travel waypoint already within ExistingWaypointRadius cells of the campfire is used as it is;
// - otherwise, when somebody is beyond the long rest distance, a temporary waypoint is added next to the campfire's
//   user with the game's own GameLocation.AddWaypoint (the game builds its waypoints from the map's WorldNodes when a
//   location loads; none of them is saved, so a temporary one can't reach a save). It is removed once the fast travel
//   ends, a long rest starts, another campfire is used, or the location unloads.
// The destination is set where the campfire's functor runs, which is on every computer in the lockstep: in
// multiplayer every computer must have the waypoint, as the game's fast travel looks it up by position on each one.
//
// The travel is the game's networked one (ICommandService.RequestFastTravel, a lockstep command; then the map's
// MapWaypointItem.PrepareFastTravel and GameLocationMapManager.FastTravel, ExecutePath and SetupPartyOnDestination,
// all on every computer). The game moves the whole party: the leader along the path, then everyone placed in
// formation at the destination. During a campfire fast travel those methods get the travellers instead of the party
// (FastTravelPatcher), so only the characters beyond the long rest distance move; the first of them leads, so it is
// their route the game checks for traps and watching enemies. The travellers are worked out when the travel command
// runs, from positions every computer shares, so every computer moves the same characters.
internal static class CampfireFastTravelContext
{
    // an existing fast travel waypoint this close to the campfire's user is used instead of making one
    private const float ExistingWaypointRadius = 5f;

    // the game's long rest rule: every two living party members within this many cells
    // (GameRestingManager.CanExecuteLocationRest)
    private const float RestDistance = 10f;

    // the travel didn't start or finish in this long (the game dropped it): the gather goes on by walking
    private const float TravelTimeoutSeconds = 60f;

    private const string WaypointName = "FinishingBusinessCampfireWaypoint";

    // the destination of the last campfire used, on every computer
    private static Destination _destination;

    // the characters moving in the campfire fast travel under way, on every computer; null otherwise
    private static List<GameLocationCharacter> _travellers;

    #region Destination

    // FunctorStartLongRest on every computer: the campfire's fast travel destination, an existing waypoint within
    // ExistingWaypointRadius cells or, when somebody is beyond the long rest distance, a temporary one. Positions and
    // waypoints are the same on every computer, so all of them pick the same one.
    internal static void PrepareDestination(GameLocationCharacter user)
    {
        RemoveDestination();

        var gameLocation = ServiceRepository.GetService<IGameLocationService>()?.GameLocation;

        if (user == null || !gameLocation || !Main.Settings.GatherPartyForCampfireRest)
        {
            return;
        }

        // the user asked that no second fast travel point be made next to one that is already there; a waypoint
        // hidden by the map (its node inactive) doesn't count, as the game doesn't offer it either
        var existing = gameLocation.MapWaypoints
            .Where(x => x.Definition is { AllowFastTravel: true, LocationExit: false } && x.Shown &&
                        Distance(x.Position, user.LocationPosition) <= ExistingWaypointRadius)
            .OrderBy(x => Distance(x.Position, user.LocationPosition))
            .FirstOrDefault();

        if (existing != null)
        {
            _destination = new Destination { GameLocation = gameLocation, User = user, Waypoint = existing };

            return;
        }

        var travellers = FindTravellers(user);

        // nobody beyond the long rest distance: a walk at most, no waypoint needed
        if (travellers.Count == 0)
        {
            return;
        }

        var waypoint = AddTemporaryWaypoint(gameLocation, user, travellers[0]);

        if (waypoint != null)
        {
            _destination = new Destination
            {
                GameLocation = gameLocation, User = user, Waypoint = waypoint, Temporary = true
            };
        }
    }

    // a waypoint next to the campfire's user, on a free cell (a real one stands on an open cell, and the fast travel
    // paths the leader to it), found as the game finds places for a group move
    private static GameMapWaypoint AddTemporaryWaypoint(
        GameLocation gameLocation, GameLocationCharacter user, GameLocationCharacter traveller)
    {
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        var worldLocation = ServiceRepository.GetService<IGameLocationService>()?.WorldLocation;

        if (positioning == null || characterService == null || !worldLocation)
        {
            return null;
        }

        var positions = new List<int3>();
        var sizes = new List<RulesetActor.SizeParameters>();

        positioning.ComputeFormationPlacementPositions([traveller], user.LocationPosition, user.Orientation,
            characterService.PartyFormation.FormationPositions, CellHelpers.PlacementMode.PlanningStation, positions,
            sizes, 10, false, false);

        if (positions.Count == 0)
        {
            return null;
        }

        var definition = ScriptableObject.CreateInstance<MapWaypointDefinition>();

        definition.name = WaypointName;
        definition.guiPresentation =
            GuiPresentationBuilder.Build(null, "Screen/&CampfireFastTravelWaypointTitle", Gui.NoLocalization);
        definition.AllowFastTravel = true;
        definition.LongRestAvailable = true;

        // under the location's scene, so it goes away with the location; placed before it is bound, as
        // GameMapWaypoint.Bind reads its cell from its position
        var gameObject = new GameObject(WaypointName);

        gameObject.transform.SetParent(worldLocation.transform, false);
        gameObject.transform.position = positioning.GetWorldPositionFromGridPosition(positions[0]);

        var node = gameObject.AddComponent<WorldNode>();

        node.mapWaypointDefinition = definition;
        node.revealedByDefault = true;

        // the game's own registration (it appends the new waypoint)
        gameLocation.AddWaypoint(node);

        return gameLocation.MapWaypoints.LastOrDefault();
    }

    // drops the destination; a temporary waypoint leaves the map again
    internal static void RemoveDestination()
    {
        var destination = _destination;

        _destination = null;

        if (destination is not { Temporary: true })
        {
            return;
        }

        var node = destination.Waypoint.worldNode;

        // a location that unloaded took its waypoints and the node with it
        if (destination.GameLocation)
        {
            destination.GameLocation.MapWaypoints.Remove(destination.Waypoint);
        }

        destination.Waypoint.Unbind();

        if (node)
        {
            Object.Destroy(node.gameObject);
        }

        Object.Destroy(destination.Waypoint.Definition);
    }

    // characters beyond the long rest distance from the campfire's user (the game's rule only counts characters who
    // are up)
    private static List<GameLocationCharacter> FindTravellers(GameLocationCharacter user)
    {
        return (ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters ?? [])
            .Where(x => x != user &&
                        x.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                        Distance(x.LocationPosition, user.LocationPosition) > RestDistance)
            .ToList();
    }

    private static float Distance(int3 a, int3 b)
    {
        return (a - b).magnitude;
    }

    #endregion

    #region Travel

    // the gather on the computer that started it: the characters beyond the long rest distance fast travel to the
    // campfire's destination, then travelled runs (also when the travel couldn't finish, so the walk takes over).
    // False when nobody needs it or the game can't fast travel now (the walk does it all).
    internal static bool TryStart(GameLocationCharacter user, Action travelled)
    {
        if (_destination is not { } destination || destination.User != user || !destination.GameLocation)
        {
            return false;
        }

        var travellers = FindTravellers(user);
        var waypoint = destination.Waypoint;
        var mapService = ServiceRepository.GetService<IGameLocationMapService>();

        // the game's own conditions for this fast travel: the map offers the waypoint (shown and revealed), its
        // same-area rule, nothing blocking fast travel now (running functors or actions blocking saves, a battle)
        if (travellers.Count == 0 || !waypoint.Shown || !waypoint.Revealed ||
            !IsInWaypointArea(waypoint, travellers[0]) ||
            mapService == null || mapService.IsPreparingFastTravel || mapService.IsFastTraveling ||
            Gui.Battle != null ||
            ServiceRepository.GetService<IFastTravelService>() is not { CanFastTravel: true } ||
            ServiceRepository.GetService<ICommandService>() is not { } commands)
        {
            return false;
        }

        commands.RequestFastTravel(waypoint.Position);
        new GameObject("FinishingBusinessCampfireFastTravel").AddComponent<TravelDriver>().Travelled = travelled;

        return true;
    }

    // MapWaypointDefinition.FastTravelFromSameSectorOnly: the game only offers such a waypoint to a party in its
    // sector (MapWaypointItem.Bind)
    private static bool IsInWaypointArea(GameMapWaypoint waypoint, GameLocationCharacter leader)
    {
        if (!waypoint.Definition.FastTravelFromSameSectorOnly)
        {
            return true;
        }

        var accessor = new GridAccessor(waypoint.Position);
        var sector = accessor.sector;

        accessor.FetchSector(leader.LocationPosition);

        return accessor.sector == sector;
    }

    // LocalCommandManager.RequestFastTravel, on every computer at the command's tick: a fast travel to the campfire's
    // destination moves only the characters beyond the long rest distance, worked out now from shared positions
    internal static void OnFastTravelRequested(int3 position)
    {
        _travellers = null;

        if (_destination is not { } destination || !destination.GameLocation ||
            destination.Waypoint.Position != position)
        {
            return;
        }

        var travellers = FindTravellers(destination.User);

        if (travellers.Count > 0)
        {
            _travellers = travellers;
        }
    }

    // MapWaypointItem.PrepareFastTravel: a fast travel elsewhere (a single player one from the map doesn't go through
    // the travel command) moves the whole party as usual
    internal static void OnPrepareFastTravel(GameMapWaypoint waypoint)
    {
        if (_travellers != null && _destination?.Waypoint.Position != waypoint?.Position)
        {
            _travellers = null;
        }
    }

    // GameLocationMapManager.SetupPartyOnDestination's end, on every computer: the campfire fast travel is over
    internal static void OnFastTravelEnded()
    {
        if (_travellers == null)
        {
            return;
        }

        _travellers = null;

        if (_destination is { Temporary: true })
        {
            RemoveDestination();
        }
    }

    // the party list the game's fast travel steps read: the travellers during a campfire fast travel
    internal static List<GameLocationCharacter> PartyForFastTravel(IGameLocationCharacterService service)
    {
        return _travellers ?? service.PartyCharacters;
    }

    // the guests the game's fast travel steps read: none during a campfire fast travel (they stay where they are)
    internal static List<GameLocationCharacter> GuestsForFastTravel(IGameLocationCharacterService service)
    {
        return _travellers != null ? [] : service.GuestCharacters;
    }

    // the gather ended without a rest: in single player the temporary waypoint goes at once. In multiplayer it stays
    // until a lockstep point removes it on every computer (a long rest, the next campfire, the location unloading),
    // as a player could still fast travel to it from the map and every computer must still have it then.
    internal static void OnGatherAbandoned()
    {
        if (!Global.IsMultiplayer)
        {
            RemoveDestination();
        }
    }

    // leaving or joining a room, or loading
    internal static void Reset()
    {
        _travellers = null;
        RemoveDestination();
    }

    #endregion

    private sealed class Destination
    {
        internal GameLocation GameLocation;

        // made for this campfire, removed again afterwards
        internal bool Temporary;

        // the character who used the campfire
        internal GameLocationCharacter User;
        internal GameMapWaypoint Waypoint;
    }

    // waits for the campfire fast travel on the computer that asked for it: it runs at a later command tick, and ends
    // when the game has placed the travellers and is back to exploring
    private sealed class TravelDriver : MonoBehaviour
    {
        internal Action Travelled;
        private bool _started;
        private float _startTime;

        private void Start()
        {
            _startTime = Time.unscaledTime;
        }

        private void Update()
        {
            var mapService = ServiceRepository.GetService<IGameLocationMapService>();
            var travelling = _travellers != null || mapService is { IsFastTraveling: true };

            _started |= travelling;

            if (_started && !travelling && IsExploring())
            {
                Finish();

                return;
            }

            if (Time.unscaledTime - _startTime < TravelTimeoutSeconds)
            {
                return;
            }

            // the game dropped the travel (for example its waypoint lookup failed); its party lists go back to normal
            _travellers = null;
            Finish();
        }

        private void Finish()
        {
            Destroy(gameObject);
            Travelled?.Invoke();
        }

        private static bool IsExploring()
        {
            return ServiceRepository.GetService<IGameLocationService>()?.GameLocation?.FiniteStateMachine
                ?.CurrentState is LocationState_Exploration;
        }
    }
}
