// Ported from https://github.com/orlp/glidesort, MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll.
// C# port 2026 — glidesort.rs: LogicalRun, MergeStack, the glidesort() main loop and
// run_length_at_start. The powersort depth math lives in Sorts.Powersort (shared with
// DriftSort); the physical merge family in GlideMerge; stable quicksort in GlideQuicksort.
//
// Rust's LogicalRun enum carries MutSlice regions; here every run lives inside the one
// input span v, so a run is (Start, Length) indices into v plus a Kind — no branding
// needed. MutSlice's concat-abort contiguity guarantee is preserved by construction:
// LogicalMerge only ever joins runs that were split off the same v in order.
using System;
using System.Diagnostics;
using System.Numerics;

namespace Sorts;

/// <summary>Kind of a logical run (glidesort.rs:14-18): unsorted elements, a sorted run,
/// or two sorted runs adjacent in memory.</summary>
internal enum LogicalRunKind : byte
{
    Unsorted = 0,
    Sorted = 1,
    DoubleSorted = 2,
}

/// <summary>A logical run of elements (glidesort.rs:12-133): Start/Length index into the
/// input span the owning GlidesortImpl.Sort call was given. For DoubleSorted, Mid is the
/// length of the first of the two adjacent sorted runs.</summary>
internal readonly struct LogicalRun
{
    internal readonly int Start;
    internal readonly int Length;
    internal readonly LogicalRunKind Kind;
    internal readonly int Mid;

    private LogicalRun(int start, int length, LogicalRunKind kind, int mid)
    {
        Start = start;
        Length = length;
        Kind = kind;
        Mid = mid;
    }

    internal static LogicalRun UnsortedRun(int start, int length)
        => new(start, length, LogicalRunKind.Unsorted, 0);

    internal static LogicalRun SortedRun(int start, int length)
        => new(start, length, LogicalRunKind.Sorted, 0);

    internal static LogicalRun DoubleSortedRun(int start, int length, int mid)
        => new(start, length, LogicalRunKind.DoubleSorted, mid);
}

// Each logical run on the merge stack represents a node in the merge tree. This
// node has fully completed merging its left children, the result of these merge
// operations is the logical run stored on the stack (even though physically the
// run might be DoubleSorted in which case it would still need one more merge
// operation).
//
// Each node doesn't know exactly its depth in the final merge tree, but it does
// know which depth it would *like* to have in the final merge tree. Using these
// desired depths calculated using Powersort's logic we decide which logical
// runs to merge if any when a new run arrives. Powersort guarantees that our
// stack size remains constant if we follow these merge depths as any desired
// depth is less than 64 and the desired depths on the stack is strictly ascending.
//
// Upstream uses MaybeUninit<[LogicalRun; 64]>; C# arrays are always initialized,
// and the one-time zero-fill of 64 slots is negligible.
internal sealed class MergeStack
{
    private readonly LogicalRun[] _leftChildren = new LogicalRun[64];
    private readonly byte[] _desiredDepths = new byte[64];
    private int _len;

    /// <summary>Number of nodes currently on the stack.</summary>
    internal int Length => _len;

    /// <summary>Push a merge node on the stack given its left child and desired depth
    /// (glidesort.rs:167-171).</summary>
    internal void PushNode(LogicalRun leftChild, byte desiredDepth)
    {
        _leftChildren[_len] = leftChild;
        _desiredDepths[_len] = desiredDepth;
        _len++;
    }

    /// <summary>Pop a merge node off the stack, returning its left child
    /// (glidesort.rs:174-186). Callers guard on Length &gt; 0, mirroring upstream's Option.</summary>
    internal LogicalRun PopNode()
    {
        _len--;
        return _leftChildren[_len];
    }

    /// <summary>Returns the desired depth of the merge node at the top of the stack
    /// (glidesort.rs:189-201). Only valid when Length &gt; 0.</summary>
    internal byte PeekDesiredDepth() => _desiredDepths[_len - 1];
}

/// <summary>The glidesort() driver (glidesort.rs:203-265): run detection, the powersort
/// merge-stack main loop, and the final collapse + physical sort.</summary>
internal static class GlidesortImpl
{
    /// <summary>Sorts v in place. scratch is merge/quickSort workspace; the alloc formula
    /// guarantees scratch.Length &gt;= SMALL_SORT (upstream's sanity fallback Vec is
    /// unreachable here — see GlideSort.GlidesortAllocSize).</summary>
    internal static void Sort<T, TC>(Span<T> v, Span<T> scratch, TC cmp, bool eagerSmallsort)
        where TC : struct, IIsLess<T>
    {
        // Sanity fallback (glidesort.rs:209-216): upstream re-allocates a SMALL_SORT Vec
        // when scratch is too small; our alloc formula's .max(SMALL_SORT) makes that
        // unreachable, so assert it instead.
        Debug.Assert(scratch.Length >= GlideSmallSort.SmallSort);

        ulong scale = Powersort.MergeTreeScaleFactor(v.Length);
        var mergeStack = new MergeStack();

        Span<T> el = v;
        int prevRunStartIdx = 0;
        LogicalRun prevRun = CreateRun(el, elStart: 0, cmp, eagerSmallsort, out el);
        while (el.Length > 0)
        {
            int nextRunStartIdx = prevRunStartIdx + prevRun.Length;
            LogicalRun nextRun = CreateRun(el, nextRunStartIdx, cmp, eagerSmallsort, out el);

            byte desiredDepth = Powersort.MergeTreeDepth(
                prevRunStartIdx,
                nextRunStartIdx,
                nextRunStartIdx + nextRun.Length,
                scale);

            // Create the left child of our next node and eagerly merge all nodes
            // with a deeper desired merge depth into it.
            LogicalRun leftChild = prevRun;
            while (mergeStack.Length > 0 && mergeStack.PeekDesiredDepth() >= desiredDepth)
            {
                LogicalRun leftDescendant = mergeStack.PopNode();
                leftChild = LogicalMerge(leftDescendant, leftChild, v, scratch, cmp);
            }

            mergeStack.PushNode(leftChild, desiredDepth);
            prevRunStartIdx = nextRunStartIdx;
            prevRun = nextRun;
        }

        // Collapse the stack down to a single logical run and physically sort it.
        LogicalRun result = prevRun;
        while (mergeStack.Length > 0)
            result = LogicalMerge(mergeStack.PopNode(), result, v, scratch, cmp);
        PhysicalSortRun(result, v, scratch, cmp);
    }

    /// <summary>Create a new logical run at the start of el, returning the rest
    /// (glidesort.rs:30-73). elStart is el[0]'s index in the input span.</summary>
    private static LogicalRun CreateRun<T, TC>(
        Span<T> el, int elStart, TC cmp, bool eagerSmallsort, out Span<T> rest)
        where TC : struct, IIsLess<T>
    {
        // Check if input is (partially) pre-sorted in a meaningful way.
        if (el.Length >= GlideSmallSort.SmallSort)
        {
            (int runLength, bool descending) = RunLengthAtStart(el, cmp);
            if (runLength >= GlideSmallSort.SmallSort
                && (long)runLength * runLength >= el.Length / 2)
            {
                if (descending)
                    el.Slice(0, runLength).Reverse();
                rest = el.Slice(runLength);
                return LogicalRun.SortedRun(elStart, runLength);
            }
        }

        // Otherwise create a small unsorted run. Capping this at SMALL_SORT ensures
        // we're always able to sort this later, regardless of scratch space size.
        int skip = Math.Min(GlideSmallSort.SmallSort, el.Length);
        Span<T> run = el.Slice(0, skip);
        rest = el.Slice(skip);
        if (eagerSmallsort)
        {
            GlideSmallSort.Sort(run, cmp);
            return LogicalRun.SortedRun(elStart, skip);
        }
        return LogicalRun.UnsortedRun(elStart, skip);
    }

    /// <summary>Returns the length of the run at the start of v, and if that run is
    /// strictly descending (glidesort.rs:269-285).</summary>
    private static (int RunLength, bool Descending) RunLengthAtStart<T, TC>(Span<T> v, TC cmp)
        where TC : struct, IIsLess<T>
    {
        bool descending = v.Length >= 2 && cmp.IsLess(in v[1], in v[0]);
        if (descending)
        {
            for (int i = 2; i < v.Length; i++)
            {
                if (!cmp.IsLess(in v[i], in v[i - 1]))
                    return (i, true);
            }
        }
        else
        {
            for (int i = 2; i < v.Length; i++)
            {
                if (cmp.IsLess(in v[i], in v[i - 1]))
                    return (i, false);
            }
        }
        return (v.Length, descending);
    }

    /// <summary>Merges runs left (self) and right using the given scratch space
    /// (glidesort.rs:78-116) — the 7-arm match, ordered exactly as upstream: the
    /// Unsorted+Unsorted concat guard first, then Unsorted-left (any right), then
    /// Unsorted-right, then the three sorted combinations. left and right must be
    /// contiguous in v (left.Start + left.Length == right.Start), mirroring upstream's
    /// concat abort.</summary>
    private static LogicalRun LogicalMerge<T, TC>(
        LogicalRun left, LogicalRun right, Span<T> v, Span<T> scratch, TC cmp)
        where TC : struct, IIsLess<T>
    {
        // Only combine unsorted runs if it still fits in the scratch space.
        if (left.Kind == LogicalRunKind.Unsorted)
        {
            if (right.Kind == LogicalRunKind.Unsorted && left.Length + right.Length <= scratch.Length)
                return LogicalRun.UnsortedRun(left.Start, left.Length + right.Length);
            QuickSortRun(v, left, scratch, cmp);
            left = LogicalRun.SortedRun(left.Start, left.Length);
        }
        if (right.Kind == LogicalRunKind.Unsorted)
        {
            QuickSortRun(v, right, scratch, cmp);
            right = LogicalRun.SortedRun(right.Start, right.Length);
        }

        if (left.Kind == LogicalRunKind.Sorted && right.Kind == LogicalRunKind.Sorted)
            return LogicalRun.DoubleSortedRun(left.Start, left.Length + right.Length, mid: left.Length);

        Span<T> leftSpan = v.Slice(left.Start, left.Length);
        Span<T> rightSpan = v.Slice(right.Start, right.Length);
        if (left.Kind == LogicalRunKind.DoubleSorted && right.Kind == LogicalRunKind.DoubleSorted)
        {
            GlideMerge.PhysicalQuadMerge(
                leftSpan.Slice(0, left.Mid),
                leftSpan.Slice(left.Mid),
                rightSpan.Slice(0, right.Mid),
                rightSpan.Slice(right.Mid),
                scratch, cmp);
        }
        else if (left.Kind == LogicalRunKind.DoubleSorted)
        {
            // (DoubleSorted(l, mid), Sorted(r)) — glidesort.rs:102-105.
            GlideMerge.PhysicalTripleMerge(leftSpan.Slice(0, left.Mid), leftSpan.Slice(left.Mid), rightSpan, scratch, cmp);
        }
        else
        {
            // (Sorted(l), DoubleSorted(r, mid)) — glidesort.rs:106-109.
            GlideMerge.PhysicalTripleMerge(leftSpan, rightSpan.Slice(0, right.Mid), rightSpan.Slice(right.Mid), scratch, cmp);
        }
        return LogicalRun.SortedRun(left.Start, left.Length + right.Length);
    }

    /// <summary>Ensures that this run is physically sorted (glidesort.rs:119-132).</summary>
    private static void PhysicalSortRun<T, TC>(LogicalRun run, Span<T> v, Span<T> scratch, TC cmp)
        where TC : struct, IIsLess<T>
    {
        switch (run.Kind)
        {
            case LogicalRunKind.Sorted:
                return;
            case LogicalRunKind.Unsorted:
                QuickSortRun(v, run, scratch, cmp);
                return;
            case LogicalRunKind.DoubleSorted:
                Span<T> whole = v.Slice(run.Start, run.Length);
                GlideMerge.PhysicalMerge(whole.Slice(0, run.Mid), whole.Slice(run.Mid), scratch, cmp);
                return;
        }
    }

    /// <summary>Stable quicksort of an unsorted run in place (stable_quicksort.rs:548-570
    /// called from glidesort.rs:91/95/126). Upstream passes limit = 2 * bit_length(n);
    /// the concat arm's scratch guard keeps every Unsorted run within scratch, which is
    /// quicksort's split_at(n) contract.</summary>
    private static void QuickSortRun<T, TC>(Span<T> v, LogicalRun run, Span<T> scratch, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int n = run.Length;
        Debug.Assert(n <= scratch.Length, "unsorted run must fit in scratch");
        int logn = 64 - BitOperations.LeadingZeroCount((ulong)(uint)n);
        GlideQuicksort.Quicksort(v.Slice(run.Start, n), scratch, 2 * logn, cmp);
    }
}
