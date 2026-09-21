using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Ring size = InvocationCount (invocations per iteration). One constant shared
/// by every benchmark class so the fresh-data math in RingData's doc comment holds
/// everywhere (see RingData.cs for the full derivation).
///
/// Note on OperationsPerInvoke: BenchmarkDotNet 0.15.4 removed the standalone
/// [OperationsPerInvoke] attribute (it is now a [Benchmark] property) and computes
/// totalOperations = InvocationCount × OperationsPerInvoke. One invocation here sorts
/// exactly one array, so per-operation reporting == per-sort reporting requires the
/// default OperationsPerInvoke = 1; setting it to 64 would divide every reported time
/// and allocation by 64. Hence [InvocationCount(Ring.Size)] alone.</summary>
internal static class Ring
{
    public const int Size = 64;
}

/// <summary>Shared plumbing for the three matrices: template init + the two benchmark
/// methods (Array.Sort baseline + Ipnsort). Derived classes declare the
/// [Params] and build the template in [GlobalSetup] via Init. Every invocation
/// consumes one fresh clone from the ring — no IterationSetup (see RingData.cs).</summary>
public abstract class SortMatrixBase<T> where T : IComparable<T>
{
    protected const int Seed = 20260918; // fixed for reproducibility across runs

    private RingData<T> _ring = null!;

    /// <summary>Builds the 64-slot ring from an unsorted template. Call once from
    /// GlobalSetup, after params are populated.</summary>
    protected void Init(T[] template) => _ring = new RingData<T>(template, Ring.Size);

    /// <summary>Next fresh clone — for extra benchmark methods declared by derived
    /// classes (the base's own four methods call the ring directly).</summary>
    protected T[] Next() => _ring.Next();

    [Benchmark(Baseline = true)]
    public void ArraySort_Generic() => Array.Sort(_ring.Next());

    [Benchmark]
    public void Ipnsort() => Sorts.Ipnsort.Sort(_ring.Next());
}

/// <summary>Core matrix: all 12 distributions × 2 sizes × 2 implementations — the main
/// adaptivity/robustness grid (24 parameter cases, 48 benchmark cases). The 1M
/// point was cut: 4MB working sets exceed realistic sort payloads and dominated
/// the run; the scaling curve lives in ScalingBench.</summary>
[Config(typeof(BenchConfig))]
[InvocationCount(Ring.Size)]
public class CoreMatrixBench : SortMatrixBase<int>
{
    // Enumerated [Params] (same set as Enum.GetValues<Distribution>) rather than
    // [ParamsSource]: BDN displays plain [Params] members in declaration order, so
    // "Dist: Random" precedes "N: 100000" in the case name and the documented smoke
    // filter '*CoreMatrixBench*Random*100000*' matches.
    [Params(
        Distribution.Random, Distribution.Ascending, Distribution.Descending,
        Distribution.Sawtooth, Distribution.OrganPipe, Distribution.RandomD20,
        Distribution.RandomP5, Distribution.RandomS95, Distribution.Zipfian,
        Distribution.AllEqual, Distribution.FewUnique, Distribution.RandomTail)]
    public Distribution Dist { get; set; }

    [Params(1_000, 100_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup() => Init(DataGen.Ints(Dist, N, Seed));
}
