// Ported from https://github.com/Voultapher/sort-research-rs (ipnsort src/lib.rs:148-170),
// MIT OR Apache-2.0, by Lukas Bergoll. C# port 2026 — the ipnsort entry.
//
// Reuse decision (cross-ref): find_existing_run is DriftImpl.FindExistingRun —
// ipnsort's version (lib.rs:181-201) is semantically identical to driftsort's
// (drift.rs:265-293): len < 2 => (len, false), run of len 2 grown while strictly
// descending (is_less(v[i], v[i-1])) or non-ascending (!is_less). Exposed internal
// for reuse rather than duplicated.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

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
        (int runLen, bool wasReversed) = DriftImpl.FindExistingRun(v, cmp);

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
}
