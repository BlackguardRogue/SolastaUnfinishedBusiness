using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SolastaUnfinishedBusiness.Models;

// Data attached to objects the mod doesn't own, for as long as those objects live. Use this rather than a
// ConditionalWeakTable: on the game's Boehm runtime that table never lets go of its keys or values (tested on the installed
// mono-2.0-bdwgc.dll: 256 of 256 survived forced collections), so whatever it's given stays in memory for the whole launch.
// Keys are held by weak references, which that runtime does honour, and go by object identity, not Equals.
// A value must not refer to its key, directly or indirectly: that would keep the key alive, and the entry with it.
// Dead entries are dropped as the table grows, so its size stays in proportion to the keys still alive. Main thread only.
internal sealed class WeakKeyTable<TKey, TValue>
    where TKey : class
    where TValue : class
{
    private const int MinimumPurgeCount = 64;

    private readonly Dictionary<int, List<Entry>> _buckets = [];
    private int _count;
    private int _purgeAt = MinimumPurgeCount;

    // entries held, including any whose key has died since the last purge
    internal int Count => _count;

    internal bool TryGetValue(TKey key, out TValue value)
    {
        value = null;

        if (key == null || !_buckets.TryGetValue(RuntimeHelpers.GetHashCode(key), out var bucket))
        {
            return false;
        }

        foreach (var entry in bucket)
        {
            if (entry.Key.TryGetTarget(out var target) && ReferenceEquals(target, key))
            {
                value = entry.Value;

                return true;
            }
        }

        return false;
    }

    // replaces any value the key already has
    internal void Add(TKey key, TValue value)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        Remove(key);

        if (_count >= _purgeAt)
        {
            Purge();
        }

        var hash = RuntimeHelpers.GetHashCode(key);

        if (!_buckets.TryGetValue(hash, out var bucket))
        {
            bucket = new List<Entry>(1);
            _buckets.Add(hash, bucket);
        }

        bucket.Add(new Entry(new WeakReference<TKey>(key), value));
        _count++;
    }

    internal bool Remove(TKey key)
    {
        if (key == null)
        {
            return false;
        }

        var hash = RuntimeHelpers.GetHashCode(key);

        if (!_buckets.TryGetValue(hash, out var bucket))
        {
            return false;
        }

        var index = bucket.FindIndex(x => x.Key.TryGetTarget(out var target) && ReferenceEquals(target, key));

        if (index < 0)
        {
            return false;
        }

        bucket.RemoveAt(index);
        _count--;

        if (bucket.Count == 0)
        {
            _buckets.Remove(hash);
        }

        return true;
    }

    internal void Clear()
    {
        _buckets.Clear();
        _count = 0;
        _purgeAt = MinimumPurgeCount;
    }

    // Drops entries whose key has died. The next purge is at twice what's left, so the work stays in proportion to the adds.
    internal void Purge()
    {
        var emptied = new List<int>();

        foreach (var bucket in _buckets)
        {
            _count -= bucket.Value.RemoveAll(x => !x.Key.TryGetTarget(out _));

            if (bucket.Value.Count == 0)
            {
                emptied.Add(bucket.Key);
            }
        }

        foreach (var hash in emptied)
        {
            _buckets.Remove(hash);
        }

        _purgeAt = Math.Max(MinimumPurgeCount, _count * 2);
    }

    private readonly struct Entry(WeakReference<TKey> key, TValue value)
    {
        internal readonly WeakReference<TKey> Key = key;
        internal readonly TValue Value = value;
    }
}
