// Ported from https://github.com/Voultapher/sort-research-rs (ipnsort src/quicksort.rs:10-76),
// MIT OR Apache-2.0, by Lukas Bergoll. C# port 2026 — the quicksort driver.
//
// Reuse decisions (cross-refs): choose_pivot is IpnPivot.ChoosePivot (pivot.rs, shared
// with the small-sort primitives port — see SmallSortPrimitives.cs). The ancestor pivot
// is PivotRef<T> (by-value optional element) — see its doc comment for why a copy
// rather than a ref.
using System;
using System.Runtime.CompilerServices;

namespace Sorts;

internal static class IpnQuicksort
{
    /// <summary>quicksort (quicksort.rs:14-76): sorts v recursively. limit is the
    /// number of allowed imbalanced partitions before switching to heapsort; if zero
    /// on entry this call immediately heapsorts. ancestorPivot is the value of the
    /// pivot this slice's predecessor partitioned around (Has = false at the top):
    /// when the freshly chosen pivot is not greater than it, every element equal to
    /// it is batched to the left in one inverted partition and skipped.</summary>
    internal static void Quicksort<T, TC>(Span<T> v, PivotRef<T> ancestorPivot, int limit, TC cmp)
        where TC : struct, IIsLess<T>
    {
        while (true)
        {
            int len = v.Length;

            if (len <= IpnSmallSort.Threshold<T>())
            {
                IpnSmallSort.SmallSort(v, cmp);
                return;
            }

            // If too many bad pivot choices were made, simply fall back to heapsort
            // in order to guarantee O(N x log(N)) worst-case (quicksort.rs:29-34).
            if (limit == 0)
            {
                IpnHeapsort.Heapsort(v, cmp);
                return;
            }

            limit--;

            // Choose a pivot (quicksort.rs:40).
            int pivotPos = IpnPivot.ChoosePivot(v, cmp);
            int numLt;

            // If the chosen pivot is equal to the predecessor, then it's the smallest
            // element in the slice. Partition the slice into elements equal to and
            // elements greater than the pivot — usually hit when the slice contains
            // many duplicate elements (quicksort.rs:43-57). Upstream passes the
            // reversed closure |a, b| !is_less(b, a); the port wraps the comparer in
            // InvertedCmp<T, TC> — a separate JIT monomorphization, so the inversion
            // is free per element (see Comparers.cs). The inverted comparer makes
            // Partition's "num_lt" count elements <= pivot, so v[num_lt] is the
            // pivot with every equal element before it: skip past all of them and
            // drop the ancestor (nothing in the remainder can equal it).
            if (ancestorPivot.Has && !cmp.IsLess(in ancestorPivot.Value, in v[pivotPos]))
            {
                numLt = IpnPartition.Partition(v, pivotPos, new InvertedCmp<T, TC>(cmp));
                v = v.Slice(numLt + 1);
                ancestorPivot = default;
                continue;
            }

            // Partition the slice (quicksort.rs:59). num_lt is in-bounds and v[num_lt]
            // holds the pivot (upstream intrinsics::assume(num_lt < v.len())).
            numLt = IpnPartition.Partition(v, pivotPos, cmp);

            // Recurse into the left side; a fixed recursion limit is fine, testing
            // shows no real benefit for recursing into the shorter side
            // (quicksort.rs:61-66). Take the by-value pivot copy before recursing.
            var nextPivot = new PivotRef<T>(v[numLt]);
            Quicksort(v.Slice(0, numLt), ancestorPivot, limit, cmp);

            // Continue with the right side, carrying the pivot (quicksort.rs:68-70).
            v = v.Slice(numLt + 1);
            ancestorPivot = nextPivot;
        }
    }
}
