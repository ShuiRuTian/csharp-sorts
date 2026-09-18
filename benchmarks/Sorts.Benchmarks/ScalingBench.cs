using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Scaling curve: int, Random distribution, twelve sizes from 1k to 10M
/// (doubling steps, 2^16/2^20 checkpoints, then the 10M stress point) × 4
/// implementations. Log-log slope of Array.Sort vs ours shows where the
/// cache behaviour diverges.</summary>
[Config(typeof(BenchConfig))]
[InvocationCount(Ring.Size)]
public class ScalingBench : SortMatrixBase<int>
{
    [Params(1_000, 2_000, 4_000, 8_000, 16_000, 32_000, 65_536, 131_072,
        262_144, 524_288, 1_048_576, 10_000_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => Init(DataGen.Ints(Distribution.Random, N, Seed));
}
