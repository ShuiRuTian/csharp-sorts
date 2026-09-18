using System;
using System.Collections.Generic;
using System.Linq;
using Sorts.TestData;
using Xunit;

public abstract class SortContractTests<T> where T : IComparable<T>
{
    protected abstract void Sort(T[] array);
    protected abstract T[] Gen(Distribution d, int n, int seed);

    /// <summary>Total order over T including tiebreak (payload) — defines "same permutation".</summary>
    protected abstract Comparison<T> Canonical { get; }

    public static TheoryData<Distribution, int, int> SizeSweep =>
        BuildCases(dists: AllDists, sizes: Enumerable.Range(0, 65), seed: 1234);

    public static TheoryData<Distribution, int, int> RandomSizes =>
        BuildCases(dists: new[] { Distribution.Random, Distribution.RandomD20,
                                  Distribution.RandomS95, Distribution.Zipfian },
                   sizes: new[] { 65, 100, 511, 1000, 4096, 20_000 },
                   seeds: Enumerable.Range(1, 200));

    private static TheoryData<Distribution, int, int> BuildCases(
        Distribution[] dists, IEnumerable<int> sizes, int seed) =>
        BuildCases(dists, sizes, new[] { seed });

    private static TheoryData<Distribution, int, int> BuildCases(
        Distribution[] dists, IEnumerable<int> sizes, IEnumerable<int> seeds)
    {
        var data = new TheoryData<Distribution, int, int>();
        foreach (var d in dists)
            foreach (var n in sizes)
                foreach (var s in seeds)
                    data.Add(d, n, s);
        return data;
    }

    private static Distribution[] AllDists => Enum.GetValues<Distribution>();

    [Theory]
    [MemberData(nameof(SizeSweep))]
    public void IsCorrectPermutation(Distribution d, int n, int seed)
    {
        var input = Gen(d, n, seed);
        var expected = input.ToArray(); // untouched copy
        Sort(input);

        // 1) Output is non-decreasing under the sort's own comparer.
        for (int i = 1; i < input.Length; i++)
            Assert.False(input[i].CompareTo(input[i - 1]) < 0,
                $"descending pair at {i} for {d} n={n} seed={seed}");

        // 2) Output is a permutation of the input (canonical full order on both).
        var lhs = input.OrderBy(x => x, Comparer<T>.Create(Canonical)).ToArray();
        var rhs = expected.OrderBy(x => x, Comparer<T>.Create(Canonical)).ToArray();
        Assert.Equal(rhs, lhs);
    }

    [Theory]
    [MemberData(nameof(RandomSizes))]
    public void IsDeterministic(Distribution d, int n, int seed)
    {
        var a = Gen(d, n, seed);
        var b = Gen(d, n, seed); // independent instance (catches instance-identity bugs)
        Sort(a);
        Sort(b);
        // Element-wise identical output, INCLUDING tie positions (Pair payload),
        // i.e. stricter than multiset equality.
        Assert.Equal(a, b);
    }
}
