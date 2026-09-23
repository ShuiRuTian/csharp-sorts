using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;

namespace Sorts.Benchmarks;

/// <summary>Fast screening config for large parameter matrices: BDN's ShortRun job
/// (fewer warmup/iteration counts) instead of the default job. Used to locate losses
/// quickly; any candidate is re-confirmed with the default BenchConfig afterwards.
/// Same platform/GC settings as BenchConfig so the two stay comparable.</summary>
public class QuickBenchConfig : ManualConfig
{
    public QuickBenchConfig()
    {
        AddJob(Job.ShortRun
            .WithPlatform(BenchConfig.JobPlatform)
            .WithGcServer(false)
            .WithGcForce(false));
        AddColumn(StatisticColumn.P90);
        AddExporter(MarkdownExporter.GitHub);
    }
}
