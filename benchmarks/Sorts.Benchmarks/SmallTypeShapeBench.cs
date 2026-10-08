using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Small-N × element-type matrix: the small-array leaf strategy is dispatched
/// by type (int -> 32 network; string/Struct16 -> 32 general; Struct128 -> 16 fallback),
/// so the int-only small-size conclusions do not extrapolate. Three types × three shapes
/// at two sizes.</summary>
public class SmallTypeShapeBench
{
    [Config(typeof(BenchConfig))]
    public class Int : SortMatrixBase<int>
    {
        [Params(Distribution.Random, Distribution.SortedSwap3, Distribution.FewUnique)]
        public Distribution Dist { get; set; }

        [Params(32, 128)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => InitPool(i => DataGen.Ints(Dist, N, Seed + i * SeedStride), PoolSize(N));
    }

    [Config(typeof(BenchConfig))]
    public class Str : SortMatrixBase<string>
    {
        [Params(Distribution.Random, Distribution.SortedSwap3, Distribution.FewUnique)]
        public Distribution Dist { get; set; }

        [Params(32, 128)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => InitPool(i => DataGen.Strings(Dist, N, Seed + i * SeedStride), PoolSize(N));
    }

    [Config(typeof(BenchConfig))]
    public class Struct16Bench : SortMatrixBase<Struct16>
    {
        [Params(Distribution.Random, Distribution.SortedSwap3, Distribution.FewUnique)]
        public Distribution Dist { get; set; }

        [Params(32, 128)]
        public int N { get; set; }

        [GlobalSetup]
        public void Setup() => InitPool(
            i =>
            {
                int[] keys = DataGen.Ints(Dist, N, Seed + i * SeedStride);
                var a = new Struct16[keys.Length];
                for (int j = 0; j < a.Length; j++) a[j] = new Struct16(keys[j], j, j);
                return a;
            },
            PoolSize(N));
    }
}
