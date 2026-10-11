using System;
using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Extension element types for the upstream-aligned matrix: the same pattern
/// vocabulary (upstream base 7) and the same upstream pattern generators, but element
/// types beyond upstream's default i32/u64/string/1k set — our element-size axis
/// (8B double, 24B Struct16, 128B Struct128, 8B Pair). Sizes are a representative
/// subset of the upstream size table (10/100/1k/10k) to bound the case count; the
/// default four-type matrix carries the full 21-size grid. Replaces the former
/// TypeMatrixBench / PairBench 100k matrices. The double mapping is order-preserving
/// and NaN-free (upstream strips NaNs in its opt-in f64 type; NaN coverage lives in
/// the Extras project).</summary>
public abstract class UpstreamBenchTypeMatrix<T> : SortMatrixBase<T> where T : IComparable<T>
{
    protected abstract T[] Map(int[] source);

    /// <summary>Upstream's base 7 patterns (bench.rs:67-75).</summary>
    [Params("random", "random_z1", "random_d20", "random_p5", "random_s95", "ascending", "descending")]
    public string Pattern { get; set; } = "random";

    /// <summary>Representative subset of upstream's test_sizes (≤10k cap).</summary>
    [Params(10, 100, 1_000, 10_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => InitPool(
        i => Map(UpstreamBenchPatterns.Get(Pattern, N, i * SeedStride)),
        PoolCount(N));

    /// <summary>Keeps the pool's total footprint around 4 MB (Struct128 at 10k would
    /// otherwise allocate ~80 MB), clamped to [8, 1024] entries.</summary>
    private static int PoolCount(int n)
    {
        if (n <= 1)
            return 16;
        long bytesPerEntry = (long)n * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        return (int)Math.Clamp(4_000_000 / Math.Max(bytesPerEntry, 1), 8, 1024);
    }
}

[Config(typeof(BenchConfig))]
public class UpstreamBenchDoubleMatrix : UpstreamBenchTypeMatrix<double>
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
public class UpstreamBenchStruct16Matrix : UpstreamBenchTypeMatrix<Struct16>
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
public class UpstreamBenchStruct128Matrix : UpstreamBenchTypeMatrix<Struct128>
{
    protected override Struct128[] Map(int[] source)
    {
        var a = new Struct128[source.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = new Struct128(source[i], source[i], source[i], source[i], source[i], source[i], source[i], source[i], source[i], source[i], source[i], source[i], source[i], source[i], source[i], source[i]);
        return a;
    }
}

[Config(typeof(BenchConfig))]
public class UpstreamBenchPairMatrix : UpstreamBenchTypeMatrix<Pair>
{
    protected override Pair[] Map(int[] source)
    {
        var a = new Pair[source.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = new Pair(source[i], source[i]);
        return a;
    }
}
