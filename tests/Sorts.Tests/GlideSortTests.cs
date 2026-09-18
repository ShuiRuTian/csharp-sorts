using System;
using Sorts.TestData;
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
