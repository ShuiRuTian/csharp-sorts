// Ported from https://github.com/orlp/driftsort, MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — smallsort.rs: the SmallSortTypeImpl dispatch, the small-sort network
// (sort_small_general) and insertion_sort_shift_left.
//
// Rust's MaybeUninit scratch exists because the sort moves elements through memory the
// borrow checker cannot prove initialized; this port keeps the identical scratch layout
// contract (the sort8 ping-pong regions need len + 17 elements, smallsort.rs:76-78) but
// performs every element move as a plain copy on a real Span<T>, write-before-read by
// construction. The CopyOnDrop guards (smallsort.rs:129-146, 190-194) are panic-recovery
// only — upstream forgets them on the success path — so they have no port: an exception
// from the comparator leaves the buffers in an unspecified state, the same policy
// GlideSmallSort adopted.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>DriftSort small-sort kernel (upstream smallsort.rs): sorts spans of at most
/// Threshold&lt;T&gt;() elements in place, either with the branchless presort + bidirectional
/// merge network (Freeze-like T) or insertion sort.</summary>
internal static class DriftSmallSort
{
    /// <summary>Upstream MIN_SMALL_SORT_SCRATCH_LEN (smallsort.rs:48) — the scratch every
    /// SortSmall call must supply. Upstream computes i32::SMALL_SORT_THRESHOLD + 17 = 49;
    /// the interface pins 50, which still satisfies every internal requirement
    /// (len + 17 for len &lt;= 32).</summary>
    internal const int MinSmallSortScratchLen = 50;

    /// <summary>Upstream SMALL_SORT_THRESHOLD (smallsort.rs:28 default, :54 Freeze impl
    /// — Freeze-only, no size bound): 32 for Freeze-like T, else 16.</summary>
    internal static int Threshold<T>() => SmallSortConfig<T>.IsFreezeLike ? 32 : 16;

    /// <summary>sort_small (smallsort.rs:20-24 dispatch; :30-45 default impl;
    /// :56-63 Freeze impl): sorts v, v.Length &lt;= Threshold&lt;T&gt;(), in place. scratch must
    /// supply at least MinSmallSortScratchLen elements. Upstream aborts on violated
    /// contracts — this seam throws instead.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SortSmall<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        if (v.Length > Threshold<T>())
            ThrowTooLong(v.Length, Threshold<T>());
        if (scratch.Length < MinSmallSortScratchLen)
            ThrowScratchTooSmall(scratch.Length, MinSmallSortScratchLen);

        if (SmallSortConfig<T>.IsFreezeLike)
        {
            SortSmallGeneral(v, scratch, cmp);
        }
        else if (v.Length >= 2)
        {
            // Default impl: plain insertion sort. Upstream needs one scratch element as
            // the insert gap; the local-copy port needs none (scratch guaranteed non-empty
            // by the seam guard above, mirroring upstream's abort at smallsort.rs:36-38).
            InsertionSortShiftLeft(v, cmp, 1);
        }
    }

    /// <summary>sort_small_general (smallsort.rs:66-148): presort both halves of v into
    /// scratch with a stable sort4/sort8 network, extend each to a full half with
    /// insert_tail, then bidirectional_merge scratch back into v.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // upstream #[inline(never)] (smallsort.rs:56)
    private static void SortSmallGeneral<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (len < 2)
            return;
        if (scratch.Length < len + 17)
            ThrowScratchTooSmall(scratch.Length, len + 17); // upstream abort (smallsort.rs:76-78)

        ref T vBase = ref MemoryMarshal.GetReference(v);
        ref T scratchBase = ref MemoryMarshal.GetReference(scratch);
        int lenDiv2 = len / 2;

        int presortedLen;
        if (Unsafe.SizeOf<T>() <= 16 && len >= 16)
        {
            // First half: v[0..8] into scratch[0..8], ping-ponging through scratch[len..].
            Sort8Stable(ref vBase, ref scratchBase, ref Unsafe.Add(ref scratchBase, len), cmp);
            // Second half: v[lenDiv2..lenDiv2+8] into scratch[lenDiv2..lenDiv2+8],
            // ping-ponging through scratch[len+8..len+16].
            Sort8Stable(
                ref Unsafe.Add(ref vBase, lenDiv2),
                ref Unsafe.Add(ref scratchBase, lenDiv2),
                ref Unsafe.Add(ref scratchBase, len + 8), cmp);
            presortedLen = 8;
        }
        else if (len >= 8)
        {
            Sort4Stable(ref vBase, ref scratchBase, cmp);
            Sort4Stable(ref Unsafe.Add(ref vBase, lenDiv2), ref Unsafe.Add(ref scratchBase, lenDiv2), cmp);
            presortedLen = 4;
        }
        else
        {
            scratchBase = vBase;
            Unsafe.Add(ref scratchBase, lenDiv2) = Unsafe.Add(ref vBase, lenDiv2);
            presortedLen = 1;
        }

        // Extend each presorted half to its full length in scratch (smallsort.rs:112-127).
        for (int half = 0; half < 2; half++)
        {
            int offset = half == 0 ? 0 : lenDiv2;
            ref T src = ref Unsafe.Add(ref vBase, offset);
            ref T dst = ref Unsafe.Add(ref scratchBase, offset);
            int desiredLen = half == 0 ? lenDiv2 : len - lenDiv2;
            for (int i = presortedLen; i < desiredLen; i++)
            {
                Unsafe.Add(ref dst, i) = Unsafe.Add(ref src, i);
                InsertTail(ref dst, i, cmp);
            }
        }

        // Both halves of scratch are sorted: merge them back into v. Upstream wraps this
        // in CopyOnDrop for panic recovery and forgets it on success (smallsort.rs:129-146).
        BidirectionalMerge(ref scratchBase, len, ref vBase, cmp);
    }

    /// <summary>insertion_sort_shift_left (smallsort.rs:217-246): sort v assuming
    /// v[..start] is already sorted. Upstream aborts when start == 0 or start &gt; v.Length
    /// (smallsort.rs:224-226) — this seam throws. Upstream's scratch element becomes a
    /// local copy inside InsertTail.</summary>
    internal static void InsertionSortShiftLeft<T, TC>(Span<T> v, TC cmp, int start = 1) where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (start == 0 || start > len)
            ThrowStartContract(start, len);
        // Upstream writes the loop with raw pointers because LLVM likes to unroll a for
        // loop here, which it does not want; a plain C# for loop has no such behavior.
        ref T vBase = ref MemoryMarshal.GetReference(v);
        for (int tail = start; tail < len; tail++)
            InsertTail(ref vBase, tail, cmp);
    }

    /// <summary>insert_tail (smallsort.rs:170-214): sorts range [0, tailIdx] of dstBase
    /// assuming [0, tailIdx) is already sorted, through a gap that walks left. Upstream
    /// parks the tail element in scratch_tmp and lets a CopyOnDrop guard place it into the
    /// gap on scope exit; the port saves it in a local and writes it after the loop — the
    /// identical element movement.</summary>
    private static void InsertTail<T, TC>(ref T dstBase, int tailIdx, TC cmp) where TC : struct, IIsLess<T>
    {
        int sift = tailIdx - 1;
        if (!cmp.IsLess(in Unsafe.Add(ref dstBase, tailIdx), in Unsafe.Add(ref dstBase, sift)))
            return;

        T tmp = Unsafe.Add(ref dstBase, tailIdx);
        int gap = tailIdx;
        while (true)
        {
            Unsafe.Add(ref dstBase, gap) = Unsafe.Add(ref dstBase, sift);
            gap = sift;
            if (sift == 0)
                break;
            sift--;
            if (!cmp.IsLess(in tmp, in Unsafe.Add(ref dstBase, sift)))
                break;
        }
        // gap_guard drop: place the saved element into the remaining gap.
        Unsafe.Add(ref dstBase, gap) = tmp;
    }

    /// <summary>sort4_stable (smallsort.rs:250-305): optimal 5-comparison stable network
    /// sorting vBase[0..4] into dst[0..4]; every element is copied exactly once. The
    /// pointer select (smallsort.rs:298-304) becomes a conditional ref expression, which
    /// compiles to cmov.</summary>
    private static void Sort4Stable<T, TC>(ref T vBase, ref T dst, TC cmp) where TC : struct, IIsLess<T>
    {
        // Stably create two pairs a <= b and c <= d.
        int c1 = cmp.IsLess(in Unsafe.Add(ref vBase, 1), in vBase) ? 1 : 0;
        int c2 = cmp.IsLess(in Unsafe.Add(ref vBase, 3), in Unsafe.Add(ref vBase, 2)) ? 1 : 0;
        ref T a = ref Unsafe.Add(ref vBase, c1);
        ref T b = ref Unsafe.Add(ref vBase, c1 ^ 1);
        ref T c = ref Unsafe.Add(ref vBase, 2 + c2);
        ref T d = ref Unsafe.Add(ref vBase, 2 + (c2 ^ 1));

        // Compare (a, c) and (b, d) to identify max/min. We're left with two
        // unknown elements, but because we are a stable sort we must know which
        // one is leftmost and which one is rightmost.
        // c3, c4 | min max unk_left unk_right
        //  0,  0 |  a   d    b         c
        //  0,  1 |  a   b    c         d
        //  1,  0 |  c   d    a         b
        //  1,  1 |  c   b    a         d
        bool c3 = cmp.IsLess(in c, in a);
        bool c4 = cmp.IsLess(in d, in b);
        ref T min = ref (c3 ? ref c : ref a);
        ref T max = ref (c4 ? ref b : ref d);
        ref T unkLeft = ref (c3 ? ref a : ref (c4 ? ref c : ref b));
        ref T unkRight = ref (c4 ? ref d : ref (c3 ? ref b : ref c));

        // Sort the last two unknown elements.
        bool c5 = cmp.IsLess(in unkRight, in unkLeft);
        ref T lo = ref (c5 ? ref unkRight : ref unkLeft);
        ref T hi = ref (c5 ? ref unkLeft : ref unkRight);

        dst = min;
        Unsafe.Add(ref dst, 1) = lo;
        Unsafe.Add(ref dst, 2) = hi;
        Unsafe.Add(ref dst, 3) = max;
    }

    /// <summary>sort8_stable (smallsort.rs:310-327): sorts vBase[0..8] into dst[0..8] via
    /// two sort4_stable networks into scratchBase[0..8], then a bidirectional merge.</summary>
    private static void Sort8Stable<T, TC>(ref T vBase, ref T dst, ref T scratchBase, TC cmp) where TC : struct, IIsLess<T>
    {
        Sort4Stable(ref vBase, ref scratchBase, cmp);
        Sort4Stable(ref Unsafe.Add(ref vBase, 4), ref Unsafe.Add(ref scratchBase, 4), cmp);
        BidirectionalMerge(ref scratchBase, 8, ref dst, cmp);
    }

    /// <summary>merge_up (smallsort.rs:329-360): branchless single-element merge step —
    /// the lesser of src[left]/src[right] (ties left) goes to dst[outPos], exactly one of
    /// the two read cursors advances.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SmallMergeUp<T, TC>(ref T src, ref T dst, ref int left, ref int right, ref int outPos, TC cmp)
        where TC : struct, IIsLess<T>
    {
        bool isL = !cmp.IsLess(in Unsafe.Add(ref src, right), in Unsafe.Add(ref src, left));
        ref T pick = ref (isL ? ref Unsafe.Add(ref src, left) : ref Unsafe.Add(ref src, right));
        Unsafe.Add(ref dst, outPos) = pick;
        right += isL ? 0 : 1;
        left += isL ? 1 : 0;
        outPos++;
    }

    /// <summary>merge_down (smallsort.rs:362-393): the mirrored step at the back — the
    /// greater of src[leftRev]/src[rightRev] (ties right) goes to dst[outRev], exactly one
    /// of the two read cursors retreats.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SmallMergeDown<T, TC>(ref T src, ref T dst, ref int leftRev, ref int rightRev, ref int outRev, TC cmp)
        where TC : struct, IIsLess<T>
    {
        bool isL = !cmp.IsLess(in Unsafe.Add(ref src, rightRev), in Unsafe.Add(ref src, leftRev));
        ref T pick = ref (isL ? ref Unsafe.Add(ref src, rightRev) : ref Unsafe.Add(ref src, leftRev));
        Unsafe.Add(ref dst, outRev) = pick;
        rightRev -= isL ? 1 : 0;
        leftRev -= isL ? 0 : 1;
        outRev--;
    }

    /// <summary>bidirectional_merge (smallsort.rs:407-484): merges the sorted halves
    /// src[0..len/2] and src[len/2..len] into dst[0..len], one element per end per
    /// iteration (2 writes instead of quadsort's 4). len must be >= 2 — every caller
    /// (sort_small_general len >= 2, sort8_stable len 8) guarantees it, mirroring
    /// upstream's assume(len_div_2 != 0). Upstream's wrapping one-past-start pointers
    /// become plain int offsets, in-bounds at every read for any comparator outcome.
    /// T must be Freeze-like (see SmallSortConfig) — the comparator may observe outdated
    /// temporary copies that never reach the final array.</summary>
    private static void BidirectionalMerge<T, TC>(ref T src, int len, ref T dst, TC cmp) where TC : struct, IIsLess<T>
    {
        int lenDiv2 = len / 2;
        int left = 0, right = lenDiv2, outPos = 0;
        int leftRev = lenDiv2 - 1, rightRev = len - 1, outRev = len - 1;

        for (int i = 0; i < lenDiv2; i++)
        {
            SmallMergeUp(ref src, ref dst, ref left, ref right, ref outPos, cmp);
            SmallMergeDown(ref src, ref dst, ref leftRev, ref rightRev, ref outRev, cmp);
        }

        int leftEnd = leftRev + 1;
        int rightEnd = rightRev + 1;

        // Odd length, so one element is left unconsumed in the input.
        if (len % 2 != 0)
        {
            bool leftNonempty = left < leftEnd;
            ref T lastSrc = ref (leftNonempty ? ref Unsafe.Add(ref src, left) : ref Unsafe.Add(ref src, right));
            Unsafe.Add(ref dst, outPos) = lastSrc;
            left += leftNonempty ? 1 : 0;
            right += leftNonempty ? 0 : 1;
        }

        // We now should have consumed the full input exactly once; only a comparison
        // operator that fails to be Ord can violate this (smallsort.rs:480-483).
        if (left != leftEnd || right != rightEnd)
            ThrowOrdViolation();
    }

    /// <summary>sort_small contract violations — upstream aborts (smallsort.rs:36-38,
    /// 76-78; 224-226), this seam throws.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooLong(int len, int threshold) =>
        throw new ArgumentException(
            $"v.Length ({len}) violates the small-sort contract: must be <= Threshold<T>() ({threshold}).");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowScratchTooSmall(int scratchLen, int needed) =>
        throw new ArgumentException(
            $"scratch.Length ({scratchLen}) violates the small-sort contract: at least {needed} elements are required.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStartContract(int start, int len) =>
        throw new ArgumentException(
            $"start ({start}) violates the insertion-sort contract: must be in [1, {len}].");

    /// <summary>panic_on_ord_violation (smallsort.rs:486-489, #[inline(never)]).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOrdViolation() =>
        throw new InvalidOperationException("Ord violation");
}

/// <summary>SmallSortTypeImpl type dispatch (smallsort.rs:16-64): Freeze types take the
/// optimized network with SMALL_SORT_THRESHOLD = 32, everything else insertion sort with
/// threshold 16. Upstream's dispatch is Freeze-only with no size bound (smallsort.rs:50
/// `impl&lt;T: crate::Freeze&gt;`). Rust's Freeze auto-trait (lib.rs:153-160 — no interior
/// mutability) INCLUDES String/&amp;T/Box, so upstream runs the network for managed
/// element types; a copied C# reference aliases the same object, so the network's
/// compares-on-copies are equally hazard-free here. Managed types therefore take the
/// network too; only value types CONTAINING managed references stay on insertion sort
/// (conservative — upstream has no such types to check against). The const size_of
/// branch inside sort_small_general (smallsort.rs:88) only selects the sort8 vs
/// sort4 presort within the network — it is not part of the dispatch.</summary>
internal static class SmallSortConfig<T>
{
    /// <summary>Whether T qualifies for sort_small_general; otherwise SortSmall falls
    /// back to insertion_sort_shift_left.</summary>
    internal static readonly bool IsFreezeLike =
        !typeof(T).IsValueType || !RuntimeHelpers.IsReferenceOrContainsReferences<T>();
}
