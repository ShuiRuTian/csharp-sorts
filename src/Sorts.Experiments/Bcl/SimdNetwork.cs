// SIMD sorting-network small-sort, generic over the 4/8-byte scalar types �?// int/uint/float (16 lanes per Vector512, 8 per Vector256) and
// long/ulong/double (8 lanes per Vector512, 4 per Vector256). The structure
// mirrors x86-simd-sort / the JDK's libsimdsort:
//   - chunks of one vector are sorted by an in-register bitonic network;
//   - chunks are progressively merged (bottom-up bitonic merge over the vector
//     array); the vector count is the smallest power of two that fits
//     (shrink-to-fit, up to 16 vectors for Vector512 / 32 for Vector256);
//   - partial chunks use masked loads/stores and are padded with the type's
//     MaxValue (float/double: +Infinity �?the caller's NaN pre-pass keeps the
//     slice NaN-free, and ties with real infinities are harmless for sortedness).
//
// Performance-first design (disasm-driven):
//   - NO stackalloc/localloc anywhere: localloc is a hard JIT inlining blocker.
//     Full-width chunks load/store directly from the span; partial chunks use
//     masked loads/stores (AVX-512's MaskLoad takes the padding as its source
//     operand; AVX2's zero-fills, so the padding is blended in).
//   - NO step arrays / loops in the network itself: the bitonic steps are
//     unrolled straight-line code over static readonly vectors (built once per
//     type in the cctor; the shuffle indices / blend masks are bit patterns, so
//     they are built as 32/64-bit integers and reinterpreted).
//   - Every method is AggressiveInlining; ISA and type checks fold per call site,
//     so the whole chain inlines into the caller (GraftSort.IntroSort).
//
// Pointer lifetime contract (Unsafe.AsPointer): the masked load/store helpers take
// a raw pointer from a managed byref and consume it in the SAME expression �?the
// intrinsic is a single instruction and there is no allocation, no GC-triggering
// call and no safepoint between taking the pointer and using it, so the GC cannot
// move the array inside that window. The byref itself is GC-tracked; the raw
// pointer is not �?it must never survive the statement.
//
// Priority (mirrors the JDK): the caller runs insertion sort first for the
// smallest partitions (<= 16) and only uses this network above that.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sorts.Experiments.Bcl;

/// <summary>Which SIMD sorting network the graft's small-sort slot should use.
/// The ops policy selects it; an unsupported ISA falls back to the scalar path.</summary>
public enum SimdNetworkKind
{
    None,
    Avx256,
    Avx512,
}

internal static class SimdNetwork
{
    /// <summary>Max span length handled: 16 vectors of Vector512 (256 int32 / 128
    /// int64 lanes) or 32 vectors of Vector256.</summary>
    internal const int Threshold = 256;
}

/// <summary>Per-element-type dispatch: only the supported scalar types reach the
/// worker (the typeof checks fold away at JIT time for concrete T).</summary>
public static class SimdNetwork<T>
{
    internal static bool IsSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get =>
            typeof(T) == typeof(int) ||
            typeof(T) == typeof(uint) ||
            typeof(T) == typeof(long) ||
            typeof(T) == typeof(ulong) ||
            typeof(T) == typeof(float) ||
            typeof(T) == typeof(double);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySort(Span<T> v, SimdNetworkKind kind)
    {
        if (typeof(T) == typeof(int))
        {
            return SimdNetworkWorker<int>.TrySort(Unsafe.As<Span<T>, Span<int>>(ref v), kind);
        }
        if (typeof(T) == typeof(uint))
        {
            return SimdNetworkWorker<uint>.TrySort(Unsafe.As<Span<T>, Span<uint>>(ref v), kind);
        }
        if (typeof(T) == typeof(long))
        {
            return SimdNetworkWorker<long>.TrySort(Unsafe.As<Span<T>, Span<long>>(ref v), kind);
        }
        if (typeof(T) == typeof(ulong))
        {
            return SimdNetworkWorker<ulong>.TrySort(Unsafe.As<Span<T>, Span<ulong>>(ref v), kind);
        }
        if (typeof(T) == typeof(float))
        {
            return SimdNetworkWorker<float>.TrySort(Unsafe.As<Span<T>, Span<float>>(ref v), kind);
        }
        if (typeof(T) == typeof(double))
        {
            return SimdNetworkWorker<double>.TrySort(Unsafe.As<Span<T>, Span<double>>(ref v), kind);
        }
        return false;
    }
}

/// <summary>The network for one concrete element type. The lane width (and with it
/// the unrolled step sequence) is selected by Vector512&lt;T&gt;.Count /
/// Vector256&lt;T&gt;.Count �?both JIT constants per instantiation.</summary>
internal static class SimdNetworkWorker<T> where T : unmanaged, IComparable<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TrySort(Span<T> v, SimdNetworkKind kind)
    {
        if (v.Length > SimdNetwork.Threshold)
        {
            return false;
        }

        if (kind == SimdNetworkKind.Avx512)
        {
            if (!Avx512F.IsSupported)
            {
                return false;
            }
            SortAvx512(v);
            return true;
        }

        if (kind == SimdNetworkKind.Avx256)
        {
            if (!Avx2.IsSupported)
            {
                return false;
            }
            SortAvx256(v);
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- AVX-512

    // Not inlined into the caller: this is where the ~2 KB vector buffer lives, and
    // SkipLocalsInit must apply to the frame owner. The buffer is fully written
    // before it is read, so the JIT's per-entry zero-init is pure overhead; keeping
    // the buffer out of IntroSort also keeps every other variant's frame/codegen
    // untouched. The network steps themselves still inline into this method.
    //
    // nv is specialized for 2/4/8 (the common small partitions): those paths hold the
    // vectors in registers and unroll the whole merge �?no buffer traffic, no loop
    // control, no address arithmetic. 16/32 keep the general loop version.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static unsafe void SortAvx512(Span<T> v)
    {
        int len = v.Length;
        if (len <= 1)
        {
            return;
        }

        int lanes = Vector512<T>.Count;                    // 16 or 8
        int chunks = (len + lanes - 1) / lanes;
        int nv = 2;
        while (nv < chunks)
        {
            nv <<= 1;                                      // shrink-to-fit power of two
        }

        ref T p = ref MemoryMarshal.GetReference(v);
        if ((typeof(T) == typeof(int) || typeof(T) == typeof(uint)) && nv <= 4 && len >= (nv / 2) * 16)
        {
            // Source-generated transposed network (column sort + scale merge), fully
            // expanded. Capped at nv = 4: at nv = 8 the eight simultaneously-live
            // vectors make RyuJIT spill (~106 store/load pairs), which eats the
            // algorithm's instruction advantage — nv = 8 keeps the hand-written
            // pipelined row-wise path instead.
            if (typeof(T) == typeof(int))
            {
                SimdNetworkGenerated.SortAvx512_Int(ref Unsafe.As<T, int>(ref p), len, nv);
            }
            else
            {
                SimdNetworkGeneratedUInt.SortAvx512_UInt(ref Unsafe.As<T, uint>(ref p), len, nv);
            }
            return;
        }

        if (len / lanes < nv / 2)
        {
            // Fewer full chunks than the specialized paths assume (tiny spans where the
            // nv floor of 2 over-provisions): use the general loop version.
            SortNLoop_512(ref p, len, lanes, nv);
            return;
        }

        switch (nv)
        {
            case 2:
                SortN2_512(ref p, len, lanes);
                return;
            case 4:
                SortN4_512(ref p, len, lanes);
                return;
            case 8:
                SortN8_512(ref p, len, lanes);
                return;
            default:
                SortNLoop_512(ref p, len, lanes, nv);
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SortNLoop_512(ref T p, int len, int lanes, int nv)
    {
        Unsafe.SkipInit(out VecBuf512<T> buf);
        Span<Vector512<T>> vecs = buf;
        ref Vector512<T> vec0 = ref MemoryMarshal.GetReference(vecs);

        for (int i = 0; i < nv; i++)
        {
            Unsafe.Add(ref vec0, i) = SortVec512(Load512(ref Unsafe.Add(ref p, i * lanes), len - i * lanes));
        }

        MergeVecs512(vecs[..nv]);

        for (int i = 0; i < nv; i++)
        {
            Store512(ref Unsafe.Add(ref p, i * lanes), len - i * lanes, Unsafe.Add(ref vec0, i));
        }
    }

    /// <summary>Compare-exchange two whole vectors (min to a, max to b).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Coex512(ref Vector512<T> a, ref Vector512<T> b)
    {
        Vector512<T> x = a;
        Vector512<T> y = b;
        a = Min512(x, y);
        b = Max512(x, y);
    }

    /// <summary>Merges two sorted single vectors (register pair): reverse the second,
    /// compare-exchange, then the within-vector levels on both.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MergePair512(ref Vector512<T> a, ref Vector512<T> b)
    {
        b = Rev512(b);
        Coex512(ref a, ref b);
        a = MergeSteps512(a);
        b = MergeSteps512(b);
    }

    /// <summary>Merges two sorted 2-vector blocks held in registers.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MergeBlocks512(ref Vector512<T> a0, ref Vector512<T> a1, ref Vector512<T> b0, ref Vector512<T> b1)
    {
        Vector512<T> t = Rev512(b0);
        b0 = Rev512(b1);
        b1 = t;
        Coex512(ref a0, ref b0);
        Coex512(ref a1, ref b1);
        Coex512(ref a0, ref a1);
        Coex512(ref b0, ref b1);
        a0 = MergeSteps512(a0);
        a1 = MergeSteps512(a1);
        b0 = MergeSteps512(b0);
        b1 = MergeSteps512(b1);
    }

    /// <summary>Merges two sorted 4-vector blocks held in registers.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MergeBlocks4_512(
        ref Vector512<T> a0, ref Vector512<T> a1, ref Vector512<T> a2, ref Vector512<T> a3,
        ref Vector512<T> b0, ref Vector512<T> b1, ref Vector512<T> b2, ref Vector512<T> b3)
    {
        Vector512<T> t0 = Rev512(b0);
        Vector512<T> t1 = Rev512(b1);
        b0 = Rev512(b3);
        b1 = Rev512(b2);
        b2 = t1;
        b3 = t0;
        Coex512(ref a0, ref b0);
        Coex512(ref a1, ref b1);
        Coex512(ref a2, ref b2);
        Coex512(ref a3, ref b3);
        Coex512(ref a0, ref a2);
        Coex512(ref a1, ref a3);
        Coex512(ref b0, ref b2);
        Coex512(ref b1, ref b3);
        Coex512(ref a0, ref a1);
        Coex512(ref a2, ref a3);
        Coex512(ref b0, ref b1);
        Coex512(ref b2, ref b3);
        a0 = MergeSteps512(a0);
        a1 = MergeSteps512(a1);
        a2 = MergeSteps512(a2);
        a3 = MergeSteps512(a3);
        b0 = MergeSteps512(b0);
        b1 = MergeSteps512(b1);
        b2 = MergeSteps512(b2);
        b3 = MergeSteps512(b3);
    }

    /// <summary>Two chunks (nv = 2): sort both, then one register-pair merge.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SortN2_512(ref T p, int len, int lanes)
    {
        // Chunk 0 is always full (nv = 2 => chunks = 2); chunk 1 may be partial.
        Vector512<T> v0 = SortVec512(Vector512.LoadUnsafe(ref p));
        Vector512<T> v1 = SortVec512(Load512(ref Unsafe.Add(ref p, lanes), len - lanes));
        MergePair512(ref v0, ref v1);
        v0.StoreUnsafe(ref p);
        Store512(ref Unsafe.Add(ref p, lanes), len - lanes, v1);
    }

    /// <summary>Four chunks (nv = 4): 2 pairs + one 4-block merge, all in registers.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SortN4_512(ref T p, int len, int lanes)
    {
        // Chunks 0..1 are always full (nv = 4 => chunks in 3..4). Pipelined order.
        Vector512<T> v0 = SortVec512(Vector512.LoadUnsafe(ref p));
        Vector512<T> v1 = SortVec512(Vector512.LoadUnsafe(ref Unsafe.Add(ref p, lanes)));
        MergePair512(ref v0, ref v1);
        Vector512<T> v2 = SortVec512(Load512(ref Unsafe.Add(ref p, 2 * lanes), len - 2 * lanes));
        Vector512<T> v3 = SortVec512(Load512(ref Unsafe.Add(ref p, 3 * lanes), len - 3 * lanes));
        MergePair512(ref v2, ref v3);
        MergeBlocks512(ref v0, ref v1, ref v2, ref v3);
        v0.StoreUnsafe(ref p);
        v1.StoreUnsafe(ref Unsafe.Add(ref p, lanes));
        Store512(ref Unsafe.Add(ref p, 2 * lanes), len - 2 * lanes, v2);
        Store512(ref Unsafe.Add(ref p, 3 * lanes), len - 3 * lanes, v3);
    }

    /// <summary>Eight chunks (nv = 8): 4 pairs + 2 4-blocks + one 8-block merge.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SortN8_512(ref T p, int len, int lanes)
    {
        // Pipelined (depth-first) order: sort a pair, merge it, then move on �?fewer
        // vectors stay live across the by-value SortVec calls, so fewer spill/reloads.
        Vector512<T> v0 = SortVec512(Vector512.LoadUnsafe(ref p));
        Vector512<T> v1 = SortVec512(Vector512.LoadUnsafe(ref Unsafe.Add(ref p, lanes)));
        MergePair512(ref v0, ref v1);
        Vector512<T> v2 = SortVec512(Vector512.LoadUnsafe(ref Unsafe.Add(ref p, 2 * lanes)));
        Vector512<T> v3 = SortVec512(Vector512.LoadUnsafe(ref Unsafe.Add(ref p, 3 * lanes)));
        MergePair512(ref v2, ref v3);
        MergeBlocks512(ref v0, ref v1, ref v2, ref v3);
        Vector512<T> v4 = SortVec512(Load512(ref Unsafe.Add(ref p, 4 * lanes), len - 4 * lanes));
        Vector512<T> v5 = SortVec512(Load512(ref Unsafe.Add(ref p, 5 * lanes), len - 5 * lanes));
        MergePair512(ref v4, ref v5);
        Vector512<T> v6 = SortVec512(Load512(ref Unsafe.Add(ref p, 6 * lanes), len - 6 * lanes));
        Vector512<T> v7 = SortVec512(Load512(ref Unsafe.Add(ref p, 7 * lanes), len - 7 * lanes));
        MergePair512(ref v6, ref v7);
        MergeBlocks512(ref v4, ref v5, ref v6, ref v7);
        MergeBlocks4_512(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);
        v0.StoreUnsafe(ref p);
        v1.StoreUnsafe(ref Unsafe.Add(ref p, lanes));
        v2.StoreUnsafe(ref Unsafe.Add(ref p, 2 * lanes));
        v3.StoreUnsafe(ref Unsafe.Add(ref p, 3 * lanes));
        Store512(ref Unsafe.Add(ref p, 4 * lanes), len - 4 * lanes, v4);
        Store512(ref Unsafe.Add(ref p, 5 * lanes), len - 5 * lanes, v5);
        Store512(ref Unsafe.Add(ref p, 6 * lanes), len - 6 * lanes, v6);
        Store512(ref Unsafe.Add(ref p, 7 * lanes), len - 7 * lanes, v7);
    }

    /// <summary>Loads up to a vector; lanes at or past <paramref name="count"/> become
    /// the type's MaxValue. The full-chunk fast path inlines (a single load); the
    /// masked slow path is a call so its mask-table access (and the generic static
    /// base fetch that comes with it) stays out of every caller.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector512<T> Load512(ref T p, int count)
    {
        if (count >= Vector512<T>.Count)
        {
            return Vector512.LoadUnsafe(ref p);
        }
        if (count <= 0)
        {
            return Pad512;
        }
        return Load512Partial(ref p, count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe Vector512<T> Load512Partial(ref T p, int count)
    {
        if (Vector512<T>.Count == 16)
        {
            Vector512<int> mask = Tables16.MaskTable[count];
            Vector512<int> loaded = Avx512F.MaskLoad((int*)Unsafe.AsPointer(ref p), mask, AsI(Pad512));
            return AsT(loaded);
        }
        else
        {
            Vector512<long> mask = Tables8.MaskTable[count];
            Vector512<long> loaded = Avx512F.MaskLoad((long*)Unsafe.AsPointer(ref p), mask, AsL(Pad512));
            return AsT64(loaded);
        }
    }

    /// <summary>Stores the first <paramref name="count"/> lanes: the full-chunk fast
    /// path inlines (a single store), the masked slow path is a call.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Store512(ref T p, int count, Vector512<T> value)
    {
        if (count >= Vector512<T>.Count)
        {
            value.StoreUnsafe(ref p);
            return;
        }
        if (count > 0)
        {
            Store512Partial(ref p, count, value);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void Store512Partial(ref T p, int count, Vector512<T> value)
    {
        if (Vector512<T>.Count == 16)
        {
            Avx512F.MaskStore((int*)Unsafe.AsPointer(ref p), Tables16.MaskTable[count], AsI(value));
        }
        else
        {
            Avx512F.MaskStore((long*)Unsafe.AsPointer(ref p), Tables8.MaskTable[count], AsL(value));
        }
    }

    /// <summary>Sorts the vectors (each sorted ascending), then merges them into one
    /// sorted run, bottom-up: for each block width (1, 2, 4, ... vectors) the two
    /// sorted halves of every block are merged �?reverse the second half's global
    /// element order (turning the two ascending runs into one bitonic sequence), run
    /// the cross-vector bitonic levels (d = width..1, vector units), then the
    /// within-vector levels (j = lanes/2..1). The flat "reverse once, one level
    /// sweep" variant is only valid for two vectors: for four or more the first half
    /// must itself be a single sorted run.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MergeVecs512(Span<Vector512<T>> vecs)
    {
        int n = vecs.Length;
        ref Vector512<T> v0 = ref MemoryMarshal.GetReference(vecs);
        for (int width = 1; width < n; width <<= 1)
        {
            for (int start = width; start < n; start += width << 1)
            {
                for (int i = 0; i < (width >> 1); i++)
                {
                    Vector512<T> t = Rev512(Unsafe.Add(ref v0, start + i));
                    Unsafe.Add(ref v0, start + i) = Rev512(Unsafe.Add(ref v0, start + width - 1 - i));
                    Unsafe.Add(ref v0, start + width - 1 - i) = t;
                }
                if ((width & 1) != 0)
                {
                    ref Vector512<T> mid = ref Unsafe.Add(ref v0, start + (width >> 1));
                    mid = Rev512(mid);
                }
            }

            for (int d = width; d >= 1; d >>= 1)
            {
                for (int i = 0; i < n; i++)
                {
                    if ((i & d) == 0)
                    {
                        ref Vector512<T> lo = ref Unsafe.Add(ref v0, i);
                        ref Vector512<T> hi = ref Unsafe.Add(ref v0, i + d);
                        Vector512<T> a = lo;
                        Vector512<T> b = hi;
                        lo = Min512(a, b);
                        hi = Max512(a, b);
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                ref Vector512<T> e = ref Unsafe.Add(ref v0, i);
                e = MergeSteps512(e);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> Rev512(Vector512<T> v) =>
        Vector512<T>.Count == 16
            ? AsT(Avx512F.PermuteVar16x32(AsI(v), Tables16.Reverse))
            : AsT64(Avx512F.PermuteVar8x64(AsL(v), Tables8.Reverse));

    /// <summary>Raw lane permute. The generic Vector*.Shuffle carries .NET's
    /// out-of-range semantics (negative index => zero lane) and the JIT pays a
    /// compare + mask-expand + AND per shuffle; the PermuteVar intrinsics are the
    /// bare vpermd/vpermq. Indices are integer lane numbers, so the value is
    /// reinterpreted bit-preservingly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> Perm512(Vector512<T> v, Vector512<T> idx) =>
        Vector512<T>.Count == 16
            ? AsT(Avx512F.PermuteVar16x32(AsI(v), AsI(idx)))
            : AsT64(Avx512F.PermuteVar8x64(AsL(v), AsL(idx)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector512<T> SortVec512(Vector512<T> v)
    {
        if (Vector512<T>.Count == 16)
        {
            v = Cx512(v, AsT(Tables16.Xor1), AsT(Tables16.S2J1));
            v = Cx512(v, AsT(Tables16.Xor2), AsT(Tables16.S4J2));
            v = Cx512(v, AsT(Tables16.Xor1), AsT(Tables16.S4J1));
            v = Cx512(v, AsT(Tables16.Xor4), AsT(Tables16.S8J4));
            v = Cx512(v, AsT(Tables16.Xor2), AsT(Tables16.S8J2));
            v = Cx512(v, AsT(Tables16.Xor1), AsT(Tables16.S8J1));
            v = Cx512(v, AsT(Tables16.Xor8), AsT(Tables16.S16J8));
            v = Cx512(v, AsT(Tables16.Xor4), AsT(Tables16.S16J4));
            v = Cx512(v, AsT(Tables16.Xor2), AsT(Tables16.S16J2));
            v = Cx512(v, AsT(Tables16.Xor1), AsT(Tables16.S16J1));
        }
        else
        {
            v = Cx512(v, AsT64(Tables8.Xor1), AsT64(Tables8.S2J1));
            v = Cx512(v, AsT64(Tables8.Xor2), AsT64(Tables8.S4J2));
            v = Cx512(v, AsT64(Tables8.Xor1), AsT64(Tables8.S4J1));
            v = Cx512(v, AsT64(Tables8.Xor4), AsT64(Tables8.S8J4));
            v = Cx512(v, AsT64(Tables8.Xor2), AsT64(Tables8.S8J2));
            v = Cx512(v, AsT64(Tables8.Xor1), AsT64(Tables8.S8J1));
        }
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> MergeSteps512(Vector512<T> v)
    {
        if (Vector512<T>.Count == 16)
        {
            v = Cx512(v, AsT(Tables16.Xor8), AsT(Tables16.S16J8));
            v = Cx512(v, AsT(Tables16.Xor4), AsT(Tables16.S16J4));
            v = Cx512(v, AsT(Tables16.Xor2), AsT(Tables16.S16J2));
            v = Cx512(v, AsT(Tables16.Xor1), AsT(Tables16.S16J1));
        }
        else
        {
            v = Cx512(v, AsT64(Tables8.Xor4), AsT64(Tables8.S8J4));
            v = Cx512(v, AsT64(Tables8.Xor2), AsT64(Tables8.S8J2));
            v = Cx512(v, AsT64(Tables8.Xor1), AsT64(Tables8.S8J1));
        }
        return v;
    }

    /// <summary>One compare-exchange step: pair lane i with i^j and keep min/max per
    /// the precomputed keep-max mask. Both the shuffle and the selection run on the
    /// integer lane view (bit-preserving): Vector512.Shuffle has no generic overload,
    /// and the generic Vector512.ConditionalSelect lowers to a boolean-vector
    /// sequence (compare + mask expand + logic ops) while Avx512F.BlendVariable is a
    /// single masked blend (mask MSB per lane).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> Cx512(Vector512<T> v, Vector512<T> xor, Vector512<T> keepMax)
    {
        Vector512<T> t = Perm512(v, xor);
        Vector512<T> lo = Min512(v, t);
        Vector512<T> hi = Max512(v, t);
        return Vector512<T>.Count == 16
            ? AsT(Avx512F.BlendVariable(AsI(lo), AsI(hi), AsI(keepMax)))
            : AsT64(Avx512F.BlendVariable(AsL(lo), AsL(hi), AsL(keepMax)));
    }

    // ------------------------------------------------------------------- AVX2

    // Same frame/SkipLocalsInit reasoning as SortAvx512, and the same nv
    // specialization for 2/4/8 (register-resident merge).
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static unsafe void SortAvx256(Span<T> v)
    {
        int len = v.Length;
        if (len <= 1)
        {
            return;
        }

        int lanes = Vector256<T>.Count;                    // 8 or 4
        int chunks = (len + lanes - 1) / lanes;
        int nv = 2;
        while (nv < chunks)
        {
            nv <<= 1;
        }

        ref T p = ref MemoryMarshal.GetReference(v);
        if (len / lanes < nv / 2)
        {
            SortNLoop_256(ref p, len, lanes, nv);
            return;
        }

        switch (nv)
        {
            case 2:
                SortN2_256(ref p, len, lanes);
                return;
            case 4:
                SortN4_256(ref p, len, lanes);
                return;
            case 8:
                SortN8_256(ref p, len, lanes);
                return;
            default:
                SortNLoop_256(ref p, len, lanes, nv);
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SortNLoop_256(ref T p, int len, int lanes, int nv)
    {
        Unsafe.SkipInit(out VecBuf256<T> buf);
        Span<Vector256<T>> vecs = buf;
        ref Vector256<T> vec0 = ref MemoryMarshal.GetReference(vecs);

        for (int i = 0; i < nv; i++)
        {
            Unsafe.Add(ref vec0, i) = SortVec256(Load256(ref Unsafe.Add(ref p, i * lanes), len - i * lanes));
        }

        MergeVecs256(vecs[..nv]);

        for (int i = 0; i < nv; i++)
        {
            Store256(ref Unsafe.Add(ref p, i * lanes), len - i * lanes, Unsafe.Add(ref vec0, i));
        }
    }

    /// <summary>Compare-exchange two whole vectors (min to a, max to b).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Coex256(ref Vector256<T> a, ref Vector256<T> b)
    {
        Vector256<T> x = a;
        Vector256<T> y = b;
        a = Min256(x, y);
        b = Max256(x, y);
    }

    /// <summary>Merges two sorted single vectors (register pair).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MergePair256(ref Vector256<T> a, ref Vector256<T> b)
    {
        b = Rev256(b);
        Coex256(ref a, ref b);
        a = MergeSteps256(a);
        b = MergeSteps256(b);
    }

    /// <summary>Merges two sorted 2-vector blocks held in registers.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MergeBlocks256(ref Vector256<T> a0, ref Vector256<T> a1, ref Vector256<T> b0, ref Vector256<T> b1)
    {
        Vector256<T> t = Rev256(b0);
        b0 = Rev256(b1);
        b1 = t;
        Coex256(ref a0, ref b0);
        Coex256(ref a1, ref b1);
        Coex256(ref a0, ref a1);
        Coex256(ref b0, ref b1);
        a0 = MergeSteps256(a0);
        a1 = MergeSteps256(a1);
        b0 = MergeSteps256(b0);
        b1 = MergeSteps256(b1);
    }

    /// <summary>Merges two sorted 4-vector blocks held in registers.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MergeBlocks4_256(
        ref Vector256<T> a0, ref Vector256<T> a1, ref Vector256<T> a2, ref Vector256<T> a3,
        ref Vector256<T> b0, ref Vector256<T> b1, ref Vector256<T> b2, ref Vector256<T> b3)
    {
        Vector256<T> t0 = Rev256(b0);
        Vector256<T> t1 = Rev256(b1);
        b0 = Rev256(b3);
        b1 = Rev256(b2);
        b2 = t1;
        b3 = t0;
        Coex256(ref a0, ref b0);
        Coex256(ref a1, ref b1);
        Coex256(ref a2, ref b2);
        Coex256(ref a3, ref b3);
        Coex256(ref a0, ref a2);
        Coex256(ref a1, ref a3);
        Coex256(ref b0, ref b2);
        Coex256(ref b1, ref b3);
        Coex256(ref a0, ref a1);
        Coex256(ref a2, ref a3);
        Coex256(ref b0, ref b1);
        Coex256(ref b2, ref b3);
        a0 = MergeSteps256(a0);
        a1 = MergeSteps256(a1);
        a2 = MergeSteps256(a2);
        a3 = MergeSteps256(a3);
        b0 = MergeSteps256(b0);
        b1 = MergeSteps256(b1);
        b2 = MergeSteps256(b2);
        b3 = MergeSteps256(b3);
    }

    /// <summary>Two chunks (nv = 2).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SortN2_256(ref T p, int len, int lanes)
    {
        // Chunk 0 is always full (nv = 2 => chunks = 2); chunk 1 may be partial.
        Vector256<T> v0 = SortVec256(Vector256.LoadUnsafe(ref p));
        Vector256<T> v1 = SortVec256(Load256(ref Unsafe.Add(ref p, lanes), len - lanes));
        MergePair256(ref v0, ref v1);
        v0.StoreUnsafe(ref p);
        Store256(ref Unsafe.Add(ref p, lanes), len - lanes, v1);
    }

    /// <summary>Four chunks (nv = 4).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SortN4_256(ref T p, int len, int lanes)
    {
        // Chunks 0..1 are always full (nv = 4 => chunks in 3..4). Pipelined order.
        Vector256<T> v0 = SortVec256(Vector256.LoadUnsafe(ref p));
        Vector256<T> v1 = SortVec256(Vector256.LoadUnsafe(ref Unsafe.Add(ref p, lanes)));
        MergePair256(ref v0, ref v1);
        Vector256<T> v2 = SortVec256(Load256(ref Unsafe.Add(ref p, 2 * lanes), len - 2 * lanes));
        Vector256<T> v3 = SortVec256(Load256(ref Unsafe.Add(ref p, 3 * lanes), len - 3 * lanes));
        MergePair256(ref v2, ref v3);
        MergeBlocks256(ref v0, ref v1, ref v2, ref v3);
        v0.StoreUnsafe(ref p);
        v1.StoreUnsafe(ref Unsafe.Add(ref p, lanes));
        Store256(ref Unsafe.Add(ref p, 2 * lanes), len - 2 * lanes, v2);
        Store256(ref Unsafe.Add(ref p, 3 * lanes), len - 3 * lanes, v3);
    }

    /// <summary>Eight chunks (nv = 8).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SortN8_256(ref T p, int len, int lanes)
    {
        // Pipelined (depth-first) order: see SortN8_512.
        Vector256<T> v0 = SortVec256(Vector256.LoadUnsafe(ref p));
        Vector256<T> v1 = SortVec256(Vector256.LoadUnsafe(ref Unsafe.Add(ref p, lanes)));
        MergePair256(ref v0, ref v1);
        Vector256<T> v2 = SortVec256(Vector256.LoadUnsafe(ref Unsafe.Add(ref p, 2 * lanes)));
        Vector256<T> v3 = SortVec256(Vector256.LoadUnsafe(ref Unsafe.Add(ref p, 3 * lanes)));
        MergePair256(ref v2, ref v3);
        MergeBlocks256(ref v0, ref v1, ref v2, ref v3);
        Vector256<T> v4 = SortVec256(Load256(ref Unsafe.Add(ref p, 4 * lanes), len - 4 * lanes));
        Vector256<T> v5 = SortVec256(Load256(ref Unsafe.Add(ref p, 5 * lanes), len - 5 * lanes));
        MergePair256(ref v4, ref v5);
        Vector256<T> v6 = SortVec256(Load256(ref Unsafe.Add(ref p, 6 * lanes), len - 6 * lanes));
        Vector256<T> v7 = SortVec256(Load256(ref Unsafe.Add(ref p, 7 * lanes), len - 7 * lanes));
        MergePair256(ref v6, ref v7);
        MergeBlocks256(ref v4, ref v5, ref v6, ref v7);
        MergeBlocks4_256(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);
        v0.StoreUnsafe(ref p);
        v1.StoreUnsafe(ref Unsafe.Add(ref p, lanes));
        v2.StoreUnsafe(ref Unsafe.Add(ref p, 2 * lanes));
        v3.StoreUnsafe(ref Unsafe.Add(ref p, 3 * lanes));
        Store256(ref Unsafe.Add(ref p, 4 * lanes), len - 4 * lanes, v4);
        Store256(ref Unsafe.Add(ref p, 5 * lanes), len - 5 * lanes, v5);
        Store256(ref Unsafe.Add(ref p, 6 * lanes), len - 6 * lanes, v6);
        Store256(ref Unsafe.Add(ref p, 7 * lanes), len - 7 * lanes, v7);
    }

    /// <summary>Loads up to a vector: fast path inline, masked slow path a call (see
    /// Load512).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<T> Load256(ref T p, int count)
    {
        if (count >= Vector256<T>.Count)
        {
            return Vector256.LoadUnsafe(ref p);
        }
        if (count <= 0)
        {
            return Pad256;
        }
        return Load256Partial(ref p, count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe Vector256<T> Load256Partial(ref T p, int count)
    {
        if (Vector256<T>.Count == 8)
        {
            Vector256<int> mask = Tables256_8.MaskTable[count];
            Vector256<int> loaded = Avx2.MaskLoad((int*)Unsafe.AsPointer(ref p), mask);
            return Vector256.ConditionalSelect(AsT256(mask), AsT256(loaded), Pad256);
        }
        else
        {
            Vector256<long> mask = Tables256_4.MaskTable[count];
            Vector256<long> loaded = Avx2.MaskLoad((long*)Unsafe.AsPointer(ref p), mask);
            return Vector256.ConditionalSelect(AsT256_64(mask), AsT256_64(loaded), Pad256);
        }
    }

    /// <summary>Stores the first <paramref name="count"/> lanes: fast path inline,
    /// masked slow path a call (see Store512).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Store256(ref T p, int count, Vector256<T> value)
    {
        if (count >= Vector256<T>.Count)
        {
            value.StoreUnsafe(ref p);
            return;
        }
        if (count > 0)
        {
            Store256Partial(ref p, count, value);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void Store256Partial(ref T p, int count, Vector256<T> value)
    {
        if (Vector256<T>.Count == 8)
        {
            Avx2.MaskStore((int*)Unsafe.AsPointer(ref p), Tables256_8.MaskTable[count], AsI256(value));
        }
        else
        {
            Avx2.MaskStore((long*)Unsafe.AsPointer(ref p), Tables256_4.MaskTable[count], AsL256(value));
        }
    }

    /// <summary>Same bottom-up block merge as MergeVecs512, on 256-bit vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MergeVecs256(Span<Vector256<T>> vecs)
    {
        int n = vecs.Length;
        ref Vector256<T> v0 = ref MemoryMarshal.GetReference(vecs);
        for (int width = 1; width < n; width <<= 1)
        {
            for (int start = width; start < n; start += width << 1)
            {
                for (int i = 0; i < (width >> 1); i++)
                {
                    Vector256<T> t = Rev256(Unsafe.Add(ref v0, start + i));
                    Unsafe.Add(ref v0, start + i) = Rev256(Unsafe.Add(ref v0, start + width - 1 - i));
                    Unsafe.Add(ref v0, start + width - 1 - i) = t;
                }
                if ((width & 1) != 0)
                {
                    ref Vector256<T> mid = ref Unsafe.Add(ref v0, start + (width >> 1));
                    mid = Rev256(mid);
                }
            }

            for (int d = width; d >= 1; d >>= 1)
            {
                for (int i = 0; i < n; i++)
                {
                    if ((i & d) == 0)
                    {
                        ref Vector256<T> lo = ref Unsafe.Add(ref v0, i);
                        ref Vector256<T> hi = ref Unsafe.Add(ref v0, i + d);
                        Vector256<T> a = lo;
                        Vector256<T> b = hi;
                        lo = Min256(a, b);
                        hi = Max256(a, b);
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                ref Vector256<T> e = ref Unsafe.Add(ref v0, i);
                e = MergeSteps256(e);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> Rev256(Vector256<T> v) =>
        Vector256<T>.Count == 8
            ? Perm256(v, Tables256_8.Reverse)
            : Perm256(v, Tables256_4.Reverse32);

    /// <summary>Raw lane permute for the 256-bit network: Avx2.PermuteVar8x32 (vpermd)
    /// on the integer view �?works for both lane widths; the 64-bit-lane tables are
    /// kept at 32-bit granularity. The generic Vector256.Shuffle carries .NET's
    /// out-of-range semantics and costs a compare + mask-expand + AND per shuffle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> Perm256(Vector256<T> v, Vector256<int> idx32) =>
        AsT256(Avx2.PermuteVar8x32(AsI256(v), idx32));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector256<T> SortVec256(Vector256<T> v)
    {
        if (Vector256<T>.Count == 8)
        {
            v = Cx256(v, Tables256_8.Xor1, AsT256(Tables256_8.S2J1));
            v = Cx256(v, Tables256_8.Xor2, AsT256(Tables256_8.S4J2));
            v = Cx256(v, Tables256_8.Xor1, AsT256(Tables256_8.S4J1));
            v = Cx256(v, Tables256_8.Xor4, AsT256(Tables256_8.S8J4));
            v = Cx256(v, Tables256_8.Xor2, AsT256(Tables256_8.S8J2));
            v = Cx256(v, Tables256_8.Xor1, AsT256(Tables256_8.S8J1));
        }
        else
        {
            v = Cx256(v, Tables256_4.Xor1_32, AsT256_64(Tables256_4.S2J1));
            v = Cx256(v, Tables256_4.Xor2_32, AsT256_64(Tables256_4.S4J2));
            v = Cx256(v, Tables256_4.Xor1_32, AsT256_64(Tables256_4.S4J1));
        }
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> MergeSteps256(Vector256<T> v)
    {
        if (Vector256<T>.Count == 8)
        {
            v = Cx256(v, Tables256_8.Xor4, AsT256(Tables256_8.S8J4));
            v = Cx256(v, Tables256_8.Xor2, AsT256(Tables256_8.S8J2));
            v = Cx256(v, Tables256_8.Xor1, AsT256(Tables256_8.S8J1));
        }
        else
        {
            v = Cx256(v, Tables256_4.Xor2_32, AsT256_64(Tables256_4.S4J2));
            v = Cx256(v, Tables256_4.Xor1_32, AsT256_64(Tables256_4.S4J1));
        }
        return v;
    }

    /// <summary>One compare-exchange step on 256-bit vectors: the shuffle runs on the
    /// integer lane view and the selection uses Avx.BlendVariable (vblendvps/vblendvpd,
    /// sign-bit per lane) instead of the generic ConditionalSelect's boolean-vector
    /// sequence �?both bit-preserving.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> Cx256(Vector256<T> v, Vector256<int> xor32, Vector256<T> keepMax)
    {
        Vector256<T> t = Perm256(v, xor32);
        Vector256<T> lo = Min256(v, t);
        Vector256<T> hi = Max256(v, t);
        return Vector256<T>.Count == 8
            ? FromSingle256(Avx.BlendVariable(AsSingle256(lo), AsSingle256(hi), AsSingle256(keepMax)))
            : FromDouble256(Avx.BlendVariable(AsDouble256(lo), AsDouble256(hi), AsDouble256(keepMax)));
    }

    // ------------------------------------------------- reinterpret helpers (free)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> AsT(Vector512<int> v) => Unsafe.As<Vector512<int>, Vector512<T>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> AsT64(Vector512<long> v) => Unsafe.As<Vector512<long>, Vector512<T>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<int> AsI(Vector512<T> v) => Unsafe.As<Vector512<T>, Vector512<int>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<long> AsL(Vector512<T> v) => Unsafe.As<Vector512<T>, Vector512<long>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<float> AsSingle512(Vector512<T> v) => Unsafe.As<Vector512<T>, Vector512<float>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> FromSingle512(Vector512<float> v) => Unsafe.As<Vector512<float>, Vector512<T>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> AsDouble512(Vector512<T> v) => Unsafe.As<Vector512<T>, Vector512<double>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> FromDouble512(Vector512<double> v) => Unsafe.As<Vector512<double>, Vector512<T>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> AsT256(Vector256<int> v) => Unsafe.As<Vector256<int>, Vector256<T>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> AsT256_64(Vector256<long> v) => Unsafe.As<Vector256<long>, Vector256<T>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> AsI256(Vector256<T> v) => Unsafe.As<Vector256<T>, Vector256<int>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<long> AsL256(Vector256<T> v) => Unsafe.As<Vector256<T>, Vector256<long>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> AsSingle256(Vector256<T> v) => Unsafe.As<Vector256<T>, Vector256<float>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> FromSingle256(Vector256<float> v) => Unsafe.As<Vector256<float>, Vector256<T>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> AsDouble256(Vector256<T> v) => Unsafe.As<Vector256<T>, Vector256<double>>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> FromDouble256(Vector256<double> v) => Unsafe.As<Vector256<double>, Vector256<T>>(ref v);

    // ------------------------------------------- hardware min/max (raw IEEE)

    /// <summary>Raw lane-wise min/max. The generic Vector*.Min/Max carries .NET's
    /// NaN-propagation semantics and the JIT expands the floating-point cases into
    /// vrangeps/vfixupimmps sequences; the Avx/Avx512F intrinsics are the bare
    /// instructions. The network's contract is NaN-free input (the caller's pre-pass),
    /// so raw vminps/vminpd is exact here. Integer lanes keep the generic form, which
    /// already lowers to a single instruction (and has no AVX2 64-bit variant).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> Min512(Vector512<T> a, Vector512<T> b)
    {
        if (typeof(T) == typeof(float))
        {
            return FromSingle512(Avx512F.Min(AsSingle512(a), AsSingle512(b)));
        }
        if (typeof(T) == typeof(double))
        {
            return FromDouble512(Avx512F.Min(AsDouble512(a), AsDouble512(b)));
        }
        return Vector512.Min(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> Max512(Vector512<T> a, Vector512<T> b)
    {
        if (typeof(T) == typeof(float))
        {
            return FromSingle512(Avx512F.Max(AsSingle512(a), AsSingle512(b)));
        }
        if (typeof(T) == typeof(double))
        {
            return FromDouble512(Avx512F.Max(AsDouble512(a), AsDouble512(b)));
        }
        return Vector512.Max(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> Min256(Vector256<T> a, Vector256<T> b)
    {
        if (typeof(T) == typeof(float))
        {
            return FromSingle256(Avx.Min(AsSingle256(a), AsSingle256(b)));
        }
        if (typeof(T) == typeof(double))
        {
            return FromDouble256(Avx.Min(AsDouble256(a), AsDouble256(b)));
        }
        return Vector256.Min(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> Max256(Vector256<T> a, Vector256<T> b)
    {
        if (typeof(T) == typeof(float))
        {
            return FromSingle256(Avx.Max(AsSingle256(a), AsSingle256(b)));
        }
        if (typeof(T) == typeof(double))
        {
            return FromDouble256(Avx.Max(AsDouble256(a), AsDouble256(b)));
        }
        return Vector256.Max(a, b);
    }

    // --------------------------------------------------- per-type padding values

    private static readonly Vector512<T> Pad512 = MakePad512();
    private static readonly Vector256<T> Pad256 = MakePad256();

    private static Vector512<T> MakePad512() => Vector512.Create(MaxValueOf());

    private static Vector256<T> MakePad256() => Vector256.Create(MaxValueOf());

    private static T MaxValueOf()
    {
        if (typeof(T) == typeof(int))
        {
            return (T)(object)int.MaxValue;
        }
        if (typeof(T) == typeof(uint))
        {
            return (T)(object)uint.MaxValue;
        }
        if (typeof(T) == typeof(long))
        {
            return (T)(object)long.MaxValue;
        }
        if (typeof(T) == typeof(ulong))
        {
            return (T)(object)ulong.MaxValue;
        }
        if (typeof(T) == typeof(float))
        {
            return (T)(object)float.PositiveInfinity;
        }
        return (T)(object)double.PositiveInfinity;
    }

    // ------------------------------------------- 32-bit lane tables (16 lanes)

    private static class Tables16
    {
        internal static readonly Vector512<int> Reverse = Make(i => 15 - i);
        internal static readonly Vector512<int> Xor1 = Make(i => i ^ 1);
        internal static readonly Vector512<int> Xor2 = Make(i => i ^ 2);
        internal static readonly Vector512<int> Xor4 = Make(i => i ^ 4);
        internal static readonly Vector512<int> Xor8 = Make(i => i ^ 8);
        internal static readonly Vector512<int> S2J1 = Make(i => ((i & 2) != 0) ^ ((i & 1) != 0) ? -1 : 0);
        internal static readonly Vector512<int> S4J2 = Make(i => ((i & 4) != 0) ^ ((i & 2) != 0) ? -1 : 0);
        internal static readonly Vector512<int> S4J1 = Make(i => ((i & 4) != 0) ^ ((i & 1) != 0) ? -1 : 0);
        internal static readonly Vector512<int> S8J4 = Make(i => ((i & 8) != 0) ^ ((i & 4) != 0) ? -1 : 0);
        internal static readonly Vector512<int> S8J2 = Make(i => ((i & 8) != 0) ^ ((i & 2) != 0) ? -1 : 0);
        internal static readonly Vector512<int> S8J1 = Make(i => ((i & 8) != 0) ^ ((i & 1) != 0) ? -1 : 0);
        internal static readonly Vector512<int> S16J8 = Make(i => (i & 8) != 0 ? -1 : 0);
        internal static readonly Vector512<int> S16J4 = Make(i => (i & 4) != 0 ? -1 : 0);
        internal static readonly Vector512<int> S16J2 = Make(i => (i & 2) != 0 ? -1 : 0);
        internal static readonly Vector512<int> S16J1 = Make(i => (i & 1) != 0 ? -1 : 0);
        internal static readonly Vector512<int>[] MaskTable = BuildMaskTable();

        private static Vector512<int> Make(Func<int, int> f)
        {
            int[] a = new int[16];
            for (int i = 0; i < 16; i++)
            {
                a[i] = f(i);
            }
            ref int r = ref MemoryMarshal.GetArrayDataReference(a);
            return Vector512.LoadUnsafe(ref r);
        }

        private static Vector512<int>[] BuildMaskTable()
        {
            var t = new Vector512<int>[16];
            for (int count = 0; count < 16; count++)
            {
                int c = count;
                t[count] = Make(i => i < c ? -1 : 0);
            }
            return t;
        }
    }

    // ------------------------------------------- 64-bit lane tables (8 lanes)

    private static class Tables8
    {
        internal static readonly Vector512<long> Reverse = Make(i => 7 - i);
        internal static readonly Vector512<long> Xor1 = Make(i => i ^ 1);
        internal static readonly Vector512<long> Xor2 = Make(i => i ^ 2);
        internal static readonly Vector512<long> Xor4 = Make(i => i ^ 4);
        internal static readonly Vector512<long> S2J1 = Make(i => ((i & 2) != 0) ^ ((i & 1) != 0) ? -1 : 0);
        internal static readonly Vector512<long> S4J2 = Make(i => ((i & 4) != 0) ^ ((i & 2) != 0) ? -1 : 0);
        internal static readonly Vector512<long> S4J1 = Make(i => ((i & 4) != 0) ^ ((i & 1) != 0) ? -1 : 0);
        internal static readonly Vector512<long> S8J4 = Make(i => (i & 4) != 0 ? -1 : 0);
        internal static readonly Vector512<long> S8J2 = Make(i => (i & 2) != 0 ? -1 : 0);
        internal static readonly Vector512<long> S8J1 = Make(i => (i & 1) != 0 ? -1 : 0);
        internal static readonly Vector512<long>[] MaskTable = BuildMaskTable();

        private static Vector512<long> Make(Func<int, long> f)
        {
            long[] a = new long[8];
            for (int i = 0; i < 8; i++)
            {
                a[i] = f(i);
            }
            ref long r = ref MemoryMarshal.GetArrayDataReference(a);
            return Vector512.LoadUnsafe(ref r);
        }

        private static Vector512<long>[] BuildMaskTable()
        {
            var t = new Vector512<long>[8];
            for (int count = 0; count < 8; count++)
            {
                int c = count;
                t[count] = Make(i => i < c ? -1L : 0L);
            }
            return t;
        }
    }

    // ------------------------------------------- 32-bit lane tables (8 lanes)

    private static class Tables256_8
    {
        internal static readonly Vector256<int> Reverse = Make(i => 7 - i);
        internal static readonly Vector256<int> Xor1 = Make(i => i ^ 1);
        internal static readonly Vector256<int> Xor2 = Make(i => i ^ 2);
        internal static readonly Vector256<int> Xor4 = Make(i => i ^ 4);
        internal static readonly Vector256<int> S2J1 = Make(i => ((i & 2) != 0) ^ ((i & 1) != 0) ? -1 : 0);
        internal static readonly Vector256<int> S4J2 = Make(i => ((i & 4) != 0) ^ ((i & 2) != 0) ? -1 : 0);
        internal static readonly Vector256<int> S4J1 = Make(i => ((i & 4) != 0) ^ ((i & 1) != 0) ? -1 : 0);
        internal static readonly Vector256<int> S8J4 = Make(i => (i & 4) != 0 ? -1 : 0);
        internal static readonly Vector256<int> S8J2 = Make(i => (i & 2) != 0 ? -1 : 0);
        internal static readonly Vector256<int> S8J1 = Make(i => (i & 1) != 0 ? -1 : 0);
        internal static readonly Vector256<int>[] MaskTable = BuildMaskTable();

        private static Vector256<int> Make(Func<int, int> f)
        {
            int[] a = new int[8];
            for (int i = 0; i < 8; i++)
            {
                a[i] = f(i);
            }
            ref int r = ref MemoryMarshal.GetArrayDataReference(a);
            return Vector256.LoadUnsafe(ref r);
        }

        private static Vector256<int>[] BuildMaskTable()
        {
            var t = new Vector256<int>[8];
            for (int count = 0; count < 8; count++)
            {
                int c = count;
                t[count] = Make(i => i < c ? -1 : 0);
            }
            return t;
        }
    }

    // ------------------------------------------- 64-bit lane tables (4 lanes)

    private static class Tables256_4
    {
        // Permute indices live at 32-bit granularity (Avx2.PermuteVar8x32 = vpermd is
        // the only variable lane permute available on the AVX2 path): each 64-bit lane
        // index maps to the index pair (2*lane, 2*lane+1) of its halves.
        internal static readonly Vector256<int> Reverse32 = Make32(i => (((3 - (i >> 1)) << 1) | (i & 1)));
        internal static readonly Vector256<int> Xor1_32 = Make32(i => ((((i >> 1) ^ 1) << 1) | (i & 1)));
        internal static readonly Vector256<int> Xor2_32 = Make32(i => ((((i >> 1) ^ 2) << 1) | (i & 1)));
        internal static readonly Vector256<long> S2J1 = Make(i => ((i & 2) != 0) ^ ((i & 1) != 0) ? -1 : 0);
        internal static readonly Vector256<long> S4J2 = Make(i => (i & 2) != 0 ? -1 : 0);
        internal static readonly Vector256<long> S4J1 = Make(i => (i & 1) != 0 ? -1 : 0);
        internal static readonly Vector256<long>[] MaskTable = BuildMaskTable();

        private static Vector256<int> Make32(Func<int, int> f)
        {
            int[] a = new int[8];
            for (int i = 0; i < 8; i++)
            {
                a[i] = f(i);
            }
            ref int r = ref MemoryMarshal.GetArrayDataReference(a);
            return Vector256.LoadUnsafe(ref r);
        }

        private static Vector256<long> Make(Func<int, long> f)
        {
            long[] a = new long[4];
            for (int i = 0; i < 4; i++)
            {
                a[i] = f(i);
            }
            ref long r = ref MemoryMarshal.GetArrayDataReference(a);
            return Vector256.LoadUnsafe(ref r);
        }

        private static Vector256<long>[] BuildMaskTable()
        {
            var t = new Vector256<long>[4];
            for (int count = 0; count < 4; count++)
            {
                int c = count;
                t[count] = Make(i => i < c ? -1L : 0L);
            }
            return t;
        }
    }

    // ------------------------------------------------- vector buffers (stack)

    [InlineArray(32)]
    private struct VecBuf512<U>
    {
        private Vector512<U> _e0;
    }

    [InlineArray(64)]
    private struct VecBuf256<U>
    {
        private Vector256<U> _e0;
    }
}









