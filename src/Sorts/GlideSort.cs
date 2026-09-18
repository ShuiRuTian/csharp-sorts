// Ported from https://github.com/orlp/glidesort, MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — architecture-faithful, C# performance idioms.
using System;
using System.Collections.Generic;

namespace Sorts;

/// <summary>GlideSort — robust generic stable adaptive sort. The kernel (the SortSpan
/// implementation) lands in a later task; until then every Sort overload that reaches
/// the kernel throws NotImplementedException.</summary>
public static class GlideSort
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
        => throw new NotImplementedException(); // TODO(task-6/10/13): wire adapter

    /// <summary>THE kernel each algorithm task implements.</summary>
    internal static void SortSpan<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
        => throw new NotImplementedException();
}
