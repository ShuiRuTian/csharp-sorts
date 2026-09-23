using System.Linq;
using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Large-N (100k) regression guard for the driver-leaf change: only the General
/// dispatch class (string, Struct16) is affected (Struct128 is Fallback, scalars were
/// already insertion). Default accuracy so the before/after comparison is meaningful.</summary>
public class LargeTypeLeafBench
{
    [Config(typeof(BenchConfig))]
    public class Str : SortMatrixBase<string>
    {
        [Params(Distribution.Random, Distribution.RandomS95)]
        public Distribution Dist { get; set; }

        [Params(100_000)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => Init(DataGen.Strings(Dist, N, Seed));
    }

    [Config(typeof(BenchConfig))]
    public class Struct16Bench : SortMatrixBase<Struct16>
    {
        [Params(Distribution.Random, Distribution.RandomS95)]
        public Distribution Dist { get; set; }

        [Params(100_000)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => Init(DataGen.Ints(Dist, N, Seed)
            .Select(v => new Struct16(v, v, v)).ToArray());
    }
}
