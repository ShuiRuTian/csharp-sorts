using System;
using System.Collections.Generic;
using System.Linq;
using Sorts;
using Sorts.TestData;
using Xunit;

public class IpnPartitionTests
{
    // partition() contract (quicksort.rs:102-112): on return v[0..num_lt) all compare
    // less than the pivot value, v[num_lt] holds the pivot, v[num_lt+1..] all compare
    // >= pivot, and v is a permutation of the input.
    private static void AssertPartitioned<T>(T[] a, int numLt, T pivotValue, T[] original, Comparison<T> canonical)
    {
        for (int i = 0; i < numLt; i++)
            Assert.True(canonical(a[i], pivotValue) < 0, $"v[{i}] must be < pivot");
        Assert.Equal(0, canonical(a[numLt], pivotValue));
        for (int i = numLt + 1; i < a.Length; i++)
            Assert.True(canonical(a[i], pivotValue) >= 0, $"v[{i}] must be >= pivot");
        Assert.Equal(SortedCopy(original, canonical), SortedCopy(a, canonical));
    }

    private static T[] SortedCopy<T>(T[] a, Comparison<T> cmp) =>
        a.OrderBy(x => x, Comparer<T>.Create(cmp)).ToArray();

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.RandomD20)]
    [InlineData(Distribution.FewUnique)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.AllEqual)]
    public void LomutoPartitionCorrectOnSmallInputs(Distribution d)
    {
        var rng = new Random(1234);
        for (int n = 1; n <= 64; n++)
        {
            var a = DataGen.Ints(d, n, 900 + n);
            var original = (int[])a.Clone();
            int pivotPos = rng.Next(n);
            int pivotValue = original[pivotPos];
            int numLt = IpnPartition.Partition<int, ComparableCmp<int>>(a, pivotPos, new());
            Assert.InRange(numLt, 0, n - 1);
            AssertPartitioned(a, numLt, pivotValue, original, (x, y) => x.CompareTo(y));
        }
    }

    [Fact]
    public void LomutoPartitionEdges()
    {
        // n == 1: the pivot is the only element.
        var one = new[] { 42 };
        Assert.Equal(0, IpnPartition.Partition<int, ComparableCmp<int>>(one, 0, new()));
        Assert.Equal(42, one[0]);

        // Sorted input, pivot at k -> exactly k elements below the pivot value k.
        for (int n = 8; n <= 64; n += 7)
        {
            for (int k = 0; k < n; k++)
            {
                var a = DataGen.Ints(Distribution.Ascending, n, 1);
                var original = (int[])a.Clone();
                int numLt = IpnPartition.Partition<int, ComparableCmp<int>>(a, k, new());
                Assert.Equal(k, numLt);
                AssertPartitioned(a, numLt, original[k], original, (x, y) => x.CompareTo(y));
            }
        }

        // Reverse-sorted, pivot at k has value n-1-k -> exactly n-1-k below.
        for (int n = 8; n <= 64; n += 7)
        {
            int k = n / 3;
            var a = DataGen.Ints(Distribution.Descending, n, 2);
            var original = (int[])a.Clone();
            int numLt = IpnPartition.Partition<int, ComparableCmp<int>>(a, k, new());
            Assert.Equal(n - 1 - k, numLt);
            AssertPartitioned(a, numLt, original[k], original, (x, y) => x.CompareTo(y));
        }

        // Pivot at the very front / very end.
        var front = DataGen.Ints(Distribution.Random, 32, 3);
        var frontOrig = (int[])front.Clone();
        int nl1 = IpnPartition.Partition<int, ComparableCmp<int>>(front, 0, new());
        AssertPartitioned(front, nl1, frontOrig[0], frontOrig, (x, y) => x.CompareTo(y));
        var end = DataGen.Ints(Distribution.Random, 32, 4);
        var endOrig = (int[])end.Clone();
        int nl2 = IpnPartition.Partition<int, ComparableCmp<int>>(end, end.Length - 1, new());
        AssertPartitioned(end, nl2, endOrig[^1], endOrig, (x, y) => x.CompareTo(y));
    }

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.FewUnique)]
    public void LomutoPartitionHandlesReferenceTypes(Distribution d)
    {
        // sizeof(string) <= 96 -> branchless Lomuto; the cyclic permutation moves
        // references, never pointee clones.
        var rng = new Random(4321);
        for (int n = 2; n <= 64; n += 3)
        {
            var a = DataGen.Strings(d, n, 600 + n);
            var original = (string[])a.Clone();
            int pivotPos = rng.Next(n);
            int numLt = IpnPartition.Partition<string, ComparableCmp<string>>(a, pivotPos, new());
            Assert.InRange(numLt, 0, n - 1);
            AssertPartitioned(a, numLt, original[pivotPos], original, (x, y) => x.CompareTo(y));
        }
    }

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.RandomD20)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.AllEqual)]
    public void HoarePartitionCorrectForLargeTypes(Distribution d)
    {
        // Unsafe.SizeOf<HugeStruct>() == 132 > 96 (quicksort.rs:151) -> Hoare.
        var rng = new Random(5678);
        for (int n = 2; n <= 64; n++)
        {
            var keys = DataGen.Ints(d, n, 700 + n);
            var a = new HugeStruct[n];
            for (int i = 0; i < n; i++) a[i] = new HugeStruct(keys[i]);
            var original = (HugeStruct[])a.Clone();
            int pivotPos = rng.Next(n);
            int numLt = IpnPartition.Partition<HugeStruct, ComparableCmp<HugeStruct>>(a, pivotPos, new());
            Assert.InRange(numLt, 0, n - 1);
            AssertPartitioned(a, numLt, original[pivotPos], original, (x, y) => x.A.CompareTo(y.A));
        }
    }

    [Fact]
    public void HoarePartitionHandlesManagedFields()
    {
        // A > 96 byte struct with a managed field still dispatches to Hoare; the
        // GapGuard value is a reference copy with the same aliasing semantics.
        var rng = new Random(99);
        for (int n = 2; n <= 33; n++)
        {
            var keys = DataGen.Ints(Distribution.Random, n, 800 + n);
            var a = new HugeRefStruct[n];
            for (int i = 0; i < n; i++) a[i] = new HugeRefStruct(keys[i], keys[i].ToString());
            var original = (HugeRefStruct[])a.Clone();
            int pivotPos = rng.Next(n);
            int numLt = IpnPartition.Partition<HugeRefStruct, ComparableCmp<HugeRefStruct>>(a, pivotPos, new());
            Assert.InRange(numLt, 0, n - 1);
            AssertPartitioned(a, numLt, original[pivotPos], original, (x, y) =>
            {
                int c = x.A.CompareTo(y.A);
                return c != 0 ? c : string.CompareOrdinal(x.Tag, y.Tag);
            });
        }
    }

    // ---- Heapsort (heapsort.rs:9-31) ----

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.RandomD20)]
    [InlineData(Distribution.FewUnique)]
    [InlineData(Distribution.Ascending)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.AllEqual)]
    [InlineData(Distribution.Sawtooth)]
    [InlineData(Distribution.OrganPipe)]
    public void HeapsortSortsCorrectly(Distribution d)
    {
        for (int n = 0; n <= 100; n++)
        {
            var a = DataGen.Ints(d, Math.Max(n, 1), 100 + n)[..n];
            var expected = a.OrderBy(x => x).ToArray();
            IpnHeapsort.Heapsort<int, ComparableCmp<int>>(a, new());
            Assert.Equal(expected, a);
        }
        foreach (var n in new[] { 1000, 10_000 })
        {
            var a = DataGen.Ints(d, n, n);
            var expected = a.OrderBy(x => x).ToArray();
            IpnHeapsort.Heapsort<int, ComparableCmp<int>>(a, new());
            Assert.Equal(expected, a);
        }
    }

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.FewUnique)]
    public void HeapsortSortsPairsAndStrings(Distribution d)
    {
        Comparison<Pair> pairCmp = (a, b) =>
        {
            int c = a.Key.CompareTo(b.Key);
            return c != 0 ? c : a.Payload.CompareTo(b.Payload);
        };
        for (int n = 0; n <= 100; n++)
        {
            var p = DataGen.Pairs(d, Math.Max(n, 1), 200 + n)[..n];
            var untouched = p.ToArray();
            IpnHeapsort.Heapsort<Pair, ComparableCmp<Pair>>(p, new());
            // Heapsort is unstable and Pair compares by Key only: non-decreasing under
            // the sort's own comparer, plus canonical (Key, Payload) multiset equality.
            for (int i = 1; i < p.Length; i++)
                Assert.True(p[i].CompareTo(p[i - 1]) >= 0, $"descending Pair at {i}");
            Assert.Equal(SortedCopy(untouched, pairCmp), SortedCopy(p, pairCmp));

            var s = DataGen.Strings(d, Math.Max(n, 1), 300 + n)[..n];
            var untouchedS = s.ToArray();
            IpnHeapsort.Heapsort<string, ComparableCmp<string>>(s, new());
            for (int i = 1; i < s.Length; i++)
                Assert.True(s[i].CompareTo(s[i - 1]) >= 0, $"descending string at {i}");
            Assert.Equal(untouchedS.OrderBy(x => x).ToArray(), s.OrderBy(x => x).ToArray());
        }
    }

    [Fact]
    public void HeapsortAndPartitionAreAllocationFree()
    {
        // Warm up the JIT outside the measured region.
        IpnHeapsort.Heapsort<int, ComparableCmp<int>>(
            DataGen.Ints(Distribution.Random, 10_000, 7), new());

        var a = DataGen.Ints(Distribution.Random, 10_000, 8);
        var b = DataGen.Pairs(Distribution.Random, 512, 9);
        var keys = DataGen.Ints(Distribution.Random, 300, 10);
        var c = new HugeStruct[300];
        for (int i = 0; i < c.Length; i++) c[i] = new HugeStruct(keys[i]);
        var p = DataGen.Ints(Distribution.Random, 512, 11);

        long before = GC.GetAllocatedBytesForCurrentThread();
        IpnHeapsort.Heapsort<int, ComparableCmp<int>>(a, new());
        IpnHeapsort.Heapsort<Pair, ComparableCmp<Pair>>(b, new());
        IpnHeapsort.Heapsort<HugeStruct, ComparableCmp<HugeStruct>>(c, new());
        int numLt1 = IpnPartition.Partition<int, ComparableCmp<int>>(p, p.Length / 2, new());
        int numLt2 = IpnPartition.Partition<HugeStruct, ComparableCmp<HugeStruct>>(c, c.Length / 2, new());
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.InRange(numLt1, 0, 511);
        Assert.InRange(numLt2, 0, 299);
        Assert.Equal(0, after - before);
    }

    // 4 + 16*8 = 132 bytes > MAX_BRANCHLESS_PARTITION_SIZE (96) -> Hoare path.
    private readonly record struct HugeStruct(
        int A, long B = 0, long C = 0, long D = 0, long E = 0, long F = 0, long G = 0, long H = 0,
        long I = 0, long J = 0, long K = 0, long L = 0, long M = 0, long N = 0, long O = 0, long P = 0)
        : IComparable<HugeStruct>
    {
        public int CompareTo(HugeStruct other) => A.CompareTo(other.A);
    }

    // 4 + pad + 8 (string) + 12*8 = 112 bytes with a managed field -> Hoare path.
    private readonly record struct HugeRefStruct(
        int A, string Tag, long B = 0, long C = 0, long D = 0, long E = 0, long F = 0,
        long G = 0, long H = 0, long I = 0, long J = 0, long K = 0, long L = 0, long M = 0)
        : IComparable<HugeRefStruct>
    {
        public int CompareTo(HugeRefStruct other) => A.CompareTo(other.A);
    }
}
