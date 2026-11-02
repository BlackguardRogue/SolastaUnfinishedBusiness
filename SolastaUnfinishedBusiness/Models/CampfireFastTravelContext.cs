using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Builders;
using TA;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

// Fast travel for the campfire gather (user directions, 2026-11-02: "ensure that there is no fast travel point within
// 5 cells of the campfire before creating one. Then, if necessary, create one and fast travel people who are further
// than the distance required for long rest"; then "have all players be moved to the fast travel point if ANY player is
// outside the long rest range"). CampfireRestContext uses it before its walk: when any character is further than the
// long rest distance from the campfire's user, the whole party fast travels to the campfire's point, and the walk
// covers whatever is left.
//
// The destination is a map waypoint, as the game's fast travel only goes to one:
// - a fast travel waypoint already within ExistingWaypointRadius cells of the campfire is used as it is;
// - otherwise, when somebody is beyond the long rest distance, a temporary waypoint is added next to the campfire's
//   user with the game's own GameLocation.AddWaypoint (the game builds its waypoints from the map's WorldNodes when a
//   location loads; none of them is saved, so a temporary one can't reach a save). It is removed once a fast travel
//   ends, a long rest starts, another campfire is used, or the location unloads.
// The destination is set where the campfire's functor runs, which is on every computer in the lockstep: in
// multiplayer every computer must have the waypoint, as the game's fast travel looks it up by position on each one.
//
// The travel is the game's own networked fast travel, unchanged (ICommandService.RequestFastTravel, a lockstep
// command; then the map's MapWaypointItem.PrepareFastTravel and GameLocationMapManager.FastTravel, all on every
// computer): the leader goes along the path, with the game's checks for traps and watching enemies, then the whole
// party, guests included, is placed in formation at the point. f32eeadb moved only the far characters by changing the
// party list those steps read; Codex's review of it (2026-11-02) found that this list could differ between computers
// (a local timeout cleared it on one only) and that ordinary map travel to the same point was taken for a gather.
// Moving the whole party needs no such list, so neither can happen.
internal static class CampfireFastTravelContext
{
    // an existing fast travel waypoint this close to the campfire's user is used instead of making one
    private const float ExistingWaypointRadius = 5f;

    // the game's long rest rule: every two living party members within this many cells
    // (GameRestingManager.CanExecuteLocationRest)
    private const float RestDistance = 10f;

    private const string WaypointName = "FinishingBusinessCampfireWaypoint";

    // the destination of the last campfire used, on every computer
    private static Destination _destination;

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

        var far = FindFarCharacters(user);

        // nobody beyond the long rest distance: a walk at most, no waypoint needed
        if (far.Count == 0)
        {
            return;
        }

        var waypoint = AddTemporaryWaypoint(gameLocation, user, far[0]);

        if (waypoint != null)
        {
            _destination = new Destination
            {
                GameLocation = gameLocation, User = user, Waypoint = waypoint, Temporary = true
            };
        }
    }

    // a waypoint next to the campfire's user, on a free cell (a real one stands on an open cell, and the fast travel
    // paths the leader to it), found as the game finds a place for another party member there
    private static GameMapWaypoint AddTemporaryWaypoint(
        GameLocation gameLocation, GameLocationCharacter user, GameLocationCharacter other)
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

        positioning.ComputeFormationPlacementPositions([other], user.LocationPosition, user.Orientation,
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
    private static List<GameLocationCharacter> FindFarCharacters(GameLocationCharacter user)
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

    // the gather on the computer that started it: when anybody is beyond the long rest distance, the whole party fast
    // travels to the campfire's point, then travelled runs (with false when the travel never began, so the walk does
    // it all). False when nobody needs it or the game can't fast travel now (the walk does it all).
    internal static bool TryStart(GameLocationCharacter user, Func<bool> isWanted, Action<bool> travelled)
    {
        if (_destination is not { } destination || destination.User != user || !destination.GameLocation)
        {
            return false;
        }

        var waypoint = destination.Waypoint;
        var mapService = ServiceRepository.GetService<IGameLocationMapService>();
        var leader = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters.FirstOrDefault();

        // the game's own conditions for this fast travel: the map offers the waypoint (shown and revealed), its
        // same-area rule for the party's leader, nothing blocking fast travel now (running functors or actions
        // blocking saves, a battle)
        if (FindFarCharacters(user).Count == 0 || leader == null || !waypoint.Shown || !waypoint.Revealed ||
            !IsInWaypointArea(waypoint, leader) ||
            mapService == null || mapService.IsPreparingFastTravel || mapService.IsFastTraveling ||
            Gui.Battle != null ||
            ServiceRepository.GetService<IFastTravelService>() is not { CanFastTravel: true } ||
            ServiceRepository.GetService<ICommandService>() is not { } commands)
        {
            return false;
        }

        commands.RequestFastTravel(waypoint.Position);

        var driver = new GameObject("FinishingBusinessCampfireFastTravel").AddComponent<TravelDriver>();

        driver.IsWanted = isWanted;
        driver.Travelled = travelled;

        return true;
    }

    // MapWaypointDefinition.FastTravelFromSameSectorOnly: the game only offers such a waypoint to a party whose leader
    // is in its sector (MapWaypointItem.Bind)
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

    // GameLocationMapManager.SetupPartyOnDestination's end, on every computer: a fast travel is over, so a temporary
    // campfire waypoint has done its job
    internal static void OnFastTravelEnded()
    {
        if (_destination is { Temporary: true })
        {
            RemoveDestination();
        }
    }

    // the gather ended without a rest: in single player the temporary waypoint goes at once. In multiplayer it stays
    // until a lockstep point removes it on every computer (a long rest, a fast travel, the next campfire, the location
    // unloading), as a player could still fast travel to it from the map and every computer must still have it then.
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

    // waits for the campfire fast travel on the computer that asked for it. The travel command runs at a later tick;
    // once the game's travel has begun there is no time limit, and the gather only goes on once the game is back to
    // exploring, so nothing starts while the party is still travelling. Only a travel that never began here (and so
    // never began anywhere, as every computer runs the command at the same tick) is given up after
    // StartTimeoutSeconds, and the gather walks instead. It goes away without going on when the gather was dropped
    // meanwhile (another rest screen opened, a reset).
    private sealed class TravelDriver : MonoBehaviour
    {
        private const float StartTimeoutSeconds = 20f;

        internal Func<bool> IsWanted;
        internal Action<bool> Travelled;
        private bool _began;
        private float _startTime;

        private void Start()
        {
            _startTime = Time.unscaledTime;
        }

        private void Update()
        {
            if (IsWanted?.Invoke() == false)
            {
                Destroy(gameObject);

                return;
            }

            var mapService = ServiceRepository.GetService<IGameLocationMapService>();
            var travelling = mapService is { IsPreparingFastTravel: true } or { IsFastTraveling: true };

            if (!_began)
            {
                _began = travelling;

                if (!_began && Time.unscaledTime - _startTime >= StartTimeoutSeconds)
                {
                    Finish(false);
                }

                return;
            }

            if (!travelling && IsExploring())
            {
                Finish(true);
            }
        }

        private void Finish(bool travelled)
        {
            Destroy(gameObject);
            Travelled?.Invoke(travelled);
        }

        private static bool IsExploring()
        {
            return ServiceRepository.GetService<IGameLocationService>()?.GameLocation?.FiniteStateMachine
                ?.CurrentState is LocationState_Exploration;
        }
    }
}
