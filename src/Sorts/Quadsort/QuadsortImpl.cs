// Ported from https://github.com/scandum/quadsort, public domain / unlicense, by Igor van den Hoven.
// C# port 2026 — clang (branchless ternary) macro forms only, inlined where upstream inlines.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>QuadSort kernel internals (Task 4 scope: sorts of 0-31 elements,
/// quad_swap 32-block builder, tail machinery).</summary>
internal static partial class QuadsortImpl
{
    /// <summary>Upstream QUAD_CACHE — L3-friendly cache limit (elements).</summary>
    internal const int QuadCache = 262144;

    // ---- quadsort.h macro ports, clang branchless forms (quadsort.h:38-108) ----

    /// <summary>branchless_swap: sort the pair [pta, pta+1] (quadsort.h:96-100), returning the
    /// macro's disorder flag (true when the pair was out of order). Writes both slots
    /// unconditionally (cmov-friendly).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool BranchlessSwap<T, TC>(ref T pta, TC cmp) where TC : struct, IIsLess<T>
    {
        var a0 = pta;
        var a1 = Unsafe.Add(ref pta, 1);
        bool gt = cmp.IsLess(in a1, in a0); // cmp(pta, pta+1) > 0
        pta = gt ? a1 : a0;
        Unsafe.Add(ref pta, 1) = gt ? a0 : a1;
        return gt;
    }

    /// <summary>head_branchless_merge clang form: *ptd++ = cmp(ptl,ptr) &lt;= 0 ? *ptl++ : *ptr++
    /// (quadsort.h:47-49). Writes the smaller of ptl/ptr through ptd and returns true when the
    /// left run was taken; the caller reseats its ref-local cursors (ptl/ptr/ptd) with Unsafe.Add.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HeadBranchlessMerge<T, TC>(ref T ptd, ref T ptl, ref T ptr, TC cmp) where TC : struct, IIsLess<T>
    {
        bool le = !cmp.IsLess(in ptr, in ptl); // cmp(ptl, ptr) <= 0
        ptd = le ? ptl : ptr;
        return le;
    }

    /// <summary>tail_branchless_merge clang form: *tpd-- = cmp(tpl,tpr) &gt; 0 ? *tpl-- : *tpr--
    /// (quadsort.h:60-62). Writes the larger of tpl/tpr through tpd and returns true when the
    /// left run was taken; the caller reseats its ref-local cursors backward.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TailBranchlessMerge<T, TC>(ref T tpd, ref T tpl, ref T tpr, TC cmp) where TC : struct, IIsLess<T>
    {
        bool gt = cmp.IsLess(in tpr, in tpl); // cmp(tpl, tpr) > 0
        tpd = gt ? tpl : tpr;
        return gt;
    }

    // ---- the next seven functions are used for sorting 0 to 31 elements (quadsort.c:3) ----

    /// <summary>parity_swap_four (quadsort.c:5-21): sorting network for 4 elements, no scratch.</summary>
    internal static void ParitySwapFour<T, TC>(Span<T> array, TC cmp) where TC : struct, IIsLess<T>
    {
        ref var r = ref MemoryMarshal.GetReference(array);
        int pta = 0;

        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2;   // (0,1)
        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta -= 1;   // (2,3)

        if (cmp.IsLess(in Unsafe.Add(ref r, pta + 1), in Unsafe.Add(ref r, pta)))
        {   // cmp(pta, pta+1) > 0: pivot pair (1,2) out of order
            var tmp = Unsafe.Add(ref r, pta);
            Unsafe.Add(ref r, pta) = Unsafe.Add(ref r, pta + 1);
            Unsafe.Add(ref r, pta + 1) = tmp;
            pta -= 1;

            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2;  // (0,1)
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta -= 1;   // (2,3)
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp);             // (1,2)
        }
    }

    /// <summary>parity_swap_five (quadsort.c:23-42): sorting network for 5 elements, no scratch.</summary>
    internal static void ParitySwapFive<T, TC>(Span<T> array, TC cmp) where TC : struct, IIsLess<T>
    {
        ref var r = ref MemoryMarshal.GetReference(array);
        int pta = 0;
        bool x, y;

        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2;   // (0,1)
        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta -= 1;   // (2,3)
        x = BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2; // (1,2)
        y = BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp);           // (3,4)
        pta = 0;

        if (x | y) // upstream: if (x + y)
        {
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2;   // (0,1)
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta -= 1;   // (2,3)
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2;   // (1,2)
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp);             // (3,4)
            pta = 0;
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2;   // (0,1)
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta -= 1;   // (2,3)
        }
    }

    /// <summary>parity_swap_six (quadsort.c:44-74): 6-element sort; merge path uses swap[0..5].</summary>
    internal static void ParitySwapSix<T, TC>(Span<T> array, Span<T> swap, TC cmp) where TC : struct, IIsLess<T>
    {
        ref var r = ref MemoryMarshal.GetReference(array);
        ref var s = ref MemoryMarshal.GetReference(swap);
        int pta = 0;

        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 1;   // (0,1)
        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 3;   // (1,2)
        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta -= 1;   // (4,5)
        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp);             // (3,4)
        pta = 0;

        if (!cmp.IsLess(in Unsafe.Add(ref r, 3), in Unsafe.Add(ref r, 2))) // cmp(pta+2, pta+3) <= 0
        {
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 4;  // (0,1)
            BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp);             // (4,5)
            return;
        }

        // gather two 3-runs into swap: [s01,s01,a2] and [a3,s45,s45]
        bool x = cmp.IsLess(in Unsafe.Add(ref r, 1), in Unsafe.Add(ref r, 0));
        Unsafe.Add(ref s, 0) = x ? Unsafe.Add(ref r, 1) : Unsafe.Add(ref r, 0); // swap[0] = pta[x]
        Unsafe.Add(ref s, 1) = x ? Unsafe.Add(ref r, 0) : Unsafe.Add(ref r, 1); // swap[1] = pta[!x]
        Unsafe.Add(ref s, 2) = Unsafe.Add(ref r, 2);
        x = cmp.IsLess(in Unsafe.Add(ref r, 5), in Unsafe.Add(ref r, 4));
        Unsafe.Add(ref s, 4) = x ? Unsafe.Add(ref r, 5) : Unsafe.Add(ref r, 4);
        Unsafe.Add(ref s, 5) = x ? Unsafe.Add(ref r, 4) : Unsafe.Add(ref r, 5);
        Unsafe.Add(ref s, 3) = Unsafe.Add(ref r, 3); // swap[3] = pta[-1]

        int ptl = 0, ptr = 3, ptd = 0; // into swap / swap+3 / array
        bool le;
        le = HeadBranchlessMerge(ref Unsafe.Add(ref r, ptd), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
        ptl += le ? 1 : 0; ptr += le ? 0 : 1; ptd++;
        le = HeadBranchlessMerge(ref Unsafe.Add(ref r, ptd), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
        ptl += le ? 1 : 0; ptr += le ? 0 : 1; ptd++;
        le = HeadBranchlessMerge(ref Unsafe.Add(ref r, ptd), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
        ptl += le ? 1 : 0; ptr += le ? 0 : 1; ptd++;

        // tail: pta = array+5, ptl = swap+2, ptr = swap+5
        int tpl = 2, tpr = 5, tpd = 5;
        bool gt;
        gt = TailBranchlessMerge(ref Unsafe.Add(ref r, tpd), ref Unsafe.Add(ref s, tpl), ref Unsafe.Add(ref s, tpr), cmp);
        tpl += gt ? -1 : 0; tpr += gt ? 0 : -1; tpd--;
        gt = TailBranchlessMerge(ref Unsafe.Add(ref r, tpd), ref Unsafe.Add(ref s, tpl), ref Unsafe.Add(ref s, tpr), cmp);
        tpl += gt ? -1 : 0; tpr += gt ? 0 : -1; tpd--;

        // *pta = cmp(ptl, ptr) > 0 ? *ptl : *ptr
        Unsafe.Add(ref r, tpd) = cmp.IsLess(in Unsafe.Add(ref s, tpr), in Unsafe.Add(ref s, tpl))
            ? Unsafe.Add(ref s, tpl) : Unsafe.Add(ref s, tpr);
    }

    /// <summary>parity_swap_seven (quadsort.c:76-108): 7-element sort; merge path uses swap[0..6].</summary>
    internal static void ParitySwapSeven<T, TC>(Span<T> array, Span<T> swap, TC cmp) where TC : struct, IIsLess<T>
    {
        ref var r = ref MemoryMarshal.GetReference(array);
        ref var s = ref MemoryMarshal.GetReference(swap);
        int pta = 0;
        bool x, y;

        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2;     // (0,1)
        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2;     // (2,3)
        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta -= 3;     // (4,5)
        y = BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); pta += 2; // (1,2)
        x = BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); y |= x; pta += 2; // (3,4)
        x = BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); y |= x; pta -= 1; // (5,6)

        if (!y) return;

        BranchlessSwap(ref Unsafe.Add(ref r, pta), cmp); // (4,5)
        pta = 0;

        // gather a 3-run and a 4-run into swap
        x = cmp.IsLess(in Unsafe.Add(ref r, 1), in Unsafe.Add(ref r, 0));
        Unsafe.Add(ref s, 0) = x ? Unsafe.Add(ref r, 1) : Unsafe.Add(ref r, 0); // swap[0] = pta[x]
        Unsafe.Add(ref s, 1) = x ? Unsafe.Add(ref r, 0) : Unsafe.Add(ref r, 1); // swap[1] = pta[!x]
        Unsafe.Add(ref s, 2) = Unsafe.Add(ref r, 2);
        x = cmp.IsLess(in Unsafe.Add(ref r, 4), in Unsafe.Add(ref r, 3));
        Unsafe.Add(ref s, 3) = x ? Unsafe.Add(ref r, 4) : Unsafe.Add(ref r, 3); // pta = 3
        Unsafe.Add(ref s, 4) = x ? Unsafe.Add(ref r, 3) : Unsafe.Add(ref r, 4);
        x = cmp.IsLess(in Unsafe.Add(ref r, 6), in Unsafe.Add(ref r, 5));
        Unsafe.Add(ref s, 5) = x ? Unsafe.Add(ref r, 6) : Unsafe.Add(ref r, 5); // pta = 5
        Unsafe.Add(ref s, 6) = x ? Unsafe.Add(ref r, 5) : Unsafe.Add(ref r, 6);

        int ptl = 0, ptr = 3, ptd = 0; // into swap / swap+3 / array
        bool le;
        le = HeadBranchlessMerge(ref Unsafe.Add(ref r, ptd), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
        ptl += le ? 1 : 0; ptr += le ? 0 : 1; ptd++;
        le = HeadBranchlessMerge(ref Unsafe.Add(ref r, ptd), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
        ptl += le ? 1 : 0; ptr += le ? 0 : 1; ptd++;
        le = HeadBranchlessMerge(ref Unsafe.Add(ref r, ptd), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
        ptl += le ? 1 : 0; ptr += le ? 0 : 1; ptd++;

        // tail: pta = array+6, ptl = swap+2, ptr = swap+6
        int tpl = 2, tpr = 6, tpd = 6;
        bool gt;
        gt = TailBranchlessMerge(ref Unsafe.Add(ref r, tpd), ref Unsafe.Add(ref s, tpl), ref Unsafe.Add(ref s, tpr), cmp);
        tpl += gt ? -1 : 0; tpr += gt ? 0 : -1; tpd--;
        gt = TailBranchlessMerge(ref Unsafe.Add(ref r, tpd), ref Unsafe.Add(ref s, tpl), ref Unsafe.Add(ref s, tpr), cmp);
        tpl += gt ? -1 : 0; tpr += gt ? 0 : -1; tpd--;
        gt = TailBranchlessMerge(ref Unsafe.Add(ref r, tpd), ref Unsafe.Add(ref s, tpl), ref Unsafe.Add(ref s, tpr), cmp);
        tpl += gt ? -1 : 0; tpr += gt ? 0 : -1; tpd--;

        Unsafe.Add(ref r, tpd) = cmp.IsLess(in Unsafe.Add(ref s, tpr), in Unsafe.Add(ref s, tpl))
            ? Unsafe.Add(ref s, tpl) : Unsafe.Add(ref s, tpr);
    }

    /// <summary>tiny_sort (quadsort.c:110-141): dispatcher for 0-7 elements.</summary>
    internal static void TinySort<T, TC>(Span<T> array, Span<T> swap, TC cmp) where TC : struct, IIsLess<T>
    {
        ref var r = ref MemoryMarshal.GetReference(array);
        switch (array.Length)
        {
            case 0:
            case 1:
                return;
            case 2:
                BranchlessSwap(ref r, cmp);
                return;
            case 3:
                BranchlessSwap(ref Unsafe.Add(ref r, 0), cmp);
                BranchlessSwap(ref Unsafe.Add(ref r, 1), cmp);
                BranchlessSwap(ref Unsafe.Add(ref r, 0), cmp);
                return;
            case 4:
                ParitySwapFour(array, cmp);
                return;
            case 5:
                ParitySwapFive(array, cmp);
                return;
            case 6:
                ParitySwapSix(array, swap, cmp);
                return;
            case 7:
                ParitySwapSeven(array, swap, cmp);
                return;
            default:
                // upstream tiny_sort has no default (callers pass < 8); the brief requires
                // TinySort to cover 0..31, so delegate to the tail machinery for 8..31
                TailSwap(array, swap, cmp);
                return;
        }
    }

    /// <summary>parity_merge (quadsort.c:145-183): merge two sorted runs; left must be
    /// equal to or one smaller than right. clang form (no QUAD_CACHE split).</summary>
    internal static void ParityMerge<T, TC>(Span<T> dest, Span<T> from, int left, int right, TC cmp) where TC : struct, IIsLess<T>
    {
        ref var fd = ref MemoryMarshal.GetReference(from);
        ref var dd = ref MemoryMarshal.GetReference(dest);

        int ptl = 0, ptr = left;                 // into from
        int tpl = left - 1, tpr = tpl + right;   // into from, from the right end
        int ptd = 0, tpd = left + right - 1;     // into dest

        // if (left < right) extra head step
        if (left < right)
        {
            bool le = !cmp.IsLess(in Unsafe.Add(ref fd, ptr), in Unsafe.Add(ref fd, ptl));
            Unsafe.Add(ref dd, ptd) = le ? Unsafe.Add(ref fd, ptl) : Unsafe.Add(ref fd, ptr);
            ptl += le ? 1 : 0; ptr += le ? 0 : 1; ptd++;
        }
        {
            bool le = !cmp.IsLess(in Unsafe.Add(ref fd, ptr), in Unsafe.Add(ref fd, ptl));
            Unsafe.Add(ref dd, ptd) = le ? Unsafe.Add(ref fd, ptl) : Unsafe.Add(ref fd, ptr);
            ptl += le ? 1 : 0; ptr += le ? 0 : 1; ptd++;
        }

        while (--left > 0)
        {
            bool le = HeadBranchlessMerge(ref Unsafe.Add(ref dd, ptd), ref Unsafe.Add(ref fd, ptl), ref Unsafe.Add(ref fd, ptr), cmp);
            ptl += le ? 1 : 0; ptr += le ? 0 : 1; ptd++;
            bool gt = TailBranchlessMerge(ref Unsafe.Add(ref dd, tpd), ref Unsafe.Add(ref fd, tpl), ref Unsafe.Add(ref fd, tpr), cmp);
            tpl += gt ? -1 : 0; tpr += gt ? 0 : -1; tpd--;
        }

        // *tpd = cmp(tpl, tpr) > 0 ? *tpl : *tpr
        Unsafe.Add(ref dd, tpd) = cmp.IsLess(in Unsafe.Add(ref fd, tpr), in Unsafe.Add(ref fd, tpl))
            ? Unsafe.Add(ref fd, tpl) : Unsafe.Add(ref fd, tpr);
    }

    /// <summary>tail_swap (quadsort.c:185-215): recursive quad-split merge sort for the tail.</summary>
    internal static void TailSwap<T, TC>(Span<T> array, Span<T> swap, TC cmp) where TC : struct, IIsLess<T>
    {
        int nmemb = array.Length;
        if (nmemb < 8)
        {
            TinySort(array, swap, cmp);
            return;
        }

        int half1 = nmemb / 2;
        int quad1 = half1 / 2;
        int quad2 = half1 - quad1;
        int half2 = nmemb - half1;
        int quad3 = half2 / 2;
        int quad4 = half2 - quad3;

        ref var r = ref MemoryMarshal.GetReference(array);
        int pta = 0;

        TailSwap(array[..quad1], swap, cmp); pta += quad1;
        TailSwap(array[pta..(pta + quad2)], swap, cmp); pta += quad2;
        TailSwap(array[pta..(pta + quad3)], swap, cmp); pta += quad3;
        TailSwap(array[pta..(pta + quad4)], swap, cmp);

        if (!cmp.IsLess(in Unsafe.Add(ref r, quad1), in Unsafe.Add(ref r, quad1 - 1))
            && !cmp.IsLess(in Unsafe.Add(ref r, half1), in Unsafe.Add(ref r, half1 - 1))
            && !cmp.IsLess(in Unsafe.Add(ref r, pta), in Unsafe.Add(ref r, pta - 1)))
        {   // cmp(a,b) <= 0 three times: quads already in order
            return;
        }
        ParityMerge(swap, array[..half1], quad1, quad2, cmp);
        ParityMerge(swap[half1..], array[half1..], quad3, quad4, cmp);
        ParityMerge(array, swap, half1, half2, cmp);
    }

    /// <summary>quad_reversal (quadsort.c:219-241): reverse the span. Upstream takes pta/ptz
    /// inclusive pointers; ranges of length &lt; 3 are caller-impossible upstream (unsigned
    /// wrap landmine) and handled defensively here.</summary>
    internal static void QuadReversal<T, TC>(Span<T> a, TC cmp) where TC : struct, IIsLess<T>
    {
        int first = 0, last = a.Length - 1;
        if (last <= first) return;
        if (last - first == 1)
        {
            var t = a[0]; a[0] = a[1]; a[1] = t;
            return;
        }
        int loop = (last - first) / 2;

        int ptb = first + loop;
        int pty = last - loop;

        ref var r = ref MemoryMarshal.GetReference(a);
        T tmp1, tmp2;

        if ((loop & 1) == 0)
        {
            tmp2 = Unsafe.Add(ref r, ptb);
            Unsafe.Add(ref r, ptb) = Unsafe.Add(ref r, pty);
            Unsafe.Add(ref r, pty) = tmp2;
            ptb--; pty++; loop--;
        }

        loop /= 2;

        do
        {
            tmp1 = Unsafe.Add(ref r, first); Unsafe.Add(ref r, first) = Unsafe.Add(ref r, last); Unsafe.Add(ref r, last) = tmp1;
            first++; last--;
            tmp2 = Unsafe.Add(ref r, ptb); Unsafe.Add(ref r, ptb) = Unsafe.Add(ref r, pty); Unsafe.Add(ref r, pty) = tmp2;
            ptb--; pty++;
        }
        while (loop-- > 0);
    }

    /// <summary>quad_swap_merge (quadsort.c:243-253): merge a sorted 8-block via the
    /// parity_merge_two / parity_merge_four macro expansions (quadsort.h:66-86).</summary>
    internal static void QuadSwapMerge<T, TC>(Span<T> array, Span<T> swap, TC cmp) where TC : struct, IIsLess<T>
    {
        ref var r = ref MemoryMarshal.GetReference(array);
        ref var s = ref MemoryMarshal.GetReference(swap);
        int ptl, ptr, pts;
        bool le, gt;

        // parity_merge_two(array + 0, swap + 0, ...)
        {
            ptl = 0; ptr = 2; pts = 0; // into array / swap
            le = HeadBranchlessMerge(ref Unsafe.Add(ref s, pts), ref Unsafe.Add(ref r, ptl), ref Unsafe.Add(ref r, ptr), cmp);
            ptl += le ? 1 : 0; ptr += le ? 0 : 1; pts++;
            Unsafe.Add(ref s, pts) = !cmp.IsLess(in Unsafe.Add(ref r, ptr), in Unsafe.Add(ref r, ptl))
                ? Unsafe.Add(ref r, ptl) : Unsafe.Add(ref r, ptr);

            ptl = 1; ptr = 3; pts = 3;
            gt = TailBranchlessMerge(ref Unsafe.Add(ref s, pts), ref Unsafe.Add(ref r, ptl), ref Unsafe.Add(ref r, ptr), cmp);
            ptl += gt ? -1 : 0; ptr += gt ? 0 : -1; pts--;
            Unsafe.Add(ref s, pts) = cmp.IsLess(in Unsafe.Add(ref r, ptr), in Unsafe.Add(ref r, ptl))
                ? Unsafe.Add(ref r, ptl) : Unsafe.Add(ref r, ptr);
        }
        // parity_merge_two(array + 4, swap + 4, ...)
        {
            ptl = 4; ptr = 6; pts = 4;
            le = HeadBranchlessMerge(ref Unsafe.Add(ref s, pts), ref Unsafe.Add(ref r, ptl), ref Unsafe.Add(ref r, ptr), cmp);
            ptl += le ? 1 : 0; ptr += le ? 0 : 1; pts++;
            Unsafe.Add(ref s, pts) = !cmp.IsLess(in Unsafe.Add(ref r, ptr), in Unsafe.Add(ref r, ptl))
                ? Unsafe.Add(ref r, ptl) : Unsafe.Add(ref r, ptr);

            ptl = 5; ptr = 7; pts = 7;
            gt = TailBranchlessMerge(ref Unsafe.Add(ref s, pts), ref Unsafe.Add(ref r, ptl), ref Unsafe.Add(ref r, ptr), cmp);
            ptl += gt ? -1 : 0; ptr += gt ? 0 : -1; pts--;
            Unsafe.Add(ref s, pts) = cmp.IsLess(in Unsafe.Add(ref r, ptr), in Unsafe.Add(ref r, ptl))
                ? Unsafe.Add(ref r, ptl) : Unsafe.Add(ref r, ptr);
        }
        // parity_merge_four(swap, array, ...) — 3 head + 3 tail + finals
        {
            ptl = 0; ptr = 4; pts = 0; // into swap / array
            le = HeadBranchlessMerge(ref Unsafe.Add(ref r, pts), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
            ptl += le ? 1 : 0; ptr += le ? 0 : 1; pts++;
            le = HeadBranchlessMerge(ref Unsafe.Add(ref r, pts), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
            ptl += le ? 1 : 0; ptr += le ? 0 : 1; pts++;
            le = HeadBranchlessMerge(ref Unsafe.Add(ref r, pts), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
            ptl += le ? 1 : 0; ptr += le ? 0 : 1; pts++;
            Unsafe.Add(ref r, pts) = !cmp.IsLess(in Unsafe.Add(ref s, ptr), in Unsafe.Add(ref s, ptl))
                ? Unsafe.Add(ref s, ptl) : Unsafe.Add(ref s, ptr);

            ptl = 3; ptr = 7; pts = 7;
            gt = TailBranchlessMerge(ref Unsafe.Add(ref r, pts), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
            ptl += gt ? -1 : 0; ptr += gt ? 0 : -1; pts--;
            gt = TailBranchlessMerge(ref Unsafe.Add(ref r, pts), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
            ptl += gt ? -1 : 0; ptr += gt ? 0 : -1; pts--;
            gt = TailBranchlessMerge(ref Unsafe.Add(ref r, pts), ref Unsafe.Add(ref s, ptl), ref Unsafe.Add(ref s, ptr), cmp);
            ptl += gt ? -1 : 0; ptr += gt ? 0 : -1; pts--;
            Unsafe.Add(ref r, pts) = cmp.IsLess(in Unsafe.Add(ref s, ptr), in Unsafe.Add(ref s, ptl))
                ? Unsafe.Add(ref s, ptl) : Unsafe.Add(ref s, ptr);
        }
    }

    // ---- quad_swap support (quadsort.c:257+) ----

    /// <summary>Flag-driven pair swap: swap [i, i+1] iff disordered (analyzer flag already
    /// computed — avoids re-comparison). quadsort.c:293-296 and 358-361.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapPairIf<T>(ref T r, int i, bool disordered)
    {
        var a0 = Unsafe.Add(ref r, i);
        var a1 = Unsafe.Add(ref r, i + 1);
        Unsafe.Add(ref r, i) = disordered ? a1 : a0;
        Unsafe.Add(ref r, i + 1) = disordered ? a0 : a1;
    }

    /// <summary>cmp(&amp;r[i], &amp;r[j]) &gt; 0, i.e. r[i] &gt; r[j]. Positional form of the
    /// upstream cmp(x, y) &gt; 0 analyzer tests.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GtAt<T, TC>(ref T r, int i, int j, TC cmp) where TC : struct, IIsLess<T>
        => cmp.IsLess(in Unsafe.Add(ref r, j), in Unsafe.Add(ref r, i));

    /// <summary>cmp(&amp;r[i], &amp;r[j]) &lt;= 0, i.e. r[i] &lt;= r[j]. Positional form of the
    /// upstream cmp(x, y) &lt;= 0 tests.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LeAt<T, TC>(ref T r, int i, int j, TC cmp) where TC : struct, IIsLess<T>
        => !cmp.IsLess(in Unsafe.Add(ref r, j), in Unsafe.Add(ref r, i));

    /// <summary>quad_swap (quadsort.c:257-414): 8-block analyzer with ordered/reversed fast
    /// paths; builds sorted 32-blocks. Returns 1 iff the whole array was one descending run.
    /// The final nmemb % 32 > 8 tail_merge branch (quadsort.c:409-412) is Task 5 scope — stubbed.</summary>
    internal static int QuadSwap<T, TC>(Span<T> array, Span<T> swap, TC cmp) where TC : struct, IIsLess<T>
    {
        int nmemb = array.Length;
        ref var r = ref MemoryMarshal.GetReference(array);
        int count = nmemb / 8;
        int pta = 0, pts = 0;
        int rem = nmemb % 8;
        bool v1 = false, v2 = false, v3 = false, v4 = false;
        bool skipTail = false;

        while (count-- > 0)
        {
            v1 = GtAt(ref r, pta, pta + 1, cmp);
            v2 = GtAt(ref r, pta + 2, pta + 3, cmp);
            v3 = GtAt(ref r, pta + 4, pta + 5, cmp);
            v4 = GtAt(ref r, pta + 6, pta + 7, cmp);

            switch ((v1 ? 1 : 0) + (v2 ? 1 : 0) * 2 + (v3 ? 1 : 0) * 4 + (v4 ? 1 : 0) * 8)
            {
                case 0:
                    if (LeAt(ref r, pta + 1, pta + 2, cmp) && LeAt(ref r, pta + 3, pta + 4, cmp) && LeAt(ref r, pta + 5, pta + 6, cmp))
                        goto ordered;
                    QuadSwapMerge(array.Slice(pta, 8), swap, cmp);
                    goto block_done;

                case 15:
                    if (GtAt(ref r, pta + 1, pta + 2, cmp) && GtAt(ref r, pta + 3, pta + 4, cmp) && GtAt(ref r, pta + 5, pta + 6, cmp))
                    { pts = pta; goto reversed; }
                    goto not_ordered;

                default:
                    goto not_ordered;
            }

        not_ordered:
            // pair swaps driven by precomputed disorder flags, then merge the 8-block
            SwapPairIf(ref r, pta, v1); pta += 2;
            SwapPairIf(ref r, pta, v2); pta += 2;
            SwapPairIf(ref r, pta, v3); pta += 2;
            SwapPairIf(ref r, pta, v4); pta -= 6;
            QuadSwapMerge(array.Slice(pta, 8), swap, cmp);

        block_done:
            pta += 8;
            continue;

        ordered:
            // previous 8-block was fully ordered — process the next one directly
            pta += 8;

            if (count-- > 0)
            {
                v1 = GtAt(ref r, pta, pta + 1, cmp);
                v2 = GtAt(ref r, pta + 2, pta + 3, cmp);
                v3 = GtAt(ref r, pta + 4, pta + 5, cmp);
                v4 = GtAt(ref r, pta + 6, pta + 7, cmp);

                if (v1 | v2 | v3 | v4)
                {
                    if ((v1 & v2 & v3 & v4) && GtAt(ref r, pta + 1, pta + 2, cmp) && GtAt(ref r, pta + 3, pta + 4, cmp) && GtAt(ref r, pta + 5, pta + 6, cmp))
                    { pts = pta; goto reversed; }
                    goto not_ordered;
                }
                if (LeAt(ref r, pta + 1, pta + 2, cmp) && LeAt(ref r, pta + 3, pta + 4, cmp) && LeAt(ref r, pta + 5, pta + 6, cmp))
                    goto ordered;
                QuadSwapMerge(array.Slice(pta, 8), swap, cmp);
                pta += 8;
                continue;
            }
            goto loop_tail; // C break — count exhausted

        reversed:
            // previous 8-block was fully reversed — extend the descending run
            pta += 8;

            if (count-- > 0)
            {
                // v flags are IN-ORDER here: cmp(pta+k, pta+k+1) <= 0
                v1 = LeAt(ref r, pta, pta + 1, cmp);
                v2 = LeAt(ref r, pta + 2, pta + 3, cmp);
                v3 = LeAt(ref r, pta + 4, pta + 5, cmp);
                v4 = LeAt(ref r, pta + 6, pta + 7, cmp);

                if (v1 | v2 | v3 | v4)
                {
                    // not reversed — fall through to run reversal below
                }
                else if (GtAt(ref r, pta - 1, pta, cmp) && GtAt(ref r, pta + 1, pta + 2, cmp) && GtAt(ref r, pta + 3, pta + 4, cmp) && GtAt(ref r, pta + 5, pta + 6, cmp))
                {
                    goto reversed;
                }

                QuadReversal(array.Slice(pts, pta - pts), cmp); // [pts, pta-1]

                if ((v1 & v2 & v3 & v4) && LeAt(ref r, pta + 1, pta + 2, cmp) && LeAt(ref r, pta + 3, pta + 4, cmp) && LeAt(ref r, pta + 5, pta + 6, cmp))
                    goto ordered;
                if (!(v1 | v2 | v3 | v4) && GtAt(ref r, pta + 1, pta + 2, cmp) && GtAt(ref r, pta + 3, pta + 4, cmp) && GtAt(ref r, pta + 5, pta + 6, cmp))
                { pts = pta; goto reversed; }

                // pair swaps: v = in-order flag, so swap when NOT in order
                SwapPairIf(ref r, pta, !v1); pta += 2;
                SwapPairIf(ref r, pta, !v2); pta += 2;
                SwapPairIf(ref r, pta, !v3); pta += 2;
                SwapPairIf(ref r, pta, !v4); pta -= 6;

                if (GtAt(ref r, pta + 1, pta + 2, cmp) || GtAt(ref r, pta + 3, pta + 4, cmp) || GtAt(ref r, pta + 5, pta + 6, cmp))
                    QuadSwapMerge(array.Slice(pta, 8), swap, cmp);
                pta += 8;
                continue;
            }

            // count exhausted — Duff-style tail check: break on first in-order pair
            switch (rem)
            {
                case 7: if (LeAt(ref r, pta + 5, pta + 6, cmp)) goto partial_tail; goto case 6;
                case 6: if (LeAt(ref r, pta + 4, pta + 5, cmp)) goto partial_tail; goto case 5;
                case 5: if (LeAt(ref r, pta + 3, pta + 4, cmp)) goto partial_tail; goto case 4;
                case 4: if (LeAt(ref r, pta + 2, pta + 3, cmp)) goto partial_tail; goto case 3;
                case 3: if (LeAt(ref r, pta + 1, pta + 2, cmp)) goto partial_tail; goto case 2;
                case 2: if (LeAt(ref r, pta + 0, pta + 1, cmp)) goto partial_tail; goto case 1;
                case 1: if (LeAt(ref r, pta - 1, pta + 0, cmp)) goto partial_tail; goto case 0;
                case 0:
                    QuadReversal(array.Slice(pts, pta + rem - pts), cmp); // [pts, pta+rem-1]
                    if (pts == 0)
                        return 1; // the whole array was one descending run
                    skipTail = true;
                    goto loop_tail;
            }

        partial_tail:
            QuadReversal(array.Slice(pts, pta - pts), cmp); // [pts, pta-1]
            goto loop_tail;
        } // end analyzer loop

    loop_tail:
        // reverse_end: (when skipTail) the %8 tail was part of the reversed run
        if (!skipTail)
            TailSwap(array.Slice(pta, rem), swap, cmp);

        // 32-block pass (quadsort.c:396-407)
        pta = 0;
        for (count = nmemb / 32; count-- > 0; pta += 32)
        {
            if (LeAt(ref r, pta + 7, pta + 8, cmp) && LeAt(ref r, pta + 15, pta + 16, cmp) && LeAt(ref r, pta + 23, pta + 24, cmp))
                continue;
            ParityMerge(swap, array.Slice(pta, 16), 8, 8, cmp);
            ParityMerge(swap.Slice(16), array.Slice(pta + 16, 16), 8, 8, cmp);
            ParityMerge(array.Slice(pta, 32), swap, 16, 16, cmp);
        }

        if (nmemb % 32 > 8)
        {
            throw new NotImplementedException(); // TODO(task-5): tail_merge for 9..31-element tails
        }
        return 0;
    }
}
