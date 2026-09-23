using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Small-array focus: the payload sizes that dominate everyday use
/// (10–400 elements), Random int. This is where per-call driver overhead and the
/// small-sort leaf strategy matter most, and where the JIT must reach Tier-1 for
/// the comparison to be meaningful — hence no pinned InvocationCount (see
/// MatrixBase). Ratios are Ipnsort ÷ Array.Sort&lt;T&gt;(T[]).</summary>
[Config(typeof(BenchConfig))]
public class SmallArrayBench : SortMatrixBase<int>
{
    [Params(10, 20, 30, 40, 50, 80, 100, 150, 200)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => Init(DataGen.Ints(Distribution.Random, N, Seed));
}
