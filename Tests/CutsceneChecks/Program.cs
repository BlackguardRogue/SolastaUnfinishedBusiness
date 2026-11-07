// Checks the cutscene back row (CutsceneFormation) on marker layouts logged from the game. Run with:
// dotnet run --project Tests/CutsceneChecks
using System;
using System.Linq;
using SolastaUnfinishedBusiness.Models;
using Point = SolastaUnfinishedBusiness.Models.CutsceneFormation.Point;

var failures = 0;

void Check(string name, bool passed)
{
    Console.WriteLine($"{(passed ? "pass" : "FAIL")}  {name}");
    failures += passed ? 0 : 1;
}

static float Flat(Point a, Point b) =>
    (float)Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

static float Along(Point p, Point direction) => (p.X * direction.X) + (p.Z * direction.Z);

// FactionDialog_Antiquarians_02, Caer Cyflen: the 4 seats face +x
Point[] caerSeats = [new(7.1f, 6f, 8.8f), new(7.0f, 6f, 8.3f), new(7.2f, 6f, 9.2f), new(7.1f, 6f, 7.8f)];
Point[] caerForwards = [new(1f, 0f, 0f), new(1f, 0f, 0f), new(0.8f, 0f, -0.5f), new(0.9f, 0f, 0.4f)];
var caer = CutsceneFormation.Compute(caerSeats, caerForwards, 2);

Check("the party faces the seats' way", caer.Forward.X > 0.99f);
Check("heroes 5 and 6 are behind every seat", caer.Spots.All(s =>
    caerSeats.All(seat => Along(s, caer.Forward) <= Along(seat, caer.Forward) - CutsceneFormation.BackRowDistance + 0.001f)));
// the user's sketch (evidence/cutscene-staging-e8e1a944/user-sketch-outer-gaps.jpg): behind the two outer gaps, z about 9.0 and 8.05
Check("heroes 5 and 6 behind the two outer gaps (z about 9.0 and 8.05)",
    caer.Spots.Any(s => Math.Abs(s.Z - 9.0f) < 0.1f) && caer.Spots.Any(s => Math.Abs(s.Z - 8.05f) < 0.1f));
Check("nobody behind the middle gap (z about 8.55)", caer.Spots.All(s => Math.Abs(s.Z - 8.55f) > 0.3f));
Check("they stand within 3 m of the row's middle", caer.Spots.All(s => Flat(s, new Point(7.1f, 6f, 8.5f)) < 3f));
Check("they stand at the seats' height", caer.Spots.All(s => Math.Abs(s.Y - 6f) < 0.01f));

// Abjuration_01_MeetKythaela: only the first 4 markers are passed in; markers 4-7 are about 54 m away at z -67
Point[] kythaelaSeats =
    [new(-48.9f, -15f, -13.4f), new(-48.9f, -15f, -12.5f), new(-49f, -15f, -13.9f), new(-49f, -15f, -12f)];
Point[] kythaelaForwards =
    [new(-1f, 0f, 0f), new(-1f, 0f, 0f), new(-0.9f, 0f, 0.4f), new(-0.9f, 0f, -0.5f)];
var kythaela = CutsceneFormation.Compute(kythaelaSeats, kythaelaForwards, 2);

Check("Kythaela: both heroes stay with the front group", kythaela.Spots.All(s =>
    kythaelaSeats.All(seat => Flat(s, seat) < 3f)));

// more heroes than the 6 of a usual big party: no two spots on one place, the first 3 at the 3 gaps' midpoints
var many = CutsceneFormation.Compute(caerSeats, caerForwards, 7);
var smallestGap = many.Spots
    .SelectMany((a, i) => many.Spots.Skip(i + 1).Select(b => Flat(a, b)))
    .Min();
// measured along the row as the formation sees it: the seats' average facing is turned a little from +x
var caerLaterals = caerSeats.Select(s => Along(s, many.Right)).OrderBy(x => x).ToArray();
var caerGaps = caerLaterals.Zip(caerLaterals.Skip(1), (a, b) => (a + b) * 0.5f).ToArray();

Check("7 spots are all distinct", smallestGap >= 0.01f);
Check("7 spots: the gaps are used at their midpoints",
    caerGaps.All(g => many.Spots.Take(3).Any(s => Math.Abs(Along(s, many.Right) - g) < 0.001f)));
Check("spots go out to both ends", many.Spots.Any(s => s.Z > 9.2f) && many.Spots.Any(s => s.Z < 7.8f));

// a single seat: the spots still don't stack
var single = CutsceneFormation.Compute([new Point(0f, 0f, 0f)], [new Point(0f, 0f, 1f)], 3);

Check("one seat: the 3 spots are distinct",
    Flat(single.Spots[0], single.Spots[1]) >= 0.01f && Flat(single.Spots[0], single.Spots[2]) >= 0.01f &&
    Flat(single.Spots[1], single.Spots[2]) >= 0.01f);

// opposite-facing markers have no average facing, which must still give a usable direction
var opposite = CutsceneFormation.Compute(
    [new Point(0f, 0f, 0f), new Point(1f, 0f, 0f)], [new Point(0f, 0f, 1f), new Point(0f, 0f, -1f)], 1);

Check("opposite-facing seats still give a direction",
    Math.Abs((opposite.Forward.X * opposite.Forward.X) + (opposite.Forward.Z * opposite.Forward.Z) - 1f) < 0.01f);

// Abjuration_03_MeetNikannen1, from the scene file. The second stage is markers 4-7, facing +z.
Point[] stageSeats = [new(9.25f, 0f, -17.5f), new(9.75f, 0f, -17.5f), new(10.25f, 0f, -17.5f), new(8.75f, 0f, -17.5f)];
Point[] stageForwards = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)];
var stage = CutsceneFormation.Compute(stageSeats, stageForwards, 2);

// the user's sketch, with no camera involved: exactly behind the two outer gaps' midpoints (9.0 and 10.0), middle gap empty.
// 475912d2 put them at 8.64 and 10.36, past the end heroes, by lining them up with the prompt camera.
Console.WriteLine($"      Nikannen second stage: {string.Join(" ", stage.Spots.Select(s => $"({s.X:F2}, {s.Z:F2})"))}");
Check("Nikannen: rear heroes exactly behind the outer gaps (9.0 and 10.0)",
    Math.Abs(stage.Spots[0].X - 9.0f) < 0.001f && Math.Abs(stage.Spots[1].X - 10.0f) < 0.001f);
Check("Nikannen: both inside the row's span, between the end heroes", stage.Spots.All(s => s.X is > 8.75f and < 10.25f));
Check("Nikannen: both at the back-row distance (0.6 m)",
    stage.Spots.All(s => Math.Abs(s.Z - (-17.5f - CutsceneFormation.BackRowDistance)) < 0.001f));

// the first stage's markers are staggered (z -29.3 and -29.4): the back row is measured from the rearmost one
Point[] firstSeats = [new(9.3f, 0f, -29.4f), new(9.9f, 0f, -29.3f), new(8.8f, 0f, -29.3f), new(10.4f, 0f, -29.3f)];
Point[] firstForwards = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0.2f, 0f, 1f), new(-0.2f, 0f, 1f)];
var first = CutsceneFormation.Compute(firstSeats, firstForwards, 2);

Check("first stage: behind the outer gaps (9.05 and 10.15), 0.6 m behind the rearmost marker",
    Math.Abs(first.Spots[0].X - 9.05f) < 0.001f && Math.Abs(first.Spots[1].X - 10.15f) < 0.001f &&
    first.Spots.All(s => Math.Abs(s.Z - (-29.4f - CutsceneFormation.BackRowDistance)) < 0.001f));

// one rear hero: behind the middle gap; two seats (one gap): the second past the end
var single1 = CutsceneFormation.Compute(stageSeats, stageForwards, 1);

Check("one rear hero: behind the middle gap", Math.Abs(single1.Spots[0].X - 9.5f) < 0.001f);

var twoSeats = CutsceneFormation.Compute([stageSeats[0], stageSeats[1]], [stageForwards[0], stageForwards[1]], 2);

Check("two seats: one behind the gap, one past an end",
    Math.Abs(twoSeats.Spots[0].X - 9.5f) < 0.001f && (twoSeats.Spots[1].X < 9.25f || twoSeats.Spots[1].X > 9.75f));

// 4 + 3: the rear heroes stand behind gaps, on both sides of the middle, and turn with the row
var seven = CutsceneFormation.Compute(stageSeats, stageForwards, 3);

Check("4 + 3: one each side of the middle", seven.Spots[1].X < 9.5f && seven.Spots[2].X > 9.5f);
Check("4 + 3: all behind the row", seven.Spots.All(s => Math.Abs(s.Z - (-17.5f - CutsceneFormation.BackRowDistance)) < 0.01f));

Point[] turnedSeats = [new(0f, 0f, 9.25f), new(0f, 0f, 9.75f), new(0f, 0f, 10.25f), new(0f, 0f, 8.75f)];
Point[] turnedForwards = [new(-1f, 0f, 0f), new(-1f, 0f, 0f), new(-1f, 0f, 0f), new(-1f, 0f, 0f)];
var turned = CutsceneFormation.Compute(turnedSeats, turnedForwards, 3);

Check("a row facing another way has its back row behind it", turned.Spots.All(s => Math.Abs(s.X - CutsceneFormation.BackRowDistance) < 0.01f));

// Whole-party height order. Seats from the centre outward: 0, 1, then 2, 3. Six heroes of heights given in party order.
int[] heights = [70, 60, 66, 75, 64, 68];
var seatsFromCentre = CutsceneFormation.SeatsFromCentre(stageSeats, stageForwards);

Check("seats from the centre: the two inner markers first",
    seatsFromCentre.Take(2).OrderBy(x => x).SequenceEqual(new[] { 0, 1 }));

var (seatHeroes, spotHeroes) = CutsceneFormation.AssignByHeight(heights, seatsFromCentre);
var inner = seatsFromCentre.Take(2).Select(s => heights[seatHeroes[s]]).ToList();
var outer = seatsFromCentre.Skip(2).Select(s => heights[seatHeroes[s]]).ToList();

Check("the two shortest stand at the front centre", inner.OrderBy(x => x).SequenceEqual(new[] { 60, 64 }));
Check("the two tallest stand on the flanks", outer.OrderBy(x => x).SequenceEqual(new[] { 70, 75 }));
Check("the medium heights stand behind, shorter first", spotHeroes.Select(h => heights[h]).SequenceEqual(new[] { 66, 68 }));
Check("everyone has one place", seatHeroes.Concat(spotHeroes).OrderBy(x => x).SequenceEqual(Enumerable.Range(0, 6)));

// equal heights keep the party's order, so the result doesn't depend on anything but the party
var (sameSeats, sameSpots) = CutsceneFormation.AssignByHeight([70, 70, 70, 70, 70], seatsFromCentre);

Check("equal heights: the same answer each time, with party order settling it",
    sameSeats[seatsFromCentre[0]] == 0 && sameSpots.SequenceEqual(new[] { 2 }));

// the seats a teleport uses can be in any order: the same heights land in the same places
Point[] reversedSeats = [stageSeats[3], stageSeats[2], stageSeats[1], stageSeats[0]];
var reversedOrder = CutsceneFormation.SeatsFromCentre(reversedSeats, stageForwards);
var (reversedHeroes, _) = CutsceneFormation.AssignByHeight(heights, reversedOrder);
var tallestSeat = reversedSeats[Array.IndexOf(reversedHeroes, 3)];

Check("the tallest hero is on a flank whatever the seats' order", tallestSeat.X is 8.75f or 10.25f);

// Close-ups, from the Nikannen scene's authored cameras and markers. Their numbers don't match the markers.
Point[] nikannenMarkers =
[
    new(9.32f, 0f, -29.35f), new(9.86f, 0f, -29.35f), new(8.82f, 0f, -29.32f), new(10.38f, 0f, -29.32f),
    new(9.25f, 0f, -17.5f), new(9.75f, 0f, -17.5f), new(10.25f, 0f, -17.5f), new(8.75f, 0f, -17.5f)
];

Check("CharSingle1 frames marker 1", CutsceneFormation.FindFramedPoint(
    new Point(9.55f, 0.87f, -27.58f), new Point(0.17f, 0f, -0.98f), nikannenMarkers) == 1);
Check("CharSingle2 frames marker 2", CutsceneFormation.FindFramedPoint(
    new Point(9.07f, 0.87f, -27.54f), new Point(-0.14f, 0f, -0.99f), nikannenMarkers) == 2);
Check("CharSingle3 frames marker 3", CutsceneFormation.FindFramedPoint(
    new Point(11.69f, 0.87f, -28.08f), new Point(-0.73f, 0f, -0.69f), nikannenMarkers) == 3);
Check("CharSingle0 frames marker 5, not marker 0", CutsceneFormation.FindFramedPoint(
    new Point(9.75f, 0.6f, -15.7f), new Point(0f, 0f, -1f), nikannenMarkers) == 5);
Check("a shot looking at no marker frames nothing", CutsceneFormation.FindFramedPoint(
    new Point(9.5f, 0.87f, -17.3f), new Point(0f, 0f, 1f), nikannenMarkers) == -1);

Console.WriteLine(failures == 0 ? "All passed." : $"{failures} FAILED.");

return failures == 0 ? 0 : 1;
