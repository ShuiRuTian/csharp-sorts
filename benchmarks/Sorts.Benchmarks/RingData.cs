using System;

namespace Sorts.Benchmarks;

/// <summary>Fresh-data ring for benchmarking destructive in-place sorts (the standard
/// BenchmarkDotNet recipe — NO IterationSetup).
///
/// In-place sorts destroy their input, and IterationSetup would run only once per
/// *iteration* (i.e. many invocations), so later invocations would re-sort already
/// sorted arrays — poison for adaptive sorts. Instead GlobalSetup pre-generates
/// <c>InvocationCount</c> unsorted clones of the template into this ring, and every
/// invocation sorts <c>Next()</c>, the current ring slot.
///
/// Fresh-data invariant math (InvocationCount = OperationsPerInvoke = RingSize = 64):
/// one benchmark iteration is exactly 64 invocations; the cursor advances by one per
/// invocation, so the ring wraps ONLY at a full batch boundary and each clone is
/// sorted exactly once per 64-invocation batch. When the cursor wraps, every slot is
/// re-copied from the immutable template, so EVERY invocation sorts unsorted data —
/// not just the first batch. Re-copy cost is 64 plain Array.Copy's per 64 sorts
/// (memory bandwidth vs. O(n log n) comparisons), well under a few percent of the
/// batch, and identical for every method being compared.
///
/// Clones are shallow for reference-element arrays (string[]): sorting never mutates
/// the elements themselves, only swaps references, and strings are immutable, so
/// restoring the reference sequence via Array.Copy fully restores unsortedness.
/// Pair/Struct16/Struct128 are value structs, so the same holds trivially.</summary>
internal sealed class RingData<T>
{
    private readonly T[] _template;
    private readonly T[][] _slots;
    private int _cursor;

    public RingData(T[] template, int size)
    {
        _template = template;
        _slots = new T[size][];
        for (int i = 0; i < size; i++)
            _slots[i] = (T[])template.Clone();
        _cursor = 0;
    }

    /// <summary>Returns the next unsorted clone; re-copies all slots from the template
    /// when the cursor wraps (batch boundary) so the next batch is fresh again.</summary>
    public T[] Next()
    {
        T[] slot = _slots[_cursor];
        _cursor++;
        if (_cursor == _slots.Length)
        {
            _cursor = 0;
            for (int i = 0; i < _slots.Length; i++)
                Array.Copy(_template, _slots[i], _template.Length);
        }
        return slot;
    }
}
