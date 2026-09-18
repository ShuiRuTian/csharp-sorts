using System;
using System.Linq;
using Sorts;
using Sorts.TestData;
using Xunit;

public class DriftMergeTests
{
    private static int[] SortedRun(Distribution d, int n, int seed) =>
        DataGen.Ints(d, n, seed).OrderBy(x => x).ToArray();

    [Theory]
    [InlineData(500, 500)]
    [InlineData(1, 999)]
    [InlineData(999, 1)]
    public void MergeMergesSortedHalves(int l, int r)
    {
        // scratch >= v.Length - v.Length / 2 — the upstream contract.
        int total = l + r;
        var v = new int[total];
        SortedRun(Distribution.Random, l, 7).CopyTo(v, 0);
        SortedRun(Distribution.RandomD20, r, 11).CopyTo(v, l);
        var expected = v.OrderBy(x => x).ToArray();
        var scratch = new int[total - total / 2];
        DriftMerge.Merge<int, ComparableCmp<int>>(v, scratch, l, new());
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData(1, 999)]
    [InlineData(999, 1)]
    [InlineData(500, 500)]
    [InlineData(2, 3)]
    public void MergeWorksWithExactMinimumScratch(int l, int r)
    {
        // The internal requirement is exactly the shorter run's length.
        int total = l + r;
        var v = new int[total];
        SortedRun(Distribution.Random, l, 13).CopyTo(v, 0);
        SortedRun(Distribution.Random, r, 17).CopyTo(v, l);
        var expected = v.OrderBy(x => x).ToArray();
        var scratch = new int[Math.Min(l, r)];
        DriftMerge.Merge<int, ComparableCmp<int>>(v, scratch, l, new());
        Assert.Equal(expected, v);
    }

    [Fact]
    public void MergeHandlesSmallAndOneSidedShapes()
    {
        (int, int)[] shapes = { (1, 1), (2, 1), (1, 2), (3, 2), (2, 3), (7, 5), (0, 8), (8, 0), (1, 0) };
        foreach (var (l, r) in shapes)
        {
            int total = l + r;
            var v = new int[total];
            SortedRun(Distribution.RandomD20, l, 3).CopyTo(v, 0);
            SortedRun(Distribution.RandomD20, r, 5).CopyTo(v, l);
            var expected = v.OrderBy(x => x).ToArray();
            var scratch = new int[Math.Max(1, total - total / 2)];
            DriftMerge.Merge<int, ComparableCmp<int>>(v, scratch, l, new());
            Assert.Equal(expected, v);
        }
    }

    [Fact]
    public void MergeInterleavesOverlappingRanges()
    {
        // Both runs draw from an overlapping window so the merge genuinely interleaves.
        var rng = new Random(5);
        const int l = 400, r = 600;
        var v = new int[l + r];
        for (int i = 0; i < l; i++)
            v[i] = rng.Next(300);
        for (int i = 0; i < r; i++)
            v[l + i] = 200 + rng.Next(300);
        Array.Sort(v, 0, l);
        Array.Sort(v, l, r);
        var expected = v.OrderBy(x => x).ToArray();
        DriftMerge.Merge<int, ComparableCmp<int>>(v, new int[l + r - (l + r) / 2], l, new());
        Assert.Equal(expected, v);
    }

    [Fact]
    public void MergeIsStableOnEqualKeys()
    {
        // Unique payloads across both halves; equal keys must keep input order.
        const int l = 500, r = 500;
        int[] leftKeys = DataGen.Ints(Distribution.RandomD20, l, 3).OrderBy(x => x).ToArray();
        int[] rightKeys = DataGen.Ints(Distribution.RandomD20, r, 4).OrderBy(x => x).ToArray();
        var v = new Pair[l + r];
        for (int i = 0; i < l; i++)
            v[i] = new Pair(leftKeys[i], i);
        for (int i = 0; i < r; i++)
            v[l + i] = new Pair(rightKeys[i], l + i);
        DriftMerge.Merge<Pair, ComparableCmp<Pair>>(v, new Pair[l + r - (l + r) / 2], l, new());
        for (int i = 1; i < v.Length; i++)
            Assert.False(v[i].Key == v[i - 1].Key && v[i].Payload < v[i - 1].Payload,
                $"unstable pair at {i}: ({v[i].Key},{v[i].Payload}) after ({v[i - 1].Key},{v[i - 1].Payload})");
    }

    [Fact]
    public void MergeRejectsInsufficientScratch()
    {
        // Upstream silently returns on this contract violation (unreachable for its
        // callers); the port's seam throws instead of leaving the input unmerged.
        const int l = 50, r = 50;
        var v = new int[l + r];
        SortedRun(Distribution.Random, l, 7).CopyTo(v, 0);
        SortedRun(Distribution.Random, r, 11).CopyTo(v, l);
        Assert.Throws<ArgumentException>(() =>
            DriftMerge.Merge<int, ComparableCmp<int>>(v, new int[Math.Min(l, r) - 1], l, new()));
    }
}
