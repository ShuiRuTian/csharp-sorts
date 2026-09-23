using System;

namespace Sorts.TestData;

/// <summary>Data generators ported from sort-research-rs so the benchmark matrix is
/// comparable to upstream (not to the C# BCL): the int patterns are faithful ports of
/// <c>sort_test_tools/src/patterns.rs</c> plus the two helpers in
/// <c>benches/bench.rs</c> (random_x_percent, random_sorted/random_merge). Upstream
/// benches use a fresh random seed per run (use_random_seed_each_time); here the seed is
/// an explicit parameter so runs are reproducible.
///
/// Mapping (our name -> upstream):
///   Random      -> patterns::random                         (full i32 range, negatives included)
///   Ascending   -> patterns::ascending
///   Descending  -> patterns::descending
///   Sawtooth    -> patterns::saw_ascending(len, round(log2 len))
///   OrganPipe   -> patterns::pipe_organ
///   RandomD20   -> patterns::random_uniform(len, 0..20)     (values 0..=19)
///   RandomP5    -> random_x_percent(len, 5.0)               (95% zero, 5% random, shuffled)
///   RandomS95   -> patterns::random_sorted(len, 95.0)       (random values, first 95% sorted)
///   RandomMerge -> patterns::random_merge(len, 95.0)        (random values, both runs sorted)
///   Zipfian     -> patterns::random_zipf(len, 1.0)          (Zipf over 1..=len)
///   AllEqual    -> patterns::all_equal                      (constant 66)
///   FewUnique   -> patterns::random_uniform(len, 0..4)      (values 0..=3)
///   SortedSwap1/3 -> near-sorted: ascending + k random exchanges (CPython 3sort, fixed k)
///   RandomSnl   -> near-sorted: first n-5 sorted, last 5 random (CPython +sort / snl)
///   Plateau     -> trapezoid: rise / plateau / fall (Go plateau idea; structure + equals)
///   Stagger     -> (i*3) % n (OpenJDK/Go stagger; deterministic modular permutation)
///   Median3Killer -> CPython !sort: descending first half, ascending second half
///
/// The string and double transforms mirror upstream's i32 -> FFIString / F64NonNanCmp
/// mapping (shift_i32_to_u32, then 10-digit decimal / bit-reinterpretation), so the
/// non-int matrices keep the same order structure as the int patterns.</summary>
public static class DataGen
{
    public static int[] Ints(Distribution d, int n, int seed)
    {
        var a = new int[n];
        switch (d)
        {
            case Distribution.Random:
                FillRandom(a, seed);
                break;
            case Distribution.Ascending:
                for (int i = 0; i < n; i++) a[i] = i;
                break;
            case Distribution.Descending:
                for (int i = 0; i < n; i++) a[i] = n - 1 - i;
                break;
            case Distribution.Sawtooth:
                SawAscending(a, seed);
                break;
            case Distribution.OrganPipe:
                PipeOrgan(a, seed);
                break;
            case Distribution.RandomD20:
                FillUniform(a, 20, seed);
                break;
            case Distribution.RandomP5:
                RandomXPercent(a, 5.0, seed);
                break;
            case Distribution.RandomS95:
                RandomSorted(a, 95.0, seed);
                break;
            case Distribution.RandomMerge:
                RandomMerge(a, 95.0, seed);
                break;
            case Distribution.SortedSwap1:
                SortedSwapK(a, 1, seed);
                break;
            case Distribution.SortedSwap3:
                SortedSwapK(a, 3, seed);
                break;
            case Distribution.RandomSnl:
                RandomSnl(a, 5, seed);
                break;
            case Distribution.Plateau:
                Plateau(a);
                break;
            case Distribution.Stagger:
                Stagger(a);
                break;
            case Distribution.Median3Killer:
                Median3Killer(a);
                break;
            case Distribution.Zipfian:
                Zipf(a, 1.0, seed);
                break;
            case Distribution.AllEqual:
                for (int i = 0; i < n; i++) a[i] = 66;
                break;
            case Distribution.FewUnique:
                FillUniform(a, 4, seed);
                break;
        }
        return a;
    }

    // patterns::random: rng.gen::<i32>() — uniform over the full i32 range.
    private static void FillRandom(int[] a, int seed)
    {
        var r = new Random(seed);
        for (int i = 0; i < a.Length; i++)
            a[i] = (int)r.NextInt64(int.MinValue, (long)int.MaxValue + 1);
    }

    // patterns::random_uniform: uniform over [0, exclusiveMax).
    private static void FillUniform(int[] a, int exclusiveMax, int seed)
    {
        var r = new Random(seed);
        for (int i = 0; i < a.Length; i++) a[i] = r.Next(exclusiveMax);
    }

    // bench.rs random_x_percent: (100 - percent)% zeros + percent% random, shuffled.
    private static void RandomXPercent(int[] a, double percent, int seed)
    {
        var r = new Random(seed);
        int lenZero = SplitLen(a.Length, 100.0 - percent);
        for (int i = 0; i < lenZero; i++) a[i] = 0;
        for (int i = lenZero; i < a.Length; i++)
            a[i] = (int)r.NextInt64(int.MinValue, (long)int.MaxValue + 1);
        Shuffle(a, r);
    }

    // patterns::random_sorted: random values, first sorted_percent% sorted ascending.
    private static void RandomSorted(int[] a, double sortedPercent, int seed)
    {
        FillRandom(a, seed);
        int sortedLen = SplitLen(a.Length, sortedPercent);
        Array.Sort(a, 0, sortedLen);
    }

    // patterns::random_merge: random values, first run and the remainder each sorted.
    private static void RandomMerge(int[] a, double firstRunPercent, int seed)
    {
        FillRandom(a, seed);
        int firstRunLen = SplitLen(a.Length, firstRunPercent);
        Array.Sort(a, 0, firstRunLen);
        Array.Sort(a, firstRunLen, a.Length - firstRunLen);
    }

    // Near-sorted family. CPython 3sort idea: ascending base + k random exchanges.
    // k is FIXED (not a percentage) so the shape stays meaningful at small N — the
    // percentage variants (+sort/%sort) collapse to "fully sorted" below ~N=100.
    private static void SortedSwapK(int[] a, int k, int seed)
    {
        for (int i = 0; i < a.Length; i++) a[i] = i;
        if (a.Length < 2) return;
        var r = new Random(seed);
        for (int t = 0; t < k; t++)
        {
            int x = r.Next(a.Length);
            int y = r.Next(a.Length);
            (a[x], a[y]) = (a[y], a[x]);
        }
    }

    // patterns::random_sorted_not_last / CPython +sort: first n-k sorted, last k random.
    // Fixed k = 5 so it does not collapse at small N.
    private static void RandomSnl(int[] a, int k, int seed)
    {
        FillRandom(a, seed);
        int sorted = Math.Max(a.Length - k, 0);
        Array.Sort(a, 0, sorted);
    }

    // Go plateau idea as a trapezoid: rise to a plateau, flat, then fall. Combines
    // ordered structure with a large run of equal values (OrganPipe × FewUnique).
    private static void Plateau(int[] a)
    {
        int n = a.Length;
        int peak = Math.Max(n / 4, 1);
        for (int i = 0; i < n; i++)
            a[i] = Math.Min(Math.Min(i, n - 1 - i), peak);
    }

    // OpenJDK/Go stagger: deterministic modular permutation (mid-scale inversions).
    private static void Stagger(int[] a)
    {
        int n = a.Length;
        if (n == 0) return;
        for (int i = 0; i < n; i++) a[i] = (int)((long)i * 3 % n);
    }

    // CPython !sort (median-of-3 killer): descending first half, ascending second half.
    private static void Median3Killer(int[] a)
    {
        int half = a.Length / 2;
        for (int i = 0; i < a.Length; i++)
            a[i] = i < half ? half - 1 - i : i - half;
    }

    // patterns::saw_ascending: random values, chunks of len/saw_count sorted ascending,
    // with saw_count = round(log2(len)).
    private static void SawAscending(int[] a, int seed)
    {
        FillRandom(a, seed);
        int sawCount = (int)Math.Round(Math.Log2(Math.Max(a.Length, 1)));
        int chunkSize = a.Length / Math.Max(sawCount, 1);
        if (chunkSize < 1) chunkSize = Math.Max(a.Length, 1);
        for (int start = 0; start < a.Length; start += chunkSize)
        {
            int len = Math.Min(chunkSize, a.Length - start);
            Array.Sort(a, start, len);
        }
    }

    // patterns::pipe_organ: random values, first half ascending, second half descending.
    private static void PipeOrgan(int[] a, int seed)
    {
        FillRandom(a, seed);
        int half = a.Length / 2;
        Array.Sort(a, 0, half);
        Array.Sort(a, half, a.Length - half);
        Array.Reverse(a, half, a.Length - half);
    }

    // patterns::random_zipf(len, exponent): exact Zipf sample over 1..=len via inverse
    // transform of the discrete weights 1/k^exponent.
    private static void Zipf(int[] a, double exponent, int seed)
    {
        int n = a.Length;
        if (n == 0) return;

        var cdf = new double[n];
        double acc = 0;
        for (int k = 1; k <= n; k++)
        {
            acc += 1.0 / Math.Pow(k, exponent);
            cdf[k - 1] = acc;
        }

        var r = new Random(seed);
        for (int i = 0; i < n; i++)
        {
            double u = r.NextDouble() * acc;
            a[i] = LowerBound(cdf, u) + 1; // 1..=n
        }
    }

    // First index whose cumulative weight is >= u.
    private static int LowerBound(double[] cdf, double u)
    {
        int lo = 0, hi = cdf.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (cdf[mid] < u) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // bench.rs split_len: round(len / 100 * part_percent), half away from zero like
    // Rust's f64::round.
    private static int SplitLen(int len, double partPercent) =>
        (int)Math.Round(len / 100.0 * partPercent, MidpointRounding.AwayFromZero);

    private static void Shuffle(int[] a, Random r)
    {
        for (int i = a.Length - 1; i > 0; i--)
        {
            int j = r.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    // bench.rs shift_i32_to_u32: order-preserving i32 -> u32.
    private static uint ShiftI32ToU32(int val) => (uint)((long)val + (1L << 31));

    // bench.rs f64 transform (F64NonNanCmp::new): reinterpret
    // extend_i32_to_u64(val) as f64, mapping NaN to 0.0.
    private static double ToF64(int val)
    {
        ulong bits = (ulong)ShiftI32ToU32(val) * (ulong)int.MaxValue;
        double d = BitConverter.Int64BitsToDouble((long)bits);
        return double.IsNaN(d) ? 0.0 : d;
    }

    // bench.rs FFIString transform: order-preserving 10-digit decimal.
    private static string ToStr(int val) => ShiftI32ToU32(val).ToString("D10");

    public static double[] Doubles(Distribution d, int n, int seed)
    {
        int[] values = Ints(d, n, seed);
        var a = new double[n];
        for (int i = 0; i < n; i++) a[i] = ToF64(values[i]);
        return a;
    }

    public static string[] Strings(Distribution d, int n, int seed)
    {
        int[] values = Ints(d, n, seed);
        var a = new string[n];
        for (int i = 0; i < n; i++) a[i] = ToStr(values[i]);
        return a;
    }

    // Pair struct: (int Key, int Payload) with Payload a unique per-element
    // permutation stamp 0..n-1 — lets tests observe permutation order exactly.
    // Upstream has no paired-value pattern; the key patterns are the faithful ports.
    public static Pair[] Pairs(Distribution d, int n, int seed)
    {
        int[] keys = Ints(d, n, seed);
        var a = new Pair[n];
        for (int i = 0; i < n; i++) a[i] = new Pair(keys[i], i);
        return a;
    }
}

public readonly record struct Pair(int Key, int Payload) : IComparable<Pair>
{
    public int CompareTo(Pair other) => Key.CompareTo(other.Key);
}
