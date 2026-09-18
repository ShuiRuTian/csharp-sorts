using System;
using System.Linq;
using Sorts;
using Sorts.TestData;
using Xunit;

public class DriftQuicksortTests
{
    private static void SortWithQuicksort<T>(T[] a, int limit) where T : IComparable<T>
    {
        // Drift's quicksort needs v.Length scratch for the partition and 50 for
        // the small-sort base case; n covers both.
        var scratch = new T[Math.Max(a.Length, DriftSmallSort.MinSmallSortScratchLen)];
        DriftQuicksort.StableQuicksort<T, ComparableCmp<T>>(a, scratch, limit, default, new());
    }

    [Fact]
    public void StableQuicksortSortsAllSizesAndPatterns()
    {
        // 33 = smallsort threshold + 1 for Freeze-like int: the first size that
        // exercises the partition. Sweep through 300 to cover both small-sort
        // impls' thresholds (32/16) and the unrolled loop, then large sizes.
        var sizes = Enumerable.Range(33, 268).Concat(new[] { 1000, 4096, 20000 });
        foreach (int n in sizes)
        {
            foreach (Distribution d in new[]
                { Distribution.Random, Distribution.RandomD20, Distribution.Zipfian,
                  Distribution.AllEqual, Distribution.Descending })
            {
                var a = DataGen.Ints(d, n, 7);
                var expected = a.OrderBy(x => x).ToArray();
                SortWithQuicksort(a, limit: 64);
                for (int i = 1; i < a.Length; i++)
                    Assert.False(a[i].CompareTo(a[i - 1]) < 0,
                        $"descending pair at {i} for {d} n={n}");
                Assert.Equal(expected, a);
            }
        }
    }

    [Fact]
    public void AncestorPivotGivesEqualBatchingOnAllEqual()
    {
        // AllEqual n=100k: the first partition leaves a 0-length left side, which
        // triggers the equal-partition path (inverted comparator) that batches
        // every equal element on the left and skips it — O(n log k) for k distinct
        // values. Informational: comparisons are not observable from C#; assert
        // correctness (and that it terminates quickly, i.e. no quadratic blowup).
        const int n = 100_000;
        var a = DataGen.Ints(Distribution.AllEqual, n, 3);
        var expected = a.OrderBy(x => x).ToArray();
        SortWithQuicksort(a, limit: 64);
        Assert.Equal(expected, a);
    }

    [Fact]
    public void StableQuicksortDepthLimitFallsBackToEager()
    {
        // limit == 0: immediate eager fallback (runs of <= threshold via SortSmall,
        // then bottom-up DriftMerge). Adversarial organ-pipe-of-pipes exercises it
        // at size, plus a plain sweep at limit 0.
        const int n = 100_000;
        var a = new int[n];
        for (int i = 0; i < n; i++)
        {
            int half = (i < n / 2) ? i : n - 1 - i;
            a[i] = ((half / 8) % 2 == 0) ? half : (n - half);
        }
        var expected = a.OrderBy(x => x).ToArray();
        SortWithQuicksort(a, limit: 0);
        Assert.Equal(expected, a);

        foreach (int m in new[] { 33, 100, 1000 })
        {
            var b = DataGen.Ints(Distribution.RandomD20, m, 5);
            var expectedB = b.OrderBy(x => x).ToArray();
            SortWithQuicksort(b, limit: 0);
            Assert.Equal(expectedB, b);
        }
    }

    [Fact]
    public void StableQuicksortIsStableOnEqualKeys()
    {
        // Stable partition + stable smallsort + stable merge: payload order of
        // equal keys must be preserved through the whole quicksort.
        foreach (int n in new[] { 33, 300, 5000 })
        {
            var a = DataGen.Pairs(Distribution.RandomD20, n, 9);
            var scratch = new Pair[Math.Max(n, DriftSmallSort.MinSmallSortScratchLen)];
            DriftQuicksort.StableQuicksort<Pair, ComparableCmp<Pair>>(a, scratch, 64, default, new());
            for (int i = 1; i < a.Length; i++)
                Assert.False(a[i].Key == a[i - 1].Key && a[i].Payload < a[i - 1].Payload,
                    $"unstable pair at {i}: ({a[i].Key},{a[i].Payload}) after ({a[i - 1].Key},{a[i - 1].Payload})");
        }
    }

    [Fact]
    public void StablePartitionRoutesElementsAroundPivot()
    {
        // Direct seam test: num_left = count of elements < pivot, stability of
        // both sides, pivot slot handled per pivotGoesLeft.
        var v = new int[] { 5, 1, 9, 3, 5, 8, 0, 2, 5 };
        var scratch = new int[v.Length];
        int numLeft = DriftQuicksort.StablePartition<int, ComparableCmp<int>>(
            v, scratch, 0, pivotGoesLeft: false, invert: false, new());
        // Elements < 5: 1,3,0,2 (input order) at the front; >= 5 at the back in
        // input order (5,9,5,8,5).
        Assert.Equal(4, numLeft);
        Assert.Equal(new[] { 1, 3, 0, 2, 5, 9, 5, 8, 5 }, v);
    }

    [Fact]
    public void StablePartitionWithInvertedComparatorBatchesEquals()
    {
        // The equal-partition call shape (quicksort.rs:69): invert + pivotGoesLeft.
        // Inverted closure g(a, b) = !is_less(b, a): towards_left = g(scan, pivot)
        // = !is_less(pivot, scan), i.e. scan <= pivot goes LEFT. Pivot itself goes
        // left (independent of invert).
        var v = new int[] { 5, 1, 9, 3, 5, 8, 0, 2, 5 };
        var scratch = new int[v.Length];
        int numLeft = DriftQuicksort.StablePartition<int, ComparableCmp<int>>(
            v, scratch, 0, pivotGoesLeft: true, invert: true, new());
        // towards_left = !(pivot < cur) = cur <= pivot: everything <= 5 goes LEFT
        // (1,3,5,0,2,5), the pivot itself (pivotPos=0 is scanned first, goes left
        // via pivotGoesLeft), and 9,8 go right. num_left = 7, both sides in input order.
        Assert.Equal(7, numLeft);
        Assert.Equal(new[] { 5, 1, 3, 5, 0, 2, 5, 9, 8 }, v);
    }

    [Fact]
    public void ChoosePivotSamplesMedianOfThreeRegions()
    {
        // n = 65 > 64: recursion threshold. len_div_8 = 8; regions a=[0,8), b=[32,40),
        // c=[56,64). Distinct values make median3 pick the middle region's median.
        // Simpler: n < 64 pins plain median3 over positions 0, n/8*4, n/8*7.
        const int n = 56; // len_div_8 = 7 -> positions 0, 28, 49
        var v = new int[n];
        for (int i = 0; i < n; i++)
            v[i] = i;
        // median3(0, 28, 49) -> 28 (the median of three sorted values).
        Assert.Equal(28, DriftQuicksort.ChoosePivot<int, ComparableCmp<int>>(v, new()));
    }
}
