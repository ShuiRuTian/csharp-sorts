// Ported from https://github.com/orlp/glidesort, MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — architecture-faithful, C# performance idioms.
using System;
using System.Collections.Generic;

namespace Sorts;

/// <summary>GlideSort — robust generic stable adaptive sort (the powersort-driven
/// merge-tree port of orlp/glidesort).</summary>
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
        => SortSpan<T, ComparerAdapter<T, TC>>(span, scratch: default, new ComparerAdapter<T, TC>(cmp));

    /// <summary>THE kernel each algorithm task implements — the full upstream glidesort()
    /// (glidesort.rs:203-265) via GlidesortImpl. Scratch sizing follows glidesort_alloc_size
    /// (lib.rs:64-70): when sorting N elements we allocate a buffer of at most size N, N/2
    /// or N/8 depending on how large the data is, and never less than SMALL_SORT.
    /// Upstream's two allocation-free fast paths (lib.rs:132-139, 272-295) are included:
    /// inputs below SMALL_SORT go straight to small_sort with no scratch and no
    /// merge-stack machinery, and inputs whose scratch fits the small band use the
    /// per-(T, thread) buffer instead of the heap.</summary>
    internal static void SortSpan<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        if (v.Length < 2) return;
        if (v.Length < GlideSmallSort.SmallSort)
        {
            GlideSmallSort.Sort(v, cmp);
            return;
        }
        int alloc = GlidesortAllocSize<T>(v.Length);
        if (scratch.Length < alloc)
        {
            // For small inputs 4KiB of storage suffices, avoiding the
            // (de-)allocator (lib.rs:272-295, glidesort_with_max_stack_scratch).
            // stackalloc is impossible for generic T, so a per-(T, thread)
            // 512-element buffer (DriftSort's ScratchCache pattern) stands in,
            // used when the allocation fits; otherwise allocate on the heap.
            if (alloc <= StackScratchLen)
            {
                var buf = ScratchCache<T>.Buffer;
                if (buf is null)
                {
                    ScratchCache<T>.Buffer = buf = new T[StackScratchLen];
                }
                scratch = buf;
            }
            else
            {
                scratch = GC.AllocateUninitializedArray<T>(alloc);
            }
        }
        GlidesortImpl.Sort(v, scratch, cmp, eagerSmallsort: false);
    }

    private const int StackScratchLen = 512;

    /// <summary>Per-(T, thread) holder standing in for upstream's 4096-byte
    /// AlignedStorage stack scratch (lib.rs:125-145). Same reentrancy caveat as
    /// QuadSort/DriftSort's ThreadStatic buffers: a GlideSort re-entered on the same
    /// thread while a parent call still uses the buffer would alias it — impossible
    /// in the current call graph, and identical to the documented existing policy.</summary>
    private static class ScratchCache<T>
    {
        [ThreadStatic]
        internal static T[]? Buffer;
    }

    // lib.rs:24-27 — when sorting N elements we allocate a buffer of at most size N,
    // N/2 or N/8 depending on how large the data is.
    private const int FullAllocMaxBytes = 1024 * 1024;
    private const int HalfAllocMaxBytes = 1024 * 1024 * 1024;

    /// <summary>glidesort_alloc_size (lib.rs:64-70): full_allowed = min(n, 1MiB/sizeOf),
    /// half_allowed = min(n/2, 1GiB/sizeOf), eighth = n/8, max'ed with SMALL_SORT so the
    /// SMALL_SORT sanity fallback in glidesort() is unreachable.</summary>
    internal static int GlidesortAllocSize<T>(int n)
    {
        int tlen = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        int fullAllowed = Math.Min(n, FullAllocMaxBytes / tlen);
        int halfAllowed = Math.Min(n / 2, HalfAllocMaxBytes / tlen);
        int eighthAllowed = n / 8;
        int m = Math.Max(fullAllowed, halfAllowed);
        return Math.Max(Math.Max(m, eighthAllowed), GlideSmallSort.SmallSort);
    }
}
