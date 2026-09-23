using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Small-size core matrix: every distribution × the realistic everyday array
/// sizes (10-200). Real payloads are mostly small, so this is the regime that matters
/// most; the 1k/100k CoreMatrix is kept separately for the large-array story.
/// Screening config (ShortRun) — re-confirm losses with the default BenchConfig.</summary>
[Config(typeof(QuickBenchConfig))]
public class SmallDistBench : SortMatrixBase<int>
{
    [Params(
        Distribution.Random, Distribution.Ascending, Distribution.Descending,
        Distribution.Sawtooth, Distribution.OrganPipe, Distribution.RandomD20,
        Distribution.RandomP5, Distribution.RandomS95, Distribution.Zipfian,
        Distribution.AllEqual, Distribution.FewUnique, Distribution.RandomMerge)]
    public Distribution Dist { get; set; }

    [Params(10, 20, 30, 40, 50, 80, 100, 150, 200)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => Init(DataGen.Ints(Dist, N, Seed));
}
