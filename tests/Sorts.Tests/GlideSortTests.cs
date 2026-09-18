using System;
using Sorts.TestData;
using System.Collections.Generic;
using Xunit;

public class GlideSortTests : SortContractTests<int>
{
    protected override void Sort(int[] array) => Sorts.GlideSort.Sort(array);
    protected override int[] Gen(Distribution d, int n, int seed) => DataGen.Ints(d, n, seed);
    protected override Comparison<int> Canonical => (a, b) => a.CompareTo(b);
}

public class GlideSortPairTests : SortContractTests<Pair>
{
    protected override void Sort(Pair[] array) => Sorts.GlideSort.Sort(array);
    protected override Pair[] Gen(Distribution d, int n, int seed) => DataGen.Pairs(d, n, seed);
    protected override Comparison<Pair> Canonical => (a, b) =>
    {
        var c = a.Key.CompareTo(b.Key);
        return c != 0 ? c : a.Payload.CompareTo(b.Payload);
    };
}

public class GlideSortDoubleTests : SortContractTests<double>
{
    protected override void Sort(double[] array) => Sorts.GlideSort.Sort(array);
    protected override double[] Gen(Distribution d, int n, int seed) => DataGen.Doubles(d, n, seed);
    protected override Comparison<double> Canonical => (a, b) => a.CompareTo(b);
}

public class GlideSortStringTests : SortContractTests<string>
{
    protected override void Sort(string[] array) => Sorts.GlideSort.Sort(array);
    protected override string[] Gen(Distribution d, int n, int seed) => DataGen.Strings(d, n, seed);
    protected override Comparison<string> Canonical => (a, b) => a.CompareTo(b);
}

internal struct IntSpanCmp : IComparer<int>
{
    public int Compare(int x, int y) => x.CompareTo(y);
}

public class GlideSortSpanAdapterTests
{
    [Fact]
    public void SpanComparerOverloadSorts()
    {
        var a = new int[500];
        var rng = new Random(9);
        for (int i = 0; i < a.Length; i++) a[i] = rng.Next(100);
        Sorts.GlideSort.Sort<int, IntSpanCmp>(a.AsSpan(), new IntSpanCmp());
        for (int i = 1; i < a.Length; i++)
            Assert.True(a[i - 1] <= a[i]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(47)]
    [InlineData(100)]
    [InlineData(1 << 20)]       // full tier: min(n, 1MiB/4) = 262144
    [InlineData(1 << 28)]       // half tier: min(n/2, 1GiB/4)
    [InlineData(int.MaxValue)]  // eighth tier
    public void AllocSizeMatchesUpstreamFormula(int n)
    {
        int fullAllowed = Math.Min(n, 1024 * 1024 / sizeof(int));
        int halfAllowed = Math.Min(n / 2, 1024 * 1024 * 1024 / sizeof(int));
        int expected = Math.Max(Math.Max(fullAllowed, halfAllowed), Math.Max(n / 8, 48));
        Assert.Equal(expected, Sorts.GlideSort.GlidesortAllocSize<int>(n));
    }
}
