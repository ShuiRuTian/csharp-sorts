using System;
using System.Linq;
using Sorts;
using Sorts.TestData;
using Xunit;

public class DriftSmallSortTests
{
    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.AllEqual)]
    public void SortSmallSortsEverySizeUpTo32(Distribution d)
    {
        // Sweep 0..=32 covering both thresholds (32 Freeze-like, 16 otherwise) and the
        // presort branches: <8 (presorted 1), >=8 (sort4), >=16 (sort8).
        var scratch = new int[DriftSmallSort.MinSmallSortScratchLen];
        for (int n = 0; n <= 32; n++)
        {
            var a = DataGen.Ints(d, Math.Max(n, 1), n + 3)[..n];
            var expected = a.OrderBy(x => x).ToArray();
            DriftSmallSort.SortSmall<int, ComparableCmp<int>>(a, scratch, new());
            Assert.Equal(expected, a);
        }
    }

    [Fact]
    public void ThresholdIs32ForFreezeLikeAnd16Otherwise()
    {
        // Freeze-like (smallsort.rs:50-54): value type, <= 16 bytes, no managed refs.
        Assert.Equal(32, DriftSmallSort.Threshold<int>());
        Assert.Equal(32, DriftSmallSort.Threshold<Pair>());
        // Default impl (smallsort.rs:27-28): reference types, ref-containing or
        // oversized structs.
        Assert.Equal(16, DriftSmallSort.Threshold<string>());
        Assert.Equal(16, DriftSmallSort.Threshold<BigStruct>());
        Assert.Equal(16, DriftSmallSort.Threshold<RefStruct>());
    }

    [Fact]
    public void SortSmallIsStableOnEqualKeys()
    {
        var a = DataGen.Pairs(Distribution.RandomD20, 32, 9);
        var scratch = new Pair[DriftSmallSort.MinSmallSortScratchLen];
        DriftSmallSort.SortSmall<Pair, ComparableCmp<Pair>>(a, scratch, new());
        for (int i = 1; i < a.Length; i++)
            Assert.False(a[i].Key == a[i - 1].Key && a[i].Payload < a[i - 1].Payload,
                $"unstable pair at {i}: ({a[i].Key},{a[i].Payload}) after ({a[i - 1].Key},{a[i - 1].Payload})");
    }

    [Fact]
    public void SortSmallHandlesReferenceTypeElements()
    {
        // Reference-type T takes the insertion-sort path (upstream default impl,
        // threshold 16).
        var a = DataGen.Strings(Distribution.RandomD20, 16, 7);
        var expected = a.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var scratch = new string[DriftSmallSort.MinSmallSortScratchLen];
        DriftSmallSort.SortSmall<string, ComparableCmp<string>>(a, scratch, new());
        Assert.Equal(expected, a);
    }

    [Fact]
    public void SortSmallHandlesRefContainingStructs()
    {
        var rng = new Random(4);
        var a = new RefStruct[16];
        for (int i = 0; i < a.Length; i++)
            a[i] = new RefStruct(rng.Next(20), null);
        var expected = a.Select(x => x.Key).OrderBy(x => x).ToArray();
        var scratch = new RefStruct[DriftSmallSort.MinSmallSortScratchLen];
        DriftSmallSort.SortSmall<RefStruct, ComparableCmp<RefStruct>>(a, scratch, new());
        Assert.Equal(expected, a.Select(x => x.Key).ToArray());
    }

    [Fact]
    public void InsertionSortShiftLeftSortsFromStartByDefault()
    {
        var a = DataGen.Ints(Distribution.Random, 20, 6);
        var expected = a.OrderBy(x => x).ToArray();
        DriftSmallSort.InsertionSortShiftLeft<int, ComparableCmp<int>>(a, new());
        Assert.Equal(expected, a);
    }

    [Fact]
    public void InsertionSortShiftLeftExtendsSortedPrefix()
    {
        // v[..start] is already sorted; the sort extends it over the rest.
        var a = DataGen.Ints(Distribution.Random, 33, 5);
        var sortedHead = a.Take(10).OrderBy(x => x).ToArray();
        sortedHead.CopyTo(a, 0);
        var expected = a.OrderBy(x => x).ToArray();
        DriftSmallSort.InsertionSortShiftLeft<int, ComparableCmp<int>>(a, new(), 10);
        Assert.Equal(expected, a);
    }

    [Fact]
    public void SortSmallRejectsViolatedContracts()
    {
        // Upstream aborts (intrinsics::abort) on these — the port throws at the seam.
        var scratch = new int[DriftSmallSort.MinSmallSortScratchLen];
        Assert.Throws<ArgumentException>(() =>
            DriftSmallSort.SortSmall<int, ComparableCmp<int>>(new int[33], scratch, new()));

        var shortScratch = new int[DriftSmallSort.MinSmallSortScratchLen - 1];
        Assert.Throws<ArgumentException>(() =>
            DriftSmallSort.SortSmall<int, ComparableCmp<int>>(new int[32], shortScratch, new()));

        // Boundary values are legal: v.Length == Threshold, start == len (no-op).
        var ok = new int[32];
        DriftSmallSort.SortSmall<int, ComparableCmp<int>>(ok, scratch, new());
        DriftSmallSort.InsertionSortShiftLeft<int, ComparableCmp<int>>(ok, new(), 32);
    }

    [Fact]
    public void InsertionSortShiftLeftRejectsBadStart()
    {
        var a = new int[8];
        Assert.Throws<ArgumentException>(() =>
            DriftSmallSort.InsertionSortShiftLeft<int, ComparableCmp<int>>(a, new(), 0));
        Assert.Throws<ArgumentException>(() =>
            DriftSmallSort.InsertionSortShiftLeft<int, ComparableCmp<int>>(a, new(), 9));
    }

    // > 16 bytes with no managed references: not Freeze-like under the port's config.
    private readonly record struct BigStruct(int A, int B, int C, int D, int E) : IComparable<BigStruct>
    {
        public int CompareTo(BigStruct other) => A.CompareTo(other.A);
    }

    // Value type containing a managed reference: insertion-sort path.
    private readonly record struct RefStruct(int Key, string? Tag) : IComparable<RefStruct>
    {
        public int CompareTo(RefStruct other) => Key.CompareTo(other.Key);
    }
}
