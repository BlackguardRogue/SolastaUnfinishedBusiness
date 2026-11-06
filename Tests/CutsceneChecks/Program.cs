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
    caerSeats.All(seat => Along(s, caer.Forward) <= Along(seat, caer.Forward) - 1f)));
Check("hero 5 is behind the middle of the row (z about 8.5)", Math.Abs(caer.Spots[0].Z - 8.5f) < 0.15f);
Check("hero 6 is past seat 2's end of the row (z above 9.2)", caer.Spots[1].Z > 9.2f);
Check("heroes 5 and 6 are at least 1 m apart (they were 0.5 m apart)", Flat(caer.Spots[0], caer.Spots[1]) >= 1f);
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

// more heroes than the 6 of a usual big party: all the spots stay apart
var many = CutsceneFormation.Compute(caerSeats, caerForwards, 7);
var smallestGap = many.Spots
    .SelectMany((a, i) => many.Spots.Skip(i + 1).Select(b => Flat(a, b)))
    .Min();

Check("7 spots are all apart", smallestGap >= CutsceneFormation.SideSpacing - 0.01f);
Check("spots go out to both ends", many.Spots.Any(s => s.Z > 9.2f) && many.Spots.Any(s => s.Z < 7.8f));

// a single seat: the spots still don't stack
var single = CutsceneFormation.Compute([new Point(0f, 0f, 0f)], [new Point(0f, 0f, 1f)], 3);

Check("one seat: the 3 spots are apart",
    Flat(single.Spots[0], single.Spots[1]) >= CutsceneFormation.SideSpacing - 0.01f &&
    Flat(single.Spots[0], single.Spots[2]) >= CutsceneFormation.SideSpacing - 0.01f);

// opposite-facing markers have no average facing, which must still give a usable direction
var opposite = CutsceneFormation.Compute(
    [new Point(0f, 0f, 0f), new Point(1f, 0f, 0f)], [new Point(0f, 0f, 1f), new Point(0f, 0f, -1f)], 1);

Check("opposite-facing seats still give a direction",
    Math.Abs((opposite.Forward.X * opposite.Forward.X) + (opposite.Forward.Z * opposite.Forward.Z) - 1f) < 0.01f);

Console.WriteLine(failures == 0 ? "All passed." : $"{failures} FAILED.");

return failures == 0 ? 0 : 1;
