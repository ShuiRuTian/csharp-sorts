// Ported from https://github.com/orlp/glidesort, MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — branchless_merge.rs, physical_merges.rs and the parts of merge_reduction.rs
// physical_merges.rs uses: the bidirectional branchless merge state, the disjoint/gap merge
// drivers, and the physical (in-place) merge family plus the eager fallback sort.
//
// gap_guard.rs is deliberately NOT ported as a type: it exists upstream to guarantee the
// scratch/gap gets refilled when a comparison panics (Rust unwinding) and to prove
// data/gap non-overlap via types. In this C# layout scratch and input are separate
// allocations (distinct Span arguments), so the overlap it guards against cannot occur;
// its success-path data movement is inlined as explicit copies below. Panic recovery has
// no port either — an exception from cmp leaves buffers in an unspecified state, the same
// policy GlideSmallSort adopted.
//
// Rust's MutSlice typestate only proves read-before-write discipline; the ported code
// maintains that discipline by construction on plain Span<T>.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>GlideSort merge kernel (upstream physical_merges.rs / branchless_merge.rs /
/// merge_reduction.rs). All merges are stable and in place: the merged result occupies the
/// concatenated input region, which callers slice afterwards (upstream returns the merged
/// MutSlice; here the destination IS the leftmost span).</summary>
internal static class GlideMerge
{
    /// <summary>Upstream MERGE_SPLIT_THRESHOLD (lib.rs:29) — merges above this size get
    /// split into (instruction-level) parallel sub-merges.</summary>
    internal const int MergeSplitThreshold = 32;

    /// <summary>shrink_stable_merge (merge_reduction.rs:8-33): returns (l, r) such that
    /// (left[l..], right[..r]) is the remaining merge, or null when left's tail already
    /// sorts before right's head (nothing to do).</summary>
    private static (int Left, int Right)? ShrinkStableMerge<T, TC>(Span<T> left, Span<T> right, TC cmp)
        where TC : struct, IIsLess<T>
    {
        if (left.Length > 0 && right.Length > 0)
        {
            ref T lastLeft = ref left[left.Length - 1];
            ref T firstRight = ref right[0];
            if (cmp.IsLess(in firstRight, in lastLeft))
            {
                // Extremal elements that end up in a different position when merged.
                int firstLeftToRight = -1;
                for (int i = 0; i < left.Length; i++)
                    if (cmp.IsLess(in firstRight, in left[i])) { firstLeftToRight = i; break; }
                int lastRightToLeft = -1;
                ref T rightBase = ref MemoryMarshal.GetReference(right);
                for (int i = right.Length - 1; i >= 0; i--)
                    if (cmp.IsLess(in Unsafe.Add(ref rightBase, i), in lastLeft)) { lastRightToLeft = i; break; }
                // Unreachable for a valid comparator; upstream also bails out here.
                if (firstLeftToRight >= 0 && lastRightToLeft >= 0)
                    return (firstLeftToRight, lastRightToLeft + 1); // r + 1: exclusive bound.
            }
        }
        return null;
    }

    /// <summary>crossover_point (merge_reduction.rs:40-82): given sorted left, right of
    /// equal length n, the smallest i such that every l in left[i..] is greater than every
    /// r in right[..n-i]. Binary search over the "ruled out" positions.</summary>
    private static int CrossoverPoint<T, TC>(Span<T> left, Span<T> right, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int n = left.Length;
        int lo = 0, maybe = n;
        // Invariants: every position before lo is ruled out, every position after
        // lo + maybe is ruled out, and lo + maybe <= n.
        while (maybe > 0)
        {
            int step = maybe >> 1;
            int i = lo + step;
            if (cmp.IsLess(in right[n - 1 - i], in left[i]))
                maybe = step; // i is valid: rule out everything after it.
            else
            {
                lo += step + 1; // rule out everything up to and including i.
                maybe -= step + 1;
            }
        }
        return lo;
    }

    /// <summary>merge_splitpoints (merge_reduction.rs:90-95): splitpoints (l, r) such that
    /// stably merging (left[..l], right[..r]) then (left[l..], right[r..]) equals stably
    /// merging (left, right); right[..r] and left[l..] have equal length.</summary>
    private static (int Left, int Right) MergeSplitpoints<T, TC>(Span<T> left, Span<T> right, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int minlen = Math.Min(left.Length, right.Length);
        int leftSkip = left.Length - minlen;
        int i = CrossoverPoint(left.Slice(leftSkip), right.Slice(0, minlen), cmp);
        return (leftSkip + i, minlen - i);
    }

    /// <summary>Contiguous region of length starting at the beginning of start — the
    /// Span equivalent of upstream's MutSlice::concat on adjacent slices. The seams that
    /// build regions from caller input (PhysicalMerge/Triple/Quad) verify contiguity on
    /// entry via AreContiguous (Unsafe.AreSame on the boundary refs, mirroring upstream's
    /// concat abort); regions built internally from one span's slices are contiguous by
    /// construction.</summary>
    private static Span<T> Region<T>(Span<T> start, int length) =>
        MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(start), length);

    /// <summary>ptr::swap_nonoverlapping on two equal-length spans (upstream calls it on
    /// the swap path of physical_merge; a plain loop keeps element-wise semantics).</summary>
    private static void SwapSpans<T>(Span<T> a, Span<T> b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            T tmp = a[i];
            a[i] = b[i];
            b[i] = tmp;
        }
    }

    /// <summary>BranchlessMergeState (branchless_merge.rs:96-423) — the full merge state,
    /// shared with GlideSmallSort (upstream small_sort.rs imports the same type). The Rust
    /// GapLeft/GapRight/GapBoth typestate is encoded as which finish method the caller
    /// invokes; the imbalance-guarded ops are not ported because every C# type is
    /// bitwise-copyable with no drop glue (upstream may_call_ord_on_copy() == true).
    /// Indices are plain ints relative to each span's base so they may cross for a bad
    /// comparison operator, exactly as upstream's raw pointers do. Reads and writes stay
    /// inside each span while the comparator is valid; with a broken comparator,
    /// GlideSmallSort's fixed-k symmetric paths may read across the adjacent left/right
    /// span boundary — still in-bounds of the underlying buffer (upstream's raw pointers
    /// behave identically), after which SymmetricMergeSuccessful fails and the caller
    /// restores from its backup copy. left and right may alias dst (gap constructions)
    /// but never each other's unread elements — the op order (read, then write) preserves
    /// that.</summary>
    internal ref struct BranchlessMergeState<T>
    {
        private readonly Span<T> _left;
        private readonly Span<T> _right;
        private readonly Span<T> _dst;
        private int _leftBegin, _leftEnd, _rightBegin, _rightEnd, _dstBegin, _dstEnd;

        /// <summary>new_disjoint (branchless_merge.rs:132-140): left, right and dst all
        /// disjoint; dst.Length must equal left.Length + right.Length (upstream aborts).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BranchlessMergeState(Span<T> left, Span<T> right, Span<T> dst)
        {
            _left = left;
            _right = right;
            _dst = dst;
            _leftBegin = 0;
            _leftEnd = left.Length;
            _rightBegin = 0;
            _rightEnd = right.Length;
            _dstBegin = 0;
            _dstEnd = dst.Length;
        }

        /// <summary>new_gap_left (branchless_merge.rs:142-155): dst is the contiguous
        /// region [gap | right] and leftData (length == gap) is disjoint from it; right's
        /// data doubles as dst's tail, so only at-begin operations are valid.</summary>
        internal static BranchlessMergeState<T> NewGapLeft(Span<T> leftData, Span<T> gap, Span<T> right)
            => new BranchlessMergeState<T>(leftData, right, Region(gap, gap.Length + right.Length));

        /// <summary>new_gap_right (branchless_merge.rs:157-170): dst is the contiguous
        /// region [left | gap] and rightData (length == gap) is disjoint from it; left's
        /// data doubles as dst's head, so only at-end operations are valid.</summary>
        internal static BranchlessMergeState<T> NewGapRight(Span<T> left, Span<T> rightData, Span<T> gap)
            => new BranchlessMergeState<T>(left, rightData, Region(left, left.Length + gap.Length));

        /// <summary>branchless_merge_one_at_begin (branchless_merge.rs:176-196): merge the
        /// smaller of left/right's front element to dst's front (ties towards left).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void MergeOneAtBegin<TC>(TC cmp) where TC : struct, IIsLess<T>
        {
            // Upstream note: adding 1 and subtracting right_less gave significantly faster
            // codegen than adding !right_less; the equivalent conditional increments are kept.
            ref T l = ref MemoryMarshal.GetReference(_left);
            ref T r = ref MemoryMarshal.GetReference(_right);
            ref T d = ref MemoryMarshal.GetReference(_dst);
            bool rightLess = cmp.IsLess(in Unsafe.Add(ref r, _rightBegin), in Unsafe.Add(ref l, _leftBegin));
            // Small T (JIT-constant branch): value ternary — two loads + csel/cmov +
            // one store (upstream's ptr::select is a VALUE select in LLVM); large T
            // keeps the conditional-ref source select (one load behind a branch, no
            // duplicated copies).
            if (Unsafe.SizeOf<T>() <= 16)
            {
                T lv = Unsafe.Add(ref l, _leftBegin);
                T rv = Unsafe.Add(ref r, _rightBegin);
                Unsafe.Add(ref d, _dstBegin) = rightLess ? rv : lv;
            }
            else
            {
                ref T srcBegin = ref rightLess
                    ? ref Unsafe.Add(ref r, _rightBegin)
                    : ref Unsafe.Add(ref l, _leftBegin);
                Unsafe.Add(ref d, _dstBegin) = srcBegin;
            }
            _dstBegin++;
            // Plain ternary cursor updates (cset+add, JitDisasm-verified with the
            // `&gt; 0`-shaped comparer in Comparers.cs; the former Unsafe.As
            // materialization re-branches the pick above).
            _rightBegin += rightLess ? 1 : 0;
            _leftBegin += rightLess ? 0 : 1;
        }

        /// <summary>branchless_merge_one_at_end (branchless_merge.rs:229-245): merge the
        /// larger of left/right's back element to dst's back (ties towards right).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void MergeOneAtEnd<TC>(TC cmp) where TC : struct, IIsLess<T>
        {
            ref T l = ref MemoryMarshal.GetReference(_left);
            ref T r = ref MemoryMarshal.GetReference(_right);
            ref T d = ref MemoryMarshal.GetReference(_dst);
            bool rightLess = cmp.IsLess(in Unsafe.Add(ref r, _rightEnd - 1), in Unsafe.Add(ref l, _leftEnd - 1));
            _dstEnd--;
            // Same small-T value-ternary / large-T ref-ternary split as
            // MergeOneAtBegin: the greater of the two backs (ties right) goes to
            // dst's back.
            if (Unsafe.SizeOf<T>() <= 16)
            {
                T lv = Unsafe.Add(ref l, _leftEnd - 1);
                T rv = Unsafe.Add(ref r, _rightEnd - 1);
                Unsafe.Add(ref d, _dstEnd) = rightLess ? lv : rv;
            }
            else
            {
                ref T srcEnd = ref rightLess
                    ? ref Unsafe.Add(ref l, _leftEnd - 1)
                    : ref Unsafe.Add(ref r, _rightEnd - 1);
                Unsafe.Add(ref d, _dstEnd) = srcEnd;
            }
            // Plain ternary cursor retreats — see MergeOneAtBegin.
            _leftEnd -= rightLess ? 1 : 0;
            _rightEnd -= rightLess ? 0 : 1;
        }

        /// <summary>symmetric_merge_successful (branchless_merge.rs:280-284): left_begin ==
        /// left_end implies right is exhausted too; only an invalid comparison operator
        /// can violate this. Used by GlideSmallSort's symmetric fixed-count merges.</summary>
        internal bool SymmetricMergeSuccessful => _leftBegin == _leftEnd;

        /// <summary>num_safe_merge_ops (branchless_merge.rs:289-302): how many more merge
        /// operations are safe (both sides non-empty); negative side lengths mean the
        /// scan pointers crossed, which only a broken comparator can cause.</summary>
        internal int NumSafeMergeOps()
        {
            int leftLen = _leftEnd - _leftBegin;
            int rightLen = _rightEnd - _rightBegin;
            if (leftLen < 0 || rightLen < 0)
                ThrowCrossed();
            return leftLen < rightLen ? leftLen : rightLen;
        }

        /// <summary>The Drop impl (branchless_merge.rs:400-423), which upstream runs on
        /// every completed merge, not just panics: the unwritten middle of dst receives
        /// the remaining left elements followed by the remaining right elements. In the
        /// gap constructions one of the two copies is a self-overlapping no-op.</summary>
        private void DrainRemainder()
        {
            int leftLen = _leftEnd - _leftBegin;
            int rightLen = _rightEnd - _rightBegin;
            if (leftLen < 0 || rightLen < 0 || leftLen + rightLen != _dstEnd - _dstBegin)
                ThrowCrossed(); // mirrors upstream's assert_abort in Drop.
            _left.Slice(_leftBegin, leftLen).CopyTo(_dst.Slice(_dstBegin, leftLen));
            _right.Slice(_rightBegin, rightLen).CopyTo(_dst.Slice(_dstBegin + leftLen, rightLen));
        }

        /// <summary>finish_merge, GapBoth (branchless_merge.rs:351-373): bidirectional
        /// interleaved merge loops (begin/end/begin/end, unrolled by 4) for fully disjoint
        /// input and destination.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void FinishMerge<TC>(TC cmp) where TC : struct, IIsLess<T>
        {
            while (true)
            {
                int n = NumSafeMergeOps();
                if (n == 0)
                    break;
                for (int i = 0; i < n >> 2; i++)
                {
                    MergeOneAtBegin(cmp);
                    MergeOneAtEnd(cmp);
                    MergeOneAtBegin(cmp);
                    MergeOneAtEnd(cmp);
                }
                for (int i = 0; i < (n & 3); i++)
                    MergeOneAtBegin(cmp);
            }
            DrainRemainder();
        }

        /// <summary>finish_merge, GapLeft (branchless_merge.rs:305-325): begin-only loops.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void FinishMergeAtBegin<TC>(TC cmp) where TC : struct, IIsLess<T>
        {
            while (true)
            {
                int n = NumSafeMergeOps();
                if (n == 0)
                    break;
                for (int i = 0; i < n >> 1; i++)
                {
                    MergeOneAtBegin(cmp);
                    MergeOneAtBegin(cmp);
                }
                for (int i = 0; i < (n & 1); i++)
                    MergeOneAtBegin(cmp);
            }
            DrainRemainder();
        }

        /// <summary>finish_merge, GapRight (branchless_merge.rs:328-348): end-only loops.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void FinishMergeAtEnd<TC>(TC cmp) where TC : struct, IIsLess<T>
        {
            while (true)
            {
                int n = NumSafeMergeOps();
                if (n == 0)
                    break;
                for (int i = 0; i < n >> 1; i++)
                {
                    MergeOneAtEnd(cmp);
                    MergeOneAtEnd(cmp);
                }
                for (int i = 0; i < (n & 1); i++)
                    MergeOneAtEnd(cmp);
            }
            DrainRemainder();
        }

        /// <summary>finish_merge_interleaved (branchless_merge.rs:375-397): interleave this
        /// state with another (each pair of ops issued back-to-back) while both have two
        /// or more safe ops left, then finish each independently.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void FinishMergeInterleaved<TC>(BranchlessMergeState<T> other, TC cmp)
            where TC : struct, IIsLess<T>
        {
            while (true)
            {
                int commonRemaining = Math.Min(NumSafeMergeOps(), other.NumSafeMergeOps());
                if (commonRemaining < 2)
                    break;
                for (int i = 0; i < commonRemaining >> 1; i++)
                {
                    MergeOneAtBegin(cmp);
                    other.MergeOneAtBegin(cmp);
                    MergeOneAtEnd(cmp);
                    other.MergeOneAtEnd(cmp);
                }
            }
            FinishMerge(cmp);
            other.FinishMerge(cmp);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowCrossed() =>
            throw new InvalidOperationException(
                "Comparison operator violated its contract: merge scan pointers crossed.");
    }

    /// <summary>merge_into_gap (physical_merges.rs:300-337): stably merges the disjoint
    /// sorted runs leftData, rightData into dst (dst.Length == leftData.Length +
    /// rightData, disjoint from both). Large merges split into two interleaved halves.</summary>
    private static void MergeIntoGap<T, TC>(Span<T> leftData, Span<T> rightData, Span<T> dst, TC cmp)
        where TC : struct, IIsLess<T>
    {
        if (Math.Min(leftData.Length, rightData.Length) >= MergeSplitThreshold)
        {
            (int lsplit, int rsplit) = MergeSplitpoints(leftData, rightData, cmp);
            // merged(left, right) == merged(left0, right0) ++ merged(left1, right1).
            DoubleMergeInto(
                leftData.Slice(0, lsplit), rightData.Slice(0, rsplit),
                leftData.Slice(lsplit), rightData.Slice(rsplit), dst, cmp);
        }
        else
        {
            var state = new BranchlessMergeState<T>(leftData, rightData, dst);
            state.FinishMerge(cmp);
        }
    }

    /// <summary>double_merge_into (physical_merges.rs:342-358): merges l0 ⊕ l1 into
    /// dst[0..] and r0 ⊕ r1 into dst[l0.len + l1.len..] via two interleaved merge states.
    /// dst.Length must equal the four runs' total length.</summary>
    private static void DoubleMergeInto<T, TC>(Span<T> l0, Span<T> l1, Span<T> r0, Span<T> r1, Span<T> dst, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int leftLen = l0.Length + l1.Length;
        var leftState = new BranchlessMergeState<T>(l0, l1, dst.Slice(0, leftLen));
        var rightState = new BranchlessMergeState<T>(r0, r1, dst.Slice(leftLen));
        leftState.FinishMergeInterleaved(rightState, cmp);
    }

    /// <summary>merge_left_gap (physical_merges.rs:182-213): merges the disjoint sorted
    /// run leftData with right into the contiguous region [gap | right], where gap
    /// (length == leftData.Length) sits directly before right and is free space.</summary>
    private static void MergeLeftGap<T, TC>(Span<T> leftData, Span<T> gap, Span<T> right, TC cmp)
        where TC : struct, IIsLess<T>
    {
        while (Math.Min(leftData.Length, right.Length) >= MergeSplitThreshold)
        {
            (int lsplit, int rsplit) = MergeSplitpoints(leftData, right, cmp);
            // left0 ⊕ right0 fill the whole gap; left1 then merges with right1 using
            // right0's vacated space as its gap.
            MergeIntoGap(leftData.Slice(0, lsplit), right.Slice(0, rsplit), gap, cmp);
            Span<T> oldRight = right;
            leftData = leftData.Slice(lsplit);
            right = right.Slice(rsplit);
            gap = oldRight.Slice(0, rsplit);
        }
        var state = BranchlessMergeState<T>.NewGapLeft(leftData, gap, right);
        state.FinishMergeAtBegin(cmp);
    }

    /// <summary>merge_right_gap (physical_merges.rs:220-251): merges the sorted run left
    /// with the disjoint rightData into the contiguous region [left | gap], where gap
    /// (length == rightData.Length) sits directly after left and is free space.</summary>
    private static void MergeRightGap<T, TC>(Span<T> left, Span<T> rightData, Span<T> gap, TC cmp)
        where TC : struct, IIsLess<T>
    {
        while (Math.Min(left.Length, rightData.Length) >= MergeSplitThreshold)
        {
            (int lsplit, int rsplit) = MergeSplitpoints(left, rightData, cmp);
            // left1 ⊕ right1 fill the whole gap; left0 then merges with right0 using
            // left1's vacated space as its gap.
            MergeIntoGap(left.Slice(lsplit), rightData.Slice(rsplit), gap, cmp);
            Span<T> oldLeft = left;
            left = left.Slice(0, lsplit);
            rightData = rightData.Slice(0, rsplit);
            gap = oldLeft.Slice(lsplit);
        }
        var state = BranchlessMergeState<T>.NewGapRight(left, rightData, gap);
        state.FinishMergeAtEnd(cmp);
    }

    /// <summary>BidirectionalMerge — the interleaved bidirectional merge from
    /// branchless_merge.rs (new_disjoint + finish_merge): merges the disjoint sorted runs
    /// left, right into dst, reading at the begin and writing at both ends of dst in an
    /// interleaved loop. dst must be disjoint from both runs and exactly their combined
    /// length (upstream aborts on violation; this public seam throws).</summary>
    internal static void BidirectionalMerge<T, TC>(Span<T> left, Span<T> right, Span<T> dst, TC cmp)
        where TC : struct, IIsLess<T>
    {
        if (dst.Length != left.Length + right.Length)
            ThrowDstLength(left.Length, right.Length, dst.Length);
        var state = new BranchlessMergeState<T>(left, right, dst);
        state.FinishMerge(cmp);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDstLength(int leftLen, int rightLen, int dstLen) =>
        throw new ArgumentException(
            $"dst.Length ({dstLen}) violates the merge contract: must equal left.Length + right.Length ({leftLen + rightLen}).");

    /// <summary>Contiguity of two runs — the Span equivalent of the pointer comparison
    /// upstream's MutSlice::concat aborts on: left must end exactly where right begins.
    /// A single Unsafe.AreSame on the boundary refs expresses it (a ref one-past-the-end
    /// of left equals right's first ref iff the runs are adjacent in one allocation).
    /// Empty runs at the same position compare equal, matching concat on empty slices.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AreContiguous<T>(Span<T> left, Span<T> right) =>
        Unsafe.AreSame(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(left), left.Length),
            ref MemoryMarshal.GetReference(right));

    /// <summary>Non-contiguous input at a physical-merge seam — mirrors upstream's
    /// assert_abort on MutSlice::concat of non-adjacent slices. Without the check Region()
    /// would silently over-run the left allocation, so the seams detect it instead.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotContiguous(string firstName, string secondName) =>
        throw new InvalidOperationException(
            $"{firstName} and {secondName} violate the physical merge contract: {firstName} must end exactly where {secondName} begins (contiguous runs in one region).");

    /// <summary>physical_merge (physical_merges.rs:23-73): merges the contiguous sorted
    /// runs left, right in place — the merged result occupies [left | right], which the
    /// caller slices (upstream returns that MutSlice). scratch is workspace: the upstream
    /// contract supplies at least half the total input length, which always takes the
    /// move-into-scratch path; smaller scratch degrades to the in-place swap path but
    /// stays correct. Contiguity (left ends exactly where right begins) is verified on
    /// entry, mirroring upstream's concat abort; the internal recursion runs unguarded
    /// (its slices are contiguous by construction).</summary>
    internal static void PhysicalMerge<T, TC>(Span<T> left, Span<T> right, Span<T> scratch, TC cmp)
        where TC : struct, IIsLess<T>
    {
        if (!AreContiguous(left, right))
            ThrowNotContiguous(nameof(left), nameof(right));
        PhysicalMergeCore(left, right, scratch, cmp);
    }

    /// <summary>physical_merge loop body — every call site passes contiguous slices by
    /// construction (slices of one region, shrunk only at the outer ends), so no guard.</summary>
    private static void PhysicalMergeCore<T, TC>(Span<T> left, Span<T> right, Span<T> scratch, TC cmp)
        where TC : struct, IIsLess<T>
    {
        while (true)
        {
            var shrink = ShrinkStableMerge(left, right, cmp);
            if (shrink is null)
                return; // Already ordered (or a side is empty): nothing to move.
            (int leftShrink, int rightShrink) = shrink.Value;
            left = left.Slice(leftShrink);
            right = right.Slice(0, rightShrink);

            // Split into two parallel merges with left1.Length == right0.Length.
            (int lsplit, int rsplit) = MergeSplitpoints(left, right, cmp);
            Span<T> left0 = left.Slice(0, lsplit), left1 = left.Slice(lsplit);
            Span<T> right0 = right.Slice(0, rsplit), right1 = right.Slice(rsplit);

            if (scratch.Length >= left1.Length)
            {
                // Logically swap left1 and right0, then merge (left0, left1) and
                // (right0, right1). Moving left1 into scratch lets both gaps do the rest.
                left1.CopyTo(scratch.Slice(0, left1.Length));
                // left0 ⊕ right0 into [left0 | left1's old space] (merge_right_gap).
                MergeRightGap(left0, right0, left1, cmp);
                // left1 (in scratch) ⊕ right1 into [right0's old space | right1]
                // (merge_left_gap).
                MergeLeftGap(scratch.Slice(0, left1.Length), right0, right1, cmp);
                return;
            }

            // Scratch too small: swap left1/right0 contents in place, merge the right
            // half recursively, and keep shrinking the left half around the loop.
            SwapSpans(left1, right0);
            PhysicalMergeCore(right0, right1, scratch, cmp);
            left = left0;
            right = left1;
        }
    }

    /// <summary>try_merge_into_scratch (physical_merges.rs:264-295): if scratch fits the
    /// contiguous runs left ⊕ right, merge them into scratch's front and report success
    /// (the vacated input region becomes the caller's gap). consumed elements of scratch
    /// are the front; on failure nothing is consumed.</summary>
    private static bool TryMergeIntoScratch<T, TC>(
        Span<T> left, Span<T> right, Span<T> scratch, out Span<T> merged, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int total = left.Length + right.Length;
        if (scratch.Length >= total)
        {
            merged = scratch.Slice(0, total);
            MergeIntoGap(left, right, merged, cmp);
            return true;
        }
        merged = default;
        return false;
    }

    /// <summary>physical_triple_merge (physical_merges.rs:78-108): merges the contiguous
    /// sorted runs a, b, c in place; the result occupies [a | b | c]. scratch workspace,
    /// same contract as PhysicalMerge. Contiguity of a|b and b|c is verified on entry,
    /// mirroring upstream's concat abort; internal merges run unguarded.</summary>
    internal static void PhysicalTripleMerge<T, TC>(Span<T> a, Span<T> b, Span<T> c, Span<T> scratch, TC cmp)
        where TC : struct, IIsLess<T>
    {
        if (!AreContiguous(a, b))
            ThrowNotContiguous(nameof(a), nameof(b));
        if (!AreContiguous(b, c))
            ThrowNotContiguous(nameof(b), nameof(c));
        if (a.Length < c.Length)
        {
            if (TryMergeIntoScratch(a, b, scratch, out Span<T> ab, cmp))
            {
                // ab (in scratch) merges with c into the vacated [a | b] region that
                // sits directly before c (merge_left_gap).
                MergeLeftGap(ab, Region(a, a.Length + b.Length), c, cmp);
            }
            else
            {
                PhysicalMergeCore(a, b, scratch, cmp);
                PhysicalMergeCore(Region(a, a.Length + b.Length), c, scratch, cmp);
            }
        }
        else
        {
            if (TryMergeIntoScratch(b, c, scratch, out Span<T> bc, cmp))
            {
                // bc (in scratch) merges with a into the vacated [b | c] region that
                // sits directly after a (merge_right_gap).
                MergeRightGap(a, bc, Region(b, b.Length + c.Length), cmp);
            }
            else
            {
                PhysicalMergeCore(b, c, scratch, cmp);
                PhysicalMergeCore(a, Region(b, b.Length + c.Length), scratch, cmp);
            }
        }
    }

    /// <summary>physical_quad_merge (physical_merges.rs:113-175): merges the contiguous
    /// sorted runs a, b, c, d in place; the result occupies [a | b | c | d]. scratch
    /// workspace, same contract as PhysicalMerge. Contiguity of a|b, b|c and c|d is
    /// verified on entry, mirroring upstream's concat abort; internal merges run
    /// unguarded.</summary>
    internal static void PhysicalQuadMerge<T, TC>(
        Span<T> a, Span<T> b, Span<T> c, Span<T> d, Span<T> scratch, TC cmp)
        where TC : struct, IIsLess<T>
    {
        if (!AreContiguous(a, b))
            ThrowNotContiguous(nameof(a), nameof(b));
        if (!AreContiguous(b, c))
            ThrowNotContiguous(nameof(b), nameof(c));
        if (!AreContiguous(c, d))
            ThrowNotContiguous(nameof(c), nameof(d));
        int leftLen = a.Length + b.Length;
        int rightLen = c.Length + d.Length;
        int total = leftLen + rightLen;

        if (scratch.Length >= total)
        {
            // Full-size scratch: merge both pairs into scratch (interleaved), then
            // merge them back into the vacated input region.
            Span<T> region = Region(a, total);
            DoubleMergeInto(a, b, c, d, scratch.Slice(0, total), cmp);
            MergeIntoGap(scratch.Slice(0, leftLen), scratch.Slice(leftLen, rightLen), region, cmp);
            return;
        }

        // Merge the bigger pair into scratch first: its vacated space then serves as
        // scratch for the smaller pair (physical_merges.rs:139-149).
        bool leftOk, rightOk;
        Span<T> leftMerged = default, rightMerged = default;
        if (leftLen >= rightLen)
        {
            leftOk = TryMergeIntoScratch(a, b, scratch, out leftMerged, cmp);
            rightOk = TryMergeIntoScratch(c, d, scratch.Slice(leftOk ? leftLen : 0), out rightMerged, cmp);
        }
        else
        {
            rightOk = TryMergeIntoScratch(c, d, scratch, out rightMerged, cmp);
            leftOk = TryMergeIntoScratch(a, b, scratch.Slice(rightOk ? rightLen : 0), out leftMerged, cmp);
        }

        if (leftOk && rightOk)
            ThrowUnreachableQuad(); // Both pairs fitting implies the full-size path above.

        Span<T> leftRegion = Region(a, leftLen);
        Span<T> rightRegion = Region(c, rightLen);
        if (leftOk)
        {
            // The merged left pair (in scratch) merges with c ⊕ d (merged in place into
            // the right region, using the vacated left region as scratch) back into [a|b].
            PhysicalMergeCore(c, d, leftRegion, cmp);
            MergeLeftGap(leftMerged, leftRegion, rightRegion, cmp);
        }
        else if (rightOk)
        {
            PhysicalMergeCore(a, b, rightRegion, cmp);
            MergeRightGap(leftRegion, rightMerged, rightRegion, cmp);
        }
        else
        {
            PhysicalMergeCore(a, b, scratch, cmp);
            PhysicalMergeCore(c, d, scratch, cmp);
            PhysicalMergeCore(leftRegion, rightRegion, scratch, cmp);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnreachableQuad() =>
        throw new InvalidOperationException(
            "Unreachable: both pairs fitting into scratch implies the full-scratch path was taken.");
}

/// <summary>Eager merge-only sort — the introsort-style shield for Task 9's stable
/// quicksort. Upstream, recursion-limit exhaustion calls glidesort(..., eager_smallsort:
/// true) (stable_quicksort.rs:403), whose eager path small-sorts every run immediately
/// (glidesort.rs:63-72) before the logical merge tree runs. This port expresses that shape
/// directly and deterministically: runs of at most GlideSmallSort.SmallSort elements are
/// small-sorted, then merged bottom-up pairwise with in-place physical merges — O(n log n)
/// worst case, no quicksort recursion, no randomness.</summary>
internal static class EagerSort
{
    /// <summary>Sorts v in place. scratch is merge workspace; the upstream contract asks
    /// for at least v.Length / 2 elements (smaller scratch degrades to PhysicalMerge's
    /// in-place swap path but stays correct).</summary>
    internal static void Sort<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        int n = v.Length;
        if (n < 2)
            return;
        if (n <= GlideSmallSort.SmallSort)
        {
            GlideSmallSort.Sort(v, cmp);
            return;
        }

        // Eager small-sort: every run is at most SmallSort long, so it can always be
        // sorted regardless of scratch space (glidesort.rs:63-65).
        for (int i = 0; i < n; i += GlideSmallSort.SmallSort)
            GlideSmallSort.Sort(v.Slice(i, Math.Min(GlideSmallSort.SmallSort, n - i)), cmp);

        // Bottom-up pairwise physical merges with doubling width — O(n log n).
        int width = GlideSmallSort.SmallSort;
        while (width < n)
        {
            for (int i = 0; i + width < n; i += width << 1)
            {
                int rightLen = Math.Min(width, n - i - width);
                GlideMerge.PhysicalMerge(v.Slice(i, width), v.Slice(i + width, rightLen), scratch, cmp);
            }
            if (width > (n - 1) >> 1)
                break; // The next doubling already covers n; also guards int overflow.
            width <<= 1;
        }
    }
}
