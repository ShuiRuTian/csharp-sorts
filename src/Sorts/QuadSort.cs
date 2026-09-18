// Ported from https://github.com/scandum/quadsort, public domain / unlicense, by Igor van den Hoven.
// C# port 2026 — architecture-faithful, C# performance idioms.
using System;
using System.Collections.Generic;

namespace Sorts;

/// <summary>QuadSort — stable bottom-up adaptive branchless merge sort. Sizes below 32 are
/// handled by the small-sort kernel (Task 4); larger sizes land in Task 5.</summary>
public static class QuadSort
{
    /// <summary>Per-(T, thread) scratch holder: [ThreadStatic] static T[]? s_smallScratch.</summary>
    private static class ScratchCache<T>
    {
        [ThreadStatic]
        internal static T[]? Buffer;
    }

    /// <summary>Lazily allocated per-thread 64-element scratch for the n &lt; 32 path —
    /// zero steady-state allocation.</summary>
    internal static Span<T> SmallScratch<T>()
    {
        var buf = ScratchCache<T>.Buffer;
        if (buf is null)
            ScratchCache<T>.Buffer = buf = new T[64];
        return buf;
    }

    /// <summary>Sorts the entire array in ascending order using T's CompareTo.</summary>
    public static void Sort<T>(T[] array) where T : IComparable<T> =>
        Sort(array, 0, array is null ? 0 : array.Length);

    /// <summary>Sorts array[index, index + length) in ascending order using T's CompareTo.</summary>
    public static void Sort<T>(T[] array, int index, int length) where T : IComparable<T>
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, array.Length - index);
        if (length < 2) return;
        SortSpan<T, ComparableCmp<T>>(array.AsSpan(index, length), scratch: default, new ComparableCmp<T>());
    }

    /// <summary>Sorts the entire array using the given comparer; a null comparer means Comparer&lt;T&gt;.Default.</summary>
    public static void Sort<T>(T[] array, IComparer<T>? comparer)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Length < 2) return;
        SortSpan<T, InterfaceCmp<T>>(array.AsSpan(), scratch: default, new InterfaceCmp<T>(comparer));
    }

    /// <summary>Sorts the entire array using the given comparison delegate.</summary>
    public static void Sort<T>(T[] array, Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentNullException.ThrowIfNull(comparison);
        if (array.Length < 2) return;
        SortSpan<T, ComparisonCmp<T>>(array.AsSpan(), scratch: default, new ComparisonCmp<T>(comparison));
    }

    /// <summary>Sorts the span using a struct IComparer adapter (JIT-specialized).</summary>
    public static void Sort<T, TC>(Span<T> span, TC cmp) where TC : struct, IComparer<T>
        => throw new NotImplementedException(); // TODO(task-6/10/13): wire adapter

    /// <summary>THE kernel each algorithm task implements. Task 4 scope: n &lt; 32 via the
    /// small-sort machinery (tail_swap/tiny_sort); n ≥ 32 lands in Task 5.</summary>
    internal static void SortSpan<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        if (v.Length < 32)
        {
            QuadsortImpl.TailSwap(v, scratch.Length >= v.Length ? scratch : SmallScratch<T>(), cmp);
            return;
        }
        throw new NotImplementedException(); // TODO(task-5): quad_swap + quad_merge kernel
    }
}
