// Ported from https://github.com/Voultapher/sort-research-rs (ipnsort), MIT OR Apache-2.0, by Lukas Bergdoll.
// C# port 2026 — architecture-faithful, C# performance idioms.
using System;
using System.Collections.Generic;

namespace Sorts;

/// <summary>Ipnsort — efficient, generic and robust unstable in-place sort.</summary>
public static class Ipnsort
{
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
        => SortSpan<T, ComparerAdapter<T, TC>>(span, scratch: default, new ComparerAdapter<T, TC>(cmp));

    /// <summary>THE kernel — in-place, scratch unused by design (upstream allocates
    /// nothing). unstable_sort (lib.rs:114-146): zero-length and singleton slices
    /// return; up to MAX_LEN_ALWAYS_INSERTION_SORT (20) insertion sort wins on
    /// i-cache footprint in general-purpose code; above that, ipnsort.</summary>
    internal static void SortSpan<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        // More advanced sorting methods than insertion sort are faster if called in
        // a hot loop for small inputs, but for general-purpose code the small binary
        // size of insertion sort is more important (lib.rs:124-133).
        const int MaxLenAlwaysInsertionSort = 20;
        int len = v.Length;
        if (len < 2)
            return;

        if (len <= MaxLenAlwaysInsertionSort)
        {
            SmallSortPrimitives.InsertionSortShiftLeft(v, cmp, 1);
            return;
        }

        IpnImpl.Sort(v, cmp);
    }
}
