using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Transition region 256-1024: where the small-array path (whole-array Hoare,
/// SmallArrayHoareMax) hands back to the branchless Lomuto path. Four decision-relevant
/// shapes only, to decide whether the 1024 cutoff should stay.</summary>
[Config(typeof(QuickBenchConfig))]
public class SmallTransitionBench : SortMatrixBase<int>
{
    [Params(
        Distribution.Random, Distribution.SortedSwap3,
        Distribution.RandomSnl, Distribution.FewUnique)]
    public Distribution Dist { get; set; }

    [Params(256, 512, 1_024)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => Init(DataGen.Ints(Dist, N, Seed));
}
