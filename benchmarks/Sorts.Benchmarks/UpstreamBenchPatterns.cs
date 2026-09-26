using System;
using System.Collections.Generic;
using System.Linq;

namespace Sorts.Benchmarks;

/// <summary>Faithful ports of upstream's benchmark pattern set
/// (sort-research-rs/benches/bench.rs + sort_test_tools/src/patterns.rs). Names and
/// definitions match upstream so a C# run is comparable case-for-case.
///
/// Two documented robustness deviations, both because upstream would panic:
///  - random_snl_K clamps the sorted percentage to [0, 100]; upstream's
///    random_sorted_not_last computes (1 - K/len)*100 and a negative value would panic
///    for len &lt;= K (only reachable with EXTRA_PATTERNS at tiny sizes);
///  - random_zipf is an inverse-transform over the same weights (upstream uses the `zipf`
///    crate), i.e. the same distribution family, not the same sampler.
/// Seeds are per-pattern (upstream draws one process-wide seed when not overridden),
/// so runs are reproducible; the shapes, not the values, are what align.</summary>
public static class UpstreamBenchPatterns
{
    private const int BaseSeed = 20260926;

    /// <summary>The 7 patterns upstream's bench always runs.</summary>
    public static readonly string[] Base =
    {
        "random", "random_z1", "random_d20", "random_p5", "random_s95",
        "ascending", "descending",
    };

    /// <summary>Upstream's EXTRA_PATTERNS set (bench.rs:78-248), opt-in via EXTRA_PATTERNS.</summary>
    public static readonly string[] Extras =
    {
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
        "random_m90", "random_m95", "random_m99",
    };

    /// <summary>Upstream's criterion test_sizes, filtered to our 10k cap (bench.rs:327-330).</summary>
    public static readonly int[] Sizes =
    {
        0, 1, 2, 3, 4, 6, 8, 10, 12, 17, 24, 35, 49, 70, 100, 200, 400, 900,
        2_048, 4_833, 10_000,
    };

    public static IReadOnlyList<string> All { get; } = Base.Concat(Extras).ToArray();

    private static readonly Dictionary<string, Func<int, int, int[]>> Map = Build();

    public static int[] Get(string name, int len) => Get(name, len, 0);

    /// <summary>seedOffset makes each call produce different values with the same shape
    /// (upstream gets this from a fresh rand::thread_rng seed per call); used to build the
    /// benchmark pool.</summary>
    public static int[] Get(string name, int len, int seedOffset) =>
        Map[name](len, SeedOf(name) ^ seedOffset);

    private static Dictionary<string, Func<int, int, int[]>> Build()
    {
        var m = new Dictionary<string, Func<int, int, int[]>>(StringComparer.Ordinal);
        void Add(string name, Func<int, int, int[]> f) => m[name] = f;

        // --- base ---
        Add("random", (len, s) => Random(len, s));
        Add("random_z1", (len, s) => Zipf(len, 1.0, s));
        Add("random_d20", (len, s) => Uniform(len, 20, s));
        Add("random_p5", (len, s) => RandomXPercent(len, 5.0, s));
        Add("random_s95", (len, s) => RandomSorted(len, 95.0, s));
        Add("ascending", (len, _) => Ascending(len));
        Add("descending", (len, _) => Descending(len));

        // --- extras ---
        Add("random_d20_start_block", (len, s) =>
        {
            int[] a = Uniform(len, 20, s);
            for (int i = 0; i < Math.Min(len, 100); i++) a[i] = 0;
            return a;
        });
        Add("90_one_10_zero", (len, _) => { int k = SplitLen(len, 90); var a = new int[len]; for (int i = 0; i < k; i++) a[i] = 1; return a; });
        Add("90_zero_10_one", (len, _) => { int k = SplitLen(len, 90); var a = new int[len]; for (int i = k; i < len; i++) a[i] = 1; return a; });
        Add("90_zero_10_random", (len, s) => { int k = SplitLen(len, 90); var a = new int[len]; Random(SplitLen(len, 10), s).CopyTo(a, k); return a; });
        Add("90p_zero_10p_one", (len, s) => Shuffled(OnesTail(len, SplitLen(len, 90)), s));
        Add("90p_zero_10p_random_dense_neg", (len, s) => Shuffled(Concat(new int[SplitLen(len, 90)], UniformInclusive(SplitLen(len, 10), -10, 10, s)), s));
        Add("90p_zero_10p_random_dense_pos", (len, s) => Shuffled(Concat(new int[SplitLen(len, 90)], UniformInclusive(SplitLen(len, 10), 0, 10, s)), s));
        Add("90p_zero_10p_random", (len, s) => Shuffled(Concat(new int[SplitLen(len, 90)], Random(SplitLen(len, 10), s)), s));
        Add("95p_zero_5p_random", (len, s) => Shuffled(Concat(new int[SplitLen(len, 95)], Random(SplitLen(len, 5), s)), s));
        Add("95p_d4_5p_random", (len, s) => Shuffled(Concat(Uniform(SplitLen(len, 95), 4, s), Random(SplitLen(len, 5), s)), s));
        Add("99p_zero_1p_random", (len, s) => Shuffled(Concat(new int[SplitLen(len, 99)], Random(SplitLen(len, 1), s)), s));
        Add("saw_ascending", (len, s) => Saw(len, SawCount(len), s, mixed: false, descending: false));
        Add("saw_descending", (len, s) => Saw(len, SawCount(len), s, mixed: false, descending: true));
        Add("saws_long", (len, s) => Saw(len, SawCount(len), s, mixed: true, descending: false));
        Add("pipe_organ", (len, s) => PipeOrgan(len, s));
        Add("random__div3", (len, s) => UniformInclusive(len, 0, (int)Math.Round(len / 3.0), s));
        Add("random__div5", (len, s) => UniformInclusive(len, 0, (int)Math.Round(len / 5.0), s));
        Add("random__div8", (len, s) => UniformInclusive(len, 0, (int)Math.Round(len / 8.0), s));

        foreach (int d in new[] { 2, 3, 4, 8, 10, 16, 32, 64, 128, 256, 512, 1024 })
        {
            int dLocal = d;
            Add($"random_d{d}", (len, s) => Uniform(len, dLocal, s));
        }

        foreach (double p in new[] { 1.0, 2.0, 4.0, 6.0, 8.0, 10.0, 15.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 95.0, 99.0 })
        {
            double pLocal = p;
            Add($"random_p{pLocal.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}", (len, s) => RandomXPercent(len, pLocal, s));
        }

        Add("random_z1_05", (len, s) => Zipf(len, 1.05, s));
        Add("random_z1_1", (len, s) => Zipf(len, 1.1, s));
        Add("random_z1_2", (len, s) => Zipf(len, 1.2, s));
        Add("random_z1_3", (len, s) => Zipf(len, 1.3, s));
        Add("random_z1_4", (len, s) => Zipf(len, 1.4, s));
        Add("random_z1_6", (len, s) => Zipf(len, 1.6, s));
        Add("random_z2", (len, s) => Zipf(len, 2.0, s));
        Add("random_z3", (len, s) => Zipf(len, 3.0, s));
        Add("random_z4", (len, s) => Zipf(len, 4.0, s));

        foreach (double p in new[] { 5.0, 10.0, 30.0, 50.0, 70.0, 90.0, 99.0 })
        {
            double pLocal = p;
            Add($"random_s{pLocal.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}", (len, s) => RandomSorted(len, pLocal, s));
        }

        foreach (int k in new[] { 1, 2, 5, 10 })
        {
            int kLocal = k;
            Add($"random_snl_{k}", (len, s) => RandomSorted(len, SnlPercent(len, kLocal), s));
        }

        foreach (double p in new[] { 5.0, 10.0, 30.0, 50.0, 70.0, 90.0, 99.0 })
        {
            double pLocal = p;
            Add($"random_m{pLocal.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}", (len, s) => RandomMerge(len, pLocal, s));
        }
        // Upstream quirk: its "random_m95" calls random_merge(len, 99.0); mirrored here for
        // case-for-case comparability.
        Add("random_m95", (len, s) => RandomMerge(len, 99.0, s));

        return m;
    }

    private static int SeedOf(string name)
    {
        uint h = 2166136261u;
        foreach (char c in name)
        {
            h ^= c;
            h *= 16777619u;
        }
        return (int)(h & 0x7fffffff) ^ BaseSeed;
    }

    // --- helpers (patterns.rs + bench.rs) ---

    private static int[] Random(int len, int seed)
    {
        var a = new int[len];
        var r = new Random(seed);
        for (int i = 0; i < len; i++)
            a[i] = (int)r.NextInt64(int.MinValue, (long)int.MaxValue + 1);
        return a;
    }

    private static int[] Uniform(int len, int exclusiveMax, int seed)
    {
        var a = new int[len];
        var r = new Random(seed);
        for (int i = 0; i < len; i++)
            a[i] = r.Next(exclusiveMax);
        return a;
    }

    private static int[] UniformInclusive(int len, int minInclusive, int maxInclusive, int seed)
    {
        var a = new int[len];
        var r = new Random(seed);
        for (int i = 0; i < len; i++)
            a[i] = r.Next(minInclusive, maxInclusive + 1);
        return a;
    }

    private static int[] Ascending(int len)
    {
        var a = new int[len];
        for (int i = 0; i < len; i++) a[i] = i;
        return a;
    }

    private static int[] Descending(int len)
    {
        var a = new int[len];
        for (int i = 0; i < len; i++) a[i] = len - 1 - i;
        return a;
    }

    // bench.rs random_x_percent: (100 - percent)% zeros + percent% random, shuffled.
    private static int[] RandomXPercent(int len, double percent, int seed)
    {
        var a = new int[len];
        int lenZero = SplitLen(len, 100.0 - percent);
        Random(len - lenZero, seed).CopyTo(a, lenZero);
        return Shuffled(a, seed);
    }

    // patterns.rs random_sorted: first sorted_percent% ascending.
    private static int[] RandomSorted(int len, double sortedPercent, int seed)
    {
        var a = Random(len, seed);
        int sortedLen = Math.Clamp(SplitLen(len, sortedPercent), 0, len);
        Array.Sort(a, 0, sortedLen);
        return a;
    }

    // patterns.rs random_merge: both runs ascending.
    private static int[] RandomMerge(int len, double firstRunPercent, int seed)
    {
        var a = Random(len, seed);
        int firstRun = Math.Clamp(SplitLen(len, firstRunPercent), 0, len);
        Array.Sort(a, 0, firstRun);
        Array.Sort(a, firstRun, len - firstRun);
        return a;
    }

    // patterns.rs saw_ascending / saw_descending / saw_mixed: each chunk of
    // len/max(saw_count,1) is sorted ascending; descending reverses every chunk, mixed
    // reverses each chunk by a random direction.
    private static int[] Saw(int len, int sawCount, int seed, bool mixed, bool descending)
    {
        if (len == 0)
            return Array.Empty<int>();
        var a = Random(len, seed);
        int chunk = len / Math.Max(sawCount, 1);
        if (chunk < 1)
            chunk = 1;
        var r = new Random(unchecked(seed * 31 + 7));
        for (int start = 0; start < len; start += chunk)
        {
            int l = Math.Min(chunk, len - start);
            Array.Sort(a, start, l);
            if (mixed ? r.Next(2) == 1 : descending)
                Array.Reverse(a, start, l);
        }
        return a;
    }

    // patterns.rs pipe_organ.
    private static int[] PipeOrgan(int len, int seed)
    {
        var a = Random(len, seed);
        int half = len / 2;
        Array.Sort(a, 0, half);
        Array.Sort(a, half, len - half);
        Array.Reverse(a, half, len - half);
        return a;
    }

    // patterns.rs random_zipf via the inverse transform of weights 1/k^exponent over 1..=len.
    private static int[] Zipf(int len, double exponent, int seed)
    {
        var a = new int[len];
        if (len == 0)
            return a;

        var cdf = new double[len];
        double acc = 0;
        for (int k = 1; k <= len; k++)
        {
            acc += 1.0 / Math.Pow(k, exponent);
            cdf[k - 1] = acc;
        }

        var r = new Random(seed);
        for (int i = 0; i < len; i++)
        {
            double u = r.NextDouble() * acc;
            int lo = 0, hi = len - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (cdf[mid] < u) lo = mid + 1;
                else hi = mid;
            }
            a[i] = lo + 1;
        }
        return a;
    }

    // bench.rs split_len: round(len / 100 * part_percent), half away from zero.
    private static int SplitLen(int len, double partPercent) =>
        (int)Math.Round(len / 100.0 * partPercent, MidpointRounding.AwayFromZero);

    // bench.rs random_sorted_not_last: sorted_percent = (1 - k/len) * 100, clamped
    // (upstream panics for len <= k).
    private static double SnlPercent(int len, int k) =>
        len <= 0 ? 100.0 : Math.Clamp((1.0 - (double)k / len) * 100.0, 0.0, 100.0);

    private static int SawCount(int len) => (int)Math.Round(Math.Log2(Math.Max(len, 1)));

    private static int[] OnesTail(int len, int k)
    {
        var a = new int[len];
        for (int i = k; i < len; i++) a[i] = 1;
        return a;
    }

    private static int[] Concat(int[] a, int[] b)
    {
        var r = new int[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static int[] Shuffled(int[] a, int seed)
    {
        var r = new Random(seed);
        for (int i = a.Length - 1; i > 0; i--)
        {
            int j = r.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }
}
