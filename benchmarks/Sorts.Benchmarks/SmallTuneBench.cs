using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Focused sweep for tuning the small-array path: the region where the
/// current patch still sits around parity (200-1024), plus a couple of larger
/// checkpoints to catch regressions. Random int, same data generator as
/// SmallArrayBench; ratios are Ipnsort ÷ Array.Sort&lt;T&gt;(T[]).</summary>
[Config(typeof(BenchConfig))]
public class SmallTuneBench : SortMatrixBase<int>
{
    [Params(200, 400, 512, 768, 1_024, 2_048)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => Init(DataGen.Ints(Distribution.Random, N, Seed));
}
