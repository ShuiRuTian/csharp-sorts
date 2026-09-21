using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Scaling curve: int, Random distribution, sixteen sizes from 10 to 512k
/// (small-array kernel zone 10-500, then doubling steps with 2^16/2^19
/// checkpoints) × 5 implementations. Log-log slope of Array.Sort vs ours shows
/// where the cache behaviour diverges. (A 10M stress point and the 1M checkpoint
/// were cut — 4-40MB working sets are far outside realistic sort payloads and
/// dominated the whole run.)</summary>
[Config(typeof(BenchConfig))]
[InvocationCount(Ring.Size)]
public class ScalingBench : SortMatrixBase<int>
{
    [Params(10, 20, 50, 100, 200, 500,
        1_000, 2_000, 4_000, 8_000, 16_000, 32_000, 65_536, 131_072,
        262_144, 524_288)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => Init(DataGen.Ints(Distribution.Random, N, Seed));
}
