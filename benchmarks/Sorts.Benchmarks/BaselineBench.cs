using System;
using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Baseline zoo, int Random 100k: Array.Sort via every BCL entry point
/// (generic, IComparer, Comparison) against Ipnsort. ArraySort_Generic is the
/// baseline; the IComparer and Comparison variants show the interface/virtual-
/// dispatch tax of those entry points.</summary>
[Config(typeof(BenchConfig))]
public class BaselineBench : SortMatrixBase<int>
{
    private static readonly Comparison<int> IntComparison = CompareKeys;

    private static int CompareKeys(int x, int y) => x.CompareTo(y);

    [Params(Distribution.Random)]
    public Distribution Dist { get; set; }

    [Params(100_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => InitPool(i => DataGen.Ints(Dist, N, Seed + i * SeedStride), PoolSize(N));

    // ArraySort_Generic / Ipnsort are inherited from SortMatrixBase; these are the
    // additional baseline entry points.

    [Benchmark]
    public void ArraySort_IComparer() => Array.Sort(Fresh(), PlainIntComparer.Instance);

    [Benchmark]
    public void ArraySort_Comparison() => Array.Sort(Fresh(), IntComparison);
}

/// <summary>Pair variant: struct-with-payload (int Key + int Payload) at Random 100k —
/// how the comparison changes when moving 8-byte records with a non-int comparer instead
/// of bare ints. Complements the Struct16/Struct128 cases in TypeMatrixBench.</summary>
[Config(typeof(BenchConfig))]
public class PairBench : SortMatrixBase<Pair>
{
    [Params(Distribution.Random)]
    public Distribution Dist { get; set; }

    [Params(100_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => InitPool(i => DataGen.Pairs(Dist, N, Seed + i * SeedStride), PoolSize(N));
}
