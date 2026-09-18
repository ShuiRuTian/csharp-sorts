using System;
using System.Linq;
using Sorts;
using Sorts.TestData;
using Xunit;

public class GlideQuicksortTests
{
    private static void SortWithQuicksort<T>(T[] a, int limit) where T : IComparable<T>
    {
        var scratch = new T[a.Length];
        GlideQuicksort.Quicksort<T, ComparableCmp<T>>(a, scratch, limit, new());
    }

    [Fact]
    public void QuicksortSortsAllSizesAndPatterns()
    {
        var sizes = Enumerable.Range(49, 152).Concat(new[] { 1000, 4096, 20000 });
        foreach (int n in sizes)
        {
            foreach (Distribution d in new[]
                { Distribution.Random, Distribution.RandomD20, Distribution.Zipfian,
                  Distribution.AllEqual, Distribution.Descending })
            {
                var a = DataGen.Ints(d, n, 7);
                var expected = a.OrderBy(x => x).ToArray();
                SortWithQuicksort(a, limit: 64);
                // 1) ascending
                for (int i = 1; i < a.Length; i++)
                    Assert.False(a[i].CompareTo(a[i - 1]) < 0,
                        $"descending pair at {i} for {d} n={n}");
                // 2) permutation
                Assert.Equal(expected, a);
            }
        }
    }

    [Fact]
    public void QuicksortDepthLimitFallsBackToEager()
    {
        // Organ-pipe-of-organ-pipes: adversarial for quicksort pivot selection, 100k.
        const int n = 100_000;
        var a = new int[n];
        for (int i = 0; i < n; i++)
        {
            int half = (i < n / 2) ? i : n - 1 - i; // organ pipe 0..k..0
            a[i] = ((half / 8) % 2 == 0) ? half : (n - half); // pipe-of-pipes flip
        }
        var expected = a.OrderBy(x => x).ToArray();
        SortWithQuicksort(a, limit: 1); // immediate EagerSort fallback
        Assert.Equal(expected, a);

        var b = DataGen.Ints(Distribution.OrganPipe, n, 11);
        var expectedB = b.OrderBy(x => x).ToArray();
        SortWithQuicksort(b, limit: 1);
        Assert.Equal(expectedB, b);
    }

    [Fact]
    public void QuicksortIsStableOnEqualKeys()
    {
        // The bidirectional partition must preserve input order of equal-key
        // elements (payload) — the defining property of glidesort's quicksort.
        foreach (int n in new[] { 49, 300, 5000 })
        {
            var a = DataGen.Pairs(Distribution.RandomD20, n, 9);
            var scratch = new Pair[n];
            GlideQuicksort.Quicksort<Pair, ComparableCmp<Pair>>(a, scratch, 64, new());
            for (int i = 1; i < a.Length; i++)
                Assert.False(a[i].Key == a[i - 1].Key && a[i].Payload < a[i - 1].Payload,
                    $"unstable pair at {i}: ({a[i].Key},{a[i].Payload}) after ({a[i - 1].Key},{a[i - 1].Payload})");
        }
    }

    [Fact]
    public void QuicksortCarriesPivotPositionThroughSkip()
    {
        // Strategy-layer regression: the LeftWithPivot fast path (empty less side,
        // rs:489-502) must carry the pivot ELEMENT's tracked position, not index 0.
        // n=56 keeps pivot selection a plain median-of-3 over positions 0, 21, 49.
        // Values (40, 0, 0) there make the median the minimum, so level 0 partitions
        // right around 0 with an empty less side and recurses on the whole input as
        // LeftWithPivot with the pivot element still at its logical index 21.
        // Level 1 partitions left (inverted) around that carried element: its less
        // side is exactly the two 0's — all-equal, so the equal-batch skip is sound.
        // If the carry degenerated to index 0, level 1 would pivot on v[0]=40
        // instead, its less side {40, 0, 0} would not be all-equal, and the
        // unconditional skip would leave those three elements unsorted.
        const int n = 56;
        var v = new int[n];
        for (int i = 0; i < n; i++)
            v[i] = 41 + i; // 41..96: distinct, all above the level-0 pivot value
        v[0] = 40;  // sampled position a — deliberately NOT the pivot value
        v[21] = 0;  // sampled position b — selected pivot (the minimum)
        v[49] = 0;  // sampled position c — second minimum, forces median3 -> b
        Assert.Equal(21, GlideQuicksort.ChoosePivot<int, ComparableCmp<int>>(v, new()));
        var expected = v.OrderBy(x => x).ToArray();
        SortWithQuicksort(v, limit: 64);
        Assert.Equal(expected, v);
    }

    [Fact]
    public void PartitionRoutesElementsAroundPivot()
    {
        // The brief's Partition seam. Per the header ASCII art the four output regions
        // span BOTH buffers: forward scan writes < pivot to dest's front and >= pivot
        // to scratch's front; backward scan writes < pivot to scratch's end and
        // >= pivot to dest's end. The unscanned middles of dest/scratch stay untouched
        // (zero here — all input values are non-zero), which is exactly the layout the
        // recursive driver relies on when it moves the scratch regions into place.
        var left = new int[] { 5, 1, 9, 3 };
        var right = new int[] { 8, 4, 7, 2 };
        var dest = new int[8];
        var scratch = new int[8];
        int pivot = 5;
        GlideQuicksort.Partition<int, ComparableCmp<int>>(
            left, right, dest, scratch, ref pivot, new());
        // Regions in input order: a=[1,3] (dest front), d=[8,7] (dest back),
        // c'=[5,9] (scratch front), b'=[4,2] (scratch end).
        Assert.Equal(new[] { 1, 3, 0, 0, 0, 0, 8, 7 }, dest);
        Assert.Equal(new[] { 5, 9, 0, 0, 0, 0, 4, 2 }, scratch);
        // Together the four written regions hold every input element exactly once.
        var written = dest.Concat(scratch).Where(x => x != 0).OrderBy(x => x).ToArray();
        Assert.Equal(left.Concat(right).OrderBy(x => x).ToArray(), written);
    }
}
