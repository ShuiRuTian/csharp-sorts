using System;
using System.Linq;
using Sorts;
using Sorts.TestData;
using Xunit;

public class IpnSmallSortTests
{
    [Fact]
    public void DispatchClassesMatchUpstreamPredicates()
    {
        // Network: IS_COPY (unmanaged value type) && has_efficient_in_place_swap
        // (smallsort.rs:759-763 — size_of::<T>() <= size_of::<u64>(), i.e. <= 8 bytes,
        // NOT 32) && size*32 <= MAX_STACK_ARRAY_SIZE (4096).
        Assert.Equal(IpnSmallSortKind.Network, IpnSmallSortConfig<int>.Kind);
        Assert.Equal(IpnSmallSortKind.Network, IpnSmallSortConfig<double>.Kind);
        Assert.Equal(IpnSmallSortKind.Network, IpnSmallSortConfig<Pair>.Kind); // 8 bytes
        // General: Freeze-like but not int-like. string is 24 bytes in Rust (not Copy);
        // a 20-byte unmanaged struct fails the <= 8 swap bound; decimal is 16 bytes.
        Assert.Equal(IpnSmallSortKind.General, IpnSmallSortConfig<string>.Kind);
        Assert.Equal(IpnSmallSortKind.General, IpnSmallSortConfig<BigStruct>.Kind);
        Assert.Equal(IpnSmallSortKind.General, IpnSmallSortConfig<decimal>.Kind);
        // Fallback: 128*48 = 6144 > 4096 (too big for the stack array), or value types
        // containing managed references (not Freeze-like under the port's binding).
        Assert.Equal(IpnSmallSortKind.Fallback, IpnSmallSortConfig<BigStruct128>.Kind);
        Assert.Equal(IpnSmallSortKind.Fallback, IpnSmallSortConfig<RefStruct>.Kind);
    }

    [Fact]
    public void ThresholdsMatchDispatchClass()
    {
        // smallsort.rs:37-39: Fallback 16 (SMALL_SORT_FALLBACK_THRESHOLD), General and
        // Network both 32. Non-Freeze types take the default impl, threshold 16.
        Assert.Equal(32, IpnSmallSort.Threshold<int>());
        Assert.Equal(32, IpnSmallSort.Threshold<string>());
        Assert.Equal(32, IpnSmallSort.Threshold<decimal>());
        Assert.Equal(16, IpnSmallSort.Threshold<BigStruct128>());
        Assert.Equal(16, IpnSmallSort.Threshold<RefStruct>());
    }

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.RandomD20)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.AllEqual)]
    public void NetworkPathSortsEverySizeUpToThreshold(Distribution d)
    {
        // int -> Network (smallsort.rs:225-290): sweep 0..=32 covers len < 18 (single
        // region, no merge), len >= 18 (two regions + bidirectional merge), and the
        // presort branches: region >= 13 -> sort13_optimal, >= 9 -> sort9_optimal, else 1.
        for (int n = 0; n <= 32; n++)
        {
            var a = DataGen.Ints(d, Math.Max(n, 1), n + 3)[..n];
            var expected = a.OrderBy(x => x).ToArray();
            IpnSmallSort.SmallSort<int, ComparableCmp<int>>(a, new());
            Assert.Equal(expected, a);
        }
    }

    [Fact]
    public void NetworkPathSortsAllSpecializableIntegerTypes()
    {
        // Exercises the Math.Min/Max SwapIfLess specialization for every type it covers,
        // with signed-negative and unsigned-wrap values so a signed/unsigned mix-up in the
        // specialized branch would fail. char has no Math.Min overload and stays on the
        // ternary fallback; it is included as the contrast case.
        Check<sbyte>(i => (sbyte)(i * 3 - 40));
        Check<byte>(i => (byte)((i * 37 + 5) % 256));
        Check<short>(i => (short)(i * 1900 - 30000));
        Check<ushort>(i => (ushort)(i * 17000 + 3));
        Check<int>(i => i * 1234567 - 20000000);
        Check<uint>(i => (uint)(i * 1234567u));
        Check<long>(i => i * 12345678901L - 200000000000L);
        Check<ulong>(i => (ulong)i * 12345678901234567UL);
        Check<char>(i => (char)(i * 700 + 33));

        static void Check<T>(Func<int, T> gen) where T : struct, IComparable<T>
        {
            for (int n = 0; n <= 32; n++)
            {
                var a = new T[n];
                for (int i = 0; i < n; i++) a[i] = gen(i);
                var expected = a.OrderBy(x => x).ToArray();
                IpnSmallSort.SmallSort<T, ComparableCmp<T>>(a, new());
                Assert.Equal(expected, a);
            }
        }
    }

    [Fact]
    public void NetworkPathNoMergeBoundary()
    {
        // no_merge = len < 18 (smallsort.rs:244): 17 sorts as one region, 18 splits into
        // two 9-element regions (sort9_optimal presort) merged via bidirectional_merge.
        foreach (int n in new[] { 16, 17, 18, 19, 20, 26, 27, 32 })
        {
            var a = DataGen.Ints(Distribution.Random, n, n + 11);
            var expected = a.OrderBy(x => x).ToArray();
            IpnSmallSort.SmallSort<int, ComparableCmp<int>>(a, new());
            Assert.Equal(expected, a);
        }
    }

    [Fact]
    public void NetworkPathSortsEightByteStructPreservingPayloadPermutation()
    {
        // Pair is 8 bytes and unmanaged -> Network. Unstable sort: payload order among
        // equal keys is unspecified, but the (key, payload) multiset must be preserved.
        var a = DataGen.Pairs(Distribution.RandomD20, 32, 7);
        IpnSmallSort.SmallSort<Pair, ComparableCmp<Pair>>(a, new());
        for (int i = 1; i < a.Length; i++)
            Assert.True(a[i - 1].Key <= a[i].Key, $"not sorted at {i}");
        Assert.Equal(
            a.Select(p => (p.Key, p.Payload)).OrderBy(p => p.Payload),
            DataGen.Pairs(Distribution.RandomD20, 32, 7).Select(p => (p.Key, p.Payload)).OrderBy(p => p.Payload));
    }

    [Fact]
    public void SmallSortRejectsLengthsBeyondThreshold()
    {
        // Upstream trusts the threshold contract (UnstableSmallSortTypeImpl docs,
        // smallsort.rs:11-12) and aborts on inner violations; the port throws.
        Assert.Throws<ArgumentException>(() =>
            IpnSmallSort.SmallSort<int, ComparableCmp<int>>(new int[33], new()));
        Assert.Throws<ArgumentException>(() =>
            IpnSmallSort.SmallSort<string, ComparableCmp<string>>(new string[33], new()));
        Assert.Throws<ArgumentException>(() =>
            IpnSmallSort.SmallSort<BigStruct128, ComparableCmp<BigStruct128>>(new BigStruct128[17], new()));
        // Boundary values are legal: v.Length == Threshold<T>().
        IpnSmallSort.SmallSort<int, ComparableCmp<int>>(new int[32], new());
        IpnSmallSort.SmallSort<BigStruct128, ComparableCmp<BigStruct128>>(new BigStruct128[16], new());
    }

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.RandomD20)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.AllEqual)]
    public void GeneralPathSortsReferenceTypeEverySizeUpToThreshold(Distribution d)
    {
        // string -> General (small_sort_general, smallsort.rs:113-207): sweep 0..=32
        // covers the presort branches: < 8 (presorted 1), >= 8 (sort4_stable),
        // >= 16 (sort8_stable — string is pointer-sized, <= 16 bytes).
        for (int n = 0; n <= 32; n++)
        {
            var a = DataGen.Strings(d, Math.Max(n, 1), n + 7)[..n];
            var expected = a.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            IpnSmallSort.SmallSort<string, ComparableCmp<string>>(a, new());
            Assert.Equal(expected, a);
        }
    }

    [Fact]
    public void GeneralPathSortsBigUnmanagedStructs()
    {
        // BigStruct is 20 bytes: fails has_efficient_in_place_swap (20 > 8) but
        // 20 * 48 = 960 <= 4096, so General. size_of > 16 forces the sort4_stable
        // presort even for len >= 16 (smallsort.rs:147-158).
        var rng = new Random(41);
        for (int n = 0; n <= 32; n++)
        {
            var a = new BigStruct[n];
            for (int i = 0; i < n; i++)
                a[i] = new BigStruct(rng.Next(30), i, 0, 0, 0);
            var expected = a.OrderBy(x => x.A).ToArray();
            IpnSmallSort.SmallSort<BigStruct, ComparableCmp<BigStruct>>(a, new());
            Assert.Equal(expected, a);
        }
    }

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.RandomD20)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.AllEqual)]
    public void FallbackPathSortsEverySizeUpToThreshold(Distribution d)
    {
        // BigStruct128: 128 * 48 = 6144 > 4096 -> Fallback (insertion sort), threshold 16.
        for (int n = 0; n <= 16; n++)
        {
            int[] keys = DataGen.Ints(d, Math.Max(n, 1), n * 31 + 5)[..n];
            var a = new BigStruct128[n];
            for (int i = 0; i < n; i++)
                a[i] = new BigStruct128 { A = keys[i] };
            var expected = a.OrderBy(x => x.A).ToArray();
            IpnSmallSort.SmallSort<BigStruct128, ComparableCmp<BigStruct128>>(a, new());
            Assert.Equal(expected, a);
        }
    }

    [Fact]
    public void FallbackPathSortsRefContainingStructs()
    {
        // Value types containing managed references are not Freeze-like under the
        // port's binding -> default impl -> Fallback.
        var rng = new Random(4);
        var a = new RefStruct[16];
        for (int i = 0; i < a.Length; i++)
            a[i] = new RefStruct(rng.Next(20), null);
        var expected = a.Select(x => x.Key).OrderBy(x => x).ToArray();
        IpnSmallSort.SmallSort<RefStruct, ComparableCmp<RefStruct>>(a, new());
        Assert.Equal(expected, a.Select(x => x.Key).ToArray());
    }

    // 20 bytes, unmanaged: General (not Network — exceeds the 8-byte swap bound).
    private readonly record struct BigStruct(int A, int B, int C, int D, int E) : IComparable<BigStruct>
    {
        public int CompareTo(BigStruct other) => A.CompareTo(other.A);
    }

    // 128 bytes, unmanaged: 128 * 48 > 4096 -> Fallback.
    private readonly record struct BigStruct128(int A, long B, long C, long D, long E, long F, long G, long H, long I, long J, long K, long L, long M, long N, long O, long P) : IComparable<BigStruct128>
    {
        public int CompareTo(BigStruct128 other) => A.CompareTo(other.A);
    }

    // Value type containing a managed reference: not Freeze-like -> Fallback.
    private readonly record struct RefStruct(int Key, string? Tag) : IComparable<RefStruct>
    {
        public int CompareTo(RefStruct other) => Key.CompareTo(other.Key);
    }
}
