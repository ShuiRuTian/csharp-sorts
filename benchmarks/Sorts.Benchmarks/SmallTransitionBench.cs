using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Transition region 256-2048 (int): between the small-sort leaf zone and the
/// large-array driver, where Array.Sort's branchy Hoare partition competes with
/// ipnsort's branchless Lomuto cyclic partition. Four decision-relevant shapes plus the
/// 768/2048 checkpoints.</summary>
[Config(typeof(BenchConfig))]
public class SmallTransitionBench : SortMatrixBase<int>
{
    [Params(
        Distribution.Random, Distribution.SortedSwap3,
        Distribution.RandomSnl, Distribution.FewUnique)]
    public Distribution Dist { get; set; }

    [Params(256, 512, 768, 1_024, 2_048)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => InitPool(i => DataGen.Ints(Dist, N, Seed + i * SeedStride), PoolSize(N));
}
