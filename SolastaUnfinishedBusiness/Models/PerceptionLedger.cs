using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

// Completed 10-cell perception checks, by directional (sensor, target) pair, each tagged with the phase it was rolled
// in: 0 outside combat, n in combat round n. Reading it never consumes randomness.
internal sealed class PerceptionLedger
{
    private const long SnapshotVersion = 2;
    private readonly Dictionary<(ulong sensor, ulong target), (int outcome, int phase)> _entries = new();
    private readonly HashSet<(ulong sensor, ulong target)> _resolving = [];

    internal int Count => _entries.Count;

    internal bool TryGet(ulong sensor, ulong target, out int outcome)
    {
        var found = _entries.TryGetValue((sensor, target), out var entry);
        outcome = entry.outcome;
        return found;
    }

    // rolls only if the pair has no result; a roll's notifications can query perception again, never roll a pair twice
    internal bool TryResolve(ulong sensor, ulong target, int phase, Func<int> roll, out int outcome)
    {
        if (TryGet(sensor, target, out outcome))
        {
            return true;
        }

        var key = (sensor, target);

        if (!_resolving.Add(key))
        {
            return false;
        }

        try
        {
            outcome = roll();
            _entries[key] = (outcome, phase);
                return true;
        }
        finally
        {
            _resolving.Remove(key);
        }
    }

    internal void Clear()
    {
        _entries.Clear();
    }

    // the sensor was blinded: its results (rolled, or seen with its senses) no longer hold, so its next checks roll
    // again; with a filter, only the results it matches (blindness ending drops only the failures). True if
    // anything was dropped.
    internal bool ForgetSensor(ulong sensor, Func<int, bool> outcomes = null)
    {
        var keys = _entries
            .Where(x => x.Key.sensor == sensor && (outcomes == null || outcomes(x.Value.outcome)))
            .Select(x => x.Key)
            .ToList();

        keys.ForEach(x => _entries.Remove(x));

        return keys.Count > 0;
    }

    // round 1 of a fight: keeps the checks rolled outside combat, now as round 1 checks, so a later fight (even one
    // that ends in round 1) can't inherit them; drops everything left over from an earlier fight
    internal void CarryIntoRound1()
    {

        foreach (var entry in _entries.ToList())
        {
            if (entry.Value.phase == 0)
            {
                _entries[entry.Key] = (entry.Value.outcome, 1);
            }
            else
            {
                _entries.Remove(entry.Key);
            }
        }
    }

    internal long[] Capture()
    {
        var values = new List<long> { SnapshotVersion };

        foreach (var entry in _entries.OrderBy(x => x.Key.sensor).ThenBy(x => x.Key.target))
        {
            values.Add(unchecked((long)entry.Key.sensor));
            values.Add(unchecked((long)entry.Key.target));
            values.Add(entry.Value.outcome);
            values.Add(entry.Value.phase);
        }

        return values.ToArray();
    }
}
