using System;
using System.Runtime.CompilerServices;

namespace BaseCases;

// ============================================================================
// CASE 04 (MISS) — 非泛型方法中，直接数组比较产生的 bool 驱动值三元不被 if-conversion
// ============================================================================
// 背景（源自本轮探针实验 SwapInt / SwapGen 的 A/B）：同样的值三元 swap——
//   泛型 + CompareTo 形态折叠为 csel（见 Good01）；
//   非泛型 + `<` 直接比较形态（bool 直接从带边界检查的数组读取得出）
//   编译为 cset + cbz + 两次 mov。
//
// 本 case 三个变体，用于隔离变量（比较源 / 重读 / 泛型性）：
//   A. Case04_Direct  —— `bool gt = a[i+1] < a[i];`，三元里重读数组（探针原型）
//   B. Case04_Locals  —— 先取局部变量再比较，三元用局部（无重读）
//   C. Case04_CmpTo   —— 局部变量 + CompareTo > 0（与 Good01 同形）
//
// 期望：A/B/C 语义等价，都应 csel/cmov；A 中的重复数组读（每次都带边界检查）
//       还应被消除（每元素只读一次）。
// 实际：A 在两个架构上都是分支 + 重复读（MISS）；B/C 正常转换。
//       —— 即 MISS 的触发条件是“bool 直接从带边界检查的数组读得出 +
//       三元内重读”，而不是“直接 < 比较”本身。
//
// 运行（Arm64）：
//   cd basecases && dotnet build -c Release
//   env DOTNET_TieredCompilation=0 DOTNET_JitDisasm="*" DOTNET_JitStdOutFile=arm64.txt \
//     dotnet bin/Release/net10.0/basecases.dll case04
//
// 本文件复现验证结果（Apple M3 Pro, .NET 10.0.5）
//   Arm64: Case04_Direct  csel=0  cset=1  cbz=1    <- 分支 + 重复读（MISS）
//          Case04_Locals  csel=2  cset=1  cbz=0    <- 无分支（GOOD 对照）
//          Case04_CmpTo   csel=3  cset=2  cbz=0    <- 无分支（GOOD 对照）
//   x64（Rosetta 2）:
//          Case04_Direct  cmov=0                   <- 同样 MISS
//          Case04_Locals  cmov=2                   <- GOOD
//          Case04_CmpTo   cmov=3                   <- GOOD
// ============================================================================
internal static class Case04DirectCompare
{
    /// <summary>变体 A：bool 来自直接数组比较，三元内重读数组（探针原型）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Direct(int[] a, int i)
    {
        bool gt = a[i + 1] < a[i];
        int x = gt ? a[i + 1] : a[i];
        int y = gt ? a[i] : a[i + 1];
        a[i] = x;
        a[i + 1] = y;
    }

    /// <summary>变体 B：先取局部，直接 `<` 比较，三元用局部。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Locals(int[] a, int i)
    {
        int x = a[i];
        int y = a[i + 1];
        bool gt = y < x;
        a[i] = gt ? y : x;
        a[i + 1] = gt ? x : y;
    }

    /// <summary>变体 C：局部变量 + CompareTo &gt; 0（Good01 同形，作分水岭）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void CmpTo<T>(T[] a, int i) where T : IComparable<T>
    {
        T x = a[i];
        T y = a[i + 1];
        bool gt = x.CompareTo(y) > 0;
        a[i] = gt ? y : x;
        a[i + 1] = gt ? x : y;
    }

    internal static void Run()
    {
        var rnd = new Random(17);
        var src = Data.RandomInts(256);
        long acc = 0;
        for (int iter = 0; iter < 1000; iter++)
        {
            var b = (int[])src.Clone();
            var g = (int[])src.Clone();
            for (int k = 0; k + 1 < b.Length; k += 2)
            {
                int i = rnd.Next(b.Length - 1);
                Direct(b, i);
                Locals(b, i);
                CmpTo(g, i);
            }
            acc += b[rnd.Next(b.Length)] + g[rnd.Next(g.Length)];
        }
        Console.WriteLine($"case04 acc={acc}");
    }
}
