using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Small-N × element-type matrix: the small-array leaf strategy is dispatched
/// by type (int -> 16 insertion; string/Struct16 -> 32 general; Struct128 -> fallback),
/// so the int-only small-size conclusions do not extrapolate. Three types × three shapes
/// at two sizes.</summary>
public class SmallTypeShapeBench
{
    [Config(typeof(QuickBenchConfig))]
    public class Int : SortMatrixBase<int>
    {
        [Params(Distribution.Random, Distribution.SortedSwap3, Distribution.FewUnique)]
        public Distribution Dist { get; set; }

        [Params(32, 128)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => Init(DataGen.Ints(Dist, N, Seed));
    }

    [Config(typeof(QuickBenchConfig))]
    public class Str : SortMatrixBase<string>
    {
        [Params(Distribution.Random, Distribution.SortedSwap3, Distribution.FewUnique)]
        public Distribution Dist { get; set; }

        [Params(32, 128)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => Init(DataGen.Strings(Dist, N, Seed));
    }

    [Config(typeof(QuickBenchConfig))]
    public class Struct16Bench : SortMatrixBase<Struct16>
    {
        [Params(Distribution.Random, Distribution.SortedSwap3, Distribution.FewUnique)]
        public Distribution Dist { get; set; }

        [Params(32, 128)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            int[] keys = DataGen.Ints(Dist, N, Seed);
            var a = new Struct16[keys.Length];
            for (int i = 0; i < a.Length; i++) a[i] = new Struct16(keys[i], i, i);
            Init(a);
        }
    }
}
