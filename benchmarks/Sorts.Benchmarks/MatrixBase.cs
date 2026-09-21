using System;
using BenchmarkDotNet.Attributes;
using Sorts.TestData;

namespace Sorts.Benchmarks;

/// <summary>Shared plumbing for the matrices: template init + the two benchmark methods
/// (Array.Sort baseline + Ipnsort). Derived classes declare the [Params] and build the
/// template in [GlobalSetup] via Init.
///
/// Destructive-input handling follows the dotnet/performance convention for in-place
/// sorts (the Sorting&lt;T&gt; benchmark in the "Performance Improvements in .NET 5"
/// post): [GlobalSetup] keeps an immutable unsorted template plus one working array,
/// and every [Benchmark] invocation copies the template into the working array before
/// sorting it. The copy sits inside the measured region and is identical for every
/// compared method, so the comparison stays fair.
///
/// No ring and no pinned InvocationCount here: leaving InvocationCount unset is what
/// lets BDN auto-scale invocations per iteration to its target iteration time, which in
/// turn is what fully warms the JIT for small inputs. (The former ring-of-64 with
/// [InvocationCount(Ring.Size)] pinned the count, so tiny-N cases whose short loops
/// promote slowly stopped at Tier0/partially-optimized code and reported ~10x too
/// slow.)</summary>
public abstract class SortMatrixBase<T> where T : IComparable<T>
{
    protected const int Seed = 20260918; // fixed for reproducibility across runs

    private T[] _template = null!;
    private T[] _work = null!;

    /// <summary>Stores the immutable unsorted template and allocates the working array.
    /// Call once from GlobalSetup, after params are populated.</summary>
    protected void Init(T[] template)
    {
        _template = template;
        _work = new T[template.Length];
    }

    /// <summary>The immutable unsorted template — read-only source for non-destructive
    /// benchmarks (e.g. LINQ OrderBy).</summary>
    protected T[] Template => _template;

    /// <summary>Copies the template into the working array and returns it — the
    /// per-invocation fresh-input step for destructive benchmark methods declared by
    /// derived classes (the base's own methods inline the same copy).</summary>
    protected T[] Fresh()
    {
        _template.AsSpan().CopyTo(_work);
        return _work;
    }

    [Benchmark(Baseline = true)]
    public void ArraySort_Generic()
    {
        _template.AsSpan().CopyTo(_work);
        Array.Sort(_work);
    }

    [Benchmark]
    public void Ipnsort()
    {
        _template.AsSpan().CopyTo(_work);
        Sorts.Ipnsort.Sort(_work);
    }
}

/// <summary>Core matrix: all 12 distributions × 2 sizes × 2 implementations — the main
/// adaptivity/robustness grid (24 parameter cases, 48 benchmark cases). The 1M
/// point was cut: 4MB working sets exceed realistic sort payloads and dominated
/// the run; the scaling curve lives in ScalingBench.</summary>
[Config(typeof(BenchConfig))]
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
