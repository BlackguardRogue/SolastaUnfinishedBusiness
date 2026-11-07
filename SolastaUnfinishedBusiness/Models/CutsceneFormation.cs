using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

// Where heroes without an authored cutscene seat stand: a row one step behind the seated heroes, staggered so they show
// between them. Plain arithmetic with no game or Unity types, so Tests/CutsceneChecks can run it outside the game. It uses
// only its inputs, which come from the scene, so every computer gets the same answer.
internal static class CutsceneFormation
{
    // how far the back row stands behind the rearmost seat. A hero at this distance behind the row's middle gap can be seen
    // in the Caer Cyflen party shot (evidence/cutscene-placement-8fa1ae67)
    internal const float BackRowDistance = 1.2f;

    // How far past the end of the seated row a hero stands, and the step between heroes beyond it. There is no smallest
    // distance between back-row heroes (user direction, 2026-11-07, "remove the minimum spacing between rear heroes"): the
    // 0.8 m floor it replaced pushed the second rear hero past the 0.5 m gaps to directly behind a tall flank hero, hidden in
    // party shots (evidence/cutscene-staging-79619edc).
    internal const float SideMargin = 0.4f;
    internal const float PastEndStep = 0.8f;

    // how near a camera's ray has to pass a marker, and how far along it, for the shot to count as framing that marker
    private const float FramedPointTolerance = 0.3f;
    private const float FramedPointReach = 6f;

    internal readonly struct Point(float x, float y, float z)
    {
        internal readonly float X = x;
        internal readonly float Y = y;
        internal readonly float Z = z;
    }

    internal readonly struct Camera(Point position, Point forward)
    {
        internal readonly Point Position = position;
        internal readonly Point Forward = forward;
    }

    internal sealed class Result(Point forward, Point right, Point[] spots)
    {
        // the way the party faces, flat on the ground and of length 1
        internal Point Forward { get; } = forward;

        // along the row, of length 1, used to look for another cell when a spot is taken
        internal Point Right { get; } = right;

        internal Point[] Spots { get; } = spots;
    }

    // Spots for spotCount heroes given the seats they stand behind, those seats' forward directions, the scene's front-facing
    // party cameras (the answer prompt's PartyChoice shots) and the shortest seated hero. Everything is measured from the
    // seats given, so the row turns with the party at each whole-party teleport.
    //
    // Each rear hero stands behind a different pair of neighbouring front heroes, seen through their gap from the front
    // (user direction, 2026-11-07: rear heroes "properly standing in the gaps" as the front camera shows them, and other
    // shots' clearance must not move them; overlaps with other cameras are a separate, later issue). Which gaps follows the
    // user's sketch of the same day (evidence/cutscene-staging-e8e1a944/user-sketch-outer-gaps.jpg): with 2 rear heroes and
    // 4 seats, one behind each outer gap (seats 1-2 and 3-4 counted along the row), the middle gap left empty. One rear hero
    // stands behind the middle gap; 3 take all three gaps; more go past the ends of the row. With 2 seats, so a single gap,
    // the second goes past the end on the side away from Right.
    //
    // "Behind the gap" is as the front camera sees it. A camera close to the row, like the Nikannen scene's PartyChoice 1.66 m
    // in front of its second-stage row, sees a hero straight behind a gap's midpoint shifted towards the middle; behind
    // 8c426790's places past the row's ends it saw the flank heroes instead (evidence/cutscene-staging-8c426790). So each
    // hero stands on the line from that camera through the gap's midpoint at the front row. A far camera makes that line
    // nearly straight back, so the place hardly moves; with no front camera it is straight back.
    internal static Result Compute(
        IReadOnlyList<Point> seats,
        IReadOnlyList<Point> seatForwards,
        int spotCount,
        IReadOnlyList<Camera> frontCameras = null)
    {
        if (seats.Count == 0)
        {
            throw new ArgumentException("A formation needs at least one seat.", nameof(seats));
        }

        var forward = AverageFlatDirection(seatForwards);
        var right = new Point(forward.Z, 0f, -forward.X);
        var laterals = seats.Select(seat => (seat.X * right.X) + (seat.Z * right.Z)).OrderBy(x => x).ToList();
        var depths = seats.Select(seat => (seat.X * forward.X) + (seat.Z * forward.Z)).ToList();
        var rearmost = depths.Min();
        var rowDepth = depths.Average();
        var height = seats.Average(seat => seat.Y);
        var preferred = PreferredLaterals(laterals, spotCount);
        var front = FrontCamera(frontCameras, forward, right, laterals, rowDepth);
        var spotDepth = rearmost - BackRowDistance;
        var spots = new Point[preferred.Count];

        for (var i = 0; i < preferred.Count; i++)
        {
            var lateral = preferred[i];

            // through the camera and the gap's midpoint at the front row's depth, on to the back row's depth
            if (front is var (cameraLateral, cameraDepth))
            {
                lateral = cameraLateral + ((lateral - cameraLateral) * (cameraDepth - spotDepth) / (cameraDepth - rowDepth));
            }

            spots[i] = new Point(
                (right.X * lateral) + (forward.X * spotDepth),
                height,
                (right.Z * lateral) + (forward.Z * spotDepth));
        }

        return new Result(forward, right, spots);
    }

    // The front camera to line the back row up with, as (lateral, depth) in the row's terms, or null: the nearest of the given
    // cameras that stands in front of the row, at least 0.5 m ahead, looking back at it, and not further to the side than the
    // row is wide plus a few metres. The Nikannen scene has one PartyChoice for two stages 12 m apart; at the first stage it
    // is 13 m away, which moves the places by under 0.1 m.
    private static (float Lateral, float Depth)? FrontCamera(
        IReadOnlyList<Camera> cameras, Point forward, Point right, List<float> laterals, float rowDepth)
    {
        (float Lateral, float Depth)? best = null;

        foreach (var camera in cameras ?? [])
        {
            var depth = (camera.Position.X * forward.X) + (camera.Position.Z * forward.Z);
            var lateral = (camera.Position.X * right.X) + (camera.Position.Z * right.Z);
            var looksBack = (camera.Forward.X * forward.X) + (camera.Forward.Z * forward.Z) < 0f;

            if (!looksBack || depth - rowDepth < 0.5f ||
                lateral < laterals[0] - 3f || lateral > laterals[laterals.Count - 1] + 3f)
            {
                continue;
            }

            if (best is not { } current || depth < current.Depth)
            {
                best = (lateral, depth);
            }
        }

        return best;
    }

    // Who stands where, by height: the sketch's short heroes at the front centre and tall ones on the flanks, with the rest in
    // the back row, where a medium height shows over the shoulders of the short ones. heights has one entry per hero, in the
    // party's order, which also settles ties. seatsFromCentre lists the seats from the row's centre outward (SeatsFromCentre).
    // Returns the hero index for each seat, and for each back-row spot from the first. It changes no list in the game: actor
    // order decides roles and shots, so those stay as they were.
    internal static (int[] Seats, int[] Spots) AssignByHeight(IReadOnlyList<int> heights, IReadOnlyList<int> seatsFromCentre)
    {
        var sorted = Enumerable.Range(0, heights.Count).OrderBy(i => heights[i]).ToList();
        var seatCount = seatsFromCentre.Count;
        var innerCount = seatCount / 2;
        var outerCount = seatCount - innerCount;
        var spotCount = sorted.Count - seatCount;
        var seats = new int[seatCount];

        for (var i = 0; i < innerCount; i++)
        {
            seats[seatsFromCentre[i]] = sorted[i];
        }

        for (var i = 0; i < outerCount; i++)
        {
            seats[seatsFromCentre[innerCount + i]] = sorted[innerCount + spotCount + i];
        }

        return (seats, sorted.Skip(innerCount).Take(spotCount).ToArray());
    }

    // the seats' indexes from the row's centre outward; equally central seats keep their order
    internal static int[] SeatsFromCentre(IReadOnlyList<Point> seats, IReadOnlyList<Point> seatForwards)
    {
        var forward = AverageFlatDirection(seatForwards);
        var right = new Point(forward.Z, 0f, -forward.X);
        var laterals = seats.Select(seat => (seat.X * right.X) + (seat.Z * right.Z)).ToList();
        var centre = (laterals.Min() + laterals.Max()) * 0.5f;

        return Enumerable.Range(0, seats.Count).OrderBy(i => Math.Abs(laterals[i] - centre)).ToArray();
    }

    // Which of the points a camera looks at, or -1. Scene cameras' numbers don't say which marker they're for (the Nikannen
    // scene's CharSingle0 frames Char_5), but their authored rays go through it to within centimetres.
    internal static int FindFramedPoint(Point position, Point forward, IReadOnlyList<Point> points)
    {
        var length = (float)Math.Sqrt((forward.X * forward.X) + (forward.Z * forward.Z));
        var best = -1;
        var bestMiss = FramedPointTolerance;

        if (length < 0.001f)
        {
            return best;
        }

        for (var i = 0; i < points.Count; i++)
        {
            var dx = points[i].X - position.X;
            var dz = points[i].Z - position.Z;
            var along = ((dx * forward.X) + (dz * forward.Z)) / length;
            var miss = Math.Abs((dx * forward.Z) - (dz * forward.X)) / length;

            if (along > 0f && along <= FramedPointReach && miss < bestMiss)
            {
                best = i;
                bestMiss = miss;
            }
        }

        return best;
    }

    // positions along the row, at the front row, before the front camera's perspective is applied. laterals is sorted.
    private static List<float> PreferredLaterals(List<float> laterals, int spotCount)
    {
        var centre = (laterals[0] + laterals[laterals.Count - 1]) * 0.5f;
        var gaps = laterals.Zip(laterals.Skip(1), (a, b) => (a + b) * 0.5f).ToList();
        var result = new List<float>();

        if (spotCount == 0)
        {
            return result;
        }

        // a real gap between two heroes, the one nearest the row's centre; a lone seat has none
        var middleIndex = gaps.Count == 0
            ? -1
            : gaps.IndexOf(gaps.OrderBy(gap => Math.Abs(gap - centre)).First());
        var middle = middleIndex < 0 ? centre : gaps[middleIndex];
        var pastEnd = Math.Max(((laterals[laterals.Count - 1] - laterals[0]) * 0.5f) + SideMargin, PastEndStep);

        // the user's sketch: two rear heroes behind the two outer gaps, the middle one left empty
        if (spotCount == 2 && gaps.Count >= 2)
        {
            result.Add(gaps[0]);
            result.Add(gaps[gaps.Count - 1]);

            return result;
        }

        result.Add(middle);

        if (spotCount == 2)
        {
            result.Add(middle - pastEnd);

            return result;
        }

        // walks outward from the middle on each side, through that side's gaps and then past the end of the row
        var low = middle;
        var high = middle;
        var lowGap = middleIndex - 1;
        var highGap = middleIndex + 1;

        for (var i = 1; i < spotCount; i++)
        {
            if (i % 2 == 1)
            {
                low = lowGap >= 0 ? gaps[lowGap] : Math.Min(laterals[0] - SideMargin, low - PastEndStep);
                lowGap--;
                result.Add(low);
            }
            else
            {
                high = highGap >= 0 && highGap < gaps.Count
                    ? gaps[highGap]
                    : Math.Max(laterals[laterals.Count - 1] + SideMargin, high + PastEndStep);
                highGap++;
                result.Add(high);
            }
        }

        return result;
    }


    // the seats' facing can differ (the outer ones turn inward), so the row's facing is their average, kept flat
    private static Point AverageFlatDirection(IReadOnlyList<Point> directions)
    {
        var x = 0f;
        var z = 0f;

        foreach (var direction in directions)
        {
            x += direction.X;
            z += direction.Z;
        }

        var length = (float)Math.Sqrt((x * x) + (z * z));

        // markers facing exactly opposite ways have no average: any direction is as good, and this one is stable
        return length < 0.001f ? new Point(0f, 0f, 1f) : new Point(x / length, 0f, z / length);
    }
}
