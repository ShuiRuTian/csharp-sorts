using System;
using System.Linq;
using Sorts;
using Sorts.TestData;
using Xunit;

public class GlideMergeTests
{
    private static int[] SortedRun(Distribution d, int n, int seed) =>
        DataGen.Ints(d, n, seed).OrderBy(x => x).ToArray();

    [Fact]
    public void PhysicalMergeMergesTwoSortedRuns()
    {
        // left/right contiguous in one buffer; scratch >= half the total (contract).
        (int, int)[] shapes = { (0, 8), (8, 0), (1, 1000), (1000, 1), (500, 500), (48, 48) };
        foreach (var (l, r) in shapes)
        {
            int total = l + r;
            var a = new int[total];
            SortedRun(Distribution.Random, l, 7).CopyTo(a, 0);
            SortedRun(Distribution.Random, r, 11).CopyTo(a, l);
            var expected = a.OrderBy(x => x).ToArray();
            var scratch = new int[(total + 1) / 2];
            GlideMerge.PhysicalMerge<int, ComparableCmp<int>>(
                a.AsSpan(0, l), a.AsSpan(l, r), scratch, new());
            Assert.Equal(expected, a);
        }
    }

    [Fact]
    public void PhysicalTripleMergeMergesThreeSortedRuns()
    {
        (int, int, int)[] shapes = { (10, 10, 10), (1, 500, 2), (100, 0, 100), (33, 33, 34), (500, 500, 500) };
        foreach (var (x, y, z) in shapes)
        {
            int total = x + y + z;
            var v = new int[total];
            SortedRun(Distribution.Random, x, 3).CopyTo(v, 0);
            SortedRun(Distribution.RandomD20, y, 4).CopyTo(v, x);
            SortedRun(Distribution.Random, z, 5).CopyTo(v, x + y);
            var expected = v.OrderBy(x2 => x2).ToArray();
            var scratch = new int[(total + 1) / 2];
            GlideMerge.PhysicalTripleMerge<int, ComparableCmp<int>>(
                v.AsSpan(0, x), v.AsSpan(x, y), v.AsSpan(x + y, z), scratch, new());
            Assert.Equal(expected, v);
        }
    }

    [Fact]
    public void PhysicalQuadMergeMergesFourSortedRuns()
    {
        const int q = 250;
        foreach (Distribution d in new[] { Distribution.Random, Distribution.AllEqual })
        {
            var v = new int[4 * q];
            for (int i = 0; i < 4; i++)
                SortedRun(d, q, 20 + i).CopyTo(v, i * q);
            var expected = v.OrderBy(x => x).ToArray();
            var scratch = new int[(v.Length + 1) / 2];
            GlideMerge.PhysicalQuadMerge<int, ComparableCmp<int>>(
                v.AsSpan(0, q), v.AsSpan(q, q), v.AsSpan(2 * q, q), v.AsSpan(3 * q, q), scratch, new());
            Assert.Equal(expected, v);
        }

        // Interleaved-range: each quarter draws from an overlapping window so the
        // merge must genuinely interleave all four runs.
        const int k = 100;
        var rng = new Random(5);
        var w = new int[4 * k];
        for (int i = 0; i < 4; i++)
        {
            var run = new int[k];
            for (int j = 0; j < k; j++) run[j] = i * 100 + rng.Next(400);
            Array.Sort(run);
            run.CopyTo(w, i * k);
        }
        var expectedInterleaved = w.OrderBy(x => x).ToArray();
        var interleavedScratch = new int[(w.Length + 1) / 2];
        GlideMerge.PhysicalQuadMerge<int, ComparableCmp<int>>(
            w.AsSpan(0, k), w.AsSpan(k, k), w.AsSpan(2 * k, k), w.AsSpan(3 * k, k), interleavedScratch, new());
        Assert.Equal(expectedInterleaved, w);
    }

    [Fact]
    public void BidirectionalMergeMergesIntoDestination()
    {
        var left = SortedRun(Distribution.Random, 300, 21);
        var right = SortedRun(Distribution.RandomD20, 200, 22);
        var dst = new int[500];
        GlideMerge.BidirectionalMerge<int, ComparableCmp<int>>(left, right, dst, new());
        Assert.Equal(left.Concat(right).OrderBy(x => x), dst);

        // Upstream aborts unless dst.len() == left.len() + right.len() (the
        // MutSlice concat contract); the port rejects violations with ArgumentException.
        Assert.Throws<ArgumentException>(() =>
            GlideMerge.BidirectionalMerge<int, ComparableCmp<int>>(left, right, dst.AsSpan(0, 499), new()));
    }

    [Fact]
    public void EagerSortSortsRandomDataAllSizes()
    {
        foreach (int n in new[] { 0, 1, 2, 47, 48, 49, 100, 4096, 20000 })
        {
            foreach (Distribution d in new[] { Distribution.Random, Distribution.RandomD20 })
            {
                var a = DataGen.Ints(d, n, 3);
                var expected = a.OrderBy(x => x).ToArray();
                var scratch = new int[(n + 1) / 2];
                EagerSort.Sort<int, ComparableCmp<int>>(a, scratch, new());
                Assert.Equal(expected, a);
            }
        }
    }

    [Fact]
    public void PhysicalMergeRejectsNonContiguousRuns()
    {
        // Upstream aborts when concat sees non-adjacent slices; the seam detects the
        // same violation (left must end exactly where right begins) and throws instead
        // of letting Region() run past the end of the left allocation.
        var left = SortedRun(Distribution.Random, 50, 31);
        var right = SortedRun(Distribution.Random, 50, 32); // separate allocation
        var scratch = new int[50];
        Assert.Throws<InvalidOperationException>(() =>
            GlideMerge.PhysicalMerge<int, ComparableCmp<int>>(left, right, scratch, new()));
    }

    [Fact]
    public void EagerSortIsStableOnEqualKeys()
    {
        // EagerSort must preserve input order of equal-key elements (payload).
        var a = DataGen.Pairs(Distribution.RandomD20, 3000, 9);
        EagerSort.Sort<Pair, ComparableCmp<Pair>>(a, new Pair[(3000 + 1) / 2], new());
        for (int i = 1; i < a.Length; i++)
            Assert.False(a[i].Key == a[i - 1].Key && a[i].Payload < a[i - 1].Payload,
                $"unstable pair at {i}: ({a[i].Key},{a[i].Payload}) after ({a[i - 1].Key},{a[i - 1].Payload})");
    }
}
