// Ported from https://github.com/orlp/driftsort, MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — quicksort.rs (stable_quicksort, stable_partition + PartitionState) and
// pivot.rs (choose_pivot, median3_rec, median3).
//
// Port decisions (per the task brief):
//  - upstream `Option<&T>` ancestor pivot becomes PivotRef<T> (below), a by-value
//    optional — C# generics cannot uniformly express T?, and ruling: use it for ALL T.
//  - upstream `has_direct_interior_mutability` takes the safe branch for every T: the
//    pivot is always copied into a local before partitioning (upstream pivot_copy
//    discipline, quicksort.rs:46) and always re-copied into its scratch slot after the
//    partition loop (quicksort.rs:156-158, upstream's interior-mutability branch). Cost:
//    one T copy per partition. Upstream reads v[pivotPos] fresh through a raw pointer per
//    comparison (quicksort.rs:113/131); the port compares against the local snapshot —
//    identical for any comparator that does not mutate elements mid-partition, and a
//    misbehaving comparator leaves state unspecified per the ports' established policy.
//  - `intrinsics::abort` on the scratch/pivot contract (quicksort.rs:97-99) throws here.
//  - The equal-partition call (quicksort.rs:69) inverts the comparator via
//    `|a, b| !is_less(b, a)`; the port passes an `invert: bool` into StablePartition
//    rather than allocating a delegate per call.
//  - limit == 0 falls back to EagerSortImpl below (runs of <= threshold via SortSmall,
//    then bottom-up DriftMerge) — upstream calls drift::sort(eager: true), which Task
//    13's main loop will own itself; this local copy is the quicksort's own seam.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>DriftSort stable quicksort (upstream quicksort.rs + pivot.rs): sorts v in
/// place around pseudo-median-of-3 pivots with ancestor-pivot equal batching — O(n log k)
/// for k distinct values. The partition is stable and uses scratch as its output buffer
/// before copying back.</summary>
internal static class DriftQuicksort
{
    /// <summary>Upstream PSEUDO_MEDIAN_REC_THRESHOLD (pivot.rs:2): at or above this
    /// total size, pivot selection recurses into approximate medians of the sampled
    /// regions.</summary>
    internal const int PseudoMedianRecThreshold = 64;

    /// <summary>stable_quicksort (quicksort.rs:14-80): sorts v recursively. scratch must
    /// supply at least max(v.Length, DriftSmallSort.MinSmallSortScratchLen) elements —
    /// the partition needs v.Length, the small-sort base case 50. limit, initialized to
    /// c*log(n) by the caller, bounds recursion depth; its exhaustion falls back to the
    /// eager merge sort (upstream drift::sort(v, scratch, true)). leftAncestorPivot is
    /// the value of the pivot the parent frame partitioned around (None at the top):
    /// when the freshly chosen pivot is not greater than it, all equal elements are
    /// batched to the left in one inverted partition and skipped. NoInlining mirrors
    /// upstream's #[inline(never)].</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void StableQuicksort<T, TC>(
        Span<T> v, Span<T> scratch, int limit, PivotRef<T> leftAncestorPivot, TC cmp)
        where TC : struct, IIsLess<T>
    {
        while (true)
        {
            int len = v.Length;

            if (len <= DriftSmallSort.Threshold<T>())
            {
                DriftSmallSort.SortSmall(v, scratch, cmp);
                return;
            }

            if (limit == 0)
            {
                // Too many bad pivots: switch to the O(n log n) fallback — driftsort
                // in eager mode (quicksort.rs:29-34).
                EagerSortImpl(v, scratch, cmp);
                return;
            }
            limit--;

            int pivotPos = ChoosePivot(v, cmp);
            ref T vBase = ref MemoryMarshal.GetReference(v);
            // ChoosePivot promises a valid pivot index (upstream intrinsics::assume).

            // The pivot value is copied out (upstream pivot_copy, quicksort.rs:46) and
            // carried to the right recursion as the ancestor pivot — for every T here
            // (see the header: the interior-mutability-safe branch, taken universally).
            T pivotCopy = Unsafe.Add(ref vBase, pivotPos);
            var pivotRef = new PivotRef<T>(pivotCopy);

            // If the chosen pivot equals the left ancestor, partition putting equal
            // elements on the left and do not recurse on them (quicksort.rs:57-66).
            bool performEqualPartition = leftAncestorPivot.Has
                && !cmp.IsLess(in leftAncestorPivot.Value, in Unsafe.Add(ref vBase, pivotPos));

            int leftPartitionLen = 0;
            if (!performEqualPartition)
            {
                leftPartitionLen = StablePartition(v, scratch, pivotPos, pivotGoesLeft: false, invert: false, cmp);
                performEqualPartition = leftPartitionLen == 0;
            }

            if (performEqualPartition)
            {
                int midEq = StablePartition(v, scratch, pivotPos, pivotGoesLeft: true, invert: true, cmp);
                v = v.Slice(midEq);
                leftAncestorPivot = default;
                continue;
            }

            // Process the left side on the next loop iteration, the right side by
            // recursion carrying the pivot as ancestor (quicksort.rs:75-78).
            Span<T> right = v.Slice(leftPartitionLen);
            v = v.Slice(0, leftPartitionLen);
            StableQuicksort(right, scratch, limit, pivotRef, cmp);
        }
    }

    /// <summary>The limit-0 fallback (quicksort.rs:29-34 calls drift::sort eager: true —
    /// drift.rs:194-212): eager small-sort of every run of at most Threshold&lt;T&gt;()
    /// elements via SortSmall, then bottom-up pairwise DriftMerge with doubling width —
    /// O(n log n), no quicksort recursion. Task 13's main loop owns its own eager path;
    /// this copy is the quicksort's internal seam only.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EagerSortImpl<T, TC>(Span<T> v, Span<T> scratch, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int n = v.Length;
        if (n < 2)
            return;
        int threshold = DriftSmallSort.Threshold<T>();
        if (n <= threshold)
        {
            DriftSmallSort.SortSmall(v, scratch, cmp);
            return;
        }

        // Eager small-sort: every run is at most threshold long, sortable regardless
        // of scratch size (the runs sort in place).
        for (int i = 0; i < n; i += threshold)
            DriftSmallSort.SortSmall(v.Slice(i, Math.Min(threshold, n - i)), scratch, cmp);

        // Bottom-up pairwise merges with doubling width.
        int width = threshold;
        while (width < n)
        {
            for (int i = 0; i + width < n; i += width << 1)
            {
                int rightLen = Math.Min(width, n - i - width);
                DriftMerge.Merge(v.Slice(i, width + rightLen), scratch, width, cmp);
            }
            if (width > (n - 1) >> 1)
                break; // The next doubling already covers n; also guards int overflow.
            width <<= 1;
        }
    }

    /// <summary>stable_partition (quicksort.rs:88-181): partitions v around
    /// p = v[pivotPos] and returns the number of elements comparing less than p. The
    /// relative order of the &lt; p elements and of the &gt;= p elements is preserved — a
    /// stable partition. scratch must supply at least v.Length elements. invert selects
    /// upstream's reversed comparator `|a, b| !is_less(b, a)` (quicksort.rs:69 — the
    /// equal-batch path): under it, elements &gt;= p compare "less" and go left.
    /// pivotGoesLeft places the pivot element itself and stays independent of invert.</summary>
    internal static int StablePartition<T, TC>(
        Span<T> v, Span<T> scratch, int pivotPos, bool pivotGoesLeft, bool invert, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (scratch.Length < len || pivotPos >= len)
            ThrowContractViolation(scratch.Length, len, pivotPos);

        ref T vBase = ref MemoryMarshal.GetReference(v);
        ref T scratchBase = ref MemoryMarshal.GetReference(scratch);

        // The core idea: values that compare less-than-pivot are written to the left
        // side of scratch, the others to the right side in reverse — see PartitionState.
        // The pivot is always copied into a local first (upstream pivot_copy
        // discipline; see the class header).
        T pivot = Unsafe.Add(ref vBase, pivotPos);

        var state = new PartitionState<T>(ref vBase, ref scratchBase, len);

        int pivotInScratch = 0;
        int loopEndPos = pivotPos;

        // This loop is equivalent to calling state.PartitionOne exactly len times
        // (quicksort.rs:121-152).
        while (true)
        {
            // The inner loop is unrolled 4x for small types (quicksort.rs:127-136) —
            // a size-gated unroll in upstream, ported as such.
            if (Unsafe.SizeOf<T>() <= 16)
            {
                const int UnrollLen = 4;
                int unrollEnd = loopEndPos >= UnrollLen - 1 ? loopEndPos - (UnrollLen - 1) : 0;
                while (state.Scan < unrollEnd)
                {
                    state.PartitionOne(LessThanPivot(in state.Current, in pivot, invert, cmp));
                    state.PartitionOne(LessThanPivot(in state.Current, in pivot, invert, cmp));
                    state.PartitionOne(LessThanPivot(in state.Current, in pivot, invert, cmp));
                    state.PartitionOne(LessThanPivot(in state.Current, in pivot, invert, cmp));
                }
            }

            while (state.Scan < loopEndPos)
                state.PartitionOne(LessThanPivot(in state.Current, in pivot, invert, cmp));

            if (loopEndPos == len)
                break;

            // We avoid comparing the pivot with itself (deadlocks for certain
            // comparison operators, quicksort.rs:147-149) and record its location.
            pivotInScratch = state.PartitionOne(pivotGoesLeft);

            loopEndPos = len;
        }

        // The pivot is re-copied into its correct position from the array
        // (quicksort.rs:154-158 — upstream's interior-mutability branch, taken for
        // every T here; see the class header).
        Unsafe.Add(ref scratchBase, pivotInScratch) = Unsafe.Add(ref vBase, pivotPos);

        // partition_one having run exactly len times filled scratch with a permutation
        // of v. Copy scratch[0..numLeft] to v directly, then the >= p side in reverse
        // order (quicksort.rs:166-177).
        scratch.Slice(0, state.NumLeft).CopyTo(v);
        for (int i = 0; i < len - state.NumLeft; i++)
            Unsafe.Add(ref vBase, state.NumLeft + i) = Unsafe.Add(ref scratchBase, len - 1 - i);

        return state.NumLeft;
    }

    /// <summary>The partition comparison (quicksort.rs:131's is_less over the scan
    /// element and the pivot, and its inverted closure |a, b| !is_less(b, a) at :69):
    /// towards_left for the scanned element cur — !pivot &lt; cur under inversion, else
    /// cur &lt; pivot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LessThanPivot<T, TC>(in T cur, in T pivot, bool invert, TC cmp)
        where TC : struct, IIsLess<T>
        => invert ? !cmp.IsLess(in pivot, in cur) : cmp.IsLess(in cur, in pivot);

    /// <summary>choose_pivot (pivot.rs:8-31): samples three size-(n/8) regions of v —
    /// [0, n/8), [4*n/8, 5*n/8), [7*n/8, n) — and returns a pseudo-median-of-3 index,
    /// recursing (median3_rec) above PseudoMedianRecThreshold for an overall sample of
    /// O(n^0.528) elements. Algorithm taken from glidesort by Orson Peters.</summary>
    internal static int ChoosePivot<T, TC>(Span<T> v, TC cmp) where TC : struct, IIsLess<T>
    {
        ref T vBase = ref MemoryMarshal.GetReference(v);
        int len = v.Length;
        int lenDiv8 = len / 8;

        int a = 0;             // [0, floor(n/8))
        int b = lenDiv8 * 4;   // [4*floor(n/8), 5*floor(n/8))
        int c = lenDiv8 * 7;   // [7*floor(n/8), 8*floor(n/8))

        if (len < PseudoMedianRecThreshold)
            return Median3(ref vBase, a, b, c, cmp);
        return Median3Rec(ref vBase, a, b, c, lenDiv8, cmp);
    }

    /// <summary>median3_rec (pivot.rs:41-59): an approximate median of 3 elements from
    /// sections a, b, c, or recursively from an approximation of each when they are
    /// large enough — dividing by 8 per level keeps the depth logarithmic.</summary>
    private static int Median3Rec<T, TC>(ref T vBase, int a, int b, int c, int n, TC cmp)
        where TC : struct, IIsLess<T>
    {
        if (n * 8 >= PseudoMedianRecThreshold)
        {
            int n8 = n / 8;
            a = Median3Rec(ref vBase, a, a + n8 * 4, a + n8 * 7, n8, cmp);
            b = Median3Rec(ref vBase, b, b + n8 * 4, b + n8 * 7, n8, cmp);
            c = Median3Rec(ref vBase, c, c + n8 * 4, c + n8 * 7, n8, cmp);
        }
        return Median3(ref vBase, a, b, c, cmp);
    }

    /// <summary>median3 (pivot.rs:65-84): the median of the three elements at offsets
    /// a, b, c, returned as the chosen offset. The x == y toggle selects max(b, c) when
    /// both compare above a and min(b, c) when both compare below.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Median3<T, TC>(ref T vBase, int a, int b, int c, TC cmp)
        where TC : struct, IIsLess<T>
    {
        ref T ea = ref Unsafe.Add(ref vBase, a);
        ref T eb = ref Unsafe.Add(ref vBase, b);
        ref T ec = ref Unsafe.Add(ref vBase, c);
        bool x = cmp.IsLess(in ea, in eb);
        bool y = cmp.IsLess(in ea, in ec);
        if (x == y)
        {
            // x=y=false: b, c <= a, return max(b, c). x=y=true: a < b, c, return
            // min(b, c). Toggling b < c with x gives both (pivot.rs:71-79).
            bool z = cmp.IsLess(in eb, in ec);
            return z ^ x ? c : b;
        }
        // Either c <= a < b or b <= a < c: a is the median.
        return a;
    }

    /// <summary>Contract violation — upstream aborts (quicksort.rs:97-99), this seam
    /// throws; reachable never by a conforming caller.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowContractViolation(int scratchLen, int len, int pivotPos) =>
        throw new InvalidOperationException(
            $"stable_partition contract violation: scratch.Length ({scratchLen}) < v.Length ({len}) or pivotPos ({pivotPos}) >= v.Length.");
}

/// <summary>PartitionState (quicksort.rs:183-239): the branchless partition core. The
/// current scan element is written to the growing left side of scratch (offset
/// numLeft) or to the shrinking right side (offset scratchRev + numLeft, where
/// scratchRev counts down from len); the int-offset ternary and boolean arithmetic
/// mirror upstream's conditional pointer select and towards_left-as-usize addition.</summary>
internal ref struct PartitionState<T>
{
    private readonly ref T _vBase;      // scan source base
    private readonly ref T _scratchBase; // scratch base
    private int _scan;     // current element offset, scans left to right through v
    private int _numLeft;  // number of elements written to scratch's left side
    private int _scratchRev; // reverse output offset; starts at len, pre-decremented

    /// <summary>PartitionState::new (quicksort.rs:199-206).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PartitionState(ref T vBase, ref T scratchBase, int len)
    {
        _vBase = ref vBase;
        _scratchBase = ref scratchBase;
        _scan = 0;
        _numLeft = 0;
        _scratchRev = len;
    }

    /// <summary>The current scan offset (upstream's state.scan pointer position).</summary>
    internal int Scan => _scan;

    /// <summary>The number of elements that went to the left side.</summary>
    internal int NumLeft => _numLeft;

    /// <summary>The current scan element (upstream's deref of state.scan).</summary>
    internal ref T Current => ref Unsafe.Add(ref _vBase, _scan);

    /// <summary>partition_one (quicksort.rs:216-239): writes the current scan element
    /// to the growing left or right side of scratch depending on towardsLeft, and
    /// advances the scan. May be called at most len times; called exactly len times it
    /// leaves scratch holding a permutation of v with numLeft &lt;= len. Returns the
    /// scratch offset written (upstream returns the destination pointer — the driver
    /// records it for the pivot slot).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int PartitionOne(bool towardsLeft)
    {
        // Now scratchRev == len - (i + 1) after the (i+1)th call, so
        // scratchRev + numLeft < len stays in bounds (quicksort.rs:219-232).
        _scratchRev -= 1;

        // dst = towardsLeft ? scratchBase + numLeft : scratchRev + numLeft — the
        // conditional offset select keeps this branchless.
        int dstOff = towardsLeft ? _numLeft : _scratchRev + _numLeft;
        Unsafe.Add(ref _scratchBase, dstOff) = Unsafe.Add(ref _vBase, _scan);

        _numLeft += towardsLeft ? 1 : 0;
        _scan += 1;
        return dstOff;
    }
}

/// <summary>The ancestor-pivot representation (upstream Option&lt;&amp;T&gt;,
/// quicksort.rs:18): a by-value optional element, Has = false meaning None. Chosen over
/// C# T? because a generic T constrained neither to class nor struct cannot uniformly
/// take nullable annotations, and a ref into an ancestor frame cannot be carried by
/// value through the driver loop once every T is treated as possibly having interior
/// mutability — the copy is exactly upstream's pivot_copy (quicksort.rs:46).</summary>
internal readonly struct PivotRef<T>
{
    /// <summary>Whether the optional carries a value (upstream Some).</summary>
    public readonly bool Has;

    /// <summary>The carried pivot value (upstream *Option::unwrap).</summary>
    public readonly T Value;

    public PivotRef(T value)
    {
        Has = true;
        Value = value;
    }
}
