using System;
using System.Runtime.CompilerServices;

namespace BaseCases;

// ============================================================================
// CASE 03 (MISS) — 相同的值三元 pick：独立编译折叠为 csel，内联进复杂循环后退化成分支
// ============================================================================
// 背景（源自 csharp-sorts src/Sorts/Driftsort/DriftSmallSort.cs：
// SmallMergeUp/Down 内联进 BidirectionalMerge，JitDisasm 反复验证）：
//
//   单元素 merge pick（值三元形态，Rust upstream merge_up 的语义）：
//       T lv = src[left]; T rv = src[right];
//       bool isR = cmp.IsLess(in rv, in lv);      // rv < lv 取右，平局取左
//       dst[outPos] = isR ? rv : lv;
//       right += isR ? 1 : 0; left += isR ? 0 : 1; outPos++;
//
//   —— 该方法体标 [NoInlining] 独立编译：csel×2、0 数据依赖分支（Good）；
//      同一方法体标 [AggressiveInlining] 内联进六游标双向 merge 循环
//      （前/后各一个 pick、奇数尾巴、平局清理）：pick 退化为 cbnz 分支（Miss）。
//      Arm64 与 x64 均如此（x64 上内联版 cmov=0）。
//
// 期望：if-conversion 不应取决于调用方的复杂度；内联后的相同 IR 钻石
//       结构同样应折叠为 csel/cmov。
// 实际：内联进多游标循环后放弃 if-conversion。
//
// 附带的工程代价：我们无法用 NoInlining 规避 —— pick 需要 4 个 byref 参数
//       （ref left/right/outPos），每个调用边界强制访存往返，实测端到端
//       比“内联 + 吃分支误预测”更慢（BaselineBench int Random 100k）。
//       所以库最终保留了分支形态，期望 JIT 侧修复。
//
// 运行（Arm64）：
//   cd basecases && dotnet build -c Release
//   env DOTNET_TieredCompilation=0 DOTNET_JitDisasm="*" DOTNET_JitStdOutFile=arm64.txt \
//     dotnet bin/Release/net10.0/basecases.dll case03
//   观察：Case03_PickNoInline 有 csel；Case03_BidirInline 内前/后 pick 均为
//         cbz/cbnz 数据依赖分支（cset x12, gt + cbnz 形态）。
//
// 本文件复现验证结果（Apple M3 Pro, .NET 10.0.5）
//   Arm64: Case03_PickNoInline   csel=2  cset=3  cbz=0    <- pick 无分支（GOOD）
//          Case03_BidirInline    csel=0  cset=6  cbz/cbnz=3 <- 前/后 pick 均分支（MISS）
//   x64（Rosetta 2）:
//          Case03_PickNoInline   cmov=2                   <- GOOD
//          Case03_BidirInline    cmov=0  jcc×28            <- 同样 MISS，两架构一致
// ============================================================================
internal static class Case03InlineContext
{
    // ---- 单元素 merge pick，两个编译策略，方法体逐字相同 ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PickInline<T, TC>(T[] src, T[] dst, ref int left, ref int right, ref int outPos, TC cmp)
        where TC : struct, IIsLess<T>
    {
        T lv = src[left];
        T rv = src[right];
        bool isR = cmp.IsLess(in rv, in lv);
        dst[outPos] = isR ? rv : lv;
        right += isR ? 1 : 0;
        left += isR ? 0 : 1;
        outPos++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PickNoInline<T, TC>(T[] src, T[] dst, ref int left, ref int right, ref int outPos, TC cmp)
        where TC : struct, IIsLess<T>
    {
        T lv = src[left];
        T rv = src[right];
        bool isR = cmp.IsLess(in rv, in lv);
        dst[outPos] = isR ? rv : lv;
        right += isR ? 1 : 0;
        left += isR ? 0 : 1;
        outPos++;
    }

    // ---- 复杂调用方：六游标双向 merge（BidirectionalMerge 的形状）----

    /// <summary>MISS：pick 内联进六游标循环 —— pick 退化为分支。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void BidirInline<T, TC>(T[] src, T[] dst, int len, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int lenDiv2 = len / 2;
        int left = 0, right = lenDiv2, outPos = 0;
        int leftRev = lenDiv2 - 1, rightRev = len - 1, outRev = len - 1;

        while (outPos < outRev)
        {
            PickInline(src, dst, ref left, ref right, ref outPos, cmp);

            if (outPos == outRev)
                break;

            // 尾部镜像 pick：较大者（平局取右）写入 dst[outRev]，游标后撤
            T lv = src[leftRev];
            T rv = src[rightRev];
            bool isL = !cmp.IsLess(in lv, in rv); // lv <= rv：取右
            dst[outRev] = isL ? rv : lv;
            rightRev -= isL ? 1 : 0;
            leftRev -= isL ? 0 : 1;
            outRev--;
        }

        if ((len & 1) == 1)
        {
            // 奇数尾巴：两个“中位”元素二选一，随后排空
            bool leftNonempty = outPos < outRev;
            T lastSrc = leftNonempty ? src[leftRev] : src[right];
            dst[outPos] = lastSrc;
            left += leftNonempty ? 1 : 0;
            right += leftNonempty ? 0 : 1;
            while (left < lenDiv2)
                dst[outPos++] = src[left++];
            while (right < len)
                dst[outPos++] = src[right++];
        }
    }

    /// <summary>GOOD 对照：同一循环，pick 走 NoInlining 调用 —— pick 独立折叠为 csel。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void BidirNoInlinePick<T, TC>(T[] src, T[] dst, int len, TC cmp)
        where TC : struct, IIsLess<T>
    {
        int lenDiv2 = len / 2;
        int left = 0, right = lenDiv2, outPos = 0;
        int leftRev = lenDiv2 - 1, rightRev = len - 1, outRev = len - 1;

        while (outPos < outRev)
        {
            PickNoInline(src, dst, ref left, ref right, ref outPos, cmp);

            if (outPos == outRev)
                break;

            T lv = src[leftRev];
            T rv = src[rightRev];
            bool isL = !cmp.IsLess(in lv, in rv);
            dst[outRev] = isL ? rv : lv;
            rightRev -= isL ? 1 : 0;
            leftRev -= isL ? 0 : 1;
            outRev--;
        }

        if ((len & 1) == 1)
        {
            bool leftNonempty = outPos < outRev;
            T lastSrc = leftNonempty ? src[leftRev] : src[right];
            dst[outPos] = lastSrc;
            left += leftNonempty ? 1 : 0;
            right += leftNonempty ? 0 : 1;
            while (left < lenDiv2)
                dst[outPos++] = src[left++];
            while (right < len)
                dst[outPos++] = src[right++];
        }
    }

    internal static void Run()
    {
        var src = Data.TwoSortedRuns(64);
        var dst = new int[src.Length];
        var rnd = new Random(13);
        long acc = 0;
        for (int iter = 0; iter < 1000; iter++)
        {
            int len = 32 + rnd.Next(src.Length / 2 - 32) * 2 + (rnd.Next(2) == 0 ? 1 : 0);
            Array.Copy(src, dst, len);
            BidirInline(src, dst, len, default(Cmp<int>));
            BidirNoInlinePick(src, dst, len, default(Cmp<int>));
            acc += dst[rnd.Next(len)];
        }
        Console.WriteLine($"case03 acc={acc}");
    }
}
