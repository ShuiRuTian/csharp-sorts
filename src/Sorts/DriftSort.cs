// Ported from https://github.com/Voultapher/sort-research-rs (driftsort), MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — architecture-faithful, C# performance idioms.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Sorts;

/// <summary>DriftSort — efficient, generic and robust stable sort.</summary>
public static class DriftSort
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

    /// <summary>THE kernel (lib.rs:41-109): tiny inputs take insertion sort directly
    /// (i-cache friendliness); larger inputs compute the scratch allocation policy
    /// max(max(len/2, min(len, 8MB/sizeOf)), MinSmallSortScratchLen), prefer a
    /// per-(T, thread) 512-element buffer standing in for upstream's 4096-byte stack
    /// storage, and enter DriftImpl's powersort main loop in lazy mode.</summary>
    internal static void SortSpan<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        // More advanced sorting methods than insertion sort are faster if called in a
        // hot loop for small inputs, but for general-purpose code the small binary
        // size of insertion sort is more important — any gains from an advanced
        // method are cancelled by i-cache misses during the sort (lib.rs:54-67).
        const int MaxLenAlwaysInsertionSort = 20;
        int len = v.Length;
        if (len <= MaxLenAlwaysInsertionSort)
        {
            DriftSmallSort.InsertionSortShiftLeft(v, cmp);
            return;
        }

        // By allocating n elements of memory we can ensure the entire input can be
        // sorted using stable quicksort; for large inputs we scale down to n / 2 via
        // max(n / 2, min(n, 8MB)), and the small-sort always needs at least
        // MIN_SMALL_SORT_SCRATCH_LEN elements (lib.rs:76-90).
        const int MaxFullAllocBytes = 8_000_000;
        int maxFullAlloc = MaxFullAllocBytes / Unsafe.SizeOf<T>();
        int allocLen = Math.Max(
            Math.Max(len - len / 2, Math.Min(len, maxFullAlloc)),
            DriftSmallSort.MinSmallSortScratchLen);

        // For small inputs 4KiB of storage suffices, avoiding the (de-)allocator
        // (lib.rs:92-102). stackalloc is impossible for generic T, so a per-(T,
        // thread) 512-element buffer (4096 bytes / 8) stands in, used when the
        // allocation fits; otherwise allocate on the heap.
        if (scratch.Length < allocLen)
        {
            if (allocLen <= StackScratchLen)
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
                scratch = GC.AllocateUninitializedArray<T>(allocLen);
            }
        }

        // For small inputs using quicksort is not yet beneficial, and a single
        // small-sort or two small-sorts plus a single merge outperforms it, so use
        // eager mode (lib.rs:104-108).
        DriftImpl.Sort(v, scratch, eagerSort: v.Length <= DriftSmallSort.Threshold<T>() * 2, cmp);
    }

    private const int StackScratchLen = 512;

    /// <summary>Per-(T, thread) holder standing in for upstream's 4096-byte
    /// AlignedStorage stack buffer (lib.rs:125-145).</summary>
    private static class ScratchCache<T>
    {
        [ThreadStatic]
        internal static T[]? Buffer;
    }
}
