using System.Runtime.InteropServices;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Running;
using Sorts.Benchmarks;

// Detect the process architecture at runtime instead of hardcoding X64: on an
// Apple Silicon host an X64 job fails outright (no matching runtime installed),
// so we run Arm64 there and X64 elsewhere.
BenchConfig.JobPlatform = RuntimeInformation.ProcessArchitecture switch
{
    Architecture.Arm64 => Platform.Arm64,
    _ => Platform.X64,
};

// Accepts the standard BenchmarkDotNet argument pass-through, e.g.
//   --filter '*CoreMatrixBench*Random*100000*' --job short
BenchmarkSwitcher.FromAssembly(typeof(BenchConfig).Assembly).Run(args);
