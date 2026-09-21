using System;

namespace BaseCases;

/// <summary>
/// basecases — RyuJIT if-conversion / csel(cmov) 代码生成调研的最小复现集。
///
/// 本轮在把 Rust 排序算法（ipnsort / driftsort / glidesort / quadsort）移植到 C#
/// （csharp-sorts 仓库）时，用 JitDisasm 逐方法对比了条件选择的代码生成。
/// 文件分两类：
///   Case0X_*.cs —— “应该优化但没有优化”的形态（MISS），适合提给 JIT 团队：
///     case01 条件 ref 表达式永不 if-conversion（Arm64/x64 均分支化）
///     case02 bool 经 Unsafe.As&lt;bool,byte&gt; 物化后，值三元选择退化回分支
///     case03 相同 pick：独立编译 csel，内联进多游标循环后退化为分支
///     case04 带边界检查的直接数组比较 + 三元内重读：不转换且重复读不消除
///     case05 乱序标志驱动的分析器方法（QuadSwap 形态）：两个后端全部分支化
///   Good0X_*.cs —— “符合期望”的形态（GOOD），作为同类 MISS 的对照基准：
///     good01 值三元 + `&gt; 0` 比较器（swap-if-less 的正确答案；
///          含一处 x64-only 的 CompareTo 分支材料化分歧记录）
///     good02 稳定排序网络（sort4_stable）：小 T 值三元全链路 csel，
///          另附 40B BigStruct 双形态对比，解释库中
///          `if (Unsafe.SizeOf<T>() <= 16)` 分档的 ISA 边界理由
///
/// 运行（单 case 或全部）：
///   cd basecases
///   dotnet run -c Release            # 跑全部（仅功能输出）
///   dotnet run -c Release -- case01  # 跑单个
///
/// Arm64 抓反汇编（JitDisasm 输出需经 DOTNET_JitStdOutFile 导出，stdout 不显示）：
///   dotnet build -c Release
///   env DOTNET_TieredCompilation=0 DOTNET_JitDisasm="*" DOTNET_JitStdOutFile=arm64.txt \
///     dotnet bin/Release/net10.0/basecases.dll case01
///   # 过滤器注意："*Case01*" 这类带类名前缀的模式匹配不到方法（实测 quirk），
///   # 用 "*" 全量再 grep，或按方法名过滤（如 "*Bool*"）。
///
/// x64 对照（Apple Silicon 经 Rosetta 2）：
///   dotnet publish -c Release -r osx-x64 --self-contained -p:PublishSingleFile=false
///   cd bin/Release/net10.0/osx-x64/publish
///   env DOTNET_TieredCompilation=0 DOTNET_JitDisasm="*SwapIfLess*" \
///     arch -x86_64 ./basecases good01
///   # 注意：x64 + Rosetta 下 "*" 全量过滤会崩溃；JitStdOutFile 会截断；
///   # 需按方法名过滤 + stdout 重定向逐个抓取（详见各 case 文件头部）。
///
/// 验证环境：Apple M3 Pro (Arm64) + Rosetta 2 (x64)，.NET 10.0.5，RyuJIT。
/// 每个 case 文件头部记录了该文件的复现验证结果（实测指令数）。
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        bool all = args.Length == 0;
        bool Has(string name) => all || Array.IndexOf(args, name) >= 0;

        if (Has("case01")) Case01RefTernary.Run();
        if (Has("case02")) Case02ByteMaterialization.Run();
        if (Has("case03")) Case03InlineContext.Run();
        if (Has("case04")) Case04DirectCompare.Run();
        if (Has("case05")) Case05QuadSwapDivergence.Run();
        if (Has("good01")) Good01ValueTernary.Run();
        if (Has("good02")) Good02Network.Run();
    }
}
