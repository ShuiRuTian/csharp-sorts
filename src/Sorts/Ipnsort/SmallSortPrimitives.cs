// Ported from https://github.com/Voultapher/sort-research-rs (ipnsort src/smallsort.rs),
// MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll. C# port 2026 — the small-sort
// primitives upstream ships identically in driftsort's smallsort.rs and ipnsort's:
// insertion_sort_shift_left, insert_tail, sort4_stable, sort8_stable and
// bidirectional_merge (merge_up/merge_down included), plus the Freeze dispatch config.
//
// Rust's MaybeUninit scratch exists because the sort moves elements through memory the
// borrow checker cannot prove initialized; this port performs every element move as a
// plain copy on a real Span<T>, write-before-read by construction. The CopyOnDrop
// guards are panic-recovery only — upstream forgets them on the success path — so they
// have no port: an exception from the comparator leaves the buffers in an unspecified
// state.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>Freeze dispatch config (smallsort.rs:16-64): Freeze types take the
/// optimized network paths, everything else insertion sort. Upstream's dispatch is
/// Freeze-only with no size bound (smallsort.rs:50 `impl&lt;T: crate::Freeze&gt;`).
/// Rust's Freeze auto-trait (no interior mutability) INCLUDES String/&amp;T/Box, so
/// upstream runs the networks for managed element types; a copied C# reference aliases
/// the same object, so compares-on-copies are equally hazard-free here. Managed types
/// therefore qualify too; only value types CONTAINING managed references are excluded
/// (conservative — upstream has no such types to check against).</summary>
internal static class SmallSortConfig<T>
{
    /// <summary>Whether T qualifies for the network small-sort paths; otherwise
    /// callers fall back to insertion_sort_shift_left.</summary>
    internal static readonly bool IsFreezeLike =
        !typeof(T).IsValueType || !RuntimeHelpers.IsReferenceOrContainsReferences<T>();
}

/// <summary>The ipnsort small-sort primitives: insertion sort, the sort4/sort8 stable
/// networks, and the bidirectional merge that combines them. Upstream ships these
/// byte-for-byte in both driftsort's and ipnsort's smallsort.rs.</summary>
internal static class SmallSortPrimitives
{
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
        for (nint tail = start; tail < len; tail++)
            InsertTail(ref vBase, tail, cmp);
    }

    /// <summary>insert_tail (smallsort.rs:170-214): sorts range [0, tailIdx] of dstBase
    /// assuming [0, tailIdx) is already sorted, through a gap that walks left. Upstream
    /// parks the tail element in scratch_tmp and lets a CopyOnDrop guard place it into the
    /// gap on scope exit; the port saves it in a local and writes it after the loop — the
    /// identical element movement. The gap cursors are nint: they feed Unsafe.Add and are
    /// updated inside the shift loop, the x64 sign-extension case of
    /// IpnPartition.PartitionLomutoBranchlessCyclic (JitDisasm: no movsxd/cdqe remain).</summary>
    internal static void InsertTail<T, TC>(ref T dstBase, nint tailIdx, TC cmp) where TC : struct, IIsLess<T>
    {
        nint sift = tailIdx - 1;
        if (!cmp.IsLess(in Unsafe.Add(ref dstBase, tailIdx), in Unsafe.Add(ref dstBase, sift)))
            return;

        T tmp = Unsafe.Add(ref dstBase, tailIdx);
        nint gap = tailIdx;
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
    /// sorting vBase[0..4] into dst[0..4]; every element is copied exactly once.
    /// Small T (JIT-constant branch, folded per instantiation): the pointer selects
    /// of upstream become VALUE ternaries, which the JIT if-converts to csel/cmov —
    /// the pointer-select shape itself would be a data-dependent branch. Large T:
    /// the original conditional-ref selects (branchy, but avoids duplicating large
    /// copies through value selects).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Sort4Stable<T, TC>(ref T vBase, ref T dst, TC cmp) where TC : struct, IIsLess<T>
    {
        if (Unsafe.SizeOf<T>() <= 16)
        {
            // Stably create two pairs a <= b and c <= d.
            int c1 = cmp.IsLess(in Unsafe.Add(ref vBase, 1), in vBase) ? 1 : 0;
            int c2 = cmp.IsLess(in Unsafe.Add(ref vBase, 3), in Unsafe.Add(ref vBase, 2)) ? 1 : 0;
            T a = Unsafe.Add(ref vBase, c1);
            T b = Unsafe.Add(ref vBase, c1 ^ 1);
            T c = Unsafe.Add(ref vBase, 2 + c2);
            T d = Unsafe.Add(ref vBase, 2 + (c2 ^ 1));

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
            T min = c3 ? c : a;
            T max = c4 ? b : d;
            T unkLeft = c3 ? a : (c4 ? c : b);
            T unkRight = c4 ? d : (c3 ? b : c);

            // Sort the last two unknown elements.
            bool c5 = cmp.IsLess(in unkRight, in unkLeft);
            T lo = c5 ? unkRight : unkLeft;
            T hi = c5 ? unkLeft : unkRight;

            dst = min;
            Unsafe.Add(ref dst, 1) = lo;
            Unsafe.Add(ref dst, 2) = hi;
            Unsafe.Add(ref dst, 3) = max;
        }
        else
        {
            // Stably create two pairs a <= b and c <= d.
            int c1 = cmp.IsLess(in Unsafe.Add(ref vBase, 1), in vBase) ? 1 : 0;
            int c2 = cmp.IsLess(in Unsafe.Add(ref vBase, 3), in Unsafe.Add(ref vBase, 2)) ? 1 : 0;
            ref T a = ref Unsafe.Add(ref vBase, c1);
            ref T b = ref Unsafe.Add(ref vBase, c1 ^ 1);
            ref T c = ref Unsafe.Add(ref vBase, 2 + c2);
            ref T d = ref Unsafe.Add(ref vBase, 2 + (c2 ^ 1));

            bool c3 = cmp.IsLess(in c, in a);
            bool c4 = cmp.IsLess(in d, in b);
            ref T min = ref (c3 ? ref c : ref a);
            ref T max = ref (c4 ? ref b : ref d);
            ref T unkLeft = ref (c3 ? ref a : ref (c4 ? ref c : ref b));
            ref T unkRight = ref (c4 ? ref d : ref (c3 ? ref b : ref c));

            bool c5 = cmp.IsLess(in unkRight, in unkLeft);
            ref T lo = ref (c5 ? ref unkRight : ref unkLeft);
            ref T hi = ref (c5 ? ref unkLeft : ref unkRight);

            dst = min;
            Unsafe.Add(ref dst, 1) = lo;
            Unsafe.Add(ref dst, 2) = hi;
            Unsafe.Add(ref dst, 3) = max;
        }
    }

    /// <summary>sort8_stable (smallsort.rs:310-327): sorts vBase[0..8] into dst[0..8] via
    /// two sort4_stable networks into scratchBase[0..8], then a bidirectional merge.</summary>
    internal static void Sort8Stable<T, TC>(ref T vBase, ref T dst, ref T scratchBase, TC cmp) where TC : struct, IIsLess<T>
    {
        Sort4Stable(ref vBase, ref scratchBase, cmp);
        Sort4Stable(ref Unsafe.Add(ref vBase, 4), ref Unsafe.Add(ref scratchBase, 4), cmp);
        BidirectionalMerge(ref scratchBase, 8, ref dst, cmp);
    }

    /// <summary>merge_up (smallsort.rs:329-360): branchless single-element merge step —
    /// the lesser of src[left]/src[right] (ties left) goes to dst[outPos], exactly one of
    /// the two read cursors advances. The source pick is an OFFSET select made pure
    /// arithmetic: mask = t - 1 is 0 (t = 1, take left) or -1 (t = 0, take right), so
    /// (left &amp; ~mask) | (right &amp; mask) resolves the source index with a single load
    /// and no branch. RyuJIT x64 keeps both the conditional-ref pick AND a value-ternary
    /// pick (two loads + cmov) as data-dependent branches here, mispredicting ~50% on
    /// random data (JitDisasm-verified); the offset mask works for any T because it
    /// selects indices, not values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SmallMergeUp<T, TC>(ref T src, ref T dst, ref nint left, ref nint right, ref nint outPos, TC cmp)
        where TC : struct, IIsLess<T>
    {
        bool isL = !cmp.IsLess(in Unsafe.Add(ref src, right), in Unsafe.Add(ref src, left));
        nint t = isL ? 1 : 0;
        nint mask = t - 1; // 0 or -1
        nint pickOff = (left & ~mask) | (right & mask);
        Unsafe.Add(ref dst, outPos) = Unsafe.Add(ref src, pickOff);
        right += 1 - t;
        left += t;
        outPos++;
    }

    /// <summary>merge_down (smallsort.rs:362-393): the mirrored step at the back — the
    /// greater of src[leftRev]/src[rightRev] (ties right) goes to dst[outRev], exactly one
    /// of the two read cursors retreats. Same mask-based offset select as SmallMergeUp
    /// (isL picks the RIGHT source here — ties go to the back, keeping stability).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SmallMergeDown<T, TC>(ref T src, ref T dst, ref nint leftRev, ref nint rightRev, ref nint outRev, TC cmp)
        where TC : struct, IIsLess<T>
    {
        bool isL = !cmp.IsLess(in Unsafe.Add(ref src, rightRev), in Unsafe.Add(ref src, leftRev));
        nint t = isL ? 1 : 0;
        nint mask = t - 1; // 0 or -1
        nint pickOff = (rightRev & ~mask) | (leftRev & mask);
        Unsafe.Add(ref dst, outRev) = Unsafe.Add(ref src, pickOff);
        rightRev -= t;
        leftRev -= 1 - t;
        outRev--;
    }

    /// <summary>bidirectional_merge (smallsort.rs:407-484): merges the sorted halves
    /// src[0..len/2] and src[len/2..len] into dst[0..len], one element per end per
    /// iteration (2 writes instead of quadsort's 4). len must be >= 2 — every caller
    /// guarantees it, mirroring upstream's assume(len_div_2 != 0). Upstream's wrapping
    /// one-past-start pointers become nint offsets — the same 64-bit cursor reasoning
    /// as IpnPartition.PartitionLomutoBranchlessCyclic: these cursors feed Unsafe.Add
    /// and advance unconditionally, so int would cost a sign-extension per access on
    /// x64 (JitDisasm-verified: the merge loop's per-element movsxd are gone, only the
    /// one-time len conversion in the prologue remains). Reads stay in-bounds for any
    /// comparator outcome. T must be Freeze-like (see SmallSortConfig) — the comparator
    /// may observe outdated temporary copies that never reach the final array.</summary>
    internal static void BidirectionalMerge<T, TC>(ref T src, int len, ref T dst, TC cmp) where TC : struct, IIsLess<T>
    {
        nint lenDiv2 = len / 2;
        nint left = 0, right = lenDiv2, outPos = 0;
        nint leftRev = lenDiv2 - 1, rightRev = len - 1, outRev = len - 1;

        for (nint i = 0; i < lenDiv2; i++)
        {
            SmallMergeUp(ref src, ref dst, ref left, ref right, ref outPos, cmp);
            SmallMergeDown(ref src, ref dst, ref leftRev, ref rightRev, ref outRev, cmp);
        }

        nint leftEnd = leftRev + 1;
        nint rightEnd = rightRev + 1;

        // Odd length, so one element is left unconsumed in the input.
        if (len % 2 != 0)
        {
            bool leftNonempty = left < leftEnd;
            if (Unsafe.SizeOf<T>() <= 16)
            {
                T lv = Unsafe.Add(ref src, left);
                T rv = Unsafe.Add(ref src, right);
                Unsafe.Add(ref dst, outPos) = leftNonempty ? lv : rv;
            }
            else
            {
                ref T lastSrc = ref (leftNonempty ? ref Unsafe.Add(ref src, left) : ref Unsafe.Add(ref src, right));
                Unsafe.Add(ref dst, outPos) = lastSrc;
            }
            left += leftNonempty ? 1 : 0;
            right += leftNonempty ? 0 : 1;
        }

        // We now should have consumed the full input exactly once; only a comparison
        // operator that fails to be Ord can violate this (smallsort.rs:480-483).
        if (left != leftEnd || right != rightEnd)
            ThrowOrdViolation();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStartContract(int start, int len) =>
        throw new ArgumentException(
            $"start ({start}) violates the insertion-sort contract: must be in [1, {len}].");

    /// <summary>panic_on_ord_violation (smallsort.rs:486-489, #[inline(never)]).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOrdViolation() =>
        throw new InvalidOperationException("Ord violation");
}
