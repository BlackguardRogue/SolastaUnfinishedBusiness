using System;
using System.Collections.Generic;

namespace SolastaUnfinishedBusiness.Models;

// Where heroes without an authored cutscene seat stand: a row one step behind the seated heroes. Plain arithmetic with
// no game or Unity types, so Tests/CutsceneChecks can run it outside the game. It uses only its inputs, which come from
// the scene's markers, so every computer gets the same answer.
internal static class CutsceneFormation
{
    // how far the back row stands behind the rearmost seat. A hero at about this distance could be seen through a gap in
    // the front row in the Caer Cyflen party shot
    internal const float BackRowDistance = 1.2f;

    // how far past the end of the seated row the first hero on each side stands, and the step between further heroes
    internal const float SideMargin = 0.4f;
    internal const float SideSpacing = 0.8f;

    internal readonly struct Point(float x, float y, float z)
    {
        internal readonly float X = x;
        internal readonly float Y = y;
        internal readonly float Z = z;
    }

    internal sealed class Result(Point forward, Point right, Point[] spots)
    {
        // the way the party faces, flat on the ground and of length 1
        internal Point Forward { get; } = forward;

        // along the row, of length 1, used to look for another cell when a spot is taken
        internal Point Right { get; } = right;

        internal Point[] Spots { get; } = spots;
    }

    // Spots for spotCount heroes given the seats they stand behind, and those seats' forward directions. The first spot
    // is behind the middle of the row, and the next ones go outward, past each end in turn.
    //
    // The first side is the one away from Right. In every scene logged so far seats 0 and 1 are in the middle and seat 2
    // is at that end, which is the right of the screen in the Caer Cyflen party shot. There the other end of the row is
    // hard up against the edge of the frame, so that side is used last. Cameras are authored per scene for 4 heroes, so
    // no spot can be sure of being in every shot.
    internal static Result Compute(IReadOnlyList<Point> seats, IReadOnlyList<Point> seatForwards, int spotCount)
    {
        if (seats.Count == 0)
        {
            throw new ArgumentException("A formation needs at least one seat.", nameof(seats));
        }

        var forward = AverageFlatDirection(seatForwards);
        var right = new Point(forward.Z, 0f, -forward.X);
        var minLateral = float.MaxValue;
        var maxLateral = float.MinValue;
        var rearmost = float.MaxValue;
        var height = 0f;

        foreach (var seat in seats)
        {
            var lateral = (seat.X * right.X) + (seat.Z * right.Z);
            var depth = (seat.X * forward.X) + (seat.Z * forward.Z);

            minLateral = Math.Min(minLateral, lateral);
            maxLateral = Math.Max(maxLateral, lateral);
            rearmost = Math.Min(rearmost, depth);
            height += seat.Y;
        }

        height /= seats.Count;

        var middle = (minLateral + maxLateral) * 0.5f;
        // never closer to the middle spot than the spacing, however short the seated row is
        var firstSideOffset = Math.Max(((maxLateral - minLateral) * 0.5f) + SideMargin, SideSpacing);
        var depthOfRow = rearmost - BackRowDistance;
        var spots = new Point[spotCount];

        for (var i = 0; i < spotCount; i++)
        {
            var offset = 0f;

            if (i > 0)
            {
                var rank = (i + 1) / 2;
                var side = i % 2 == 1 ? -1f : 1f;

                offset = side * (firstSideOffset + ((rank - 1) * SideSpacing));
            }

            var lateral = middle + offset;

            spots[i] = new Point(
                (right.X * lateral) + (forward.X * depthOfRow),
                height,
                (right.Z * lateral) + (forward.Z * depthOfRow));
        }

        return new Result(forward, right, spots);
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
