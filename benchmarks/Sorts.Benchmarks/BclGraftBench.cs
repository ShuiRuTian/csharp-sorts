using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Sorts.Experiments.Bcl;

namespace Sorts.Benchmarks;

/// <summary>BCL-graft matrix: can ipnsort parts improve the current C# sort
/// (Array.Sort)? Compares the real Array.Sort (baseline), the faithful BCL port, and
/// the graft variants — Unsafe.As arms; + ipnsort branchless Lomuto partition for
/// the scalar arms; + ipnsort small-sort replacing the ≤16 insertion sort; both —
/// see src/Sorts.Experiments/Bcl/GraftSort.cs. Types cover the scalar network path
/// (int, double), the general small-sort path (Struct16, string) and the reference
/// path (string). Patterns: random (main), random_d20 (20 distinct — duplicate-heavy,
/// exposes the partition's equal-split), descending. Ipnsort (the full port) is
/// included from SortMatrixBase as context.
///
/// Caveat: the grafted partition has no equal-ancestor batching (not grafted — the
/// BCL skeleton has no such driver logic), so equal-heavy inputs drive the
/// depth-limited heapsort fallback; that is an honest cost of the isolated graft,
/// visible in the random_d20 rows.</summary>
public abstract class BclGraftMatrix<T> : SortMatrixBase<T> where T : IComparable<T>
{
    protected abstract T[] Map(int[] source);

    [Params("random", "random_d20", "descending")]
    public string Pattern { get; set; } = "random";

    [Params(100, 1_000, 10_000)]
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
    public void GraftArms() => GraftSort<T, GraftArmsOps<T>>.Sort(Fresh(), null);

    [Benchmark]
    public void GraftPartition() => GraftSort<T, GraftPartitionOps<T>>.Sort(Fresh(), null);

    [Benchmark]
    public void GraftSmallSort() => GraftSort<T, GraftSmallSortOps<T>>.Sort(Fresh(), null);

    [Benchmark]
    public void GraftCombined() => GraftSort<T, GraftAllOps<T>>.Sort(Fresh(), null);
}

[Config(typeof(BenchConfig))]
public class BclGraftIntMatrix : BclGraftMatrix<int>
{
    protected override int[] Map(int[] source) => source;
}

[Config(typeof(BenchConfig))]
public class BclGraftDoubleMatrix : BclGraftMatrix<double>
{
    protected override double[] Map(int[] source)
    {
        var a = new double[source.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = source[i];
        return a;
    }
}

[Config(typeof(BenchConfig))]
public class BclGraftStruct16Matrix : BclGraftMatrix<Struct16>
{
    protected override Struct16[] Map(int[] source)
    {
        var a = new Struct16[source.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = new Struct16(source[i], source[i], source[i]);
        return a;
    }
}

[Config(typeof(BenchConfig))]
public class BclGraftStringMatrix : BclGraftMatrix<string>
{
    // bench.rs FFIString transform: order-preserving 10-digit decimal.
    protected override string[] Map(int[] source)
    {
        var a = new string[source.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = ((uint)((long)source[i] + (1L << 31))).ToString("D10", CultureInfo.InvariantCulture);
        return a;
    }
}
