using System;
using System.Runtime.CompilerServices;

namespace BaseCases;

// ============================================================================
// CASE 05 (MISS) — 乱序标志驱动的分析器方法：两个后端都整体放弃 if-conversion
// ============================================================================
// 背景（源自 csharp-sorts src/Sorts/Quadsort/QuadsortImpl.cs 的 QuadSwap，
// JitDisasm 实测）：
//
//   QuadSwap（quad_swap 分析器：4 个乱序标志 + switch/goto 案例分析 +
//   SwapPairIf 值三元对交换 + ordered/reversed 快速路径）：
//     Arm64（M3 Pro, .NET 10.0.5）: csel = 0，cset = 16，cbz/cbnz = 21
//   同文件同比较器、同为值三元选择的同族方法却转换充分：
//     QuadSwapMerge（Arm64）: csel = 32，0 数据依赖分支
//     Sort9Optimal（Arm64） : csel = 75；Sort13Optimal: csel = 135
//   —— 说明不是“不会转换”，而是该分析器方法的标志/控制流形态让
//   if-conversion 全部放弃。
//
// 本文件是 QuadSwap 分析器循环的忠实独立移植（裁掉了 32-block ParityMerge
// 收尾，Merge8/Reverse/TailSort 为 NoInlining 桩 —— 与库中未被内联的调用点
// 一致），用于在最小工程里复现。QuadSwap 的契约是产出内部有序的 8-block
// （跨块排序由后续 merge pass 完成，不在被测方法内）；本 case 的 sanity
// 检查因此校验“每个 8-block 内部非降”。
//
// 期望：SwapPairIf 的值三元、v 标志驱动的选择均应 if-conversion 为
//       csel/cmov，与同族 merge/network 方法一致。
// 实际：两个后端都全部放弃（见下方实测），分析器主循环完全分支化。
//       注：会话中曾在库内观察到“x64 有 32 个 cmov”的跨架构分歧印象，
//       但当时的过滤器 *QuadSwap* 同时匹配 QuadSwap 与 QuadSwapMerge，
//       无法归因；本独立复现（同形方法）在 x64 下 cmov 同样为 0。
//
// 运行（Arm64）：
//   cd basecases && dotnet build -c Release
//   env DOTNET_TieredCompilation=0 DOTNET_JitDisasm="*" DOTNET_JitStdOutFile=arm64.txt \
//     dotnet bin/Release/net10.0/basecases.dll case05
//   x64（Apple Silicon 经 Rosetta 2；注意 "*" 全量过滤在 x64 下会崩，
//       需按方法名过滤逐个抓取）：
//   dotnet publish -c Release -r osx-x64 --self-contained -p:PublishSingleFile=false
//   cd bin/Release/net10.0/osx-x64/publish && env DOTNET_TieredCompilation=0 \
//     DOTNET_JitDisasm="*QuadSwapCore*" arch -x86_64 ./basecases case05
//
// 本文件复现验证结果（.NET 10.0.5）
//   Arm64: Case05_QuadSwapCore  csel=0  cset=16  cbz/cbnz=24   <- 全分支化（MISS）
//          （库内 QuadSwap 原版：csel=0 cset=16 cbz/cbnz=21，一致）
//   x64（Rosetta 2）:
//          Case05_QuadSwapCore  cmov=0  setcc=16  jcc×115      <- 同样全分支化（MISS）
//   对照：同族值三元网络 Good02_Sort4Stable —— Arm64 csel=11 / x64 cmov=11。
// ============================================================================
internal static class Case05QuadSwapDivergence
{
    // ---- 内联助手（与库中同形：AggressiveInlining + 值三元） ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GtAt<T, TC>(ref T r, int i, int j, TC cmp)
        where TC : struct, IIsLess<T>
        => cmp.IsLess(in Unsafe.Add(ref r, j), in Unsafe.Add(ref r, i));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LeAt<T, TC>(ref T r, int i, int j, TC cmp)
        where TC : struct, IIsLess<T>
        => !cmp.IsLess(in Unsafe.Add(ref r, j), in Unsafe.Add(ref r, i));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapPairIf<T>(ref T r, int i, bool disordered)
    {
        var a0 = Unsafe.Add(ref r, i);
        var a1 = Unsafe.Add(ref r, i + 1);
        Unsafe.Add(ref r, i) = disordered ? a1 : a0;
        Unsafe.Add(ref r, i + 1) = disordered ? a0 : a1;
    }

    // ---- NoInlining 桩（库中 QuadSwapMerge/QuadReversal/TailSwap 未被内联进 QuadSwap）----

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Merge8<T, TC>(T[] array, int pta, T[] swap, TC cmp)
        where TC : struct, IIsLess<T>
    {
        for (int i = 1; i < 8; i++)
        {
            T v = array[pta + i];
            int j = i - 1;
            while (j >= 0 && cmp.IsLess(in v, in array[pta + j]))
            {
                array[pta + j + 1] = array[pta + j];
                j--;
            }
            array[pta + j + 1] = v;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Reverse<T, TC>(T[] array, int start, int len, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int lo = start, hi = start + len - 1;
        while (lo < hi)
        {
            (array[lo], array[hi]) = (array[hi], array[lo]);
            lo++;
            hi--;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TailSort<T, TC>(T[] array, int pta, int rem, T[] swap, TC cmp)
        where TC : struct, IIsLess<T>
    {
        for (int i = 1; i < rem; i++)
        {
            T v = array[pta + i];
            int j = i - 1;
            while (j >= 0 && cmp.IsLess(in v, in array[pta + j]))
            {
                array[pta + j + 1] = array[pta + j];
                j--;
            }
            array[pta + j + 1] = v;
        }
    }

    /// <summary>被测方法：quad_swap 分析器循环（quadsort.c:257-414 的移植）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int QuadSwapCore<T, TC>(T[] array, T[] swap, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int nmemb = array.Length;
        ref T r = ref array[0];
        int count = nmemb / 8;
        int pta = 0, pts = 0;
        int rem = nmemb % 8;
        bool v1 = false, v2 = false, v3 = false, v4 = false;
        bool skipTail = false;

        while (count-- > 0)
        {
            v1 = GtAt(ref r, pta, pta + 1, cmp);
            v2 = GtAt(ref r, pta + 2, pta + 3, cmp);
            v3 = GtAt(ref r, pta + 4, pta + 5, cmp);
            v4 = GtAt(ref r, pta + 6, pta + 7, cmp);

            switch ((v1 ? 1 : 0) + (v2 ? 1 : 0) * 2 + (v3 ? 1 : 0) * 4 + (v4 ? 1 : 0) * 8)
            {
                case 0:
                    if (LeAt(ref r, pta + 1, pta + 2, cmp) && LeAt(ref r, pta + 3, pta + 4, cmp) && LeAt(ref r, pta + 5, pta + 6, cmp))
                        goto ordered;
                    Merge8(array, pta, swap, cmp);
                    goto block_done;

                case 15:
                    if (GtAt(ref r, pta + 1, pta + 2, cmp) && GtAt(ref r, pta + 3, pta + 4, cmp) && GtAt(ref r, pta + 5, pta + 6, cmp))
                    { pts = pta; goto reversed; }
                    goto not_ordered;

                default:
                    goto not_ordered;
            }

        not_ordered:
            // 由预计算乱序标志驱动的成对交换，随后 8-block 归并
            SwapPairIf(ref r, pta, v1); pta += 2;
            SwapPairIf(ref r, pta, v2); pta += 2;
            SwapPairIf(ref r, pta, v3); pta += 2;
            SwapPairIf(ref r, pta, v4); pta -= 6;
            Merge8(array, pta, swap, cmp);

        block_done:
            pta += 8;
            continue;

        ordered:
            // 前一个 8-block 完全有序 —— 直接处理下一个
            pta += 8;

            if (count-- > 0)
            {
                v1 = GtAt(ref r, pta, pta + 1, cmp);
                v2 = GtAt(ref r, pta + 2, pta + 3, cmp);
                v3 = GtAt(ref r, pta + 4, pta + 5, cmp);
                v4 = GtAt(ref r, pta + 6, pta + 7, cmp);

                if (v1 | v2 | v3 | v4)
                {
                    if ((v1 & v2 & v3 & v4) && GtAt(ref r, pta + 1, pta + 2, cmp) && GtAt(ref r, pta + 3, pta + 4, cmp) && GtAt(ref r, pta + 5, pta + 6, cmp))
                    { pts = pta; goto reversed; }
                    goto not_ordered;
                }
                if (LeAt(ref r, pta + 1, pta + 2, cmp) && LeAt(ref r, pta + 3, pta + 4, cmp) && LeAt(ref r, pta + 5, pta + 6, cmp))
                    goto ordered;
                Merge8(array, pta, swap, cmp);
                pta += 8;
                continue;
            }
            goto loop_tail; // count 耗尽

        reversed:
            // 前一个 8-block 完全逆序 —— 延伸下降游程
            pta += 8;

            if (count-- > 0)
            {
                // 注意：此路径下 v 标志是“有序”方向（cmp(pta+k, pta+k+1) <= 0）
                v1 = LeAt(ref r, pta, pta + 1, cmp);
                v2 = LeAt(ref r, pta + 2, pta + 3, cmp);
                v3 = LeAt(ref r, pta + 4, pta + 5, cmp);
                v4 = LeAt(ref r, pta + 6, pta + 7, cmp);

                if (v1 | v2 | v3 | v4)
                {
                    // 非逆序 —— 落到下方游程反转
                }
                else if (GtAt(ref r, pta - 1, pta, cmp) && GtAt(ref r, pta + 1, pta + 2, cmp) && GtAt(ref r, pta + 3, pta + 4, cmp) && GtAt(ref r, pta + 5, pta + 6, cmp))
                {
                    goto reversed;
                }

                Reverse(array, pts, pta - pts, cmp); // [pts, pta-1]

                if ((v1 & v2 & v3 & v4) && LeAt(ref r, pta + 1, pta + 2, cmp) && LeAt(ref r, pta + 3, pta + 4, cmp) && LeAt(ref r, pta + 5, pta + 6, cmp))
                    goto ordered;
                if (!(v1 | v2 | v3 | v4) && GtAt(ref r, pta + 1, pta + 2, cmp) && GtAt(ref r, pta + 3, pta + 4, cmp) && GtAt(ref r, pta + 5, pta + 6, cmp))
                { pts = pta; goto reversed; }

                // 成对交换：v 为“有序”标志，逆序时才交换
                SwapPairIf(ref r, pta, !v1); pta += 2;
                SwapPairIf(ref r, pta, !v2); pta += 2;
                SwapPairIf(ref r, pta, !v3); pta += 2;
                SwapPairIf(ref r, pta, !v4); pta -= 6;

                if (GtAt(ref r, pta + 1, pta + 2, cmp) || GtAt(ref r, pta + 3, pta + 4, cmp) || GtAt(ref r, pta + 5, pta + 6, cmp))
                    Merge8(array, pta, swap, cmp);
                pta += 8;
                continue;
            }

            // count 耗尽 —— Duff 式尾部检查：遇到第一个有序对即跳出
            switch (rem)
            {
                case 7: if (LeAt(ref r, pta + 5, pta + 6, cmp)) goto partial_tail; goto case 6;
                case 6: if (LeAt(ref r, pta + 4, pta + 5, cmp)) goto partial_tail; goto case 5;
                case 5: if (LeAt(ref r, pta + 3, pta + 4, cmp)) goto partial_tail; goto case 4;
                case 4: if (LeAt(ref r, pta + 2, pta + 3, cmp)) goto partial_tail; goto case 3;
                case 3: if (LeAt(ref r, pta + 1, pta + 2, cmp)) goto partial_tail; goto case 2;
                case 2: if (LeAt(ref r, pta + 0, pta + 1, cmp)) goto partial_tail; goto case 1;
                case 1: if (LeAt(ref r, pta - 1, pta + 0, cmp)) goto partial_tail; goto case 0;
                case 0:
                    Reverse(array, pts, pta + rem - pts, cmp); // [pts, pta+rem-1]
                    if (pts == 0)
                        return 1; // 整个数组是一条下降游程
                    skipTail = true;
                    goto loop_tail;
            }

        partial_tail:
            Reverse(array, pts, pta - pts, cmp); // [pts, pta-1]
            goto loop_tail;
        } // 分析器循环结束

    loop_tail:
        if (!skipTail)
            TailSort(array, pta, rem, swap, cmp);

        return 0;
    }

    internal static void Run()
    {
        var rnd = new Random(23);
        long acc = 0;
        var swap = new int[32];
        // 有序 / 逆序 / 随机三种形态都跑到（覆盖 ordered/reversed/not_ordered 路径）
        for (int iter = 0; iter < 300; iter++)
        {
            int n = 8 + rnd.Next(8) * 8 + rnd.Next(8); // [8, 135]
            var a = Data.RandomInts(n, iter);
            if (iter % 3 == 1)
                Array.Sort(a);
            if (iter % 3 == 2)
                Array.Reverse(a);

            var b = (int[])a.Clone();
            QuadSwapCore(b, swap, default(Cmp<int>));

            // 正确性 sanity：每个 8-block 及尾部应内部非降（QuadSwap 的契约：
// 产出内部有序的 8-block；跨块排序属于后续 merge pass，不在本移植内）
            for (int s = 0; s < b.Length; s += 8)
                for (int k = 1; k < Math.Min(8, b.Length - s); k++)
                    if (b[s + k - 1] > b[s + k])
                        throw new Exception($"block at {s} not sorted at {k} (iter {iter})");
            acc += b[0] + b[b.Length - 1];
        }
        Console.WriteLine($"case05 acc={acc}");
    }
}
