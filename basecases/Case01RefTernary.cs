using System;
using System.Runtime.CompilerServices;

namespace BaseCases;

// ============================================================================
// CASE 01 (MISS) — 条件 ref 表达式永远不会被 if-conversion
// ============================================================================
// 形态（swap-if-less，排序内核里到处都是；源自 csharp-sorts
// src/Sorts/Ipnsort/IpnSmallSort.cs 的 SwapIfLess 等十余处）：
//
//     ref T x = ref a[i];
//     ref T y = ref a[j];
//     bool swap = x.CompareTo(y) > 0;            // x > y：该对逆序
//     ref T lo = ref (swap ? ref y : ref x);     // <- 条件 ref 选择
//     ref T hi = ref (swap ? ref x : ref y);     // <- 条件 ref 选择
//     T tmp = hi; x = lo; y = tmp;
//
// 期望：两个 byref 选择折叠为 csel/cmov（地址选择），与下面的值选择孪生
//       形态（Case01_Val，csel×3、0 数据依赖分支）一致。
// 实际：数据依赖分支（cbz + 两侧 mov），Arm64 与 x64 均如此。库中所有
//       条件 ref “选择”都编译成这样。
//
// 疑似原因：Roslyn 把条件 ref 表达式降为 IL 控制流（brtrue + 每臂一个
//       ldloca/ldarga）；RyuJIT 对 byref 类型的选择没有任何 if-conversion
//       路径（int/float 值选择有），于是保留为分支。
//
// 参照系（Rust/LLVM 同形验证，rustc 1.97.1 -O, aarch64）：
//   上游 ipnsort smallsort.rs:294-325 的 swap_if_less 就是本形态——
//   `let left_swap = if should_swap { v_b } else { v_a };`（v_a/v_b: *mut T）。
//   LLVM 降为 select i1, ptr, ptr —— 选地址，8 字节寄存器，与 T 尺寸无关：
//     int 实例化:  csel x9, xzr, x8 / csel x8, x8, xzr + 2 ldr + 1 stp —— 0 分支
//     40B 结构体:  csel x9, x0, x8 / csel x8, x8, x0 + ldp/ldr 单次拷贝 —— 0 分支
//   即：修复本 case（byref 选择 if-conversion 为地址 csel）后，库中
//   `if (Unsafe.SizeOf<T>() <= 16)` 分档可整体删除，全尺寸统一 conditional-ref，
//   大 T 也拿到 csel + 单次拷贝（现在只能分支 + 单次拷贝）。
//   对照：Rust 若写值选择（选 40B 的值而非指针），LLVM 同样退化（tbz 分支 +
//   栈上全量拷贝）——物理约束对 LLVM 一致，Rust 上游靠“只选指针”避开。
//
// 影响：随机数据下 swap-if-less 的分支 50% 不可预测，每次 mispredict
//       ~15 cycles；排序网络/merge 内层循环被拖慢。
//
// 运行（Arm64，JitDisasm 经 DOTNET_JitStdOutFile 导出）：
//   cd basecases && dotnet build -c Release
//   env DOTNET_TieredCompilation=0 DOTNET_JitDisasm="*" DOTNET_JitStdOutFile=arm64.txt \
//     dotnet bin/Release/net10.0/basecases.dll case01
//
// 本文件复现验证结果（Apple M3 Pro, .NET 10.0.5）
//   Arm64: Case01_Ref  csel=0  cset=2  cbz=1    <- 数据依赖分支（MISS）
//          Case01_Val  csel=3  cset=2  cbz=0    <- 无分支（GOOD 对照）
//   x64（Rosetta 2）:
//          Case01_Ref  cmov=0  setcc=2 jcc×4    <- 分支（MISS，两架构一致）
//          Case01_Val  cmov=3  setcc=2          <- GOOD
// ============================================================================
internal static class Case01RefTernary
{
    /// <summary>MISS：条件 ref 选择 —— 编译为数据依赖分支。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Ref<T>(T[] a, int i, int j) where T : IComparable<T>
    {
        ref T x = ref a[i];
        ref T y = ref a[j];
        bool swap = x.CompareTo(y) > 0;
        ref T lo = ref (swap ? ref y : ref x);
        ref T hi = ref (swap ? ref x : ref y);
        T tmp = hi;
        x = lo;
        y = tmp;
    }

    /// <summary>GOOD 对照：同样的 swap-if-less，值三元 —— 折叠为 csel。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Val<T>(T[] a, int i, int j) where T : IComparable<T>
    {
        T x = a[i];
        T y = a[j];
        bool swap = x.CompareTo(y) > 0;
        a[i] = swap ? y : x;
        a[j] = swap ? x : y;
    }

    internal static void Run()
    {
        var rnd = new Random(7);
        var src = Data.RandomInts(256);
        long acc = 0;
        for (int iter = 0; iter < 1000; iter++)
        {
            var b = (int[])src.Clone();
            for (int k = 0; k + 1 < b.Length; k += 2)
            {
                int i = rnd.Next(b.Length - 1);
                Ref(b, i, i + 1);
                Val(b, i, i + 1);
            }
            acc += b[rnd.Next(b.Length)];
        }
        Console.WriteLine($"case01 acc={acc}");
    }
}
