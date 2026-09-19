// Ported from https://github.com/Voultapher/sort-research-rs (driftsort), MIT OR Apache-2.0,
// by Orson Peters & Lukas Bergdoll.
// C# port 2026 — drift.rs: the powersort-heuristic main loop (sort), logical_merge,
// create_run, find_existing_run, sqrt_approx and the DriftsortRun bitfield.
//
// Port decisions (per the task brief):
//  - merge_tree_scale_factor / merge_tree_depth (drift.rs:123-172) are reused from
//    Powersort (Task 10) — identical math in both upstream files.
//  - upstream's MaybeUninit<[DriftsortRun; 66]> + [u8; 66] run stack becomes two
//    stackalloc'd spans plus stackLen — RunState is a single ulong, so 66 slots fit
//    comfortably on the stack, matching upstream's stack storage (drift.rs:46-49).
//  - DriftsortRun(usize) becomes RunState below with the same (len << 1) | sorted
//    bitfield (drift.rs:306-331).
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>The driftsort() main loop (drift.rs:16-121): a 66-slot run stack driven by
/// powersort's desired-merge-depth heuristic decides when to logically merge runs.
/// eagerSort selects upstream's O(N log N) eager mode (only small-sorts and physical
/// merges); DriftSort.SortSpan passes it for len &lt;= Threshold&lt;T&gt;() * 2 (lib.rs:104-108)
/// and DriftQuicksort's limit-0 fallback carries its own eager seam.</summary>
internal static class DriftImpl
{
    private const int MinSqrtRunLen = 64;
    private const int RunStackCapacity = 66;

    /// <summary>Sorts v based on the comparison contract cmp. scratch must be at least
    /// max(v.Length / 2, DriftSmallSort.MinSmallSortScratchLen) (drift.rs:7-12).</summary>
    internal static void Sort<T, TC>(Span<T> v, Span<T> scratch, bool eagerSort, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (len < 2)
        {
            return; // Removing this length check *increases* code size.
        }

        ulong scaleFactor = Powersort.MergeTreeScaleFactor(len);

        // It's important to have a relatively high entry barrier for pre-sorted runs,
        // as the presence of a single such run will force on average several merge
        // operations and shrink the maximum quicksort size a lot. For that reason we
        // use sqrt(len) as our pre-sorted run threshold (drift.rs:28-39).
        int minGoodRunLen = len <= MinSqrtRunLen * MinSqrtRunLen
            ? Math.Min(len - len / 2, MinSqrtRunLen) // else MIN_SQRT_RUN_LEN would break
            : SqrtApprox(len);                       // pattern detection on tiny inputs.

        // (stackLen, runs, desiredDepths) together form a stack maintaining run
        // information for the powersort heuristic. desiredDepths[i] is the desired
        // depth of the merge node that merges runs[i] with the run after it.
        Span<RunState> runs = stackalloc RunState[RunStackCapacity];
        Span<byte> desiredDepths = stackalloc byte[RunStackCapacity];
        int stackLen = 0;

        int scanIdx = 0;
        RunState prevRun = RunState.Sorted(0); // Initial dummy run.
        while (true)
        {
            // Compute the next run and the desired depth of the merge node between
            // prevRun and nextRun. On the last iteration we create a dummy run with
            // root-level desired depth to fully collapse the merge tree.
            RunState nextRun;
            byte desiredDepth;
            if (scanIdx < len)
            {
                nextRun = CreateRun(v.Slice(scanIdx), scratch, minGoodRunLen, eagerSort, cmp);
                desiredDepth = Powersort.MergeTreeDepth(
                    scanIdx - prevRun.Length, scanIdx, scanIdx + nextRun.Length, scaleFactor);
            }
            else
            {
                nextRun = RunState.Sorted(0);
                desiredDepth = 0;
            }

            // Process the merge nodes between earlier runs[i] that have a desire to be
            // deeper in the merge tree than the merge node for the splitpoint between
            // prevRun and nextRun. Invariants (drift.rs:81-103): the first stackLen
            // elements are initialized; desiredDepths is strictly ascending; the summed
            // run lengths plus prevRun.Length equal scanIdx. merge_tree_depth(..) <= 64
            // bounds the stack: at most 64 distinct depths before a push, plus the
            // initial dummy run, against capacity 66.
            while (stackLen > 1 && desiredDepths[stackLen - 1] >= desiredDepth)
            {
                // Desired depth >= the upcoming desired depth: pop the left neighbor
                // run from the stack and merge it into prevRun.
                RunState left = runs[stackLen - 1];
                int mergedLen = left.Length + prevRun.Length;
                int mergeStartIdx = scanIdx - mergedLen;
                prevRun = LogicalMerge(v.Slice(mergeStartIdx, mergedLen), scratch, left, prevRun, cmp);
                stackLen--;
            }

            // We now know desiredDepths[stackLen - 1] < desiredDepth (or stackLen <= 1),
            // maintaining the ascending invariant.
            runs[stackLen] = prevRun;
            desiredDepths[stackLen] = desiredDepth;
            stackLen++;

            // Break before overriding the last run with our dummy run.
            if (scanIdx >= len)
            {
                break;
            }

            scanIdx += nextRun.Length;
            prevRun = nextRun;
        }

        if (!prevRun.IsSorted)
        {
            StableQuicksort(v, scratch, cmp);
        }
    }

    /// <summary>Lazy logical runs as in Glidesort (drift.rs:191-218). If one or both
    /// runs are sorted, or the combined length no longer fits in scratch (we could no
    /// longer quicksort them), sort any unsorted side and physically merge; otherwise
    /// return the concatenation as a new unsorted run.</summary>
    private static RunState LogicalMerge<T, TC>(
        Span<T> v, Span<T> scratch, RunState left, RunState right, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        bool canFitInScratch = len <= scratch.Length;
        if (!canFitInScratch || left.IsSorted || right.IsSorted)
        {
            if (!left.IsSorted)
            {
                StableQuicksort(v.Slice(0, left.Length), scratch, cmp);
            }
            if (!right.IsSorted)
            {
                StableQuicksort(v.Slice(left.Length), scratch, cmp);
            }
            DriftMerge.Merge(v, scratch, left.Length, cmp);

            return RunState.Sorted(len);
        }

        return RunState.Unsorted(len);
    }

    /// <summary>Creates a new logical run (drift.rs:220-263): a pre-existing run that
    /// clears minGoodRunLen is returned sorted (reversed if strictly descending);
    /// otherwise eager mode returns a sorted run of Threshold&lt;T&gt;() elements and lazy
    /// mode an unsorted run of minGoodRunLen elements.</summary>
    private static RunState CreateRun<T, TC>(
        Span<T> v, Span<T> scratch, int minGoodRunLen, bool eagerSort, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (len >= minGoodRunLen)
        {
            (int runLen, bool wasReversed) = FindExistingRun(v, cmp);

            if (runLen >= minGoodRunLen)
            {
                if (wasReversed)
                {
                    v.Slice(0, runLen).Reverse();
                }

                return RunState.Sorted(runLen);
            }
        }

        if (eagerSort)
        {
            // We call stable_quicksort with a len that will immediately call
            // small-sort (limit 0, no ancestor pivot). By not calling the small-sort
            // directly here it can always be inlined into the quicksort itself,
            // making the recursive base case faster (drift.rs:252-259).
            int eagerRunLen = Math.Min(DriftSmallSort.Threshold<T>(), len);
            DriftQuicksort.StableQuicksort(
                v.Slice(0, eagerRunLen), scratch, 0, default(PivotRef<T>), cmp);
            return RunState.Sorted(eagerRunLen);
        }

        return RunState.Unsorted(Math.Min(minGoodRunLen, len));
    }

    /// <summary>Finds a run of sorted elements starting at the beginning of v
    /// (drift.rs:265-293). Returns the run length and whether the run is strictly
    /// descending (false means ascending, possibly with equal elements).</summary>
    private static (int RunLen, bool WasReversed) FindExistingRun<T, TC>(Span<T> v, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (len < 2)
        {
            return (len, false);
        }

        // Base-ref + rolling-cursor scan (this scan is the whole cost on the
        // sorted-input path): prev holds v[runLen - 1] throughout, so the loop
        // condition has no span bounds checks. Run-detection semantics are
        // unchanged: strictly-descending advances while each element is less than
        // its predecessor, ascending while not less (ties extend the run).
        int runLen = 2;
        ref T vBase = ref MemoryMarshal.GetReference(v);
        bool strictlyDescending = cmp.IsLess(in Unsafe.Add(ref vBase, 1), in vBase);
        if (strictlyDescending)
        {
            ref T prev = ref Unsafe.Add(ref vBase, 1);
            while (runLen < len && cmp.IsLess(in Unsafe.Add(ref prev, 1), in prev))
            {
                prev = ref Unsafe.Add(ref prev, 1);
                runLen++;
            }
        }
        else
        {
            ref T prev = ref Unsafe.Add(ref vBase, 1);
            while (runLen < len && !cmp.IsLess(in Unsafe.Add(ref prev, 1), in prev))
            {
                prev = ref Unsafe.Add(ref prev, 1);
                runLen++;
            }
        }

        return (runLen, strictlyDescending);
    }

    /// <summary>Quicksort entry with upstream's imbalance limit (drift.rs:295-304):
    /// 2 * floor(log2(len)), the OR by one eliminating the zero-check.</summary>
    private static void StableQuicksort<T, TC>(Span<T> v, Span<T> scratch, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int limit = 2 * BitOperations.Log2((uint)(v.Length | 1));
        DriftQuicksort.StableQuicksort(v, scratch, limit, default(PivotRef<T>), cmp);
    }

    /// <summary>sqrt(n) approximation (drift.rs:174-188): 2^(log2(n)/2) with a +0.5
    /// bias for the flooring log, refined by one Newton iteration.</summary>
    private static int SqrtApprox(int n)
    {
        int ilog = BitOperations.Log2((uint)(n | 1));
        int shift = (1 + ilog) / 2;
        return ((1 << shift) + (n >> shift)) / 2;
    }

    /// <summary>Compactly stores the length of a run and whether it is sorted
    /// (drift.rs:306-331): (len &lt;&lt; 1) | sortedBit.</summary>
    private readonly struct RunState
    {
        private readonly ulong _v;

        private RunState(ulong v) => _v = v;

        internal static RunState Sorted(int length) => new(((ulong)(uint)length << 1) | 1UL);

        internal static RunState Unsorted(int length) => new((ulong)(uint)length << 1);

        internal bool IsSorted => (_v & 1UL) == 1UL;

        internal int Length => (int)(_v >> 1);
    }
}
