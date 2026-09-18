using System.Runtime.InteropServices;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;

namespace Sorts.Benchmarks;

/// <summary>Shared benchmark configuration: single job, memory diagnoser, P90 tail column,
/// GitHub-flavoured markdown export. No server GC, no forced GC collection between
/// iterations (we want to observe real allocation behaviour, not hide it).</summary>
public class BenchConfig : ManualConfig
{
    /// <summary>Platform used for the job. Program.cs assigns this from
    /// RuntimeInformation.ProcessArchitecture before the switcher runs, so an
    /// Apple Silicon host runs Arm64 instead of failing to find an X64 runtime.
    /// Defaults to self-detection if Program.cs never assigned it.</summary>
    public static Platform JobPlatform { get; set; } =
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? Platform.Arm64
            : Platform.X64;

    public BenchConfig()
    {
        AddJob(Job.Default
            .WithPlatform(JobPlatform)
            .WithGcServer(false)
            .WithGcForce(false));
        AddDiagnoser(new MemoryDiagnoser(new MemoryDiagnoserConfig(displayGenColumns: true)));
        AddColumn(StatisticColumn.P90); // jitter tail visibility
        AddExporter(MarkdownExporter.GitHub);
    }
}
