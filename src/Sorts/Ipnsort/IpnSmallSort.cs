// Ported from https://github.com/Voultapher/sort-research-rs (ipnsort src/smallsort.rs),
// MIT OR Apache-2.0, by Lukas Bergdoll. C# port 2026 — the UnstableSmallSortTypeImpl
// dispatch (Fallback / General / Network), small_sort_network with its hand-tuned
// sort9_optimal / sort13_optimal comparison schedules, and small_sort_general.
//
// The shared primitives upstream ships identically in driftsort's smallsort.rs —
// insertion_sort_shift_left, insert_tail, sort4_stable, sort8_stable, bidirectional_merge
// (merge_up/merge_down included) — were diffed against this file and live in
// SmallSortPrimitives.cs (with the Freeze dispatch config); only the ipnsort-specific
// layers live here. Rust's MaybeUninit stack arrays become stackalloc over a byte buffer
// for unmanaged T (smallsort.rs:114-123, 241) and an ArrayPool rental for reference
// types — upstream stack-allocates either way, C# cannot stackalloc unconstrained T.
// CopyOnDrop / ManuallyDrop panic machinery has no port: an exception from the
// comparator leaves an unspecified state.
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sorts;

/// <summary>UnstalbeSmallSort (sic) — smallsort.rs:77-81.</summary>
internal enum IpnSmallSortKind
{
    Fallback,
    General,
    Network,
}

/// <summary>choose_unstable_small_sort (smallsort.rs:83-97), cached per T. Upstream gates
/// the dispatch on Freeze; non-Freeze types take the default impl (smallsort.rs:19-31)
/// — insertion sort, threshold 16 — which this config folds into Fallback.</summary>
internal static class IpnSmallSortConfig<T>
{
    /// <summary>Rust's Freeze auto-trait: no interior mutability. The shared predicate
    /// (SmallSortConfig&lt;T&gt;.IsFreezeLike, SmallSortPrimitives.cs): value types without
    /// managed references plus reference types themselves.</summary>
    internal static readonly bool IsFreezeLike = SmallSortConfig<T>.IsFreezeLike;

    /// <summary>Rust T::IS_COPY (smallsort.rs:773-783): a C# unmanaged value type.</summary>
    internal static readonly bool IsCopyLike =
        typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    internal static readonly IpnSmallSortKind Kind = Choose();

    private static IpnSmallSortKind Choose()
    {
        if (!IsFreezeLike)
            return IpnSmallSortKind.Fallback;

        if (IsCopyLike
            && HasEfficientInPlaceSwap()
            && Unsafe.SizeOf<T>() * IpnSmallSort.NetworkScratchLen <= IpnSmallSort.MaxStackArraySize)
        {
            // Heuristic for int like types.
            return IpnSmallSortKind.Network;
        }

        if (Unsafe.SizeOf<T>() * IpnSmallSort.GeneralScratchLen <= IpnSmallSort.MaxStackArraySize)
            return IpnSmallSortKind.General;

        return IpnSmallSortKind.Fallback;
    }

    /// <summary>has_efficient_in_place_swap (smallsort.rs:758-763):
    /// size_of::&lt;T&gt;() &lt;= size_of::&lt;u64&gt;() — at most 8 bytes. Verified against the
    /// upstream type_info test: i32/u64 yes, u128/String no.</summary>
    private static bool HasEfficientInPlaceSwap() => Unsafe.SizeOf<T>() <= sizeof(ulong);
}

/// <summary>UnstableSmallSortTypeImpl (smallsort.rs:10-52): sorts spans of at most
/// Threshold&lt;T&gt;() elements in place — either the integer-tuned network, the general
/// sort8/ping-pong path, or plain insertion sort.</summary>
internal static class IpnSmallSort
{
    /// <summary>SMALL_SORT_FALLBACK_THRESHOLD (smallsort.rs:55): optimal number of
    /// comparisons, and good perf.</summary>
    internal const int FallbackThreshold = 16;

    /// <summary>SMALL_SORT_GENERAL_THRESHOLD (smallsort.rs:58).</summary>
    internal const int GeneralThreshold = 32;

    /// <summary>SMALL_SORT_GENERAL_SCRATCH_LEN (smallsort.rs:66) = threshold + 16: the
    /// sort8_stable ping-pong regions live at scratch[len..len+8] and [len+8..len+16].</summary>
    internal const int GeneralScratchLen = GeneralThreshold + 16;

    /// <summary>SMALL_SORT_NETWORK_THRESHOLD / _SCRATCH_LEN (smallsort.rs:69-70).</summary>
    internal const int NetworkThreshold = 32;
    internal const int NetworkScratchLen = NetworkThreshold;

    /// <summary>MAX_STACK_ARRAY_SIZE (smallsort.rs:75): conservative stack usage bound.</summary>
    internal const int MaxStackArraySize = 4096;

    /// <summary>UnstableSmallSortTypeImpl::small_sort_threshold (smallsort.rs:35-41).</summary>
    internal static int Threshold<T>() => IpnSmallSortConfig<T>.Kind switch
    {
        IpnSmallSortKind.Network => NetworkThreshold,
        IpnSmallSortKind.General => GeneralThreshold,
        _ => FallbackThreshold,
    };

    /// <summary>UnstableSmallSortTypeImpl::small_sort (smallsort.rs:43-52) — the
    /// inst_unstable_small_sort (smallsort.rs:99-105) dispatch as a switch. Upstream
    /// trusts the threshold contract (smallsort.rs:11-12); this seam throws.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SmallSort<T, TC>(Span<T> v, TC cmp) where TC : struct, IIsLess<T>
    {
        if (v.Length > Threshold<T>())
            ThrowTooLong(v.Length, Threshold<T>());

        switch (IpnSmallSortConfig<T>.Kind)
        {
            case IpnSmallSortKind.Network:
                SmallSortNetwork(v, cmp);
                break;
            case IpnSmallSortKind.General:
                SmallSortGeneral(v, cmp);
                break;
            default:
                // small_sort_fallback (smallsort.rs:107-111).
                if (v.Length >= 2)
                    SmallSortPrimitives.InsertionSortShiftLeft(v, cmp, 1);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooLong(int len, int threshold) =>
        throw new ArgumentException(
            $"v.Length ({len}) violates the small-sort contract: must be <= Threshold<T>() ({threshold}).");

    /// <summary>small_sort_network (smallsort.rs:225-290): this implementation is tuned
    /// to be efficient for integer types. Presorts each half (or the whole range when
    /// no_merge) with an optimal network, extends with insertion sort, then
    /// bidirectional-merges v into stack scratch and copies back.</summary>
    private static void SmallSortNetwork<T, TC>(Span<T> v, TC cmp) where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (len < 2)
            return;

        if (len > NetworkScratchLen)
            ThrowNetworkLen(len); // upstream intrinsics::abort (smallsort.rs:237-239)

        // Network implies IsCopyLike — T is an unmanaged value type, so the stack array
        // (upstream MaybeUninit&lt;[T; 32]&gt;) is a stackalloc'd byte buffer reinterpreted
        // as Span&lt;T&gt;; alignment matches or exceeds T's natural alignment.
        Span<byte> raw = stackalloc byte[NetworkScratchLen * Unsafe.SizeOf<T>()];
        Span<T> scratch = MemoryMarshal.CreateSpan(
            ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(raw)), NetworkScratchLen);

        ref T vBase = ref MemoryMarshal.GetReference(v);
        ref T scratchBase = ref MemoryMarshal.GetReference(scratch);
        int lenDiv2 = len / 2;
        bool noMerge = len < 18;

        int regionStart = 0;
        int regionLen = noMerge ? len : lenDiv2;

        // Upstream's loop keeps region state implicit via pointer identity
        // (`region.as_ptr() != v_base`, smallsort.rs:269-275); here it is explicit —
        // first pass [0, lenDiv2), second pass [lenDiv2, len).
        while (true)
        {
            ref T regionBase = ref Unsafe.Add(ref vBase, regionStart);
            int presortedLen;
            if (regionLen >= 13)
            {
                Sort13Optimal(ref regionBase, regionLen, cmp);
                presortedLen = 13;
            }
            else if (regionLen >= 9)
            {
                Sort9Optimal(ref regionBase, regionLen, cmp);
                presortedLen = 9;
            }
            else
            {
                presortedLen = 1;
            }

            SmallSortPrimitives.InsertionSortShiftLeft(v.Slice(regionStart, regionLen), cmp, presortedLen);

            if (noMerge)
                return;

            if (regionStart != 0)
                break;

            regionStart = lenDiv2;
            regionLen = len - lenDiv2;
        }

        // bidirectional_merge into scratch, then copy back (smallsort.rs:281-289).
        SmallSortPrimitives.BidirectionalMerge(ref vBase, len, ref scratchBase, cmp);
        scratch[..len].CopyTo(v);
    }

    /// <summary>swap_if_less (smallsort.rs:294-325): swap vBase[aPos] and vBase[bPos]
    /// if the value at bPos is strictly less than the one at aPos. Branchless — value
    /// ternaries over the two loads; the JIT if-converts these to csel/cmov for the
    /// small T this network path serves (sizeof &lt;= 8, so the value copies are free).
    /// NOTE: the former conditional-REF form (`ref (shouldSwap ? ref vB : ref vA)`)
    /// always compiled to a data-dependent branch on both x64 and ARM64 — Roslyn
    /// lowers conditional ref expressions to IL control flow and RyuJIT never
    /// if-converts a byref select. JitDisasm-verified.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapIfLess<T, TC>(ref T vBase, int aPos, int bPos, TC cmp) where TC : struct, IIsLess<T>
    {
        T vA = Unsafe.Add(ref vBase, aPos);
        T vB = Unsafe.Add(ref vBase, bPos);

        bool shouldSwap = cmp.IsLess(in vB, in vA);

        // Value selects mirroring upstream's if-let value swap (smallsort.rs:313-316):
        // equal values never swap (is_less is false for equal).
        Unsafe.Add(ref vBase, aPos) = shouldSwap ? vB : vA;
        Unsafe.Add(ref vBase, bPos) = shouldSwap ? vA : vB;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNetworkLen(int len) =>
        throw new ArgumentException(
            $"v.Length ({len}) violates the network small-sort contract: must be <= {NetworkScratchLen}.");

    /// <summary>sort9_optimal (smallsort.rs:329-371): optimal sorting network, see
    /// https://bertdobbelaere.github.io/sorting-networks.html — hand-ported
    /// comparison-for-comparison. Never inlined upstream to avoid code bloat.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sort9Optimal<T, TC>(ref T vBase, int len, TC cmp) where TC : struct, IIsLess<T>
    {
        if (len < 9)
            ThrowOptimalNetworkLen(9);

        SwapIfLess(ref vBase, 0, 3, cmp);
        SwapIfLess(ref vBase, 1, 7, cmp);
        SwapIfLess(ref vBase, 2, 5, cmp);
        SwapIfLess(ref vBase, 4, 8, cmp);
        SwapIfLess(ref vBase, 0, 7, cmp);
        SwapIfLess(ref vBase, 2, 4, cmp);
        SwapIfLess(ref vBase, 3, 8, cmp);
        SwapIfLess(ref vBase, 5, 6, cmp);
        SwapIfLess(ref vBase, 0, 2, cmp);
        SwapIfLess(ref vBase, 1, 3, cmp);
        SwapIfLess(ref vBase, 4, 5, cmp);
        SwapIfLess(ref vBase, 7, 8, cmp);
        SwapIfLess(ref vBase, 1, 4, cmp);
        SwapIfLess(ref vBase, 3, 6, cmp);
        SwapIfLess(ref vBase, 5, 7, cmp);
        SwapIfLess(ref vBase, 0, 1, cmp);
        SwapIfLess(ref vBase, 2, 4, cmp);
        SwapIfLess(ref vBase, 3, 5, cmp);
        SwapIfLess(ref vBase, 6, 8, cmp);
        SwapIfLess(ref vBase, 2, 3, cmp);
        SwapIfLess(ref vBase, 4, 5, cmp);
        SwapIfLess(ref vBase, 6, 7, cmp);
        SwapIfLess(ref vBase, 1, 2, cmp);
        SwapIfLess(ref vBase, 3, 4, cmp);
        SwapIfLess(ref vBase, 5, 6, cmp);
    }

    /// <summary>sort13_optimal (smallsort.rs:375-437): optimal sorting network, see
    /// https://bertdobbelaere.github.io/sorting-networks.html — hand-ported
    /// comparison-for-comparison. Never inlined upstream to avoid code bloat.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sort13Optimal<T, TC>(ref T vBase, int len, TC cmp) where TC : struct, IIsLess<T>
    {
        if (len < 13)
            ThrowOptimalNetworkLen(13);

        SwapIfLess(ref vBase, 0, 12, cmp);
        SwapIfLess(ref vBase, 1, 10, cmp);
        SwapIfLess(ref vBase, 2, 9, cmp);
        SwapIfLess(ref vBase, 3, 7, cmp);
        SwapIfLess(ref vBase, 5, 11, cmp);
        SwapIfLess(ref vBase, 6, 8, cmp);
        SwapIfLess(ref vBase, 1, 6, cmp);
        SwapIfLess(ref vBase, 2, 3, cmp);
        SwapIfLess(ref vBase, 4, 11, cmp);
        SwapIfLess(ref vBase, 7, 9, cmp);
        SwapIfLess(ref vBase, 8, 10, cmp);
        SwapIfLess(ref vBase, 0, 4, cmp);
        SwapIfLess(ref vBase, 1, 2, cmp);
        SwapIfLess(ref vBase, 3, 6, cmp);
        SwapIfLess(ref vBase, 7, 8, cmp);
        SwapIfLess(ref vBase, 9, 10, cmp);
        SwapIfLess(ref vBase, 11, 12, cmp);
        SwapIfLess(ref vBase, 4, 6, cmp);
        SwapIfLess(ref vBase, 5, 9, cmp);
        SwapIfLess(ref vBase, 8, 11, cmp);
        SwapIfLess(ref vBase, 10, 12, cmp);
        SwapIfLess(ref vBase, 0, 5, cmp);
        SwapIfLess(ref vBase, 3, 8, cmp);
        SwapIfLess(ref vBase, 4, 7, cmp);
        SwapIfLess(ref vBase, 6, 11, cmp);
        SwapIfLess(ref vBase, 9, 10, cmp);
        SwapIfLess(ref vBase, 0, 1, cmp);
        SwapIfLess(ref vBase, 2, 5, cmp);
        SwapIfLess(ref vBase, 6, 9, cmp);
        SwapIfLess(ref vBase, 7, 8, cmp);
        SwapIfLess(ref vBase, 10, 11, cmp);
        SwapIfLess(ref vBase, 1, 3, cmp);
        SwapIfLess(ref vBase, 2, 4, cmp);
        SwapIfLess(ref vBase, 5, 6, cmp);
        SwapIfLess(ref vBase, 9, 10, cmp);
        SwapIfLess(ref vBase, 1, 2, cmp);
        SwapIfLess(ref vBase, 3, 4, cmp);
        SwapIfLess(ref vBase, 5, 7, cmp);
        SwapIfLess(ref vBase, 6, 8, cmp);
        SwapIfLess(ref vBase, 2, 3, cmp);
        SwapIfLess(ref vBase, 4, 5, cmp);
        SwapIfLess(ref vBase, 6, 7, cmp);
        SwapIfLess(ref vBase, 8, 9, cmp);
        SwapIfLess(ref vBase, 3, 4, cmp);
        SwapIfLess(ref vBase, 5, 6, cmp);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOptimalNetworkLen(int minLen) =>
        throw new ArgumentException($"region length violates sort{minLen}_optimal's contract: must be >= {minLen}.");

    /// <summary>small_sort_general (smallsort.rs:113-124): entry with the stack array.
    /// Unmanaged T gets a stackalloc'd byte buffer reinterpreted as Span&lt;T&gt;
    /// (upstream MaybeUninit&lt;[T; 48]&gt;, guaranteed by the General size bound);
    /// reference types rent from the ArrayPool — C# cannot stackalloc managed T
    /// (a two-branch pattern this port applies uniformly: stackalloc for unmanaged T,
    /// ArrayPool for reference-carrying T).</summary>
    private static void SmallSortGeneral<T, TC>(Span<T> v, TC cmp) where TC : struct, IIsLess<T>
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            T[] rented = ArrayPool<T>.Shared.Rent(GeneralScratchLen);
            try
            {
                SmallSortGeneralWithScratch(v, rented.AsSpan(0, GeneralScratchLen), cmp);
            }
            finally
            {
                ArrayPool<T>.Shared.Return(rented, clearArray: true);
            }
        }
        else
        {
            Span<byte> raw = stackalloc byte[GeneralScratchLen * Unsafe.SizeOf<T>()];
            Span<T> scratch = MemoryMarshal.CreateSpan(
                ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(raw)), GeneralScratchLen);
            SmallSortGeneralWithScratch(v, scratch, cmp);
        }
    }

    /// <summary>small_sort_general_with_scratch (smallsort.rs:126-207): presort both
    /// halves of v into scratch (sort8_stable for sizeof &lt;= 16 &amp;&amp; len &gt;= 16, else
    /// sort4_stable for len &gt;= 8, else one element each), extend each half to full
    /// length with insert_tail, then bidirectional_merge scratch back into v. The
    /// CopyOnDrop around the final merge (smallsort.rs:189-205) is panic-recovery only
    /// and has no port. Identical to driftsort's sort_small_general — diffed upstream.</summary>
    private static void SmallSortGeneralWithScratch<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
    {
        int len = v.Length;
        if (len < 2)
            return;

        if (scratch.Length < len + 16)
            ThrowScratchTooSmall(scratch.Length, len + 16); // upstream abort (smallsort.rs:136-138)

        ref T vBase = ref MemoryMarshal.GetReference(v);
        ref T scratchBase = ref MemoryMarshal.GetReference(scratch);
        int lenDiv2 = len / 2;

        int presortedLen;
        if (Unsafe.SizeOf<T>() <= 16 && len >= 16)
        {
            // First half: v[0..8] into scratch[0..8], ping-ponging through scratch[len..].
            SmallSortPrimitives.Sort8Stable(ref vBase, ref scratchBase, ref Unsafe.Add(ref scratchBase, len), cmp);
            // Second half: v[lenDiv2..lenDiv2+8] into scratch[lenDiv2..lenDiv2+8],
            // ping-ponging through scratch[len+8..len+16].
            SmallSortPrimitives.Sort8Stable(
                ref Unsafe.Add(ref vBase, lenDiv2),
                ref Unsafe.Add(ref scratchBase, lenDiv2),
                ref Unsafe.Add(ref scratchBase, len + 8), cmp);
            presortedLen = 8;
        }
        else if (len >= 8)
        {
            SmallSortPrimitives.Sort4Stable(ref vBase, ref scratchBase, cmp);
            SmallSortPrimitives.Sort4Stable(
                ref Unsafe.Add(ref vBase, lenDiv2), ref Unsafe.Add(ref scratchBase, lenDiv2), cmp);
            presortedLen = 4;
        }
        else
        {
            scratchBase = vBase;
            Unsafe.Add(ref scratchBase, lenDiv2) = Unsafe.Add(ref vBase, lenDiv2);
            presortedLen = 1;
        }

        // Extend each presorted half to its full length in scratch (smallsort.rs:171-186).
        for (int half = 0; half < 2; half++)
        {
            int offset = half == 0 ? 0 : lenDiv2;
            ref T src = ref Unsafe.Add(ref vBase, offset);
            ref T dst = ref Unsafe.Add(ref scratchBase, offset);
            int desiredLen = half == 0 ? lenDiv2 : len - lenDiv2;
            for (int i = presortedLen; i < desiredLen; i++)
            {
                Unsafe.Add(ref dst, i) = Unsafe.Add(ref src, i);
                SmallSortPrimitives.InsertTail(ref dst, i, cmp);
            }
        }

        // Both halves of scratch are sorted: merge them back into v (smallsort.rs:200-204).
        SmallSortPrimitives.BidirectionalMerge(ref scratchBase, len, ref vBase, cmp);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowScratchTooSmall(int scratchLen, int needed) =>
        throw new ArgumentException(
            $"scratch.Length ({scratchLen}) violates the general small-sort contract: at least {needed} elements are required.");
}
