using System;
using System.Runtime.CompilerServices;

namespace BaseCases;

// ============================================================================
// GOOD 02 — 稳定排序网络（sort4_stable）：小 T 值三元全链路 csel（对照基准）
// + 大 T 边界演示：同一个值三元网络在 40 字节结构体上退化为分支 + 全量拷贝，
// 解释库中 `if (Unsafe.SizeOf<T>() <= 16)` 分档存在的理由。
// ============================================================================
// 源自 csharp-sorts src/Sorts/Driftsort/DriftSmallSort.cs 的 Sort4Stable
//（Rust driftsort smallsort.rs:250-305 的移植）：5 次比较的最优稳定网络 ——
// 成对索引选择（c1/c2 物化为 0/1）+ min/max/unknown 值三元。
//
// 关键点：Good02_Sort4Stable 的全 csel 前提是“被选的值能放进寄存器”。
// Arm64 的 csel 只作用于通用寄存器（单个 8 字节，JIT 对 16 字节可拆两个
// csel）；超过后值选择在物理上就不可分支化 —— JIT 只能生成“分支 + 拷贝”，
// 而值三元形态的拷贝路径比 conditional-ref 形态更差（候选值先被完整复制到
// 局部/溢出槽再选择，等价于最多两次拷贝）。所以库中对大 T 保留
// conditional-ref 选择（每元素恰好一次拷贝，吃一个分支）——
// 这不是 if-conversion 的 MISS，而是 ISA 边界下的正确工程取舍。
// 本文件用 40 字节 BigStruct 实例化两种形态，实测验证该边界。
//
// Arm64 实测（库内同形方法）：csel = 11，cset = 5，数据依赖分支 = 0 ——
// 整个网络完全无分支。与 Case05（QuadSwap 分析器：0×csel）形成鲜明对照：
// 同一文件、同一比较器、同为值三元，网络形态全部转换成功。
//
// 运行（Arm64）：
//   cd basecases && dotnet build -c Release
//   env DOTNET_TieredCompilation=0 DOTNET_JitDisasm="*" DOTNET_JitStdOutFile=arm64.txt \
//     dotnet bin/Release/net10.0/basecases.dll good02
//
// 本文件复现验证结果（Apple M3 Pro, .NET 10.0.5）
//   Arm64: Sort4Stable[int]       csel=11  cset=10  cbz=1  <- 寄存器可容，11 个
//             值选择全部转换（GOOD）
//          Sort4Stable[BigStruct]  csel=3   cset=10  cbz=7  <- 值选择全部退化为
//             分支（那 3 个 csel 只是 CompareTo 结果的 0/正材料化，与结构体
//             选择无关）；且拷贝流量翻倍：34 ldr + 23 ldp / 26 str + 23 stp
//             （候选先复制进局部、选完再拷出）vs ref 版 21 ldr + 5 ldp /
//             4 str + 5 stp —— 每元素恰好一次读一次写。
//             这是 ISA 边界（csel 只能选寄存器值），不是 JIT MISS。
//          Sort4StableRef[BigStruct] csel=6（索引算术）分支=1 —— 库中大 T
//             的实际形态：每元素一次拷贝，吃分支。
//   x64（Rosetta 2，分号分过滤器实测，两架构结论完全对称）：
//          Sort4Stable[int]       cmov=11  setcc=10  jcc=3  <- GOOD（与 Arm64 对称）
//          Sort4Stable[BigStruct]  cmov=3（同为 CompareTo 材料化）jcc=11
//             <- 值选择全部分支化，内存流量 读 72 / 写 70 vs ref 版 读 17 / 写 12
//          Sort4StableRef[BigStruct] cmov=6  jcc=8   <- 与 Arm64 对称
//   结论：库中 `if (Unsafe.SizeOf<T>() <= 16)` 分档不是对 JIT 的妥协，
//   而是 csel/cmov 只能选寄存器值的硬约束下，大 T 用 conditional-ref
//   （一次拷贝）优于值三元（读写流量翻倍）的实测取舍 —— 该结论在
//   Arm64 与 x64 两个后端上同时成立。
// ============================================================================
internal static class Good02Network
{
    /// <summary>GOOD：把 src[b..b+4) 稳定排序到 dst[b..b+4)，5 次比较，
    /// 每个元素恰好拷贝一次。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Sort4Stable<T, TC>(T[] src, T[] dst, int b, TC cmp)
        where TC : struct, IIsLess<T>
    {
        ref T vBase = ref src[b];
        ref T d = ref dst[b];

        // 稳定构造两对 a <= b、c <= d
        int c1 = cmp.IsLess(in Unsafe.Add(ref vBase, 1), in vBase) ? 1 : 0;
        int c2 = cmp.IsLess(in Unsafe.Add(ref vBase, 3), in Unsafe.Add(ref vBase, 2)) ? 1 : 0;
        T a = Unsafe.Add(ref vBase, c1);
        T bb = Unsafe.Add(ref vBase, c1 ^ 1);
        T c = Unsafe.Add(ref vBase, 2 + c2);
        T dd = Unsafe.Add(ref vBase, 2 + (c2 ^ 1));

        // 比较 (a, c) 与 (b, d) 找出 min/max；因稳定排序，还须知道
        // 两个未知元素谁在左谁在右。
        // c3, c4 | min max unk_left unk_right
        //  0,  0 |  a   d    b         c
        //  0,  1 |  a   b    c         d
        //  1,  0 |  c   d    a         b
        //  1,  1 |  c   b    a         d
        bool c3 = cmp.IsLess(in c, in a);
        bool c4 = cmp.IsLess(in dd, in bb);
        T min = c3 ? c : a;
        T max = c4 ? bb : dd;
        T unkLeft = c3 ? a : (c4 ? c : bb);
        T unkRight = c4 ? dd : (c3 ? bb : c);

        // 排最后两个未知元素
        bool c5 = cmp.IsLess(in unkRight, in unkLeft);
        T lo = c5 ? unkRight : unkLeft;
        T hi = c5 ? unkLeft : unkRight;

        d = min;
        Unsafe.Add(ref d, 1) = lo;
        Unsafe.Add(ref d, 2) = hi;
        Unsafe.Add(ref d, 3) = max;
    }

    /// <summary>大 T 对照（与库中大 T 分支同形）：同样的网络，但选择全部
    /// conditional-ref —— 每个元素恰好一次拷贝，代价是分支化。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Sort4StableRef<T, TC>(T[] src, T[] dst, int b, TC cmp)
        where TC : struct, IIsLess<T>
    {
        ref T vBase = ref src[b];
        ref T d = ref dst[b];

        int c1 = cmp.IsLess(in Unsafe.Add(ref vBase, 1), in vBase) ? 1 : 0;
        int c2 = cmp.IsLess(in Unsafe.Add(ref vBase, 3), in Unsafe.Add(ref vBase, 2)) ? 1 : 0;
        ref T a = ref Unsafe.Add(ref vBase, c1);
        ref T bb = ref Unsafe.Add(ref vBase, c1 ^ 1);
        ref T c = ref Unsafe.Add(ref vBase, 2 + c2);
        ref T dd = ref Unsafe.Add(ref vBase, 2 + (c2 ^ 1));

        bool c3 = cmp.IsLess(in c, in a);
        bool c4 = cmp.IsLess(in dd, in bb);
        ref T min = ref (c3 ? ref c : ref a);
        ref T max = ref (c4 ? ref bb : ref dd);
        ref T unkLeft = ref (c3 ? ref a : ref (c4 ? ref c : ref bb));
        ref T unkRight = ref (c4 ? ref dd : ref (c3 ? ref bb : ref c));

        bool c5 = cmp.IsLess(in unkRight, in unkLeft);
        ref T lo = ref (c5 ? ref unkRight : ref unkLeft);
        ref T hi = ref (c5 ? ref unkLeft : ref unkRight);

        d = min;
        Unsafe.Add(ref d, 1) = lo;
        Unsafe.Add(ref d, 2) = hi;
        Unsafe.Add(ref d, 3) = max;
    }

    /// <summary>40 字节结构体：按首字段比较。用于展示“值放不进寄存器”时
    /// 值三元网络的物理边界。</summary>
    internal readonly struct BigStruct : IComparable<BigStruct>
    {
        private readonly int _key;
        private readonly long _p0, _p1, _p2, _p3; // 4 + 32 = 36 -> 40 bytes with padding

        public BigStruct(int key) { _key = key; _p0 = _p1 = _p2 = _p3 = key; }

        public int CompareTo(BigStruct other) => _key.CompareTo(other._key);
    }

    internal readonly struct CmpBig : IIsLess<BigStruct>
    {
        public bool IsLess(in BigStruct x, in BigStruct y) => y.CompareTo(x) > 0;
    }

    internal static void Run()
    {
        var rnd = new Random(31);
        long acc = 0;
        for (int iter = 0; iter < 2000; iter++)
        {
            var src = Data.RandomInts(64, iter);
            var dst = new int[src.Length];
            for (int b = 0; b + 4 <= src.Length; b += 4)
                Sort4Stable(src, dst, b, default(Cmp<int>));
            for (int b = 4; b <= dst.Length; b += 4)
                for (int k = 1; k < 4; k++)
                    if (dst[b - 4 + k - 1] > dst[b - 4 + k])
                        throw new Exception($"not sorted at {b - 4 + k}");
            acc += dst[rnd.Next(dst.Length)];
        }

        // 大 T：同一个网络，值三元版 vs conditional-ref 版，验证寄存器边界
        var bsrc = new BigStruct[64];
        var bdst = new BigStruct[64];
        var bdst2 = new BigStruct[64];
        for (int i = 0; i < bsrc.Length; i++)
            bsrc[i] = new BigStruct(i * 7919 % 64);
        for (int b = 0; b + 4 <= bsrc.Length; b += 4)
        {
            Sort4Stable(bsrc, bdst, b, default(CmpBig));
            Sort4StableRef(bsrc, bdst2, b, default(CmpBig));
        }
        for (int b = 0; b + 4 <= bdst.Length; b += 4)
            for (int k = 1; k < 4; k++)
            {
                if (bdst[b + k - 1].CompareTo(bdst[b + k]) > 0)
                    throw new Exception($"big not sorted at {b + k}");
                if (bdst2[b + k - 1].CompareTo(bdst2[b + k]) > 0)
                    throw new Exception($"bigRef not sorted at {b + k}");
            }
        acc += bdst[0].GetHashCode();

        Console.WriteLine($"good02 acc={acc}");
    }
}
