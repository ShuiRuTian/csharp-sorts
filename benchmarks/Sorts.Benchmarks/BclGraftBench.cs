using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Sorts.Experiments.Bcl;

namespace Sorts.Benchmarks;

/// <summary>BCL-graft matrix (int, small-size sweep): can ipnsort parts improve the
/// current C# sort (Array.Sort)? Compares the real Array.Sort (baseline), the
/// faithful BCL port, and the two graft variants kept for this question — + ipnsort
/// branchless Lomuto partition for the scalar arms; + ipnsort small-sort replacing
/// the ≤16 insertion sort — see src/Sorts.Experiments/Bcl/GraftSort.cs. The
/// Unsafe.As-arms-only and combined variants were dropped (arms ≈ BclPort was
/// established; combined is inferable). Sizes 10/30/50/100/500 straddle the
/// small-sort thresholds (16/32) and the recursion mid-range. Patterns: a
/// representative subset of the upstream set (17 of 82) — the zero/one-dominant
/// family (random_p* / 90p_* / 95p_* / 99p_* etc.) dropped as uninformative; kept:
/// uniform random + Zipf (z1/z3), the cardinality spread (d4/d20/d256), the
/// n-scaled range (div3), prefix-sorted (s30/s70/s95), displaced tail (snl_1),
/// two-run merge (m50), structural shapes (saw_ascending / saws_long / pipe_organ)
/// and the sorted extremes (ascending/descending). Ipnsort (the full port) is
/// included from SortMatrixBase as context.</summary>
public abstract class BclGraftMatrix<T> : SortMatrixBase<T> where T : IComparable<T>
{
    protected abstract T[] Map(int[] source);

    [Params(
        "random", "random_z1", "random_z3", "random_d4", "random_d20", "random_d256",
        "random__div3",
        "random_s30", "random_s70", "random_s95",
        "random_snl_1", "random_m50",
        "saw_ascending", "saws_long", "pipe_organ",
        "ascending", "descending")]
    public string Pattern { get; set; } = "random";

    [Params(10, 30, 50, 100, 500)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => InitPool(
        i => Map(UpstreamBenchPatterns.Get(Pattern, N, i * SeedStride)),
        PoolCount(N));

    private static int PoolCount(int n)
    {
        if (n <= 1)
            return 16;
        long bytesPerEntry = (long)n * Unsafe.SizeOf<T>();
        return (int)Math.Clamp(4_000_000 / Math.Max(bytesPerEntry, 1), 8, 1024);
    }

    private readonly GenericArraySortHelper<T> _bclPort = new();

    [Benchmark]
    public void BclPort() => _bclPort.Sort(Fresh(), null);

    [Benchmark]
    public void GraftPartition() => GraftSort<T, GraftPartitionOps<T>>.Sort(Fresh(), null);

    [Benchmark]
    public void GraftSmallSort() => GraftSort<T, GraftSmallSortOps<T>>.Sort(Fresh(), null);
}

[Config(typeof(BenchConfig))]
public class BclGraftIntMatrix : BclGraftMatrix<int>
{
    protected override int[] Map(int[] source) => source;

}
