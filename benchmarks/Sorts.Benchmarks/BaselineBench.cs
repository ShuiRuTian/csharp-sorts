using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Baseline zoo, int Random 100k: Array.Sort via every BCL entry point
/// (generic, IComparer, Comparison) plus LINQ OrderBy — the only *stable* reference —
/// against Ipnsort. ArraySort_Generic is the baseline; the IComparer and
/// Comparison variants show the interface/virtual-dispatch tax; Linq_OrderBy shows
/// the stability tax (it allocates its output array, by design).</summary>
[Config(typeof(BenchConfig))]
[InvocationCount(Ring.Size)]
public class BaselineBench : SortMatrixBase<int>
{
    private static readonly Comparison<int> IntComparison = CompareKeys;

    private static int CompareKeys(int x, int y) => x.CompareTo(y);

    [Params(Distribution.Random)]
    public Distribution Dist { get; set; }

    [Params(100_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => Init(DataGen.Ints(Dist, N, Seed));

    // ArraySort_Generic / Ipnsort are inherited from SortMatrixBase; these are the
    // additional baseline entry points.

    [Benchmark]
    public void ArraySort_IComparer() => Array.Sort(Next(), PlainIntComparer.Instance);

    [Benchmark]
    public void ArraySort_Comparison() => Array.Sort(Next(), IntComparison);

    [Benchmark]
    public void Linq_OrderBy() => Next().OrderBy(x => x).ToArray();
}

/// <summary>Pair variant: struct-with-payload (int Key + int Payload) at Random
/// 100k. Array.Sort (unstable) and Ipnsort (unstable) vs LINQ OrderBy — the
/// stable-sort cost of moving 8-byte records instead of bare ints.</summary>
[Config(typeof(BenchConfig))]
[InvocationCount(Ring.Size)]
public class PairBench : SortMatrixBase<Pair>
{
    [Params(Distribution.Random)]
    public Distribution Dist { get; set; }

    [Params(100_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => Init(DataGen.Pairs(Dist, N, Seed));

    [Benchmark]
    public void Linq_OrderBy() => Next().OrderBy(x => x.Key).ToArray();
}
