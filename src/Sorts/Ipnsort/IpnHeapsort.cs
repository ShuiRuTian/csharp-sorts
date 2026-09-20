// Ported from https://github.com/Voultapher/sort-research-rs (ipnsort src/heapsort.rs),
// MIT OR Apache-2.0, by Lukas Bergdoll. C# port 2026.
//
// Same divergence policy as the other ipnsort ports: upstream has no panic-recovery
// here either (plain swaps), so an exception from the comparer simply leaves an
// unspecified — but always memory-safe — state.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

internal static class IpnHeapsort
{
    /// <summary>heapsort (heapsort.rs:9-31): O(n log n) worst-case algorithmic
    /// fallback for the quicksort driver. Upstream #[inline(never)] — it is an
    /// unlikely fallback that must not sit in the driver's hot loop.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Heapsort<T, TC>(Span<T> v, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;

        // for i in (0..len + len / 2).rev() — heapify (i >= len) then extract (i < len).
        for (int i = len + len / 2 - 1; i >= 0; i--)
        {
            int siftIdx;
            if (i >= len)
            {
                siftIdx = i - len;
            }
            else
            {
                // Extract the max, then sift from the root over the remainder.
                Swap(ref v[0], ref v[i]);
                siftIdx = 0;
            }

            SiftDown(v[..Math.Min(i, len)], siftIdx, cmp);
        }
    }

    /// <summary>sift_down (heapsort.rs:36-77): maintains the invariant parent &gt;=
    /// child for the sub-span v. Caller guarantees node &lt;= v.Length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SiftDown<T, TC>(Span<T> v, int node, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        ref T vBase = ref MemoryMarshal.GetReference(v);

        while (true)
        {
            // Children of node.
            int child = 2 * node + 1;
            if (child >= len)
                break;

            // Choose the greater child. The bounds branch is highly predictable; the
            // comparison is done branchless (heapsort.rs:60-64:
            // child += is_less(...) as usize), which the JIT lowers to setcc/cmov.
            if (child + 1 < len)
            {
                bool rightGreater = cmp.IsLess(
                    in Unsafe.Add(ref vBase, child),
                    in Unsafe.Add(ref vBase, child + 1));
                child += rightGreater ? 1 : 0;
            }

            // Stop if the invariant holds at node.
            if (!cmp.IsLess(in Unsafe.Add(ref vBase, node), in Unsafe.Add(ref vBase, child)))
                break;

            Swap(ref Unsafe.Add(ref vBase, node), ref Unsafe.Add(ref vBase, child));
            node = child;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap<T>(ref T a, ref T b)
    {
        T tmp = a;
        a = b;
        b = tmp;
    }
}
