// Ported from https://github.com/Voultapher/sort-research-rs (ipnsort src/quicksort.rs),
// MIT OR Apache-2.0, by Lukas Bergdoll. C# port 2026 — the heart of ipnsort: the novel
// branchless Lomuto cyclic partition and the branchy Hoare cyclic partition.
//
// GapGuard divergence (binding ruling, same policy as the other ipnsort ports):
// upstream keeps the displaced "gap" element in a ManuallyDrop stack value that Drop
// writes back into the slice if is_less panics. C# has no Drop: the gap value is a
// plain local (a value copy for struct T, a reference copy for reference T — identical
// aliasing semantics) and the write-back upstream performs in Drop is done
// EXPLICITLY on the normal path (lomuto: the is_done loop_body call consumes it as
// `right` and copies it into the array; hoare: the post-loop write to gap.pos). On a
// comparer exception the slice contents are unspecified (duplicates or lost elements
// are acceptable per the parent contract) but never memory-unsafe; no finalizers.
//
// All other dataflow is 1:1: raw pointers become a base ref + int indices,
// ptr::copy / copy_nonoverlapping become read-then-write element moves, and
// ManuallyDrop becomes plain locals.
//
// C#-idiom divergences (JitDisasm-verified, ARM64 + x64):
//  1. Upstream passes the inverted comparison as a DIFFERENT closure type
//     (`&mut |a, b| !is_less(b, a)`, quicksort.rs:45) — a separate
//     monomorphization with the inversion folded in. The port threads no runtime
//     `invert: bool` through the loops; the driver wraps the comparer in
//     InvertedCmp<T, TC> (Comparers.cs), reproducing the monomorphization.
//  2. The pivot is a by-VALUE local snapshot, like the shared pivot port's
//     (IpnPivot.cs): upstream relies on &mut noalias to keep the
//     pivot in a register; RyuJIT has no such guarantee for a byref into the same
//     array being written, so `in T pivot` reloaded it from memory every
//     comparison. The snapshot cannot diverge: the impls write only v[1..] and the
//     comparer receives `in T` (no mutation), so all comparisons see the same
//     value upstream would have.
//  3. LoopBody's rightVal is passed by value: an `in` byref forced a second load
//     of the same element for the store half of the cyclic move.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

internal static class IpnPartition
{
    /// <summary>MAX_BRANCHLESS_PARTITION_SIZE (quicksort.rs:151).</summary>
    private const int MaxBranchlessPartitionSize = 96;

    /// <summary>partition (quicksort.rs:113-148): swap the pivot to the front,
    /// partition v[1..] via inst_partition (quicksort.rs:150-159: sizeof(T) &lt;= 96
    /// → branchless Lomuto, else branchy Hoare), then swap the pivot to index num_lt.
    /// On return v[0..num_lt) are &lt; pivot, v[num_lt] is the pivot and
    /// v[num_lt+1..] are &gt;= pivot; num_lt is the count of elements &lt; pivot.
    /// For the ancestor-equal path the driver passes an InvertedCmp&lt;T, TC&gt; —
    /// upstream's `|a, b| !is_less(b, a)` closure (quicksort.rs:45): "less"
    /// becomes !pivot &lt; cur, so num_lt counts elements &lt;= pivot and
    /// v[num_lt+1..] are &gt; pivot (equals land in v[0..num_lt]). Same shape as
    /// the inverted partition calls elsewhere in this port.</summary>
    internal static int Partition<T, TC>(Span<T> v, int pivotPos, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (len == 0)
            return 0;

        // upstream intrinsics::abort() — a caller bug, kept as a hard check.
        if (pivotPos < 0 || pivotPos >= len)
            throw new ArgumentOutOfRangeException(nameof(pivotPos));

        // Place the pivot at the beginning of the slice (quicksort.rs:130).
        Swap(ref v[0], ref v[pivotPos]);
        // By-value pivot snapshot (see the class header, divergence 2): every
        // comparison in the impls uses this copy; the array slot v[0] is never
        // read again until the final swap below.
        T pivot = v[0];
        Span<T> vWithoutPivot = v.Slice(1);

        // Unsafe.SizeOf is a JIT constant — the branch is folded at compile time.
        int numLt = Unsafe.SizeOf<T>() <= MaxBranchlessPartitionSize
            ? PartitionLomutoBranchlessCyclic<T, TC>(vWithoutPivot, pivot, cmp)
            : PartitionHoareBranchyCyclic<T, TC>(vWithoutPivot, pivot, cmp);

        // Place the pivot between the two partitions (quicksort.rs:145).
        Swap(ref v[0], ref v[numLt]);
        return (int)numLt;
    }

    /// <summary>partition_lomuto_branchless_cyclic (quicksort.rs:254-353). Novel
    /// partition by Lukas Bergdoll and Orson Peters: branchless Lomuto partition
    /// paired with a cyclic permutation. The scan cursors are nint (64-bit) — the
    /// C# analog of upstream's usize-based raw pointers: RyuJIT's x64 address modes
    /// need a 64-bit index, and int cursors force a movsxd sign-extension per
    /// element access in this hot loop (JitDisasm-verified; ARM64 hides the cost
    /// in its 64-bit registers, x64 does not).</summary>
    private static int PartitionLomutoBranchlessCyclic<T, TC>(Span<T> v, T pivot, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (len == 0)
            return 0;

        ref T vBase = ref MemoryMarshal.GetReference(v);

        // The gap value (quicksort.rs:302): ptr::read(v_base) — a value copy held
        // outside the array. Upstream wraps it in ManuallyDrop + GapGuardRaw; here
        // it is a plain local whose write-back is the is_done LoopBody call below.
        // Upstream's GapGuardRaw carries an explicit `pos` cursor; in this port it
        // is always right - 1 (the gap is seated at the previous scan element, and
        // right advances by exactly one per op), so the cursor is folded away and
        // LoopBody derives it — one less live register and one less move per element.
        T gapValue = vBase;

        nint numLt = 0;
        nint right = 1;

        // Manual unrolling (quicksort.rs:316-330): 2 for sizeof <= 16, else 1.
        int unrollLen = Unsafe.SizeOf<T>() <= 16 ? 2 : 1;
        nint unrollEnd = len - (unrollLen - 1);

        if (unrollLen == 2)
        {
            while (right < unrollEnd)
            {
                LoopBody(ref vBase, pivot, cmp, ref numLt, ref right,
                    Unsafe.Add(ref vBase, right));
                LoopBody(ref vBase, pivot, cmp, ref numLt, ref right,
                    Unsafe.Add(ref vBase, right));
            }
        }
        else
        {
            while (right < unrollEnd)
                LoopBody(ref vBase, pivot, cmp, ref numLt, ref right,
                    Unsafe.Add(ref vBase, right));
        }

        // Cleanup (quicksort.rs:334-349): one shared loop for the unroll remainder
        // and the final write-back of the gap value. When is_done, the saved gap
        // value acts as `right` (upstream: state.right = state.gap.value) and this
        // LoopBody call IS the explicit write-back; upstream then forgets the guard.
        nint end = len;
        while (true)
        {
            if (right == end)
            {
                LoopBody(ref vBase, pivot, cmp, ref numLt, ref right, gapValue);
                break;
            }

            LoopBody(ref vBase, pivot, cmp, ref numLt, ref right,
                Unsafe.Add(ref vBase, right));
        }

        return (int)numLt;
    }

    /// <summary>loop_body (quicksort.rs:284-295). rightVal is the element under
    /// inspection — normally v[right]; in the final cleanup call it is the saved gap
    /// value. One instantiation is shared by the unrolled main loop and both cleanup
    /// cases, as upstream does to save binary size and compile-time
    /// (quicksort.rs:332-333). pivot and rightVal are by-value: inlined they are
    /// plain locals, keeping the pivot in a register and loading rightVal once
    /// (see the class header, divergences 2 and 3). The cursors are nint — see
    /// PartitionLomutoBranchlessCyclic — and the gap position is right - 1 (see the
    /// gap-value note there), so only numLt and right are live.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoopBody<T, TC>(
        ref T vBase, T pivot, TC cmp, ref nint numLt, ref nint right, T rightVal)
        where TC : struct, IIsLess<T>
    {
        bool rightIsLt = cmp.IsLess(in rightVal, in pivot);
        nint left = numLt;

        // ptr::copy(left, gap.pos, 1) — read-then-write, memmove for one element.
        // gap.pos == right - 1 (invariant: the previous op seated the gap at its
        // scan element, which this op's right has since advanced past).
        T leftVal = Unsafe.Add(ref vBase, left);
        Unsafe.Add(ref vBase, right - 1) = leftVal;
        // ptr::copy_nonoverlapping(right, left, 1).
        Unsafe.Add(ref vBase, left) = rightVal;

        numLt += rightIsLt ? 1 : 0; // num_lt += right_is_lt as usize
        right++;
    }

    /// <summary>partition_hoare_branchy_cyclic (quicksort.rs:162-242): optimized for
    /// large types that are expensive to move and small code-gen; swaps each pair of
    /// out-of-order elements through a single-element gap (cyclic permutation).</summary>
    private static int PartitionHoareBranchyCyclic<T, TC>(Span<T> v, T pivot, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (len == 0)
            return 0;

        ref T vBase = ref MemoryMarshal.GetReference(v);

        // GapGuard (quicksort.rs:355-366) as explicit locals: gapPos is the index of
        // the current duplicate, gapValue holds the element displaced by the first
        // swap of the cycle. hasGap is upstream's gap_opt.is_none().
        bool hasGap = false;
        nint gapPos = 0;
        T gapValue = default!;

        // 64-bit cursors — the usize-based raw pointers of upstream (see the Lomuto
        // path's nint note; the scan loops here pay the same x64 movsxd tax with
        // int cursors).
        nint left = 0;
        nint right = len;

        while (true)
        {
            // Find the first element greater than the pivot (quicksort.rs:198-200).
            // cmp is the driver's comparer — plain or InvertedCmp-wrapped, the
            // upstream closure in either case (quicksort.rs:190/205's is_less calls).
            while (left < right && cmp.IsLess(in Unsafe.Add(ref vBase, left), in pivot))
                left++;

            // Find the last element equal to the pivot (quicksort.rs:203-208).
            while (true)
            {
                right--;
                if (left >= right || cmp.IsLess(in Unsafe.Add(ref vBase, right), in pivot))
                    break;
            }

            if (left >= right)
                break;

            // Swap the found pair via cyclic permutation (quicksort.rs:215-233).
            if (!hasGap)
            {
                // First pair: save the displaced value, the gap starts at right.
                gapPos = right;
                gapValue = Unsafe.Add(ref vBase, left); // ptr::read(left)
                hasGap = true;
            }
            else
            {
                // ptr::copy_nonoverlapping(left, gap.pos, 1) — close the previous gap.
                T tmp = Unsafe.Add(ref vBase, left);
                Unsafe.Add(ref vBase, gapPos) = tmp;
            }

            gapPos = right;
            // ptr::copy_nonoverlapping(right, left, 1).
            T rightVal = Unsafe.Add(ref vBase, right);
            Unsafe.Add(ref vBase, left) = rightVal;

            left++;
        }

        // GapGuard Drop, made explicit (quicksort.rs:360-365): overwrite the last
        // duplicate with the value displaced by the first swap of the cycle.
        if (hasGap)
            Unsafe.Add(ref vBase, gapPos) = gapValue;

        return (int)left; // left.offset_from_unsigned(v_base)
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap<T>(ref T a, ref T b)
    {
        T tmp = a;
        a = b;
        b = tmp;
    }
}
