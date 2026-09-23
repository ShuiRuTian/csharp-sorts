using System;
using System.Linq;
using Sorts.TestData;
using Xunit;

public class DataGenTests
{
    [Fact]
    public void DeterministicAcrossCalls()
    {
        Assert.Equal(DataGen.Ints(Distribution.Random, 1000, 7), DataGen.Ints(Distribution.Random, 1000, 7));
    }

    [Fact]
    public void AscendingIsAscending() =>
        Assert.Equal(Enumerable.Range(0, 500).ToArray(), DataGen.Ints(Distribution.Ascending, 500, 1));

    [Fact]
    public void FewUniqueHasFourDistinctValues() =>
        Assert.Equal(4, DataGen.Ints(Distribution.FewUnique, 10_000, 3).Distinct().Count());

    [Fact]
    public void PairPayloadsAreInputOrderStamps() =>
        Assert.Equal(Enumerable.Range(0, 300), DataGen.Pairs(Distribution.RandomD20, 300, 5).Select(p => p.Payload));

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.Zipfian)]
    public void NonEmptyForAllTypes(Distribution d)
    {
        Assert.NotEmpty(DataGen.Ints(d, 100, 9));
        Assert.NotEmpty(DataGen.Doubles(d, 100, 9));
        Assert.NotEmpty(DataGen.Strings(d, 100, 9));
        Assert.NotEmpty(DataGen.Pairs(d, 100, 9));
    }

    // --- Upstream-faithful pattern invariants (see DataGen header mapping) ---

    [Fact]
    public void RandomSpansFullI32Range()
    {
        var a = DataGen.Ints(Distribution.Random, 10_000, 11);
        Assert.Contains(a, x => x < 0);
        Assert.Contains(a, x => x >= 0);
    }

    [Fact]
    public void RandomD20IsZeroToNineteen()
    {
        var a = DataGen.Ints(Distribution.RandomD20, 10_000, 12);
        Assert.All(a, x => Assert.InRange(x, 0, 19));
        Assert.Contains(19, a);
    }

    [Fact]
    public void RandomP5IsMostlyZero()
    {
        var a = DataGen.Ints(Distribution.RandomP5, 10_000, 13);
        Assert.True(a.Count(x => x == 0) > 9_000);
    }

    [Fact]
    public void RandomS95HasSortedPrefix()
    {
        var a = DataGen.Ints(Distribution.RandomS95, 10_000, 14);
        int sortedLen = (int)Math.Round(10_000 / 100.0 * 95.0, MidpointRounding.AwayFromZero);
        for (int i = 1; i < sortedLen; i++) Assert.True(a[i - 1] <= a[i]);
    }

    [Fact]
    public void RandomMergeHasTwoSortedRuns()
    {
        var a = DataGen.Ints(Distribution.RandomMerge, 10_000, 15);
        int first = (int)Math.Round(10_000 / 100.0 * 95.0, MidpointRounding.AwayFromZero);
        for (int i = 1; i < first; i++) Assert.True(a[i - 1] <= a[i]);
        for (int i = first + 1; i < a.Length; i++) Assert.True(a[i - 1] <= a[i]);
    }

    [Fact]
    public void SawtoothHasSortedChunks()
    {
        const int n = 4_096; // round(log2(4096)) = 12
        var a = DataGen.Ints(Distribution.Sawtooth, n, 16);
        int chunk = n / 12;
        for (int start = 0; start + chunk <= n; start += chunk)
            for (int i = start + 1; i < start + chunk; i++)
                Assert.True(a[i - 1] <= a[i]);
    }

    [Fact]
    public void OrganPipeIsUpThenDown()
    {
        const int n = 1_000;
        var a = DataGen.Ints(Distribution.OrganPipe, n, 17);
        int half = n / 2;
        for (int i = 1; i < half; i++) Assert.True(a[i - 1] <= a[i]);
        for (int i = half + 1; i < n; i++) Assert.True(a[i - 1] >= a[i]);
    }

    [Fact]
    public void ZipfianIsHeavyTailed()
    {
        const int n = 100_000;
        var a = DataGen.Ints(Distribution.Zipfian, n, 18);
        Assert.All(a, x => Assert.InRange(x, 1, n));
        // Zipf(s=1): P(k <= 10) = H_10 / H_n ~= 0.242.
        Assert.True(a.Count(x => x <= 10) > n / 5);
    }

    [Fact]
    public void AllEqualIsConstant()
    {
        var a = DataGen.Ints(Distribution.AllEqual, 100, 19);
        Assert.All(a, x => Assert.Equal(66, x));
    }

    // --- New near-sorted / structural / adversarial shapes ---

    [Theory]
    [InlineData(Distribution.SortedSwap1, 2)]
    [InlineData(Distribution.SortedSwap3, 6)]
    public void SortedSwapStaysMostlySorted(Distribution d, int maxDisplaced)
    {
        const int n = 200;
        var a = DataGen.Ints(d, n, 21);
        int displaced = a.Where((x, i) => x != i).Count();
        Assert.InRange(displaced, 0, maxDisplaced);
    }

    [Fact]
    public void SortedSwap3ActuallyPerturbs()
    {
        const int n = 200;
        Assert.NotEqual(
            DataGen.Ints(Distribution.Ascending, n, 21),
            DataGen.Ints(Distribution.SortedSwap3, n, 21));
    }

    [Fact]
    public void RandomSnlHasSortedPrefix()
    {
        const int n = 200;
        var a = DataGen.Ints(Distribution.RandomSnl, n, 22);
        for (int i = 1; i < n - 5; i++) Assert.True(a[i - 1] <= a[i]);
    }

    [Fact]
    public void PlateauIsTrapezoid()
    {
        const int n = 200;
        int peak = Math.Max(n / 4, 1);
        var a = DataGen.Ints(Distribution.Plateau, n, 23);
        Assert.All(a, x => Assert.InRange(x, 0, peak));
        Assert.Equal(0, a[0]);
        Assert.Equal(peak, a[n / 2]);
        Assert.Equal(0, a[n - 1]);
    }

    [Fact]
    public void StaggerIsModularPermutation()
    {
        const int n = 200;
        var a = DataGen.Ints(Distribution.Stagger, n, 24);
        for (int i = 0; i < n; i++) Assert.Equal((int)((long)i * 3 % n), a[i]);
    }

    [Fact]
    public void Median3KillerMatchesCpython()
    {
        const int n = 100;
        int half = n / 2;
        var a = DataGen.Ints(Distribution.Median3Killer, n, 25);
        for (int i = 0; i < n; i++) Assert.Equal(i < half ? half - 1 - i : i - half, a[i]);
    }

    // --- Documented small-N collapses (inherited from upstream's percentage rounding;
    //     pinned so they stay known facts rather than silent surprises) ---

    [Fact]
    public void SmallNPercentageShapesCollapseAsDocumented()
    {
        Assert.All(DataGen.Ints(Distribution.RandomP5, 10, 1), x => Assert.Equal(0, x));

        var s95 = DataGen.Ints(Distribution.RandomS95, 10, 1);
        for (int i = 1; i < s95.Length; i++) Assert.True(s95[i - 1] <= s95[i]);

        Assert.Equal(
            DataGen.Ints(Distribution.RandomS95, 20, 2),
            DataGen.Ints(Distribution.RandomMerge, 20, 2));
    }
}
