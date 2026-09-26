using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace Sorts.Benchmarks;

/// <summary>Upstream-aligned benchmark matrix: the same pattern NAMES (base 7 that
/// upstream's bench always runs), the same size points (upstream's criterion test_sizes
/// filtered to our 10k cap) and the same element types (i32/u64/string/1k) as
/// sort-research-rs/benches/bench.rs, for Array.Sort vs Ipnsort. The extra patterns
/// (EXTRA_PATTERNS) are ported in <see cref="UpstreamBenchPatterns"/>;
/// <see cref="UpstreamExtrasBenchMatrix{T}"/> runs them for int.
///
/// [Params] is enumerated explicitly rather than [ParamsSource] so the pattern/size sets
/// are visible in the source and do not depend on member-resolution rules for an open
/// generic base.
///
/// Screening config (ShortRun); re-confirm interesting cells with BenchConfig.</summary>
public abstract class UpstreamBenchMatrix<T> : SortMatrixBase<T> where T : IComparable<T>
{
    protected abstract T[] Map(int[] source);

    /// <summary>Upstream's base 7 patterns (bench.rs:67-75).</summary>
    [Params("random", "random_z1", "random_d20", "random_p5", "random_s95", "ascending", "descending")]
    public string Pattern { get; set; } = "random";

    /// <summary>Upstream's test_sizes at or below 10k (bench.rs:327-330).</summary>
    [Params(0, 1, 2, 3, 4, 6, 8, 10, 12, 17, 24, 35, 49, 70, 100, 200, 400, 900, 2_048, 4_833, 10_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => InitPool(
        i => Map(UpstreamBenchPatterns.Get(Pattern, N, i * 7919)),
        PoolCount(N));

    /// <summary>Keeps the pool's total footprint around 4 MB (the 1 KiB type would
    /// otherwise allocate hundreds of MB), clamped to [8, 1024] entries.</summary>
    private static int PoolCount(int n)
    {
        if (n <= 1)
            return 16;
        long bytesPerEntry = (long)n * Unsafe.SizeOf<T>();
        return (int)Math.Clamp(4_000_000 / Math.Max(bytesPerEntry, 1), 8, 1024);
    }

    protected static string IntToDecimal(int v) =>
        ((uint)((long)v + (1L << 31))).ToString("D10", CultureInfo.InvariantCulture);
}

[Config(typeof(QuickBenchConfig))]
public class UpstreamBenchIntMatrix : UpstreamBenchMatrix<int>
{
    protected override int[] Map(int[] source) => source;
}

[Config(typeof(QuickBenchConfig))]
public class UpstreamBenchU64Matrix : UpstreamBenchMatrix<ulong>
{
    // bench.rs extend_i32_to_u64: order-preserving.
    protected override ulong[] Map(int[] source)
    {
        var a = new ulong[source.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = (ulong)(uint)((long)source[i] + (1L << 31)) * (ulong)int.MaxValue;
        return a;
    }
}

[Config(typeof(QuickBenchConfig))]
public class UpstreamBenchStringMatrix : UpstreamBenchMatrix<string>
{
    // bench.rs FFIString transform: order-preserving 10-digit decimal.
    protected override string[] Map(int[] source)
    {
        var a = new string[source.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = IntToDecimal(source[i]);
        return a;
    }
}

[Config(typeof(QuickBenchConfig))]
public class UpstreamBench1KMatrix : UpstreamBenchMatrix<OneKibiByte>
{
    // bench.rs FFIOneKibiByte: ~1 KiB stack value, compared by the stored i32.
    protected override OneKibiByte[] Map(int[] source)
    {
        var a = new OneKibiByte[source.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = new OneKibiByte(source[i]);
        return a;
    }
}

/// <summary>Upstream's EXTRA_PATTERNS set (bench.rs:78-248) at a representative size
/// subset, for int. These probe worst cases (low cardinality, mostly-zero, sawtooth,
/// merges); the full upstream bench runs them at every size, which is thousands of
/// BDN cases — filter with --filter to narrow.</summary>
[Config(typeof(QuickBenchConfig))]
public class UpstreamExtrasBenchMatrix : SortMatrixBase<int>
{
    [Params(
        "random_d20_start_block",
        "90_one_10_zero", "90_zero_10_one", "90_zero_10_random",
        "90p_zero_10p_one", "90p_zero_10p_random_dense_neg",
        "90p_zero_10p_random_dense_pos", "90p_zero_10p_random",
        "95p_zero_5p_random", "95p_d4_5p_random", "99p_zero_1p_random",
        "saw_ascending", "saw_descending", "saws_long", "pipe_organ",
        "random__div3", "random__div5", "random__div8",
        "random_d2", "random_d3", "random_d4", "random_d8", "random_d10",
        "random_d16", "random_d32", "random_d64", "random_d128", "random_d256",
        "random_d512", "random_d1024",
        "random_p1", "random_p2", "random_p4", "random_p6", "random_p8",
        "random_p10", "random_p15", "random_p20", "random_p30", "random_p40",
        "random_p50", "random_p60", "random_p70", "random_p80", "random_p90",
        "random_p95", "random_p99",
        "random_z1_05", "random_z1_1", "random_z1_2", "random_z1_3", "random_z1_4",
        "random_z1_6", "random_z2", "random_z3", "random_z4",
        "random_s5", "random_s10", "random_s30", "random_s50", "random_s70",
        "random_s90", "random_s99",
        "random_snl_1", "random_snl_2", "random_snl_5", "random_snl_10",
        "random_m5", "random_m10", "random_m30", "random_m50", "random_m70",
        "random_m90", "random_m95", "random_m99")]
    public string Pattern { get; set; } = "random_d2";

    [Params(10, 100, 1_000, 10_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => InitPool(
        i => UpstreamBenchPatterns.Get(Pattern, N, i * 7919),
        N <= 4000 ? 1024 : 64);
}

/// <summary>127 longs = 1016 bytes making up the body of <see cref="OneKibiByte"/>.</summary>
[InlineArray(127)]
public struct LongPad127
{
    public long Element0;
}

/// <summary>~1 KiB Copy struct: upstream's FFIOneKibiByte analogue.</summary>
public struct OneKibiByte : IComparable<OneKibiByte>
{
    public int Value;
    public LongPad127 Pad;

    public OneKibiByte(int value)
    {
        Value = value;
        Pad = default;
    }

    public int CompareTo(OneKibiByte other) => Value.CompareTo(other.Value);
}
