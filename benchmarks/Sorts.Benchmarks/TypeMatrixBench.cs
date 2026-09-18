using System.Linq;
using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Type matrix: five element types × four key distributions × N=100_000 — how
/// each sort behaves as element copy cost grows from 4-byte int to 128-byte struct.
/// One nested class per type (generic benchmarks do not bind well as direct params);
/// each inherits the four implementation methods from SortMatrixBase.</summary>
public class TypeMatrixBench
{
    /// <summary>int[] — the 4-byte baseline element.</summary>
    [Config(typeof(BenchConfig))]
    [InvocationCount(Ring.Size)]
    public class TypeMatrixInt : SortMatrixBase<int>
    {
        [Params(Distribution.Random, Distribution.RandomD20, Distribution.RandomS95, Distribution.Zipfian)]
        public Distribution Dist { get; set; }

        [Params(100_000)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => Init(DataGen.Ints(Dist, N, Seed));
    }

    /// <summary>double[] — 8-byte element with NaNs in the Random pattern.</summary>
    [Config(typeof(BenchConfig))]
    [InvocationCount(Ring.Size)]
    public class TypeMatrixDouble : SortMatrixBase<double>
    {
        [Params(Distribution.Random, Distribution.RandomD20, Distribution.RandomS95, Distribution.Zipfian)]
        public Distribution Dist { get; set; }

        [Params(100_000)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => Init(DataGen.Doubles(Dist, N, Seed));
    }

    /// <summary>string[] — reference element: compares cost memory traffic, pointer
    /// swaps and (for adaptive sorts) cheap movement of refs.</summary>
    [Config(typeof(BenchConfig))]
    [InvocationCount(Ring.Size)]
    public class TypeMatrixString : SortMatrixBase<string>
    {
        [Params(Distribution.Random, Distribution.RandomD20, Distribution.RandomS95, Distribution.Zipfian)]
        public Distribution Dist { get; set; }

        [Params(100_000)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => Init(DataGen.Strings(Dist, N, Seed));
    }

    /// <summary>Struct16[] — 16-byte unmanaged key+payload (see BenchTypes.cs).</summary>
    [Config(typeof(BenchConfig))]
    [InvocationCount(Ring.Size)]
    public class TypeMatrixStruct16 : SortMatrixBase<Struct16>
    {
        [Params(Distribution.Random, Distribution.RandomD20, Distribution.RandomS95, Distribution.Zipfian)]
        public Distribution Dist { get; set; }

        [Params(100_000)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => Init(DataGen.Ints(Dist, N, Seed)
            .Select(v => new Struct16(v, v, v)).ToArray());
    }

    /// <summary>Struct128[] — 128-byte unmanaged record, compare-by-Key only: copies
    /// are expensive, exercising the memmove-heavy large-element paths.</summary>
    [Config(typeof(BenchConfig))]
    [InvocationCount(Ring.Size)]
    public class TypeMatrixStruct128 : SortMatrixBase<Struct128>
    {
        [Params(Distribution.Random, Distribution.RandomD20, Distribution.RandomS95, Distribution.Zipfian)]
        public Distribution Dist { get; set; }

        [Params(100_000)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => Init(DataGen.Ints(Dist, N, Seed)
            .Select(v => new Struct128(v, v, v, v, v, v, v, v, v, v, v, v, v, v, v, v)).ToArray());
    }
}
