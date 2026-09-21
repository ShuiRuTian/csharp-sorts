// Ported from https://github.com/Voultapher/sort-research-rs (ipnsort src/pivot.rs),
// MIT OR Apache-2.0, by Orson Peters & Lukas Bergdoll. C# port 2026 — choose_pivot
// (median3_rec / median3) and the ancestor-pivot representation. The algorithm is the
// glidesort pivot selection upstream also ships in driftsort; ipnsort's pivot.rs is
// byte-for-byte identical (PSEUDO_MEDIAN_REC_THRESHOLD = 64 on both sides).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>ipnsort pivot selection (upstream pivot.rs): samples three size-(n/8)
/// regions of v and returns a pseudo-median-of-3 index, recursing (median3_rec) above
/// PseudoMedianRecThreshold for an overall sample of O(n^0.528) elements.</summary>
internal static class IpnPivot
{
    /// <summary>Upstream PSEUDO_MEDIAN_REC_THRESHOLD (pivot.rs:2): at or above this
    /// total size, pivot selection recurses into approximate medians of the sampled
    /// regions.</summary>
    internal const int PseudoMedianRecThreshold = 64;

    /// <summary>choose_pivot (pivot.rs:8-31): samples three size-(n/8) regions of v —
    /// [0, n/8), [4*n/8, 5*n/8), [7*n/8, n) — and returns a pseudo-median-of-3 index,
    /// recursing (median3_rec) above PseudoMedianRecThreshold.</summary>
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
