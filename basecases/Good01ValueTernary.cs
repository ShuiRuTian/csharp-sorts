using System;
using System.Runtime.CompilerServices;

namespace BaseCases;

// ============================================================================
// GOOD 01 — 值三元 + 泛型 CompareTo > 0：if-conversion 符合期望（对照基准）
// ============================================================================
// 这是 Case01_Ref / Case02_Byte / Case04_Direct 各 MISS 形态的“正确答案”：
//
//   - 泛型 T + IComparable<T> 约束（去虚化为 int.CompareTo），
//   - 谓词写作 `x.CompareTo(y) > 0`（大于方向；注意：`< 0` 方向会被位技巧
//     化成 lsr，破坏与 csel 的关联 —— 见 csharp-sorts Comparers.cs 的说明），
//   - 条件为普通 bool 本地（不取地址、不物化），
//   - 选择为值三元（不是条件 ref）。
//
// Arm64 实测（探针 SwapGen 同形）：cmp + cset + csel×2，0 数据依赖分支。
// 这就是各 MISS case 期望达到的代码生成。
//
// 运行（Arm64）：
//   cd basecases && dotnet build -c Release
//   env DOTNET_TieredCompilation=0 DOTNET_JitDisasm="*" DOTNET_JitStdOutFile=arm64.txt \
//     dotnet bin/Release/net10.0/basecases.dll good01
//
// 本文件复现验证结果（Apple M3 Pro, .NET 10.0.5）
//   Arm64: Good01_SwapIfLess  csel=3  cset=2  cbz=0    <- 无分支（GOOD）
//          Good01_SelectIndex csel=0  cset=2  cbz=0    <- 纯算术无分支（GOOD）
//   x64（Rosetta 2）:
//          Good01_SwapIfLess  cmov=3  setcc=2  jcc=2   <- 两个 jae 均为边界
//             检查，选择全部 cmov，数据依赖分支 0（GOOD，与 Arm64 对称）
//          Good01_SelectIndex cmov=0  setcc=2  jcc=3   <- 其中 2 个为边界检查，
//             另 1 个是数据依赖 jge：CompareTo 结果在 x64 上经分支材料化
//             （jge IG06 / mov edi,-1 两路汇合），而 Arm64 同形为 cset 纯算术。
//             这是本 GOOD 基准中唯一的跨架构分歧，值得顺带提给 JIT 团队。
//             —— 注：库内实际游标算术不依赖此形态，无性能影响。
// ============================================================================
internal static class Good01ValueTernary
{
    /// <summary>GOOD：swap-if-less 的期望形态 —— 值三元 + `&gt; 0` 比较。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SwapIfLess<T>(T[] a, int i, int j) where T : IComparable<T>
    {
        T x = a[i];
        T y = a[j];
        bool swap = x.CompareTo(y) > 0; // x > y：该对逆序
        a[i] = swap ? y : x;
        a[j] = swap ? x : y;
    }

    /// <summary>GOOD：同一条件还可以直接物化 0/1 供游标算术使用（排序网络
    /// 索引选择），同样折叠为 csel（库中 Sort9Optimal 同形，75×csel/0 分支）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int SelectIndex<T>(T[] a, int i, int j) where T : IComparable<T>
    {
        int less = a[j].CompareTo(a[i]) > 0 ? 1 : 0; // a[j] > a[i]：j 是较小者
        int pick = less * j + (1 - less) * i;
        return pick + less; // 返回选中下标并推进 1 位（模拟游标算术）
    }

    internal static void Run()
    {
        var rnd = new Random(29);
        var src = Data.RandomInts(256);
        long acc = 0;
        for (int iter = 0; iter < 1000; iter++)
        {
            var b = (int[])src.Clone();
            for (int k = 0; k + 1 < b.Length; k += 2)
            {
                int i = rnd.Next(b.Length - 1);
                SwapIfLess(b, i, i + 1);
                acc += SelectIndex(b, i, i + 1);
            }
        }
        Console.WriteLine($"good01 acc={acc}");
    }
}
