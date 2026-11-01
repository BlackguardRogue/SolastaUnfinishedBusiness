using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TA;
using UnityEngine;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Long rests at campfires (FunctorStartLongRest, the campfire gadget's functor). User direction, 2026-11-01:
// 1. In multiplayer any player can start the long rest at a campfire, not only the host. The game refuses clients
//    (GameRestingManager.CanExecuteLocationRest with player privileges, "only the host can rest"); this follows the
//    setting that already lets every player start rests from the rest screen (MultiplayerAllowPlayersToStartRests).
// 2. When the party is too spread out to rest (the game's rule: every two living party members within 10 cells), the
//    characters further away walk to the campfire first, and the approval says so (setting GatherPartyForCampfireRest).
// 3. A campfire set to rest at once starts its rest without the multiplayer approval, as in the game (user direction,
//    2026-11-02; it asked for one in 9b341d45).
//
// How the game runs a campfire: the functor runs on every computer (gadget functors are part of the lockstep), but
// only the computer whose player controls the character that used the campfire opens the rest screen
// (FunctorStartLongRest.IsRestInitiatorControlledByPlayer). An ordinary campfire checks the rest rules first, then
// opens the rest screen with food consumed, and its Start Long Rest button (RestModal.OnStartLongRestCb) starts the
// rest. A campfire set to rest at once (the functor's bool parameter) skips the checks, opens the screen without food
// and presses Start itself. Rests on the world map are the travel's own (GameTravelScheduler), not this functor.
//
// The walk: on the computer that pressed Start (after the approval, in multiplayer), each character further than
// GatherRadius cells from the character who used the campfire gets the game's own move command
// (ICommandService.MoveCharacter, which every computer runs in the lockstep) to a place in the party formation around
// that character. Once every walker arrived, the rest checks run again and the game's Start runs. A walk that can't
// finish (a character can't get there, a fight or cutscene starts, the rest screen is closed) starts no rest.
internal static class CampfireRestContext
{
    private const string PartyTooSpreadOut = "Failure/&FailureFlagPartyTooSpreadOut";

    // walkers end up within this many cells of the campfire's user, so every two characters are within the game's 10
    private const float GatherRadius = 4f;

    // a walk that hasn't finished by then is given up (a move the game can't path may never report back)
    private const float GatherTimeoutSeconds = 90f;

    // the campfire whose functor runs or whose rest screen is open on this computer
    private static Campfire _campfire;

    // set while the functor runs on this computer (each step of it)
    internal static bool InFunctor { get; private set; }

    // set while the rest checks run without the campfire's spread-out allowance (to find out whether a walk is needed)
    private static bool SuppressSpreadOutAllowance { get; set; }

    // set while the game's Start Long Rest runs again after the walk
    internal static bool IsReplayingStart { get; private set; }

    // the walk under way on this computer
    internal static bool IsGathering => _gather != null;

    private static Gather _gather;

    // a campfire set to rest at once pressing Start itself: no rest checks, no "wait until the party can rest" (the
    // campfire skipped them) and no approval
    internal static bool IsForcedStart => _campfire is { Forced: true } && InFunctor;

    // FunctorStartLongRest.Execute: runs each step of the functor with the campfire known
    internal static IEnumerator WrapFunctor(IEnumerator functor, FunctorParametersDescription parameters)
    {
        var campfire = new Campfire
        {
            User = parameters?.ActingCharacters?.FirstOrDefault(x => x != null),
            Forced = parameters?.BoolParameter == true
        };

        while (true)
        {
            bool moved;

            _campfire = campfire;
            InFunctor = true;

            try
            {
                moved = functor.MoveNext();
            }
            finally
            {
                InFunctor = false;
            }

            if (!moved)
            {
                yield break;
            }

            yield return functor.Current;
        }
    }

    // CanExecuteLocationRest: lets the campfire's functor check the rest without the host-only rule, as the rest
    // screen does (MultiplayerAllowPlayersToStartRests)
    internal static bool SkipsPlayerPrivileges => InFunctor && Main.Settings.MultiplayerAllowPlayersToStartRests;

    // CanExecuteLocationRest failed only because the party is spread out: true when the campfire's walk will fix that
    // (the functor's check, the rest screen's button and Start, before the walk)
    internal static bool AllowsSpreadOut(RestType restType, string failureFlag)
    {
        return restType == RestType.LongRest &&
               failureFlag == PartyTooSpreadOut &&
               !SuppressSpreadOutAllowance &&
               Main.Settings.GatherPartyForCampfireRest &&
               CurrentCampfire() is { Forced: false, User: not null };
    }

    // the campfire this computer's rest belongs to: during its functor, while its rest screen is open, or while it walks
    private static Campfire CurrentCampfire()
    {
        if (_campfire == null || InFunctor || IsGathering || IsReplayingStart)
        {
            return _campfire;
        }

        var restModal = Gui.GuiService?.GetScreen<RestModal>();

        if (!restModal || !restModal.Visible)
        {
            _campfire = null;
        }

        return _campfire;
    }

    // the characters who must walk to the campfire before its long rest, or none (not a campfire rest, the setting is
    // off, or the party is close enough). Checked with the game's own rule, without the spread-out allowance.
    internal static List<GameLocationCharacter> FindWalkers()
    {
        var campfire = CurrentCampfire();

        if (campfire is not { Forced: false, User: not null } || !Main.Settings.GatherPartyForCampfireRest)
        {
            return [];
        }

        if (CheckLongRest(out var failure) || failure != PartyTooSpreadOut)
        {
            return [];
        }

        var anchor = campfire.User.LocationPosition;

        // the game's rule only counts characters who are up (not dead, dying or unconscious)
        return (ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters ?? [])
            .Where(x => x != campfire.User &&
                        x.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                        (x.LocationPosition - anchor).magnitude > GatherRadius)
            .ToList();
    }

    // the game's long rest check as the rest screen runs it (every player may rest, with the setting), without the
    // campfire's spread-out allowance
    private static bool CheckLongRest(out string failure)
    {
        var inRestScreen = Patches.GameRestingManagerPatcher.InRestScreen;

        failure = string.Empty;
        SuppressSpreadOutAllowance = true;
        Patches.GameRestingManagerPatcher.InRestScreen = true;

        try
        {
            return ServiceRepository.GetService<IGameRestingService>()
                ?.CanExecuteLocationRest(RestType.LongRest, ref failure, true) == true;
        }
        finally
        {
            SuppressSpreadOutAllowance = false;
            Patches.GameRestingManagerPatcher.InRestScreen = inRestScreen;
        }
    }

    // RestModal.OnBeginShow: a rest screen that isn't the campfire's own (opened from the party bar, after a wait, or
    // after a world map rest) forgets the campfire
    internal static void OnRestScreenShown()
    {
        if (!InFunctor && !IsGathering && !IsReplayingStart)
        {
            _campfire = null;
        }
    }

    // runs the game's Start Long Rest again, past the approval and the campfire's own handling
    internal static void ReplayStart(RestModal restModal)
    {
        IsReplayingStart = true;

        try
        {
            restModal.OnStartLongRestCb();
        }
        finally
        {
            IsReplayingStart = false;
        }
    }

    // the walk to the campfire, then the rest (after the approval in multiplayer)
    internal static void StartGather(RestModal restModal, List<GameLocationCharacter> walkers)
    {
        var campfire = _campfire;
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        var commands = ServiceRepository.GetService<ICommandService>();

        if (campfire?.User == null || positioning == null || characterService == null || commands == null ||
            walkers.Count == 0)
        {
            return;
        }

        // places in the party formation around the campfire's user, found as the game finds them for a group move
        var positions = new List<int3>();
        var sizes = new List<RulesetActor.SizeParameters>();

        positioning.ComputeFormationPlacementPositions(walkers, campfire.User.LocationPosition,
            campfire.User.Orientation, characterService.PartyFormation.FormationPositions,
            CellHelpers.PlacementMode.PlanningStation, positions, sizes, 10, false, false);

        if (positions.Count < walkers.Count)
        {
            ShowGatherFailed(walkers.Skip(positions.Count));

            return;
        }

        var gather = new Gather { RestModal = restModal, Walkers = walkers, StartTime = Time.unscaledTime };

        _gather = gather;

        for (var i = 0; i < walkers.Count; i++)
        {
            var walker = walkers[i];

            // each walker reports once its move ends; aborted is true when it was stopped or couldn't get there
            commands.MoveCharacter(walker, positions[i], campfire.User.Orientation, 0.1f * i, walker.GetMoveStance(),
                aborted => OnWalkerDone(gather, walker, aborted), false, false);
        }

        new GameObject("FinishingBusinessCampfireGather").AddComponent<GatherDriver>().Gather = gather;
    }

    private static void OnWalkerDone(Gather gather, GameLocationCharacter walker, bool aborted)
    {
        if (_gather != gather)
        {
            return;
        }

        gather.Done.Add(walker);

        if (aborted)
        {
            gather.Failed.Add(walker);
        }
    }

    // every frame while walking: ends the walk when everyone reported, when it can no longer lead to a rest, or when it
    // took too long; true while it goes on
    private static bool UpdateGather(Gather gather)
    {
        if (_gather != gather)
        {
            return false;
        }

        var busy = Gui.Battle != null ||
                   ServiceRepository.GetService<INarrativeDirectionService>() is { IsSequenceInProgress: true };

        // closing the rest screen, a fight or a cutscene (which stops every character's move itself) ends the walk
        // without a rest; the game's own screens explain the fight or cutscene
        if (busy || !gather.RestModal || !gather.RestModal.Visible)
        {
            EndGather();

            return false;
        }

        if (gather.Done.Count < gather.Walkers.Count &&
            Time.unscaledTime - gather.StartTime < GatherTimeoutSeconds)
        {
            return true;
        }

        EndGather();

        // every rest rule again, now with the 10 cells too: the game's Start doesn't check them itself (only with a
        // gamepad), the rest screen's button did, before the walk
        var canRest = CheckLongRest(out var failure);

        if (gather.Failed.Count > 0 || gather.Done.Count < gather.Walkers.Count ||
            (!canRest && failure == PartyTooSpreadOut))
        {
            var notThere = gather.Walkers.Where(x => gather.Failed.Contains(x) || !gather.Done.Contains(x)).ToList();

            ShowGatherFailed(notThere.Count > 0 ? notThere : gather.Walkers);

            return false;
        }

        if (!canRest)
        {
            // something else changed during the walk (an enemy came near, ...), shown as the game shows it
            Gui.GuiService.ShowAlert(failure, Gui.ColorFailure, 2.5f);

            return false;
        }

        // everyone is here and every rule passes: the game's Start
        ReplayStart(gather.RestModal);

        return false;
    }

    private static void EndGather()
    {
        _gather = null;
    }

    private static void ShowGatherFailed(IEnumerable<GameLocationCharacter> walkers)
    {
        var names = string.Join(", ", walkers.Select(x => x.Name));

        Gui.GuiService.ShowAlert(Gui.Format("Message/&CampfireGatherFailed", names), Gui.ColorFailure, 5f);
    }

    // leaving or joining a room, or loading: forgets the campfire and any walk
    internal static void Reset()
    {
        _campfire = null;
        _gather = null;
    }

    private sealed class Campfire
    {
        // the campfire rests at once, without the rest screen's checks or food
        internal bool Forced;

        // the character who used the campfire; the walkers gather around them
        internal GameLocationCharacter User;
    }

    private sealed class Gather
    {
        internal readonly HashSet<GameLocationCharacter> Done = [];
        internal readonly HashSet<GameLocationCharacter> Failed = [];
        internal RestModal RestModal;
        internal float StartTime;
        internal List<GameLocationCharacter> Walkers;
    }

    // drives the walk frame by frame, also in single player (where no network frame runs)
    private sealed class GatherDriver : MonoBehaviour
    {
        internal Gather Gather;

        private void Update()
        {
            if (Gather == null || !UpdateGather(Gather))
            {
                Destroy(gameObject);
            }
        }
    }
}
