using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Small-size shape matrix (int): the realistic everyday sizes 10-200 across
/// the shapes that actually matter for a small-array Array.Sort replacement. Sizes are
/// aligned to the algorithm's thresholds: 16 (scalar leaf), 32 (general leaf), 64
/// (pseudo-median recursion). Degenerate/duplicate shapes at tiny N are dropped here
/// (RandomD20, Zipfian, RandomMerge) — they stay in CoreMatrixBench for large N.
/// Screening config (ShortRun); re-confirm anything interesting with BenchConfig.</summary>
[Config(typeof(QuickBenchConfig))]
public class SmallShapeBench : SortMatrixBase<int>
{
    [Params(
        Distribution.Random, Distribution.Ascending, Distribution.Descending,
        Distribution.AllEqual, Distribution.FewUnique, Distribution.RandomP5,
        Distribution.SortedSwap1, Distribution.SortedSwap3, Distribution.RandomSnl,
        Distribution.RandomS95, Distribution.OrganPipe, Distribution.Sawtooth,
        Distribution.Plateau, Distribution.Median3Killer)]
    public Distribution Dist { get; set; }

    [Params(10, 16, 32, 64, 128, 200)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => InitPool(i => DataGen.Ints(Dist, N, Seed + i * 7919), 1024);
}
