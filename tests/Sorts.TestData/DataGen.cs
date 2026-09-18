using System;
using System.Collections.Generic;

namespace Sorts.TestData;

public static class DataGen
{
    // int: full int range for Random; other patterns derived from indices.
    // RandomD20: values 0..20. RandomP5: 95% zeros + 5% full random.
    // RandomS95: 95% sorted ascending + 5% random appended at the end.
    // Zipfian: v = (int)(n / (1 + rand^2 * n)) style s≈1.0 heavy-tail.
    // FewUnique: 4 distinct values. Sawtooth: 5 teeth. OrganPipe: up then down.
    // RandomTail: sorted ascending with last 5% random.
    public static int[] Ints(Distribution d, int n, int seed)
    {
        var a = new int[n];
        switch (d)
        {
            case Distribution.Random:
            {
                var r = new Random(seed);
                for (int i = 0; i < n; i++) a[i] = r.Next();
                break;
            }
            case Distribution.Ascending:
                for (int i = 0; i < n; i++) a[i] = i;
                break;
            case Distribution.Descending:
                for (int i = 0; i < n; i++) a[i] = n - 1 - i;
                break;
            case Distribution.Sawtooth:
                for (int i = 0; i < n; i++) a[i] = i % (n / 5 + 1);
                break;
            case Distribution.OrganPipe:
                for (int i = 0; i < n; i++) a[i] = i < n / 2 ? i : n - 1 - i;
                break;
            case Distribution.RandomD20:
            {
                var r = new Random(seed);
                for (int i = 0; i < n; i++) a[i] = r.Next(21);
                break;
            }
            case Distribution.RandomP5:
            {
                var r = new Random(seed);
                for (int i = 0; i < n; i++) a[i] = r.Next(20) == 0 ? r.Next() : 0;
                break;
            }
            // RandomS95 and RandomTail share the same formula (sorted head +
            // random tail) per the plan; both labels are kept so the benchmark
            // matrix can distinguish them independently.
            case Distribution.RandomS95:
            case Distribution.RandomTail:
            {
                var r = new Random(seed);
                int head = n - n / 20; // first 95% ascending, last 5% random
                for (int i = 0; i < head; i++) a[i] = i;
                for (int i = head; i < n; i++) a[i] = r.Next();
                break;
            }
            case Distribution.Zipfian:
            {
                var r = new Random(seed);
                for (int i = 0; i < n; i++)
                    a[i] = (int)(n / (1.0 + r.NextDouble() * r.NextDouble() * n));
                break;
            }
            case Distribution.AllEqual:
                for (int i = 0; i < n; i++) a[i] = 42;
                break;
            case Distribution.FewUnique:
            {
                var r = new Random(seed);
                for (int i = 0; i < n; i++) a[i] = r.Next(4) * 1000;
                break;
            }
        }
        return a;
    }

    // double: same 12 patterns; Random spans ±1e6 with occasional NaN (1 in 1000).
    // All other patterns mirror the int values so adaptive paths stay meaningful
    // (NaN appears only in Random).
    public static double[] Doubles(Distribution d, int n, int seed)
    {
        if (d == Distribution.Random)
        {
            var r = new Random(seed);
            var a = new double[n];
            for (int i = 0; i < n; i++)
                a[i] = r.Next(1000) == 0 ? double.NaN : (r.NextDouble() * 2.0 - 1.0) * 1e6;
            return a;
        }
        int[] values = Ints(d, n, seed);
        var result = new double[n];
        for (int i = 0; i < n; i++) result[i] = values[i];
        return result;
    }

    // string: same patterns on string keys ($"item-{value:D8}"), sharing a small
    // interned pool for low-cardinality patterns so references repeat. Random and
    // Zipfian get distinct string instances built from the same value formulas.
    public static string[] Strings(Distribution d, int n, int seed)
    {
        int[] values = Ints(d, n, seed);
        var a = new string[n];
        if (d == Distribution.Random || d == Distribution.Zipfian)
        {
            for (int i = 0; i < n; i++) a[i] = $"item-{values[i]:D8}";
        }
        else
        {
            var pool = new Dictionary<int, string>();
            for (int i = 0; i < n; i++)
            {
                if (!pool.TryGetValue(values[i], out string? s))
                {
                    s = $"item-{values[i]:D8}";
                    pool[values[i]] = s;
                }
                a[i] = s;
            }
        }
        return a;
    }

    // Pair struct: (int Key, int Payload) with Payload a unique per-element
    // permutation stamp 0..n-1 — lets tests observe permutation order exactly.
    // The same pattern is applied to Key; Payload = i is the input-order stamp
    // (never shuffled).
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
