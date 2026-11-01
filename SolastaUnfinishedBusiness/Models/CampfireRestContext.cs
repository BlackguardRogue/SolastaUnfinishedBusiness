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
// that character. The rest screen closes for the walk: it pauses game time while it is open (RestModal.OnBeginShow
// calls GameTime.Pause, OnEndHide resumes it), so nobody could move with it open (in-game test, 2026-11-02: every
// walk ran out of time). Once every walker arrived, the rest checks run again, and the rest screen opens again with
// the game's Start pressed, as the game does for a campfire that rests at once. A walk that can't finish (a character
// can't get there, a fight or cutscene starts, another rest screen opens) starts no rest.
//
// A long rest that has to wait first (the party already rested that day) walks before the wait (user direction,
// 2026-11-02: "the party is auto gathered before the time skip"). The game checks the once-a-day rule before the
// distance (GameRestingManager.CanExecuteLocationRest), so a campfire offers its wait ("Waiting to Perform a Long
// Rest", FunctorStartLongRest.DoWaitLongRest) even for a spread-out party, and the long rest after the wait
// (StartDeferredLongRestAsync) then refused it as spread out, 16 hours later. Both waits, that one and the rest
// screen's combined wait and long rest (LongRestWaitContext), now walk the party to the campfire first, then wait; in
// multiplayer the walk, the wait and the rest are approved together.
internal static class CampfireRestContext
{
    private const string PartyTooSpreadOut = "Failure/&FailureFlagPartyTooSpreadOut";

    // walkers end up within this many cells of the campfire's user, so every two characters are within the game's 10
    private const float GatherRadius = 4f;

    // a walk that hasn't finished by then is given up (a move the game can't path may never report back)
    private const float GatherTimeoutSeconds = 90f;

    // the campfire whose functor runs or whose rest screen is open on this computer
    private static Campfire _campfire;

    // the campfire whose functor offered the game's wait before the long rest (its check failed on the once-a-day
    // rule), kept for the wait prompt's Yes, which runs after the functor ended
    private static Campfire _waitCampfire;

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

        // an earlier campfire's wait prompt that was answered No
        _waitCampfire = null;

        // the campfire's fast travel destination, on every computer (the functor runs on each in the lockstep): an
        // existing fast travel waypoint next to the campfire, or a temporary one when somebody is beyond the long rest
        // distance (CampfireFastTravelContext); a campfire that rests at once has no gather
        if (campfire.Forced)
        {
            CampfireFastTravelContext.RemoveDestination();
        }
        else
        {
            CampfireFastTravelContext.PrepareDestination(campfire.User);
        }

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

    // CanExecuteLocationRest's result: when the campfire functor's own check fails on the once-a-day rule, the game
    // offers its wait next (FunctorStartLongRest.Execute), so the campfire is kept for that prompt's Yes
    internal static void OnRestChecked(RestType restType, string failureFlag, bool result)
    {
        if (InFunctor && !result && restType == RestType.LongRest &&
            failureFlag == LongRestWaitContext.RestTooOften && _campfire is { Forced: false })
        {
            _waitCampfire = _campfire;
        }
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

    // the characters who must walk to the campfire before the long rest of its rest screen, or none (not a campfire
    // rest, the setting is off, or the party is close enough)
    internal static List<GameLocationCharacter> FindWalkers()
    {
        return FindWalkers(CurrentCampfire(), false);
    }

    // the characters who must walk to the campfire, or none. Checked with the game's own rule, without the spread-out
    // allowance; before a wait, without the once-a-day rule too (the wait is there for it, and the game checks it
    // before the distance, so it would hide a spread-out party). Only a party that fails on the distance alone walks.
    private static List<GameLocationCharacter> FindWalkers(Campfire campfire, bool ignoreRestFrequency)
    {
        if (campfire is not { Forced: false, User: not null } || !Main.Settings.GatherPartyForCampfireRest)
        {
            return [];
        }

        if (CheckLongRest(out var failure, ignoreRestFrequency) || failure != PartyTooSpreadOut)
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
    // campfire's spread-out allowance, and optionally without the once-a-day rule (LongRestWaitContext's switch, which
    // the hero's CanRest patch reads)
    private static bool CheckLongRest(out string failure, bool ignoreRestFrequency = false)
    {
        var inRestScreen = Patches.GameRestingManagerPatcher.InRestScreen;
        var ignoredRestFrequency = LongRestWaitContext.IgnoreRestFrequency;

        failure = string.Empty;
        SuppressSpreadOutAllowance = true;
        Patches.GameRestingManagerPatcher.InRestScreen = true;
        LongRestWaitContext.IgnoreRestFrequency = ignoreRestFrequency;

        try
        {
            return ServiceRepository.GetService<IGameRestingService>()
                ?.CanExecuteLocationRest(RestType.LongRest, ref failure, true) == true;
        }
        finally
        {
            SuppressSpreadOutAllowance = false;
            Patches.GameRestingManagerPatcher.InRestScreen = inRestScreen;
            LongRestWaitContext.IgnoreRestFrequency = ignoredRestFrequency;
        }
    }

    // RestModal.OnBeginShow: a rest screen that isn't the campfire's own (opened from the party bar, after a wait, or
    // after a world map rest) forgets the campfire; any rest screen but the one reopened after the walk also ends a
    // walk under way without a rest (the player started over; the walkers still finish their moves)
    internal static void OnRestScreenShown()
    {
        if (IsReplayingStart)
        {
            return;
        }

        EndGather();

        if (!InFunctor)
        {
            _campfire = null;
        }
    }

    // after the walk: opens the rest screen again and presses the game's Start Long Rest, past the approval and the
    // campfire's own handling; the same steps as the game's campfire that rests at once (FunctorStartLongRest.Execute:
    // ForceRestType, Show(true), OnStartLongRestCb), keeping the food choice the campfire's screen had
    private static void ReplayStart(RestModal restModal, bool consumeFood)
    {
        // the location changed during the walk (a fight or cutscene would have ended it first)
        if (!restModal)
        {
            return;
        }

        IsReplayingStart = true;

        try
        {
            if (!restModal.Visible)
            {
                restModal.ForceRestType(RestType.LongRest, consumeFood);
                restModal.Show(true);
            }

            restModal.OnStartLongRestCb();
        }
        finally
        {
            IsReplayingStart = false;
        }
    }

    // the rest screen's Start with characters too far away (after the approval in multiplayer): the rest screen
    // closes, the party walks to the campfire, then the screen opens again and the rest starts
    internal static void StartGather(RestModal restModal, List<GameLocationCharacter> walkers)
    {
        // the screen keeps its food choice (the campfire opened it with food consumed) in a field that Start reads
        var consumeFood = restModal.consumeFood;

        if (!StartGather(_campfire, walkers, false, () => ReplayStart(restModal, consumeFood)))
        {
            return;
        }

        // closing the screen resumes game time (RestModal.OnEndHide), which the walk needs; StartGather set the walk
        // first, so the campfire is kept while the screen is closed
        if (restModal.Visible)
        {
            restModal.Hide();
        }
    }

    // a long rest that waits first, at a campfire with characters too far away: the party walks to the campfire, then
    // the game's wait and long rest run (ICommandService.StartDeferredLongRest, which rests once the wait is over).
    // Called from the game's campfire wait prompt (FunctorStartLongRest.DoWaitLongRest) and from the rest screen's
    // combined wait and long rest (LongRestWaitContext); true when the walk took over, false when nobody walks and
    // the caller waits as usual. In multiplayer the walk, the wait and the rest are approved together, and the wait
    // after the walk isn't asked about again.
    internal static bool TryGatherBeforeWait()
    {
        // the game's prompt kept its campfire; the rest screen's wait finds its campfire on the open screen
        var campfire = _waitCampfire ?? CurrentCampfire();

        _waitCampfire = null;

        var walkers = FindWalkers(campfire, true);

        if (walkers.Count == 0)
        {
            return false;
        }

        var hours = LongRestWaitContext.MissingHours();

        if (!MultiplayerApprovalContext.MustWaitForApprovalWithSubjectName(
                MultiplayerApprovalContext.ApprovalAction.CampfireWaitAndLongRest,
                string.Join(", ", walkers.ConvertAll(x => x.Name)), hours.ToString(),
                (hours * 3600) + MultiplayerApprovalContext.RestSeconds(RestType.LongRest),
                () => StartGather(campfire, walkers, true, WaitThenRest)))
        {
            StartGather(campfire, walkers, true, WaitThenRest);
        }

        return true;
    }

    // after the walk before a wait: the game's wait and long rest, already approved with the walk; the hours are
    // counted again, as the game counts them, since the walk took a little game time
    private static void WaitThenRest()
    {
        MultiplayerApprovalContext.RunApproved(() =>
            ServiceRepository.GetService<ICommandService>()?.StartDeferredLongRest(LongRestWaitContext.MissingHours()));
    }

    // the gather at the campfire; arrived runs once everyone is there and the rest rules pass (before a wait, all but
    // the once-a-day rule). False when it couldn't start.
    private static bool StartGather(
        Campfire campfire, List<GameLocationCharacter> walkers, bool beforeWait, System.Action arrived)
    {
        if (campfire?.User == null || walkers.Count == 0)
        {
            return false;
        }

        var gather = new Gather
        {
            Arrived = arrived, BeforeWait = beforeWait, Walkers = walkers, StartTime = Time.unscaledTime
        };

        // first the characters beyond the long rest distance fast travel to the campfire (user direction,
        // 2026-11-02; CampfireFastTravelContext), then the walk covers whatever is left; when the game can't fast
        // travel now, or nobody is that far, everyone walks
        if (CampfireFastTravelContext.TryStart(campfire.User, () => AfterFastTravel(gather, campfire)))
        {
            _gather = gather;

            return true;
        }

        return StartWalk(campfire, gather);
    }

    // the campfire fast travel is over (or the game dropped it): whoever is still too far walks, then the rest
    private static void AfterFastTravel(Gather gather, Campfire campfire)
    {
        // another rest screen opened during the travel, or a reset
        if (_gather != gather)
        {
            return;
        }

        EndGather();

        // a fight or cutscene (an enemy saw a traveller): the game's own screens explain it
        if (IsBusy())
        {
            return;
        }

        var walkers = FindWalkers(campfire, gather.BeforeWait);

        if (walkers.Count > 0)
        {
            StartWalk(campfire,
                new Gather
                {
                    Arrived = gather.Arrived,
                    BeforeWait = gather.BeforeWait,
                    Walkers = walkers,
                    StartTime = Time.unscaledTime
                });

            return;
        }

        FinishGather(gather, []);
    }

    // the walk to the campfire. False when it couldn't start.
    private static bool StartWalk(Campfire campfire, Gather gather)
    {
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        var commands = ServiceRepository.GetService<ICommandService>();
        var walkers = gather.Walkers;

        if (positioning == null || characterService == null || commands == null)
        {
            return false;
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

            return false;
        }

        _gather = gather;

        for (var i = 0; i < walkers.Count; i++)
        {
            var walker = walkers[i];

            // each walker reports once its move ends; aborted is true when it was stopped or couldn't get there
            commands.MoveCharacter(walker, positions[i], campfire.User.Orientation, 0.1f * i, walker.GetMoveStance(),
                aborted => OnWalkerDone(gather, walker, aborted), false, false);
        }

        new GameObject("FinishingBusinessCampfireGather").AddComponent<GatherDriver>().Gather = gather;

        return true;
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

        // a fight or a cutscene (which stops every character's move itself) ends the walk without a rest; the game's
        // own screens explain them. (The rest screen is closed during the walk, so it no longer counts.)
        if (IsBusy())
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
        FinishGather(gather,
            gather.Walkers.Where(x => gather.Failed.Contains(x) || !gather.Done.Contains(x)).ToList());

        return false;
    }

    // the gather is over: the rest checks, then the rest screen's Start or the wait and rest; notThere are the
    // characters who didn't get there
    private static void FinishGather(Gather gather, List<GameLocationCharacter> notThere)
    {
        // every rest rule again, now with the 10 cells too: the game's Start doesn't check them itself (only with a
        // gamepad), the rest screen's button did, before the gather. Before a wait, all but the once-a-day rule,
        // which the wait is for; the game checks every rule again once the wait is over.
        var canRest = CheckLongRest(out var failure, gather.BeforeWait);

        if (notThere.Count > 0 || (!canRest && failure == PartyTooSpreadOut))
        {
            ShowGatherFailed(notThere.Count > 0 ? notThere : gather.Walkers);
            CampfireFastTravelContext.OnGatherAbandoned();

            return;
        }

        if (!canRest)
        {
            // something else changed during the gather (an enemy came near, ...), shown as the game shows it
            Gui.GuiService.ShowAlert(failure, Gui.ColorFailure, 2.5f);
            CampfireFastTravelContext.OnGatherAbandoned();

            return;
        }

        // everyone is here and every rule passes: the rest screen's Start, or the wait and rest
        gather.Arrived();
    }

    private static bool IsBusy()
    {
        return Gui.Battle != null ||
               ServiceRepository.GetService<INarrativeDirectionService>() is { IsSequenceInProgress: true };
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
        _waitCampfire = null;
        _gather = null;
        CampfireFastTravelContext.Reset();
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

        // what runs once everyone arrived and the rules pass: the rest screen's Start, or the wait and rest
        internal System.Action Arrived;

        // the walk comes before a wait, so its last check leaves out the once-a-day rule
        internal bool BeforeWait;
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
