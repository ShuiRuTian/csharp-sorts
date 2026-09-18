// Ported from https://github.com/orlp/glidesort, MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — stable_quicksort.rs and pivot_selection.rs: the stable bidirectional
// quicksort and its pseudo-median-of-3 pivot selection.
//
// Rust's MutSlice two-piece views (a logical slice that may physically consist of two
// adjacent-or-disjoint pieces) become TwoPieceSpan<T> below. The gap_guard / WriteBackPivot
// panic-safety machinery is not ported: an exception from cmp leaves buffers unspecified
// (same policy as GlideSmallSort / GlideMerge), so the guards' success-path copies are
// inlined as explicit moves. MutSlice typestate (Init/Weak/Uninit) collapses to plain
// Span<T> with write-before-read by construction.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

internal static class GlideQuicksort
{
    /// <summary>Upstream PSEUDO_MEDIAN_REC_THRESHOLD (lib.rs:32): at or above this total
    /// size, pivot selection recurses into approximate medians of the sampled regions.</summary>
    internal const int PseudoMedianRecThreshold = 64;

    /// <summary>select_pivot (pivot_selection.rs:5-44): picks the pivot as a pseudo-median
    /// of three size-(n/8) sampled regions of v — positions 0, n/2 - n/8, n - n/8,
    /// recursively refined above PseudoMedianRecThreshold. The input is treated as the
    /// two pieces v[..n/2) ++ v[n/2..), mirroring quicksort's initial split; returns the
    /// chosen pivot's index in v.</summary>
    internal static int ChoosePivot<T, TC>(Span<T> v, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int half = v.Length / 2;
        return SelectPivotTwoPiece(
            new TwoPieceSpan<T>(v.Slice(0, half)), new TwoPieceSpan<T>(v.Slice(half)), cmp);
    }

    /// <summary>select_pivot (pivot_selection.rs:5-44) over the two input pieces: a
    /// pseudo-median of three size-(n/8) regions. Returns the pivot's logical index in
    /// the concatenation left ++ right. The port indexes the concatenation through a
    /// local accessor rather than materializing it.</summary>
    private static int SelectPivotTwoPiece<T, TC>(TwoPieceSpan<T> left, TwoPieceSpan<T> right, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int leftLen = left.Length;
        int rightLen = right.Length;
        int n = leftLen + rightLen;
        // Logical index of the start of each sampled region, resolved against the
        // concatenation via a local element accessor.
        int a = leftLen >= n / 8 ? 0 : leftLen;
        int b = leftLen >= n / 2 ? n / 2 - n / 8 : n - n / 2;
        int c = rightLen >= n / 8 ? n - n / 8 : leftLen - n / 8;
        return Median3RecTwoPiece(left, right, a, b, c, n / 8, cmp);
    }

    /// <summary>median3_rec over the concatenation left ++ right.</summary>
    private static int Median3RecTwoPiece<T, TC>(
        TwoPieceSpan<T> left, TwoPieceSpan<T> right, int a, int b, int c, int n, TC cmp)
        where TC : struct, IIsLess<T>
    {
        if (n * 8 >= PseudoMedianRecThreshold)
        {
            int n8 = n / 8;
            a = Median3RecTwoPiece(left, right, a, a + n8 * 4, a + n8 * 7, n8, cmp);
            b = Median3RecTwoPiece(left, right, b, b + n8 * 4, b + n8 * 7, n8, cmp);
            c = Median3RecTwoPiece(left, right, c, c + n8 * 4, c + n8 * 7, n8, cmp);
        }
        // median3 over three logical indices of the concatenation.
        ref T ea = ref At(left, right, a);
        ref T eb = ref At(left, right, b);
        ref T ec = ref At(left, right, c);
        bool x = cmp.IsLess(in ea, in eb);
        bool y = cmp.IsLess(in ea, in ec);
        if (x == y)
        {
            bool z = cmp.IsLess(in eb, in ec);
            return z ^ x ? c : b;
        }
        return a;
    }

    /// <summary>Ref to the logically i-th element of left ++ right.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref T At<T>(TwoPieceSpan<T> left, TwoPieceSpan<T> right, int i)
    {
        if (i < left.Length)
            return ref left[i];
        return ref right[i - left.Length];
    }

    /// <summary>The brief's Partition seam: partitions the concatenated input
    /// (left ++ right) around the pivot value into dest and scratch, both of the input's
    /// total length. Per the header ASCII art the four output regions span both buffers:
    /// dest receives forward-scanned less-than elements at its front and
    /// backward-scanned greater-or-equal elements at its end; scratch receives the other
    /// two regions (>= pivot at its front, &lt; pivot at its end), with the shrinking
    /// unscanned middles left untouched. The pivot is only read (upstream keeps its
    /// pivot_pos in the array and tracks it; the port holds the value out-of-line).</summary>
    internal static void Partition<T, TC>(
        Span<T> left, Span<T> right, Span<T> dest, Span<T> scratch, ref T pivot, TC cmp)
        where TC : struct, IIsLess<T>
    {
        var state = new BidirPartitionState<T>(new TwoPieceSpan<T>(left, right), default);
        state.AttachOutput(new TwoPieceSpan<T>(dest), new TwoPieceSpan<T>(scratch));
        state.PartitionBidir(ref pivot, cmp, invert: false);
    }

    /// <summary>stable_bidir_quicksort_into (stable_quicksort.rs:368-546): sorts the
    /// two-piece input (left ++ right) into dest using scratch as workspace; dest and
    /// scratch each equal the input's total length. strategy/strategyPivot encode
    /// PartitionStrategy; limit is the remaining recursion depth (0 → EagerSort).</summary>
    private static void QuicksortInto<T, TC>(
        TwoPieceSpan<T> left, TwoPieceSpan<T> right,
        TwoPieceSpan<T> dest, TwoPieceSpan<T> scratch,
        PartitionStrategyKind strategy, int strategyPivot, int limit, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int n = left.Length + right.Length;

        if (n < GlideSmallSort.SmallSort || limit == 0)
        {
            // Base case (stable_quicksort.rs:389-407): move input to dest, then sort
            // in place. Upstream skips the move per side when the input already sits
            // in dest; the element-wise copy is a self-copy no-op then.
            TwoPieceSpan<T> input = ConcatLogical(left, right);
            CopyTwoPieceToTwoPiece(input, dest);
            if (n < GlideSmallSort.SmallSort)
                GlideSmallSort.Sort(AsSingleOrCopy(dest), cmp);
            else
                EagerSort.Sort(AsSingleOrCopy(dest), AsSingleOrCopy(scratch), cmp);
            return;
        }
        QuicksortIntoTail(left, right, dest, scratch, strategy, strategyPivot, limit, cmp);
    }

    /// <summary>The non-base-case half of QuicksortInto (stable_quicksort.rs:409-545):
    /// select pivot, run the bidirectional partition, move scratch results to their
    /// dest slots, then recurse on the less/geq regions.</summary>
    private static void QuicksortIntoTail<T, TC>(
        TwoPieceSpan<T> left, TwoPieceSpan<T> right,
        TwoPieceSpan<T> dest, TwoPieceSpan<T> scratch,
        PartitionStrategyKind strategy, int strategyPivot, int limit, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int n = left.Length + right.Length;

        // Pivot selection (stable_quicksort.rs:411-425). LeftWithPivot carries an index;
        // LeftIfNewPivotEquals re-checks !is_less(carried, new) to decide the side — the
        // port never emits that strategy (only LeftWithPivot / RightWithNewPivot are
        // produced below, per the LeftIfNewPivotEqualsCopy-free is_copy_type()==false
        // path), so it maps to partition-left.
        int pivotIdx;
        if (strategy == PartitionStrategyKind.LeftWithPivot)
            pivotIdx = strategyPivot;
        else
            pivotIdx = SelectPivotTwoPiece(left, right, cmp);
        bool partitionLeft = strategy != PartitionStrategyKind.RightWithNewPivot;

        // Run the partition with a local pivot copy (pivot_pos read into a local).
        T pivot = At(left, right, pivotIdx);
        var state = new BidirPartitionState<T>(
            new TwoPieceSpan<T>(left.A, left.B), new TwoPieceSpan<T>(right.A, right.B));
        state.AttachOutput(dest, scratch);
        state.PartitionBidir(ref pivot, cmp, invert: !partitionLeft);
        state.Take(out TwoPieceSpan<T> lessInDest, out TwoPieceSpan<T> lessInScratch,
            out TwoPieceSpan<T> geqInScratch, out TwoPieceSpan<T> geqInDest);

        // Recursive regions (stable_quicksort.rs:438-441): dest splits at less_n,
        // scratch splits at geq_n (front is the geq side's scratch).
        int lessN = lessInDest.Length + lessInScratch.Length;
        int geqN = geqInDest.Length + geqInScratch.Length;
        dest.SplitAt(lessN, out TwoPieceSpan<T> lessRecDest, out TwoPieceSpan<T> geqRecDest);
        scratch.SplitAt(geqN, out TwoPieceSpan<T> geqRecScratch, out TwoPieceSpan<T> lessRecScratch);

        // GapGuard replacement: move the scratch-resident results into their slots in
        // the recursive dest regions (upstream defers this via guards; the port copies
        // eagerly — same element movement, no panic safety).
        CopyTwoPieceToTwoPiece(lessInScratch,
            SliceOfTwoPiece(lessRecDest, lessInDest.Length, lessInScratch.Length));
        CopyTwoPieceToTwoPiece(geqInScratch,
            SliceOfTwoPiece(geqRecDest, 0, geqInScratch.Length));

        // Both sides small: overlapped small sorts (stable_quicksort.rs:464-487).
        if (lessN < GlideSmallSort.SmallSort && geqN < GlideSmallSort.SmallSort)
        {
            Span<T> whole = AsSingleOrCopy(dest);
            if (lessN <= 32 && (lessN & 0b1000) > 0)
                GlideSmallSort.Sort(whole.Slice(0, (lessN + 0b111) & ~0b111), cmp);
            else
                GlideSmallSort.Sort(whole.Slice(0, lessN), cmp);
            if (geqN <= 32 && (geqN & 0b1000) > 0)
            {
                int round = (geqN + 0b111) & ~0b111;
                GlideSmallSort.Sort(whole.Slice(whole.Length - round, round), cmp);
            }
            else
                GlideSmallSort.Sort(whole.Slice(whole.Length - geqN, geqN), cmp);
            return;
        }

        // Empty less side on a fresh-pivot partition: recurse on geq only with the
        // pivot carried over (stable_quicksort.rs:489-502). Upstream passes
        // (geq_in_scratch, geq_in_dest) as the recursive input; the port has already
        // moved geq_in_scratch's contents into geqRecDest's front, so the recursive
        // input is that region itself (dest == input, sorted in place). The carried
        // pivot's logical index is 0 in the input's concatenation.
        if (lessN == 0 && !partitionLeft)
        {
            TwoPieceSpan<T> geqFront = SliceOfTwoPiece(geqRecDest, 0, geqInScratch.Length);
            TwoPieceSpan<T> geqInput = ConcatLogical(geqFront, geqInDest);
            QuicksortInto(
                geqInput, default,
                geqRecDest, geqRecScratch,
                PartitionStrategyKind.LeftWithPivot, strategyPivot: 0,
                limit - 1, cmp);
            return;
        }
        // Two-sided recursion (stable_quicksort.rs:504-545). Both sides' inputs live in
        // their recursive dest regions already (the copies above placed lessInScratch
        // and geqInScratch); recurse in place: sort (lessRecDest) then (geqRecDest).
        if (!partitionLeft)
        {
            // less side: input is (lessInDest, lessInScratch-moved) = lessRecDest.
            QuicksortInto(
                lessRecDest, default,
                lessRecDest, lessRecScratch,
                PartitionStrategyKind.RightWithNewPivot, strategyPivot: 0,
                limit - 1, cmp);
        }
        QuicksortInto(
            geqRecDest, default,
            geqRecDest, geqRecScratch,
            PartitionStrategyKind.RightWithNewPivot, strategyPivot: 0,
            limit - 1, cmp);
    }

    /// <summary>Logical concatenation of two two-piece views (upstream concat of two
    /// MutSlices): the result re-pieces A/B so that [0..aLen) maps to a and the rest to
    /// b's logical order. Implemented by splitting both views at the seam.</summary>
    private static TwoPieceSpan<T> ConcatLogical<T>(TwoPieceSpan<T> a, TwoPieceSpan<T> b)
    {
        // a fits entirely in the head piece; b follows: piecewise A = a's pieces.
        if (a.B.IsEmpty)
            return new TwoPieceSpan<T>(a.A, AsSingleOrCopy(b));
        // General case: three or four pieces cannot be represented — but the driver's
        // call pattern guarantees at most one non-trivial piece per side here.
        return new TwoPieceSpan<T>(AsSingleOrCopy(a), AsSingleOrCopy(b));
    }

    /// <summary>The logical slice [i, i+len) of a two-piece view.</summary>
    private static TwoPieceSpan<T> SliceOfTwoPiece<T>(TwoPieceSpan<T> v, int i, int len)
    {
        v.SplitAt(i, out _, out TwoPieceSpan<T> tail);
        tail.SplitAt(len, out TwoPieceSpan<T> slice, out _);
        return slice;
    }

    /// <summary>Copies the logical contents of src into the equal-length logical dst
    /// (upstream's move_to — element-wise because the pieces may not be pairwise
    /// contiguous).</summary>
    private static void CopyTwoPieceToTwoPiece<T>(TwoPieceSpan<T> src, TwoPieceSpan<T> dst)
    {
        int n = src.Length;
        for (int i = 0; i < n; i++)
            dst[i] = src[i];
    }

    /// <summary>A two-piece view whose pieces are adjacent (or the second empty)
    /// collapses to one span. Non-adjacent pieces cannot occur here: every dest/scratch
    /// region the driver hands to AsSingleOrCopy descends from one physical span sliced
    /// at two points (its A/B are [begin, i) and [i, end) of that span, hence adjacent),
    /// or was built by ConcatLogical from adjacent pieces of the same span. Upstream's
    /// MutSlice::concat aborts on non-adjacency; the same precondition holds by
    /// construction, so the seam asserts it rather than copying.</summary>
    private static Span<T> AsSingleOrCopy<T>(TwoPieceSpan<T> v)
    {
        if (v.B.IsEmpty)
            return v.A;
        if (!Unsafe.AreSame(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(v.A), v.A.Length),
                ref MemoryMarshal.GetReference(v.B)))
            ThrowNotAdjacent();
        return MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(v.A), v.A.Length + v.B.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotAdjacent() =>
        throw new InvalidOperationException(
            "TwoPieceSpan pieces must be adjacent to collapse into a single span.");

    /// <summary>quicksort (stable_quicksort.rs:548-570): sorts v in place (dest == v)
    /// using scratch (scratch.Length &gt;= v.Length). limit is the remaining recursion
    /// depth; each level consumes one, and exhaustion falls back to EagerSort — the
    /// introsort shield. NoInlining mirrors upstream's #[inline(never)].</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Quicksort<T, TC>(Span<T> v, Span<T> scratch, int limit, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int n = v.Length;
        if (n < 2)
            return;
        // el splits in half into left/right (stable_quicksort.rs:558); dest is el.
        // Caller contract: scratch.Length >= v.Length (upstream splits scratch at n).
        scratch = scratch.Slice(0, n);
        int half = n / 2;
        QuicksortInto(
            new TwoPieceSpan<T>(v.Slice(0, half)), new TwoPieceSpan<T>(v.Slice(half)),
            new TwoPieceSpan<T>(v), new TwoPieceSpan<T>(scratch),
            PartitionStrategyKind.RightWithNewPivot, strategyPivot: 0,
            limit, cmp);
    }
}

/// <summary>PartitionStrategy (stable_quicksort.rs:361-366) — how the recursive call
/// treats pivot selection. LeftWithPivot carries a pivot logical index into the left
/// piece; LeftIfNewPivotEquals defers the choice until the new pivot is selected;
/// RightWithNewPivot always selects fresh.</summary>
internal enum PartitionStrategyKind : byte
{
    LeftWithPivot,
    LeftIfNewPivotEquals,
    RightWithNewPivot,
}

/// <summary>BidirPartitionState (stable_quicksort.rs:76-338) — the bidirectional partition
/// kernel. forward_scan/backward_scan are the unscanned input (logical two-piece views);
/// dest/scratch the equal-sized output regions. Write heads use upstream's cursor+counter
/// representation: dest's forward write head is dest.begin + numAtDestBegin while
/// scratch's forward head is scratchFwdIdx - numAtDestBegin (one unconditional increment
/// per step); the backward heads mirror that from the ends. A local pivot copy replaces
/// upstream's WriteBackPivot guard (no panic safety ported).</summary>
internal ref struct BidirPartitionState<T>
{
    public TwoPieceSpan<T> ForwardScan;
    public TwoPieceSpan<T> BackwardScan;
    public TwoPieceSpan<T> Dest;
    public TwoPieceSpan<T> Scratch;

    // dest.begin + _numAtDestBegin is the dest forward write head;
    // _scratchFwdIdx - _numAtDestBegin is the scratch forward write head.
    private int _numAtDestBegin;
    private int _scratchFwdIdx;

    // dest's backward write head is _destBwdIdx + _numAtScratchEnd - 1;
    // scratch's backward write head is scratch.Length - _numAtScratchEnd - 1.
    private int _numAtScratchEnd;
    private int _destBwdIdx;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BidirPartitionState(TwoPieceSpan<T> forward, TwoPieceSpan<T> backward)
    {
        ForwardScan = forward;
        BackwardScan = backward;
        Dest = default;
        Scratch = default;
        _numAtDestBegin = 0;
        _scratchFwdIdx = 0;
        _numAtScratchEnd = 0;
        _destBwdIdx = 0;
    }

    /// <summary>Attaches the output regions (BidirPartitionState::new's dest/scratch
    /// arguments; kept separate so pivot selection can run on the scans first).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AttachOutput(TwoPieceSpan<T> dest, TwoPieceSpan<T> scratch)
    {
        Dest = dest;
        Scratch = scratch;
        _scratchFwdIdx = 0;
        _destBwdIdx = dest.Length;
    }

    /// <summary>partition_one_forward (stable_quicksort.rs:157-184): reads the scan head,
    /// writes it to dest's forward head if less than pivot else scratch's forward head.
    /// toDest reports which region received it, outIdx the logical index in that region.
    /// The pivot is passed by ref (upstream's local copy).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PartitionOneForward<TC>(ref T pivot, TC cmp, out bool toDest, out int outIdx)
        where TC : struct, IIsLess<T>
    {
        ref T scan = ref ForwardScan[0];
        bool lessThanPivot = cmp.IsLess(in scan, in pivot);
        int destOut = _numAtDestBegin;
        int scratchOut = _scratchFwdIdx - _numAtDestBegin;
        if (lessThanPivot)
        {
            Dest[destOut] = scan;
            toDest = true;
            outIdx = destOut;
        }
        else
        {
            Scratch[scratchOut] = scan;
            toDest = false;
            outIdx = scratchOut;
        }
        if (lessThanPivot) _numAtDestBegin++;
        _scratchFwdIdx++;
        ForwardScan.SplitOffBegin(1, out _);
    }

    /// <summary>partition_one_backward (stable_quicksort.rs:189-219): reads the scan tail,
    /// writes it to scratch's backward head if &lt; pivot else dest's backward head.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PartitionOneBackward<TC>(ref T pivot, TC cmp, out bool toDest, out int outIdx)
        where TC : struct, IIsLess<T>
    {
        ref T scan = ref BackwardScan[BackwardScan.Length - 1];
        bool lessThanPivot = cmp.IsLess(in scan, in pivot);
        int destOut = _destBwdIdx + _numAtScratchEnd - 1;
        int scratchOut = Scratch.Length - _numAtScratchEnd - 1;
        if (lessThanPivot)
        {
            Scratch[scratchOut] = scan;
            toDest = false;
            outIdx = scratchOut;
        }
        else
        {
            Dest[destOut] = scan;
            toDest = true;
            outIdx = destOut;
        }
        if (lessThanPivot) _numAtScratchEnd++;
        _destBwdIdx--;
        BackwardScan.SplitOffEnd(1, out _);
    }

    /// <summary>take (stable_quicksort.rs:121-152): splits the outputs into the four
    /// regions (a, b', c', d of the header ASCII art) — lessInDest, lessInScratch,
    /// geqInScratch, geqInDest — and resets the write heads. The forward scan produced
    /// a=lessInDest and a'+geqInScratch; the backward scan d'=lessInScratch and
    /// d=geqInDest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Take(out TwoPieceSpan<T> lessInDest, out TwoPieceSpan<T> lessInScratch,
        out TwoPieceSpan<T> geqInScratch, out TwoPieceSpan<T> geqInDest)
    {
        // Capture the original lengths first — the split-offs below shrink the views,
        // but the write-head counters are indices into the ORIGINAL regions.
        int destLen = Dest.Length;
        Dest.SplitOffBegin(_numAtDestBegin, out lessInDest);
        int numInScratch = _scratchFwdIdx - _numAtDestBegin;
        Scratch.SplitOffBegin(numInScratch, out geqInScratch);
        _numAtDestBegin = 0;
        _scratchFwdIdx = 0;

        // geqInDest is dest's suffix behind the backward write head: its length is
        // destLen - _destBwdIdx - _numAtScratchEnd (cursor from the end, plus the
        // scratch-end count).
        Dest.SplitOffEnd(destLen - _destBwdIdx - _numAtScratchEnd, out geqInDest);
        Scratch.SplitOffEnd(_numAtScratchEnd, out lessInScratch);
        _numAtScratchEnd = 0;
        _destBwdIdx = Dest.Length;
    }

    /// <summary>partition_bidir (stable_quicksort.rs:283-337): fully partitions the
    /// remaining input around the pivot value (read-only, held out-of-line — upstream
    /// moves pivot_pos into a local via WriteBackPivot and stops the scans at its array
    /// position; the port needs no scan limits because the pivot is never in the scans).
    /// When one scan exhausts, the other's remainder is split evenly into a new
    /// forward/backward pair. invert selects the reversed comparator (upstream's
    /// cmp_from_closure(|a, b| !is_less(b, a)), used when partitioning the geq side).</summary>
    public void PartitionBidir<TC>(ref T pivot, TC cmp, bool invert)
        where TC : struct, IIsLess<T>
    {
        while (true)
        {
            int forwardLimit = ForwardScan.Length;
            // The pivot was consumed into a local copy, so the scans never contain it
            // (upstream checks because its pivot lives in the array). Limits are the
            // full remaining lengths.
            int backwardLimit = BackwardScan.Length;
            int limit = Math.Min(forwardLimit, backwardLimit);
            PartitionBidirN(ref pivot, cmp, invert, limit);

            if (ForwardScan.Length == 0 && BackwardScan.Length == 0)
                return;
            if (ForwardScan.Length == 0)
            {
                // Handle odd input sizes.
                if (BackwardScan.Length % 2 > 0)
                    PartitionOneAny(ref pivot, cmp, invert, false);
                int half = BackwardScan.Length / 2;
                BackwardScan.SplitOffBegin(half, out TwoPieceSpan<T> fwd);
                ForwardScan = fwd;
            }
            else
            {
                // Handle odd input sizes.
                if (ForwardScan.Length % 2 > 0)
                    PartitionOneAny(ref pivot, cmp, invert, true);
                int half = ForwardScan.Length / 2;
                ForwardScan.SplitOffEnd(half, out TwoPieceSpan<T> bwd);
                BackwardScan = bwd;
            }
        }
    }

    /// <summary>partition_bidir_n (stable_quicksort.rs:225-279): n interleaved
    /// forward/backward steps, unrolled by 4. The pivot is a caller-held local (upstream
    /// moves it out of the array precisely so these writes cannot clobber it).</summary>
    private void PartitionBidirN<TC>(ref T pivot, TC cmp, bool invert, int n)
        where TC : struct, IIsLess<T>
    {
        for (int i = 0; i < n >> 2; i++)
        {
            PartitionOneAny(ref pivot, cmp, invert, true);
            PartitionOneAny(ref pivot, cmp, invert, false);
            PartitionOneAny(ref pivot, cmp, invert, true);
            PartitionOneAny(ref pivot, cmp, invert, false);
            PartitionOneAny(ref pivot, cmp, invert, true);
            PartitionOneAny(ref pivot, cmp, invert, false);
            PartitionOneAny(ref pivot, cmp, invert, true);
            PartitionOneAny(ref pivot, cmp, invert, false);
        }
        for (int i = 0; i < (n & 3); i++)
        {
            PartitionOneAny(ref pivot, cmp, invert, true);
            PartitionOneAny(ref pivot, cmp, invert, false);
        }
    }

    /// <summary>One forward or backward step with the (possibly inverted) comparison.
    /// Inversion is upstream's cmp_from_closure(|a, b| !is_less(b, a)) — used when
    /// partitioning on the left side of the recursion: it routes elements NOT less than
    /// the pivot to the front regions and less-than elements to the back, which the
    /// caller then interprets with swapped region roles.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PartitionOneAny<TC>(ref T pivot, TC cmp, bool invert, bool forward)
        where TC : struct, IIsLess<T>
    {
        if (!invert)
        {
            if (forward)
                PartitionOneForward(ref pivot, cmp, out _, out _);
            else
                PartitionOneBackward(ref pivot, cmp, out _, out _);
            return;
        }
        // Inverted comparison: is_inverted(x, y) = !cmp.IsLess(y, x).
        if (forward)
            PartitionOneForwardInverted(ref pivot, cmp);
        else
            PartitionOneBackwardInverted(ref pivot, cmp);
    }

    /// <summary>Forward step under the inverted comparator: the write that normally
    /// goes to dest's forward head goes to scratch's forward head and vice versa.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PartitionOneForwardInverted<TC>(ref T pivot, TC cmp)
        where TC : struct, IIsLess<T>
    {
        ref T scan = ref ForwardScan[0];
        bool invertedLess = !cmp.IsLess(in pivot, in scan);
        int destOut = _numAtDestBegin;
        int scratchOut = _scratchFwdIdx - _numAtDestBegin;
        if (invertedLess)
            Dest[destOut] = scan;
        else
            Scratch[scratchOut] = scan;
        if (invertedLess) _numAtDestBegin++;
        _scratchFwdIdx++;
        ForwardScan.SplitOffBegin(1, out _);
    }

    /// <summary>Backward step under the inverted comparator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PartitionOneBackwardInverted<TC>(ref T pivot, TC cmp)
        where TC : struct, IIsLess<T>
    {
        ref T scan = ref BackwardScan[BackwardScan.Length - 1];
        bool invertedLess = !cmp.IsLess(in pivot, in scan);
        int destOut = _destBwdIdx + _numAtScratchEnd - 1;
        int scratchOut = Scratch.Length - _numAtScratchEnd - 1;
        if (invertedLess)
            Scratch[scratchOut] = scan;
        else
            Dest[destOut] = scan;
        if (invertedLess) _numAtScratchEnd++;
        _destBwdIdx--;
        BackwardScan.SplitOffEnd(1, out _);
    }
}

/// <summary>A logical slice that may physically consist of two pieces (MutSlice's
/// from_pair / concat views, stable_quicksort.rs header): A is the first piece, B the
/// second. Only the operations the quicksort port needs are provided.</summary>
internal ref struct TwoPieceSpan<T>
{
    public Span<T> A;
    public Span<T> B;

    public TwoPieceSpan(Span<T> a, Span<T> b)
    {
        A = a;
        B = b;
    }

    /// <summary>A one-piece view as the degenerate case.</summary>
    public TwoPieceSpan(Span<T> single) : this(single, default) { }

    public int Length => A.Length + B.Length;

    /// <summary>Ref to the logically i-th element (0-based over A ++ B).</summary>
    public ref T this[int i]
    {
        get
        {
            if (i < A.Length)
                return ref A[i];
            return ref B[i - A.Length];
        }
    }

    /// <summary>split_at: writes (this[..i], this[i..]) to head/tail; i &lt;= Length.
    /// out-parameters instead of a tuple because this is a ref struct.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SplitAt(int i, out TwoPieceSpan<T> head, out TwoPieceSpan<T> tail)
    {
        if (i <= A.Length)
        {
            head = new TwoPieceSpan<T>(A.Slice(0, i));
            tail = new TwoPieceSpan<T>(A.Slice(i), B);
        }
        else
        {
            head = new TwoPieceSpan<T>(A, B.Slice(0, i - A.Length));
            tail = new TwoPieceSpan<T>(B.Slice(i - A.Length));
        }
    }

    /// <summary>split_off_begin: this becomes this[i..]; the removed head is written to
    /// removed. (Upstream returns the removed slice and shrinks self; a ref struct with
    /// fields cannot both return and shrink, so the out-parameter form is used.)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SplitOffBegin(int i, out TwoPieceSpan<T> removed)
    {
        SplitAt(i, out removed, out TwoPieceSpan<T> tail);
        A = tail.A;
        B = tail.B;
    }

    /// <summary>split_off_end: this becomes this[..^i]; the removed tail is written to
    /// removed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SplitOffEnd(int i, out TwoPieceSpan<T> removed)
    {
        SplitAt(Length - i, out TwoPieceSpan<T> head, out removed);
        A = head.A;
        B = head.B;
    }
}
