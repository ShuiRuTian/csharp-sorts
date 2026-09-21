// Ported from https://github.com/Voultapher/sort-research-rs (ipnsort src/lib.rs:148-170),
// MIT OR Apache-2.0, by Lukas Bergoll. C# port 2026 — the ipnsort entry.
//
// find_existing_run (lib.rs:181-201): len < 2 => (len, false), run of len 2 grown while
// strictly descending (is_less(v[i], v[i-1])) or non-ascending (!is_less).
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

internal static class IpnImpl
{
    /// <summary>ipnsort (lib.rs:148-170): run-detect the full slice — an entirely
    /// pre-sorted input reverses (strictly descending) or returns untouched — else
    /// quicksort with the imbalance limit 2 * floor(log2(len)); the binary OR by
    /// one eliminates the zero-check. NoInlining mirrors upstream's
    /// #[inline(never)].</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Sort<T, TC>(Span<T> v, TC cmp) where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        (int runLen, bool wasReversed) = FindExistingRun(v, cmp);

        if (runLen == len)
        {
            if (wasReversed)
                v.Reverse();

            // In-place merging for a long existing streak is possible but makes the
            // implementation a lot bigger; users can use a stable sort for that
            // use-case (lib.rs:165-168).
            return;
        }

        int limit = 2 * BitOperations.Log2((uint)(len | 1));
        IpnQuicksort.Quicksort(v, default(PivotRef<T>), limit, cmp);
    }

    /// <summary>Finds a run of sorted elements starting at the beginning of v
    /// (lib.rs:181-201). Returns the run length and whether the run is strictly
    /// descending (false means ascending, possibly with equal elements).</summary>
    internal static (int RunLen, bool WasReversed) FindExistingRun<T, TC>(Span<T> v, TC cmp)
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
}
