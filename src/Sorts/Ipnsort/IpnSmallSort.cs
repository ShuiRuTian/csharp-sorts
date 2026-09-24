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

    /// <summary>compare-swap on two elements held in caller locals (the local-variable
    /// network — see Sort9Optimal/Sort13Optimal): places the smaller at `a` and the larger
    /// at `b`, branchless. Operating on locals lets RyuJIT keep the whole network in
    /// registers; the previous array form (`ref vBase`+pos) reloaded and stored both
    /// elements on every comparison because the JIT cannot prove no aliasing (JitDisasm:
    /// sort9 261 instructions array-form vs 129 local-form; LLVM's equivalent is 131).
    ///
    /// For integer primitives under the natural-order comparer (ComparableCmp&lt;T&gt;, the
    /// default entry) this uses Math.Min/Math.Max, which RyuJIT lowers to a single cmp +
    /// two cmov that SHARE the flags (~11 instructions). Everything else (custom/inverted/
    /// interface comparers, char and nint which have no Math.Min overload, float/double
    /// with their NaN semantics, non-primitive T) keeps the value ternary. Equal values
    /// never swap in either path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompareSwap<T, TC>(ref T a, ref T b, TC cmp) where TC : struct, IIsLess<T>
    {
        if (typeof(TC) == typeof(ComparableCmp<int>) && typeof(T) == typeof(int))
        {
            ref int ia = ref Unsafe.As<T, int>(ref a);
            ref int ib = ref Unsafe.As<T, int>(ref b);
            int x = ia, y = ib;
            ia = Math.Min(x, y);
            ib = Math.Max(x, y);
            return;
        }
        if (typeof(TC) == typeof(ComparableCmp<long>) && typeof(T) == typeof(long))
        {
            ref long ia = ref Unsafe.As<T, long>(ref a);
            ref long ib = ref Unsafe.As<T, long>(ref b);
            long x = ia, y = ib;
            ia = Math.Min(x, y);
            ib = Math.Max(x, y);
            return;
        }
        if (typeof(TC) == typeof(ComparableCmp<uint>) && typeof(T) == typeof(uint))
        {
            ref uint ia = ref Unsafe.As<T, uint>(ref a);
            ref uint ib = ref Unsafe.As<T, uint>(ref b);
            uint x = ia, y = ib;
            ia = Math.Min(x, y);
            ib = Math.Max(x, y);
            return;
        }
        if (typeof(TC) == typeof(ComparableCmp<ulong>) && typeof(T) == typeof(ulong))
        {
            ref ulong ia = ref Unsafe.As<T, ulong>(ref a);
            ref ulong ib = ref Unsafe.As<T, ulong>(ref b);
            ulong x = ia, y = ib;
            ia = Math.Min(x, y);
            ib = Math.Max(x, y);
            return;
        }
        if (typeof(TC) == typeof(ComparableCmp<short>) && typeof(T) == typeof(short))
        {
            ref short ia = ref Unsafe.As<T, short>(ref a);
            ref short ib = ref Unsafe.As<T, short>(ref b);
            short x = ia, y = ib;
            ia = Math.Min(x, y);
            ib = Math.Max(x, y);
            return;
        }
        if (typeof(TC) == typeof(ComparableCmp<ushort>) && typeof(T) == typeof(ushort))
        {
            ref ushort ia = ref Unsafe.As<T, ushort>(ref a);
            ref ushort ib = ref Unsafe.As<T, ushort>(ref b);
            ushort x = ia, y = ib;
            ia = Math.Min(x, y);
            ib = Math.Max(x, y);
            return;
        }
        if (typeof(TC) == typeof(ComparableCmp<byte>) && typeof(T) == typeof(byte))
        {
            ref byte ia = ref Unsafe.As<T, byte>(ref a);
            ref byte ib = ref Unsafe.As<T, byte>(ref b);
            byte x = ia, y = ib;
            ia = Math.Min(x, y);
            ib = Math.Max(x, y);
            return;
        }
        if (typeof(TC) == typeof(ComparableCmp<sbyte>) && typeof(T) == typeof(sbyte))
        {
            ref sbyte ia = ref Unsafe.As<T, sbyte>(ref a);
            ref sbyte ib = ref Unsafe.As<T, sbyte>(ref b);
            sbyte x = ia, y = ib;
            ia = Math.Min(x, y);
            ib = Math.Max(x, y);
            return;
        }

        T px = a;
        T py = b;
        bool shouldSwap = cmp.IsLess(in py, in px);

        // Value selects mirroring upstream's if-let value swap (smallsort.rs:313-316):
        // equal values never swap (is_less is false for equal).
        a = shouldSwap ? py : px;
        b = shouldSwap ? px : py;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNetworkLen(int len) =>
        throw new ArgumentException(
            $"v.Length ({len}) violates the network small-sort contract: must be <= {NetworkScratchLen}.");

    /// <summary>sort9_optimal (smallsort.rs:329-371): optimal sorting network, see
    /// https://bertdobbelaere.github.io/sorting-networks.html — hand-ported
    /// comparison-for-comparison. Never inlined upstream to avoid code bloat.
    ///
    /// Local-variable form: load the 9 elements once, run the network on locals (which
    /// RyuJIT keeps in registers), store once. The array form reloaded/stored both
    /// elements on every comparison because the JIT cannot prove no aliasing (JitDisasm:
    /// 261 instructions array-form vs 129 local-form).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sort9Optimal<T, TC>(ref T vBase, int len, TC cmp) where TC : struct, IIsLess<T>
    {
        if (len < 9)
            ThrowOptimalNetworkLen(9);

        T v0 = Unsafe.Add(ref vBase, 0);
        T v1 = Unsafe.Add(ref vBase, 1);
        T v2 = Unsafe.Add(ref vBase, 2);
        T v3 = Unsafe.Add(ref vBase, 3);
        T v4 = Unsafe.Add(ref vBase, 4);
        T v5 = Unsafe.Add(ref vBase, 5);
        T v6 = Unsafe.Add(ref vBase, 6);
        T v7 = Unsafe.Add(ref vBase, 7);
        T v8 = Unsafe.Add(ref vBase, 8);

        CompareSwap(ref v0, ref v3, cmp);
        CompareSwap(ref v1, ref v7, cmp);
        CompareSwap(ref v2, ref v5, cmp);
        CompareSwap(ref v4, ref v8, cmp);
        CompareSwap(ref v0, ref v7, cmp);
        CompareSwap(ref v2, ref v4, cmp);
        CompareSwap(ref v3, ref v8, cmp);
        CompareSwap(ref v5, ref v6, cmp);
        CompareSwap(ref v0, ref v2, cmp);
        CompareSwap(ref v1, ref v3, cmp);
        CompareSwap(ref v4, ref v5, cmp);
        CompareSwap(ref v7, ref v8, cmp);
        CompareSwap(ref v1, ref v4, cmp);
        CompareSwap(ref v3, ref v6, cmp);
        CompareSwap(ref v5, ref v7, cmp);
        CompareSwap(ref v0, ref v1, cmp);
        CompareSwap(ref v2, ref v4, cmp);
        CompareSwap(ref v3, ref v5, cmp);
        CompareSwap(ref v6, ref v8, cmp);
        CompareSwap(ref v2, ref v3, cmp);
        CompareSwap(ref v4, ref v5, cmp);
        CompareSwap(ref v6, ref v7, cmp);
        CompareSwap(ref v1, ref v2, cmp);
        CompareSwap(ref v3, ref v4, cmp);
        CompareSwap(ref v5, ref v6, cmp);

        Unsafe.Add(ref vBase, 0) = v0;
        Unsafe.Add(ref vBase, 1) = v1;
        Unsafe.Add(ref vBase, 2) = v2;
        Unsafe.Add(ref vBase, 3) = v3;
        Unsafe.Add(ref vBase, 4) = v4;
        Unsafe.Add(ref vBase, 5) = v5;
        Unsafe.Add(ref vBase, 6) = v6;
        Unsafe.Add(ref vBase, 7) = v7;
        Unsafe.Add(ref vBase, 8) = v8;
    }

    /// <summary>sort13_optimal (smallsort.rs:375-437): optimal sorting network, see
    /// https://bertdobbelaere.github.io/sorting-networks.html — hand-ported
    /// comparison-for-comparison. Never inlined upstream to avoid code bloat.
    /// Local-variable form (see Sort9Optimal); 13 live values may spill for 8-byte T, but
    /// still avoids the per-comparison array reload/store.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sort13Optimal<T, TC>(ref T vBase, int len, TC cmp) where TC : struct, IIsLess<T>
    {
        if (len < 13)
            ThrowOptimalNetworkLen(13);

        T v0 = Unsafe.Add(ref vBase, 0);
        T v1 = Unsafe.Add(ref vBase, 1);
        T v2 = Unsafe.Add(ref vBase, 2);
        T v3 = Unsafe.Add(ref vBase, 3);
        T v4 = Unsafe.Add(ref vBase, 4);
        T v5 = Unsafe.Add(ref vBase, 5);
        T v6 = Unsafe.Add(ref vBase, 6);
        T v7 = Unsafe.Add(ref vBase, 7);
        T v8 = Unsafe.Add(ref vBase, 8);
        T v9 = Unsafe.Add(ref vBase, 9);
        T v10 = Unsafe.Add(ref vBase, 10);
        T v11 = Unsafe.Add(ref vBase, 11);
        T v12 = Unsafe.Add(ref vBase, 12);

        CompareSwap(ref v0, ref v12, cmp);
        CompareSwap(ref v1, ref v10, cmp);
        CompareSwap(ref v2, ref v9, cmp);
        CompareSwap(ref v3, ref v7, cmp);
        CompareSwap(ref v5, ref v11, cmp);
        CompareSwap(ref v6, ref v8, cmp);
        CompareSwap(ref v1, ref v6, cmp);
        CompareSwap(ref v2, ref v3, cmp);
        CompareSwap(ref v4, ref v11, cmp);
        CompareSwap(ref v7, ref v9, cmp);
        CompareSwap(ref v8, ref v10, cmp);
        CompareSwap(ref v0, ref v4, cmp);
        CompareSwap(ref v1, ref v2, cmp);
        CompareSwap(ref v3, ref v6, cmp);
        CompareSwap(ref v7, ref v8, cmp);
        CompareSwap(ref v9, ref v10, cmp);
        CompareSwap(ref v11, ref v12, cmp);
        CompareSwap(ref v4, ref v6, cmp);
        CompareSwap(ref v5, ref v9, cmp);
        CompareSwap(ref v8, ref v11, cmp);
        CompareSwap(ref v10, ref v12, cmp);
        CompareSwap(ref v0, ref v5, cmp);
        CompareSwap(ref v3, ref v8, cmp);
        CompareSwap(ref v4, ref v7, cmp);
        CompareSwap(ref v6, ref v11, cmp);
        CompareSwap(ref v9, ref v10, cmp);
        CompareSwap(ref v0, ref v1, cmp);
        CompareSwap(ref v2, ref v5, cmp);
        CompareSwap(ref v6, ref v9, cmp);
        CompareSwap(ref v7, ref v8, cmp);
        CompareSwap(ref v10, ref v11, cmp);
        CompareSwap(ref v1, ref v3, cmp);
        CompareSwap(ref v2, ref v4, cmp);
        CompareSwap(ref v5, ref v6, cmp);
        CompareSwap(ref v9, ref v10, cmp);
        CompareSwap(ref v1, ref v2, cmp);
        CompareSwap(ref v3, ref v4, cmp);
        CompareSwap(ref v5, ref v7, cmp);
        CompareSwap(ref v6, ref v8, cmp);
        CompareSwap(ref v2, ref v3, cmp);
        CompareSwap(ref v4, ref v5, cmp);
        CompareSwap(ref v6, ref v7, cmp);
        CompareSwap(ref v8, ref v9, cmp);
        CompareSwap(ref v3, ref v4, cmp);
        CompareSwap(ref v5, ref v6, cmp);

        Unsafe.Add(ref vBase, 0) = v0;
        Unsafe.Add(ref vBase, 1) = v1;
        Unsafe.Add(ref vBase, 2) = v2;
        Unsafe.Add(ref vBase, 3) = v3;
        Unsafe.Add(ref vBase, 4) = v4;
        Unsafe.Add(ref vBase, 5) = v5;
        Unsafe.Add(ref vBase, 6) = v6;
        Unsafe.Add(ref vBase, 7) = v7;
        Unsafe.Add(ref vBase, 8) = v8;
        Unsafe.Add(ref vBase, 9) = v9;
        Unsafe.Add(ref vBase, 10) = v10;
        Unsafe.Add(ref vBase, 11) = v11;
        Unsafe.Add(ref vBase, 12) = v12;
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
