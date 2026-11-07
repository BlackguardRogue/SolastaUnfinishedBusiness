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

    // the closest the back row may stand to the front row when a camera needs the room (user direction, 2026-11-07)
    internal const float MinBackRowDistance = 0.3f;

    // how far past the end of the seated row a hero stands, and the smallest distance between two back-row heroes
    internal const float SideMargin = 0.4f;
    internal const float SideSpacing = 0.8f;

    // Scenes are authored for 4 heroes, so some cameras stand where the back row would: in the Nikannen scene GroupMediumB
    // is 1.5 m behind the second-stage row, and a hero 0.33 m from it filled the frame. 23 of 87 spots checked in Coparann's
    // scenes were within this distance of a camera. Only a camera pointing at the spot counts: NpcSingle0 stands in that
    // row looking away from the party, so a hero behind it isn't in its shot. A camera this much above the seats is over the
    // heroes' heads.
    internal const float CameraClearance = 1f;
    internal const float CameraHeightAboveSeats = 2.2f;

    // how a spot in front of a camera moves: closer to the row first, then along it, in these steps
    private const float DepthStep = 0.15f;
    private const float ShiftStep = 0.25f;
    private const int ShiftSteps = 12;

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

    // Spots for spotCount heroes given the seats they stand behind, those seats' forward directions, and the scene's camera
    // positions. Everything is measured from the seats given, so the row turns with the party at each whole-party teleport.
    //
    // The first spot is behind the row's middle gap. With 2 spots the second goes past the end of the row, as the layout
    // tested in game: behind the outer gaps of a row of markers 0.5 m apart, both heroes were hidden (1.6.17.2,
    // evidence/ingame-cutscene-1.6.17.2). With 3 or more they go behind the other gaps, then past the ends: 4 in front and 3
    // behind for 7. Parties stop at 6 today, so that case isn't reached in play.
    //
    // The first side is the one away from Right. In every scene logged so far seat 2 is at that end, which is the right of
    // the screen in the Caer Cyflen party shot, where the other end is hard up against the frame.
    internal static Result Compute(
        IReadOnlyList<Point> seats,
        IReadOnlyList<Point> seatForwards,
        int spotCount,
        IReadOnlyList<Camera> cameras = null)
    {
        if (seats.Count == 0)
        {
            throw new ArgumentException("A formation needs at least one seat.", nameof(seats));
        }

        var forward = AverageFlatDirection(seatForwards);
        var right = new Point(forward.Z, 0f, -forward.X);
        var laterals = seats.Select(seat => (seat.X * right.X) + (seat.Z * right.Z)).OrderBy(x => x).ToList();
        var rearmost = seats.Min(seat => (seat.X * forward.X) + (seat.Z * forward.Z));
        var height = seats.Average(seat => seat.Y);
        var preferred = PreferredLaterals(laterals, spotCount);
        var obstacles = (cameras ?? [])
            .Where(camera => camera.Position.Y - height < CameraHeightAboveSeats)
            .ToList();
        var chosen = new List<Point>();

        Point SpotAt(float lateral, float distanceBehind)
        {
            var depth = rearmost - distanceBehind;

            return new Point(
                (right.X * lateral) + (forward.X * depth),
                height,
                (right.Z * lateral) + (forward.Z * depth));
        }

        bool IsInFrontOfCamera(Point spot, Camera camera)
        {
            return FlatDistance(spot, camera.Position) < CameraClearance &&
                   ((spot.X - camera.Position.X) * camera.Forward.X) +
                   ((spot.Z - camera.Position.Z) * camera.Forward.Z) > 0f;
        }

        bool IsClear(Point spot)
        {
            return !obstacles.Any(camera => IsInFrontOfCamera(spot, camera)) &&
                   chosen.All(other => FlatDistance(spot, other) >= SideSpacing - 0.001f);
        }

        var centre = (laterals[0] + laterals[laterals.Count - 1]) * 0.5f;

        foreach (var lateral in preferred)
        {
            var outward = lateral > centre ? 1f : -1f;

            // The preferred place first, then closer to the row at the same place along it, then further along it at each of
            // those distances, outward first. If nothing is clear the preferred spot stands: no scene is better for losing a
            // hero. Moving closer leaves a camera's view less blocked and keeps the row centred; moving along it takes the
            // hero out of the view altogether.
            var spot = SpotAt(lateral, BackRowDistance);
            var found = false;

            for (var step = 0; step <= ShiftSteps && !found; step++)
            {
                foreach (var shift in step == 0 ? [0f] : new[] { outward * step * ShiftStep, -outward * step * ShiftStep })
                {
                    for (var behind = BackRowDistance; behind >= MinBackRowDistance - 0.001f && !found; behind -= DepthStep)
                    {
                        var candidate = SpotAt(lateral + shift, behind);

                        if (IsClear(candidate))
                        {
                            spot = candidate;
                            found = true;
                        }
                    }

                    if (found)
                    {
                        break;
                    }
                }
            }

            chosen.Add(spot);
        }

        return new Result(forward, right, chosen.ToArray());
    }

    // positions along the row, before keeping clear of cameras. laterals is sorted.
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
        var pastEnd = Math.Max(((laterals[laterals.Count - 1] - laterals[0]) * 0.5f) + SideMargin, SideSpacing);

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
                low = Math.Min(lowGap >= 0 ? gaps[lowGap] : low - SideSpacing, low - SideSpacing);
                lowGap--;
                result.Add(low);
            }
            else
            {
                high = Math.Max(highGap >= 0 && highGap < gaps.Count ? gaps[highGap] : high + SideSpacing, high + SideSpacing);
                highGap++;
                result.Add(high);
            }
        }

        return result;
    }

    private static float FlatDistance(Point a, Point b)
    {
        return (float)Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));
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
