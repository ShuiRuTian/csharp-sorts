using System;
using System.Runtime.CompilerServices;

namespace BaseCases;

// ============================================================================
// CASE 02 (MISS) — bool 经 Unsafe.As<bool, byte> 物化后，值三元选择退化回分支
// ============================================================================
// 背景：为了让“条件游标推进”做到真正的无分支（`right += isR ? 1 : 0` 在某些
// 形态下会被 Arm64 if-conversion 还原成分支），曾用
// `int g = Unsafe.As<bool, byte>(ref cond); right += g;` 物化 0/1 ——
// 这在独立探针中确实是纯算术（cset + add）。
//
// 但探针 A/B 发现（本 case 逐字还原该实验）：
//
//   形态 A（Good）：bool 直接驱动选择     -> cset + csel×2，0 条件分支
//   形态 B（Miss）：byte 物化 + `!= 0`    -> cbz + movs，数据依赖分支
//
// 期望：`g != 0 ? a1 : a0` 与 `gt ? a1 : a0` 语义等价（Unsafe.As 是无成本
//       位重释），都应折叠为 csel。
// 实际：取地址物化（byref 传给 Unsafe.As）使 bool 进入可寻址本地，
//       选择失去 if-conversion 资格。
//
// 注意：该 MISS 对上下文敏感 —— 换成数组下标访问 + 结构体比较器的形态时
//       两种写法都能转换；必须与探针完全同形（ref 参数、
//       (a0 as IComparable&lt;T&gt;).CompareTo、经 ref 的两步写入）才会退化。
//
// 运行（Arm64）：
//   cd basecases && dotnet build -c Release
//   env DOTNET_TieredCompilation=0 DOTNET_JitDisasm="*" DOTNET_JitStdOutFile=arm64.txt \
//     dotnet bin/Release/net10.0/basecases.dll case02
//
// 本文件复现验证结果（Apple M3 Pro, .NET 10.0.5）
//   Arm64: Case02_Bool  csel=3  cset=2  cbz=0    <- 无分支（GOOD 对照）
//          Case02_Byte  csel=1  cset=2  cbz=1    <- 一个选择退化为分支（MISS）
//   x64（Rosetta 2，经方法名过滤逐个抓取）：
//          Case02_Bool  cmov=3  jcc=0            <- GOOD
//          Case02_Byte  cmov=1  jcc=1            <- 同样退化（两架构一致）
// ============================================================================
internal static class Case02ByteMaterialization
{
    /// <summary>GOOD 对照（探针形态 2）：bool 直接驱动两个值三元 —— cset + csel。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Bool<T>(ref T pta)
    {
        T a0 = pta;
        T a1 = Unsafe.Add(ref pta, 1);
        bool gt = (a0 as IComparable<T>)!.CompareTo(a1) > 0;
        pta = gt ? a1 : a0;
        Unsafe.Add(ref pta, 1) = gt ? a0 : a1;
    }

    /// <summary>MISS（探针形态 3）：同一逻辑，但 bool 先经 Unsafe.As&lt;bool, byte&gt;
    /// 物化，选择条件变为 `g != 0` —— 退化为分支。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Byte<T>(ref T pta)
    {
        T a0 = pta;
        T a1 = Unsafe.Add(ref pta, 1);
        bool gt = (a0 as IComparable<T>)!.CompareTo(a1) > 0;
        int g = Unsafe.As<bool, byte>(ref gt);
        T x = g != 0 ? a1 : a0;
        T y = g != 0 ? a0 : a1;
        pta = x;
        Unsafe.Add(ref pta, 1) = y;
    }

    internal static void Run()
    {
        var rnd = new Random(11);
        var src = Data.RandomInts(256);
        long acc = 0;
        for (int iter = 0; iter < 1000; iter++)
        {
            var b = (int[])src.Clone();
            for (int k = 0; k + 1 < b.Length; k += 2)
            {
                int i = rnd.Next(b.Length - 1);
                Bool(ref b[i]);
                Byte(ref b[i + 1]);
            }
            acc += b[rnd.Next(b.Length)];
        }
        Console.WriteLine($"case02 acc={acc}");
    }
}
