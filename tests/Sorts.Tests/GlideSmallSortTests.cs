using System;
using System.Linq;
using Sorts;
using Sorts.TestData;
using Xunit;

public class GlideSmallSortTests
{
    [Fact]
    public void SortsAllSizesUpTo48()
    {
        for (int n = 0; n <= 48; n++)
        {
            var a = DataGen.Ints(Distribution.Random, Math.Max(n, 1), n + 3)[..n];
            var expected = a.OrderBy(x => x).ToArray();
            GlideSmallSort.Sort<int, ComparableCmp<int>>(a, new());
            Assert.Equal(expected, a);
        }
    }

    [Theory]
    [InlineData(Distribution.RandomD20)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.AllEqual)]
    public void HandlesAdversarialPatterns(Distribution d)
    {
        const int n = 48;
        var a = DataGen.Ints(d, n, 9);
        var expected = a.OrderBy(x => x).ToArray();
        GlideSmallSort.Sort<int, ComparableCmp<int>>(a, new());
        Assert.Equal(expected, a);
    }

    [Fact]
    public void IsStableOnEqualKeys()
    {
        // Pair keys collide heavily under RandomD20; a stable sort must keep
        // equal-key elements in input (payload) order.
        var a = DataGen.Pairs(Distribution.RandomD20, 48, 9);
        GlideSmallSort.Sort<Pair, ComparableCmp<Pair>>(a, new());
        for (int i = 1; i < a.Length; i++)
            Assert.False(a[i].Key == a[i - 1].Key && a[i].Payload < a[i - 1].Payload,
                $"unstable pair at {i}: ({a[i].Key},{a[i].Payload}) after ({a[i - 1].Key},{a[i - 1].Payload})");
    }

    [Fact]
    public void Sort32IntoNetworkSortsPow2Chunks()
    {
        // Direct network coverage: every pow2 chunk size through the public
        // SortNInto entry points (src -> dst via scratch).
        foreach (int n in new[] { 4, 8, 16, 32 })
        {
            var src = DataGen.Ints(Distribution.Random, n, n + 100).ToArray();
            var expected = src.OrderBy(x => x).ToArray();
            var dst = new int[n];
            var scratch = new int[64];
            switch (n)
            {
                case 4: GlideSmallSort.Sort4Into<int, ComparableCmp<int>>(src, dst, scratch, new()); break;
                case 8: GlideSmallSort.Sort8Into<int, ComparableCmp<int>>(src, dst, scratch, new()); break;
                case 16: GlideSmallSort.Sort16Into<int, ComparableCmp<int>>(src, dst, scratch, new()); break;
                case 32: GlideSmallSort.Sort32Into<int, ComparableCmp<int>>(src, dst, scratch, new()); break;
            }
            Assert.Equal(expected, dst);
            Assert.Equal(DataGen.Ints(Distribution.Random, n, n + 100).OrderBy(x => x), expected);
        }
    }

    [Fact]
    public void BlockInsertionSortMatchesSort()
    {
        var a = DataGen.Ints(Distribution.RandomD20, 48, 11);
        var expected = a.OrderBy(x => x).ToArray();
        GlideSmallSort.BlockInsertionSort<int, ComparableCmp<int>>(a, new());
        Assert.Equal(expected, a);
    }

    [Fact]
    public void SortsReferenceTypeElements()
    {
        // Reference-containing T takes the pooled-scratch path instead of stack bytes.
        var a = DataGen.Strings(Distribution.RandomD20, 48, 7);
        var expected = a.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        GlideSmallSort.Sort<string, ComparableCmp<string>>(a, new());
        Assert.Equal(expected, a);
    }

    [Fact]
    public void SortNIntoRejectsWrongLengths()
    {
        // Upstream enforces src.len() == N and dst.len() == N (assert at
        // small_sort.rs:122; MutSlice typestate elsewhere) — the port rejects
        // violations with ArgumentException instead of reading/writing out of contract.
        var src = DataGen.Ints(Distribution.Random, 32, 5);
        var dst = new int[32];
        var scratch = new int[64];

        // dst too long (scattered-write hazard) and src too short (overread hazard).
        Assert.Throws<ArgumentException>(() =>
            GlideSmallSort.Sort8Into<int, ComparableCmp<int>>(src.AsSpan(0, 8), dst.AsSpan(0, 16), scratch, new()));
        Assert.Throws<ArgumentException>(() =>
            GlideSmallSort.Sort8Into<int, ComparableCmp<int>>(src.AsSpan(0, 4), dst.AsSpan(0, 8), scratch, new()));
        Assert.Throws<ArgumentException>(() =>
            GlideSmallSort.Sort16Into<int, ComparableCmp<int>>(src.AsSpan(0, 16), dst.AsSpan(0, 15), scratch, new()));
        Assert.Throws<ArgumentException>(() =>
            GlideSmallSort.Sort32Into<int, ComparableCmp<int>>(src.AsSpan(0, 31), dst.AsSpan(0, 32), scratch, new()));
        Assert.Throws<ArgumentException>(() =>
            GlideSmallSort.Sort4Into<int, ComparableCmp<int>>(src.AsSpan(0, 4), dst.AsSpan(0, 5), scratch, new()));

        // Exact lengths still sort correctly through the guarded entries.
        var expected = src.OrderBy(x => x).ToArray();
        GlideSmallSort.Sort32Into<int, ComparableCmp<int>>(src.AsSpan(0, 32), dst.AsSpan(0, 32), scratch, new());
        Assert.Equal(expected, dst);
    }
}
