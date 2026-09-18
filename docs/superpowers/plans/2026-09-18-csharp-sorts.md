# csharp-sorts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port quadsort / glidesort / driftsort to C# (.NET 10) as `Array.Sort` replacement candidates, performance-first, with differential correctness + determinism tests, a layered BenchmarkDotNet matrix, and subagent review.

**Architecture:** Three self-contained static sort classes (`QuadSort`, `GlideSort`, `DriftSort`) sharing a comparer kernel (struct-generic `IIsLess<T>` for JIT specialization). Each algorithm ports its upstream architecture faithfully (`upstream/` vendored locally) with C# performance idioms (Span, `ref`/`Unsafe.Add`, `AggressiveInlining`, `GC.AllocateUninitializedArray`). Tests are a differential harness (permutation oracle + determinism) written before implementation; benchmarks cover 3 layered matrices.

**Tech Stack:** .NET 10 SDK, C# (LangVersion latest), xUnit 2.9+, BenchmarkDotNet 0.15+. No other dependencies.

**Spec:** `docs/superpowers/specs/2026-09-18-csharp-sorts-design.md`

## Global Constraints

- Target framework `net10.0` for all projects (spec §11).
- `Directory.Build.props`: `<Nullable>enable</Nullable>`, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, `<Optimize>true</Optimize>`, `<LangVersion>latest</LangVersion>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- Sorting contract (spec §2): correct ascending output (multiset-equal to input), deterministic (same input → byte-identical result every run), stability NOT promised, `ArgumentNullException` for null array, `ArgumentOutOfRangeException` for bad range, n ≤ 1 returns immediately.
- Performance is the first-priority metric (spec §1); every porting decision favors throughput.
- Upstream sources vendored at `upstream/{quadsort,glidesort,driftsort}/` with their LICENSE files committed (quadsort: public domain/unlicense; glidesort & driftsort: MIT OR Apache-2.0). Every ported source file carries an attribution header naming the upstream project, authors (Igor van den Hoven for quadsort; Orson Peters & Lukas Bergdoll for glidesort/driftsort), and license. README carries the same attribution.
- Key upstream constants (verified against vendored sources): glidesort `SMALL_SORT = 48`, `FULL_ALLOC_MAX_BYTES = 1MiB`, `HALF_ALLOC_MAX_BYTES = 1GiB` (`upstream/glidesort/src/lib.rs:24-35`); driftsort `MAX_LEN_ALWAYS_INSERTION_SORT = 20`, `MAX_FULL_ALLOC_BYTES = 8_000_000`, `MIN_SMALL_SORT_SCRATCH_LEN = 50`, smallsort threshold 32 for unmanaged ≤16-byte T else 16 (`upstream/driftsort/src/lib.rs:60,84` + `smallsort.rs:48,54`); quadsort `QUAD_CACHE = 262144`, swap cap growth from 4,194,304 (`upstream/quadsort/src/quadsort.h:31`, `quadsort.c` `quadsort()`).
- Prefer safe Span/`ref`-based code; raw pointers only where `MemoryMarshal`/`Unsafe` genuinely needs them.
- All commands run from repo root `/Users/songgao/Desktop/meituan_repo/csharp-sorts`.
- Commits after every green step; commit messages end with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.

---

### Task 1: Solution scaffold + vendored upstream + SDK check

**Files:**
- Create: `Directory.Build.props`, `csharp-sorts.sln`, `src/Sorts/Sorts.csproj`, `tests/Sorts.Tests/Sorts.Tests.csproj`, `tests/Sorts.TestData/Sorts.TestData.csproj`, `benchmarks/Sorts.Benchmarks/Sorts.Benchmarks.csproj`, `.gitignore`
- Keep (already present): `docs/superpowers/specs/2026-09-18-csharp-sorts-design.md`, `upstream/` (remove nested `.git` dirs)

**Interfaces:**
- Produces: solution layout that all later tasks build on; `src/Sorts` produces `Sorts.dll`; `tests/Sorts.TestData` is a shared non-packable class lib referenced by both tests and benchmarks.

- [ ] **Step 1: Verify SDK is current; update if not**

Run: `dotnet --version && dotnet --list-sdks`
Expected: `10.0.201` or newer 10.x. If a newer 10.x SDK exists, install via:
`curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0` then prepend the reported install dir to PATH in that shell. Record the SDK version used in `README.md` later (Task 15).

- [ ] **Step 2: Remove nested .git dirs from vendored repos**

Run: `find upstream -name ".git" -type d -prune -exec rm -rf {} +`
Then `ls upstream/*/LICENSE upstream/glidesort/Cargo.toml` to confirm licenses survive.

- [ ] **Step 3: Write project files**

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Optimize>true</Optimize>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

`src/Sorts/Sorts.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Sorts</RootNamespace>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

`tests/Sorts.TestData/Sorts.TestData.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

`tests/Sorts.Tests/Sorts.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Sorts\Sorts.csproj" />
    <ProjectReference Include="..\Sorts.TestData\Sorts.TestData.csproj" />
  </ItemGroup>
</Project>
```

`benchmarks/Sorts.Benchmarks/Sorts.Benchmarks.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.15.4" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Sorts\Sorts.csproj" />
    <ProjectReference Include="..\..\tests\Sorts.TestData\Sorts.TestData.csproj" />
  </ItemGroup>
</Project>
```

If the exact package versions above are not the latest stable on NuGet at execution time, use the newest stable instead (`dotnet add package` reports it).

`.gitignore`:
```
bin/
obj/
artifacts/
*.user
BenchmarkDotNet.Artifacts/
```

- [ ] **Step 4: Create solution and wire projects**

Run:
```bash
dotnet new sln -n csharp-sorts
dotnet sln add src/Sorts tests/Sorts.TestData tests/Sorts.Tests benchmarks/Sorts.Benchmarks
```

- [ ] **Step 5: Verify build**

Run: `dotnet build -c Release`
Expected: BUILD SUCCEEDED, 0 warnings (TreatWarningsAsErrors on).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: solution scaffold + vendored upstream sources"
```

---

### Task 2: Shared test data generators (Distributions)

**Files:**
- Create: `tests/Sorts.TestData/Distribution.cs`, `tests/Sorts.TestData/DataGen.cs`

**Interfaces:**
- Produces (used by tests Task 3+ and benchmarks Task 14+):
  - `public enum Distribution { Random, Ascending, Descending, Sawtooth, OrganPipe, RandomD20, RandomP5, RandomS95, Zipfian, AllEqual, FewUnique, RandomTail }`
  - `public static class DataGen` with `public static int[] Ints(Distribution d, int n, int seed)`, same-shaped `Doubles`, `Strings`, `Pairs`.

- [ ] **Step 1: Write the generator library**

`tests/Sorts.TestData/Distribution.cs`:
```csharp
namespace Sorts.TestData;

public enum Distribution
{
    Random, Ascending, Descending, Sawtooth, OrganPipe, RandomD20,
    RandomP5, RandomS95, Zipfian, AllEqual, FewUnique, RandomTail
}
```

`tests/Sorts.TestData/DataGen.cs` — implement with a seeded `Random(seed)` (deterministic; fixed seeds make tests reproducible). All methods must be pure (no shared state):

```csharp
namespace Sorts.TestData;

public static class DataGen
{
    // int: full int range for Random; other patterns derived from indices.
    // RandomD20: values 0..20. RandomP5: 95% zeros + 5% full random.
    // RandomS95: 95% sorted ascending + 5% random appended at the end.
    // Zipfian: v = (int)(n / (1 + rand^2 * n)) style s≈1.0 heavy-tail.
    // FewUnique: 4 distinct values. Sawtooth: 5 teeth. OrganPipe: up then down.
    // RandomTail: sorted ascending with last 5% random.
    public static int[] Ints(Distribution d, int n, int seed) { ... }

    // double: same 12 patterns; Random spans ±1e6 with occasional NaN (1 in 1000).
    public static double[] Doubles(Distribution d, int n, int seed) { ... }

    // string: same patterns on string keys ($"item-{value:D8}"), sharing a small
    // interned pool for low-cardinality patterns so references repeat.
    public static string[] Strings(Distribution d, int n, int seed) { ... }

    // Pair struct: (int Key, int Payload) with Payload a unique per-element
    // permutation stamp 0..n-1 — lets tests observe permutation order exactly.
    public static Pair[] Pairs(Distribution d, int n, int seed) { ... }
}

public readonly record struct Pair(int Key, int Payload) : IComparable<Pair>
{
    public int CompareTo(Pair other) => Key.CompareTo(other.Key);
}
```

Implementation notes for the implementer (each distribution ≤ 10 lines; derive from `Random`):
- `Ascending` = `i`; `Descending` = `n-1-i`; `AllEqual` = 42; `FewUnique` = `r.Next(4) * 1000`; `Sawtooth` = `i % (n/5+1)`; `OrganPipe` = `i < n/2 ? i : n-1-i`; `RandomD20` = `r.Next(21)`; `RandomP5` = `r.Next(20)==0 ? r.Next() : 0`; `RandomS95` = first 95% ascending, last 5% random; `RandomTail` = first 95% ascending, last 5% random appended (both differ only in which segment is random — keep both, they hit different code paths in lazy-merge sorts); `Zipfian` = `(int)(n / (1.0 + r.NextDouble()*r.NextDouble()*n))`; `Random` = `r.Next()`.
- `Pairs` applies the same pattern to `Key` and sets `Payload = i` (input order stamp), shuffled deterministically afterwards for Random-family patterns? NO — do not shuffle: `Payload = i` IS the input-order stamp; distributions that produce ordered keys with ordered stamps are fine (they exercise "sorted input" paths; the random patterns carry the permutation coverage).
- `Doubles` NaN: only in `Random` (1/1000 chance) — other patterns stay NaN-free so adaptive paths stay meaningful.

- [ ] **Step 2: Write sanity tests for the generators themselves**

Create `tests/Sorts.Tests/DataGenTests.cs`:
```csharp
using Sorts.TestData;
using Xunit;

public class DataGenTests
{
    [Fact]
    public void DeterministicAcrossCalls()
    {
        Assert.Equal(DataGen.Ints(Distribution.Random, 1000, 7), DataGen.Ints(Distribution.Random, 1000, 7));
    }

    [Fact]
    public void AscendingIsAscending() =>
        Assert.Equal(Enumerable.Range(0, 500).ToArray(), DataGen.Ints(Distribution.Ascending, 500, 1));

    [Fact]
    public void FewUniqueHasFourDistinctValues() =>
        Assert.Equal(4, DataGen.Ints(Distribution.FewUnique, 10_000, 3).Distinct().Count());

    [Fact]
    public void PairPayloadsAreInputOrderStamps() =>
        Assert.Equal(Enumerable.Range(0, 300), DataGen.Pairs(Distribution.RandomD20, 300, 5).Select(p => p.Payload));

    [Theory]
    [InlineData(Distribution.Random)]
    [InlineData(Distribution.Zipfian)]
    public void NonEmptyForAllTypes(Distribution d)
    {
        Assert.NotEmpty(DataGen.Ints(d, 100, 9));
        Assert.NotEmpty(DataGen.Doubles(d, 100, 9));
        Assert.NotEmpty(DataGen.Strings(d, 100, 9));
        Assert.NotEmpty(DataGen.Pairs(d, 100, 9));
    }
}
```

- [ ] **Step 3: Run tests, verify pass**

Run: `dotnet test -c Release`
Expected: all DataGenTests PASS.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "test: shared distribution generators for tests and benchmarks"
```

---

### Task 3: Comparer kernel + sort-class stubs + full differential harness (RED)

**Files:**
- Create: `src/Sorts/Comparers.cs`, `src/Sorts/QuadSort.cs`, `src/Sorts/GlideSort.cs`, `src/Sorts/DriftSort.cs`, `src/Sorts/Properties/AssemblyInfo.cs` (InternalsVisibleTo)
- Create: `tests/Sorts.Tests/SortContract.cs`, `tests/Sorts.Tests/QuadSortTests.cs`, `tests/Sorts.Tests/GlideSortTests.cs`, `tests/Sorts.Tests/DriftSortTests.cs`, `tests/Sorts.Tests/ApiContractTests.cs`

**Interfaces:**
- Produces (every later task depends on these exact names):
  - `internal interface IIsLess<T> { bool IsLess(in T x, in T y); }` — the kernel comparison: true iff x < y.
  - `internal readonly struct ComparableCmp<T> : IIsLess<T> where T : IComparable<T>` — `x.CompareTo(y) < 0`.
  - `internal readonly struct InterfaceCmp<T> : IIsLess<T>` — wraps `IComparer<T>` (null → `Comparer<T>.Default`): `_c.Compare(x, y) < 0`.
  - `internal readonly struct ComparisonCmp<T> : IIsLess<T>` — wraps `Comparison<T>` delegate.
  - Public API (identical shape ×3 classes; `Sorts` namespace):
    ```csharp
    public static class QuadSort // GlideSort, DriftSort same shape
    {
        public static void Sort<T>(T[] array) where T : IComparable<T>;
        public static void Sort<T>(T[] array, int index, int length) where T : IComparable<T>;
        public static void Sort<T>(T[] array, IComparer<T>? comparer);
        public static void Sort<T>(T[] array, Comparison<T> comparison);
        public static void Sort<T, TC>(Span<T> span, TC cmp) where TC : struct, IComparer<T>;
        internal static void SortSpan<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>; // THE kernel each algorithm task implements
    }
    ```
  - Test harness: `SortContractTests<T>` abstract base with abstract members `Sort(T[])`, `Gen(Distribution, int, int)`, `Canonical` (`Comparison<T>` — total order over T incl. payload tiebreak), and theories `IsCorrectPermutation`, `IsDeterministic`.

- [ ] **Step 1: Write comparer kernel**

`src/Sorts/Comparers.cs`:
```csharp
namespace Sorts;

/// <summary>Kernel comparison contract: true iff x &lt; y. Struct implementations get
/// JIT value-type specialization (devirtualized + inlined) — the C# equivalent of
/// Rust monomorphization / C cmp macros.</summary>
internal interface IIsLess<T>
{
    bool IsLess(in T x, in T y);
}

internal readonly struct ComparableCmp<T> : IIsLess<T> where T : IComparable<T>
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool IsLess(in T x, in T y) => x.CompareTo(y) < 0;
}

internal readonly struct InterfaceCmp<T> : IIsLess<T>
{
    private readonly IComparer<T> _cmp;
    public InterfaceCmp(IComparer<T>? cmp) => _cmp = cmp ?? Comparer<T>.Default;
    public bool IsLess(in T x, in T y) => _cmp.Compare(x, y) < 0;
}

internal readonly struct ComparisonCmp<T> : IIsLess<T>
{
    private readonly Comparison<T> _cmp;
    public ComparisonCmp(Comparison<T> cmp) => _cmp = cmp;
    public bool IsLess(in T x, in T y) => _cmp(x, y) < 0;
}
```

- [ ] **Step 2: Write the three sort-class stubs ( NotImplementedException kernels → whole suite RED)**

Each of `src/Sorts/QuadSort.cs`, `src/Sorts/GlideSort.cs`, `src/Sorts/DriftSort.cs` (attribution header at top of each — see Global Constraints):
```csharp
// Ported from <upstream repo URL>, <license line>, by Igor van den Hoven / Orson Peters & Lukas Bergdoll.
// C# port <year> — architecture-faithful, C# performance idioms.
namespace Sorts;

public static class QuadSort
{
    public static void Sort<T>(T[] array) where T : IComparable<T> =>
        Sort(array, 0, array is null ? 0 : array.Length);

    public static void Sort<T>(T[] array, int index, int length) where T : IComparable<T>
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, array.Length - index);
        if (length < 2) return;
        SortSpan<T, ComparableCmp<T>>(array.AsSpan(index, length), scratch: default, new ComparableCmp<T>());
    }

    public static void Sort<T>(T[] array, IComparer<T>? comparer) { /* same guard pattern; InterfaceCmp */ }
    public static void Sort<T>(T[] array, Comparison<T> comparison) { /* same guard pattern; ComparisonCmp */ }
    public static void Sort<T, TC>(Span<T> span, TC cmp) where TC : struct, IComparer<T>
        => throw new NotImplementedException(); // adapter wiring lands with kernel

    internal static void SortSpan<T, TC>(Span<T> v, Span<T> scratch, TC cmp) where TC : struct, IIsLess<T>
        => throw new NotImplementedException();
}
```
(The scratch-allocation policy differs per algorithm and lands in its own task; the stub just throws.)

`src/Sorts/Properties/AssemblyInfo.cs`:
```csharp
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Sorts.Tests")]
```

- [ ] **Step 3: Write the differential harness (the real oracle — this is the suite every algorithm task must turn green)**

`tests/Sorts.Tests/SortContract.cs`:
```csharp
using Sorts.TestData;
using Xunit;

public abstract class SortContractTests<T> where T : IComparable<T>
{
    protected abstract void Sort(T[] array);
    protected abstract T[] Gen(Distribution d, int n, int seed);

    /// <summary>Total order over T including tiebreak (payload) — defines "same permutation".</summary>
    protected abstract Comparison<T> Canonical { get; }

    public static TheoryData<Distribution, int, int> SizeSweep =>
        BuildCases(dists: AllDists, sizes: Enumerable.Range(0, 65), seed: 1234);

    public static TheoryData<Distribution, int, int> RandomSizes =>
        BuildCases(dists: new[] { Distribution.Random, Distribution.RandomD20,
                                  Distribution.RandomS95, Distribution.Zipfian },
                   sizes: new[] { 65, 100, 511, 1000, 4096, 20_000 },
                   seedCount: 200); // seeds 1..200

    private static TheoryData<Distribution, int, int> BuildCases(
        Distribution[] dists, IEnumerable<int> sizes, int seed) =>
        BuildCases(dists, sizes, new[] { seed });

    private static TheoryData<Distribution, int, int> BuildCases(
        Distribution[] dists, IEnumerable<int> sizes, IEnumerable<int> seeds)
    {
        var data = new TheoryData<Distribution, int, int>();
        foreach (var d in dists)
            foreach (var n in sizes)
                foreach (var s in seeds)
                    data.Add(d, n, s);
        return data;
    }

    private static Distribution[] AllDists => Enum.GetValues<Distribution>();

    [Theory]
    [MemberData(nameof(SizeSweep))]
    public void IsCorrectPermutation(Distribution d, int n, int seed)
    {
        var input = Gen(d, n, seed);
        var expected = input.ToArray(); // untouched copy
        Sort(input);

        // 1) Output is non-decreasing under the sort's own comparer.
        for (int i = 1; i < input.Length; i++)
            Assert.False(input[i].CompareTo(input[i - 1]) < 0,
                $"descending pair at {i} for {d} n={n} seed={seed}");

        // 2) Output is a permutation of the input (canonical full order on both).
        var lhs = input.OrderBy(x => x, Comparer<T>.Create(Canonical)).ToArray();
        var rhs = expected.OrderBy(x => x, Comparer<T>.Create(Canonical)).ToArray();
        Assert.Equal(rhs, lhs);
    }

    [Theory]
    [MemberData(nameof(RandomSizes))]
    public void IsDeterministic(Distribution d, int n, int seed)
    {
        var a = Gen(d, n, seed);
        var b = Gen(d, n, seed); // independent instance (catches instance-identity bugs)
        Sort(a);
        Sort(b);
        // Element-wise identical output, INCLUDING tie positions (Pair payload),
        // i.e. stricter than multiset equality.
        Assert.Equal(a, b);
    }
}
```

The three per-algorithm test classes (e.g. `tests/Sorts.Tests/QuadSortTests.cs`):
```csharp
using Sorts.TestData;
using Xunit;

public class QuadSortTests : SortContractTests<int>
{
    protected override void Sort(int[] array) => Sorts.QuadSort.Sort(array);
    protected override int[] Gen(Distribution d, int n, int seed) => DataGen.Ints(d, n, seed);
    protected override Comparison<int> Canonical => (a, b) => a.CompareTo(b);
}
// Plus QuadSortPairTests : SortContractTests<Pair> (Canonical: key then payload),
// QuadSortDoubleTests (Canonical must treat NaN per Double.CompareTo; generator's
// NaN cases are covered by the non-decreasing check using CompareTo), and
// QuadSortStringTests. Same 4 classes for GlideSortTests / DriftSortTests.
```
(12 concrete classes total, ~8 lines each. For `Pair`: `Canonical = (a,b) => { var c = a.Key.CompareTo(b.Key); return c != 0 ? c : a.Payload.CompareTo(b.Payload); }`.)

- [ ] **Step 4: Write API contract tests (argument validation — algorithm-independent, GREEN immediately once stubs guard properly)**

`tests/Sorts.Tests/ApiContractTests.cs`:
```csharp
using Sorts;
using Xunit;

public class ApiContractTests
{
    public static IEnumerable<object[]> AllSorts() // reflect over the 3 classes
        => new object[][] { new[] { "QuadSort" }, new[] { "GlideSort" }, new[] { "DriftSort" } };
    // Use delegates keyed by name to call Sort<int> on each class.

    [Theory] [MemberData(nameof(AllSorts))]
    public void NullArrayThrows(string sortName) { /* Assert.Throws<ArgumentNullException> */ }

    [Theory] [MemberData(nameof(AllSorts))]
    public void BadRangeThrows(string sortName)
    { /* index < 0, length < 0, index + length > array.Length → ArgumentOutOfRangeException */ }

    [Theory] [MemberData(nameof(AllSorts))]
    public void ZeroAndOneElementNoop(string sortName)
    { /* n = 0 and n = 1 arrays pass through unchanged, no throw */ }

    [Fact]
    public void IComparerNullMeansDefault()
    { /* Sort(int[], (IComparer<int>?)null) sorts ascending — needs a working kernel;
         keep this test but it will be RED until the first algorithm task lands. */ }
}
```
Implement the delegation via a `Dictionary<string, Action<int[]>>` built with lambdas so the theory body is one line per behavior.

- [ ] **Step 5: Run the suite — verify RED for the right reason**

Run: `dotnet test -c Release`
Expected: DataGenTests + guard tests PASS; all `IsCorrectPermutation`/`IsDeterministic` theories FAIL with `NotImplementedException` (3 algorithms × 4 types × all cases). This is the RED baseline the algorithm tasks will turn green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "test: differential correctness + determinism harness; comparer kernel; API stubs"
```

---

### Task 4: QuadSort — small sorts, quad_swap analyzer, 32-blocks

**Files:**
- Modify: `src/Sorts/QuadSort.cs`
- Create: `src/Sorts/Quadsort/QuadsortImpl.cs` (kernel internals; `internal static partial class QuadsortImpl`)
- Create: `tests/Sorts.Tests/QuadsortInternalTests.cs`

**Upstream reference:** `upstream/quadsort/src/quadsort.c` — `parity_swap_four`, `parity_swap_five`, `parity_swap_six`, `parity_swap_seven`, `tiny_sort`, `parity_merge`, `tail_swap`, `quad_reversal`, `quad_swap_merge`, `quad_swap`; macros `branchless_swap`, `swap_branchless`, `parity_merge_two`, `parity_merge_four` in `upstream/quadsort/src/quadsort.h:38-110`.

**Interfaces:**
- Consumes: `IIsLess<T>` kernel, `QuadSort.SortSpan<T,TC>`.
- Produces (internal, in `QuadsortImpl`):
  ```csharp
  internal static partial class QuadsortImpl
  {
      internal const int QuadCache = 262144; // upstream QUAD_CACHE
      internal static void TinySort<T, TC>(Span<T> a, Span<T> swap, TC cmp) where TC : struct, IIsLess<T>;
      internal static void ParitySwapFour/Five/Six/Seven<T, TC>(...)  // per upstream
      internal static void ParityMerge<T, TC>(Span<T> dest, Span<T> from, int leftLen, int rightLen, TC cmp);
      internal static void TailSwap<T, TC>(Span<T> a, Span<T> swap, TC cmp);
      internal static void QuadReversal<T, TC>(Span<T> a, TC cmp);
      internal static void QuadSwapMerge<T, TC>(Span<T> a, Span<T> swap, TC cmp);
      internal static int QuadSwap<T, TC>(Span<T> a, Span<T> swap, TC cmp); // returns 1 if whole array was descending
  }
  ```
- Wiring: `QuadSort.SortSpan` for this task handles `nmemb < 32` via `TinySort`/`TailSwap` with a `[ThreadStatic] static T[]? s_smallScratch` (size 64, lazily allocated — zero steady-state alloc); larger sizes still throw `NotImplementedException` (Task 5 completes them).

**Porting guidance (C# idiom mapping):**
- C pointers → `ref T` cursors: `ref var pta = ref MemoryMarshal.GetReference(a);` advance with `ref var p = ref Unsafe.Add(ref pta, i);`
- `branchless_swap` macro: `var x = pta[1]; if (cmp.IsLess(x, pta[0])) { pta[1] = pta[0]; pta[0] = x; }` — check `quadsort.h:89-96`: it is a *branched* swap on gcc / branchless ternary on clang. In C#, write BOTH forms behind `[MethodImpl(MethodImplOptions.NoInlining)] static bool SwapIfLess<T,TC>(ref T a, ref T b, TC cmp)` and pick by a static `bool UseBranchless` runtime check (`System.Runtime.CompilerServices.RuntimeFeature` has no is-clang; instead: measure nothing — default to the branchless ternary `T x = cmp.IsLess(b,a) ? a : b` form only where the macro's clang branch would, and verify via benchmark in Task 15; keep the branch form as `#if`-free alternative method so benchmarking can A/B it).
- `parity_merge_two`/`four` and `head/tail_branchless_merge` macros: port as `AggressiveInlining` static methods using the clang (branchless) form: `*pts++ = cmp.IsLess(*ptr, *ptl) ? *ptr++ : *ptl++;` — in C#: two `ref` cursors + one destination cursor; conditional-write pattern compiles to cmov-friendly selects for primitive T.
- `quad_swap`'s 8-element analyzer: the 4 pair-comparisons produce a bitmask `size_t` — port directly, including the `if (nmemb == 8)` special case and the "main is in order" / "reverse order" early exits.
- Index arithmetic uses `nint`/`int`; keep loop induction variables as `int` where upstream uses `size_t` and no overflow risk exists (n ≤ 2^31 in C# arrays anyway).

- [ ] **Step 1: Run QuadSort theory subset — verify RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~QuadSortTests"`
Expected: FAIL with NotImplementedException.

- [ ] **Step 2: Port small sorts + quad_swap + tail machinery per above signatures**

Read `upstream/quadsort/src/quadsort.c` lines around each function (grep for the FUNC names listed above); port each 1:1 into `QuadsortImpl` with the C# idiom mapping. Keep function order and comments (translated to English where needed).

- [ ] **Step 3: Add internal unit tests for the 32-block builder**

`tests/Sorts.Tests/QuadsortInternalTests.cs`:
```csharp
using Sorts;
using Sorts.TestData;
using Xunit;

public class QuadsortInternalTests
{
    private static void AssertSorted<T, TC>(T[] a, TC cmp) where TC : struct, IIsLess<T>
    {
        for (int i = 1; i < a.Length; i++)
            Assert.False(cmp.IsLess(in a[i], in a[i - 1]));
    }

    [Fact]
    public void TinySortHandles0Through31()
    {
        for (int n = 0; n <= 31; n++)
        {
            var a = DataGen.Ints(Distribution.Random, Math.Max(n, 1), n + 1)[..n];
            var copy = a.ToArray();
            QuadsortImpl.TinySort<int, ComparableCmp<int>>(a, new int[64], new());
            AssertSorted(a, new ComparableCmp<int>());
            Assert.Equal(copy.OrderBy(x => x), a);
        }
    }

    [Fact]
    public void QuadSwapProducesSorted32BlocksOnRandomData()
    { /* n=4096 random: run QuadSwap; verify each 32-block sorted; remaining tail untouched */ }

    [Fact]
    public void QuadSwapDetectsFullDescending()
    { /* n=1000 descending: QuadSwap returns 1 and array fully reversed-sorted */ }
}
```

- [ ] **Step 4: Run — verify internal tests + QuadSort n<32 theories GREEN, larger still RED**

Run: `dotnet test -c Release --filter "FullyQualifiedName~QuadSort|FullyQualifiedName~QuadsortInternal"`
Expected: QuadsortInternalTests PASS; `QuadSortTests` size ≤ 31 cases PASS; size ≥ 32 still NotImplementedException.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(quadsort): small sorts, quad-swap analyzer, 32-element block builder"
```

---

### Task 5: QuadSort — merge machinery (full sort works)

**Files:**
- Modify: `src/Sorts/Quadsort/QuadsortImpl.cs`, `src/Sorts/QuadSort.cs`

**Upstream reference:** `upstream/quadsort/src/quadsort.c` — `cross_merge`, `quad_merge_block`, `quad_merge`, `partial_forward_merge`, `partial_backward_merge`, `tail_merge`, `quadsort`, `quadsort_swap`.

**Interfaces:**
- Produces:
  ```csharp
  internal static void CrossMerge<T, TC>(Span<T> dest, Span<T> from, int leftLen, int rightLen, TC cmp);
  internal static void QuadMergeBlock<T, TC>(Span<T> a, Span<T> swap, int block, TC cmp);
  internal static int QuadMerge<T, TC>(Span<T> a, Span<T> swap, int swapSize, int nmemb, int block, TC cmp);
  internal static void PartialForwardMerge<T, TC>(Span<T> a, Span<T> swap, int swapSize, int nmemb, int block, TC cmp);
  internal static void PartialBackwardMerge<T, TC>(Span<T> a, Span<T> swap, int swapSize, int nmemb, int block, TC cmp);
  internal static void TailMerge<T, TC>(Span<T> a, Span<T> swap, int swapSize, int nmemb, int block, TC cmp);
  ```
- `QuadSort.SortSpan` final wiring for this task: allocate `swap_size = nmemb` (cap growth logic from Task 6 still TODO — this task: plain `GC.AllocateUninitializedArray<T>(nmemb)`), run `QuadSwap` → `QuadMerge(a, swap, swapSize, nmemb, 32, cmp)` → `TailMerge(a, swap, swapSize, nmemb, block, cmp)` per `quadsort()` main flow (read the exact call sequence in upstream `quadsort()`).

- [ ] **Step 1: Port the merge functions**

Port each listed function 1:1. Notes:
- `cross_merge`'s memcpy fast paths (`memcpy(dest, from, len * sizeof(VAR))`) become `from.Slice(..len).CopyTo(dest)`.
- `parity_merge` gcc fallback branch (`if (left + right > QUAD_CACHE)`) — port the *branchless* loop only; the branchy fallback exists for old gcc; C# JIT handles the select fine. Keep `QuadCache` const for fidelity documentation.
- `quad_merge_block`'s ordered-skip switch (the 2 comparisons deciding 0/1/2/3 merges) — port exactly; it is why sorted input wins.
- Signatures take `Span<T>` slices sized to the logical regions; use `ref` cursors inside.

- [ ] **Step 2: Run the full QuadSort suite — verify ALL GREEN**

Run: `dotnet test -c Release --filter "FullyQualifiedName~QuadSort|FullyQualifiedName~QuadsortInternal"`
Expected: every theory (all 4 types, all distributions, sizes 0–65 sweep + random sizes) PASSES. If failures, debug against upstream logic — the harness prints dist/size/seed on failure.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(quadsort): cross/quad/partial/tail merge — full public sort works"
```

---

### Task 6: QuadSort — buffer cap, rotate_merge fallback, adapter wiring

**Files:**
- Modify: `src/Sorts/Quadsort/QuadsortImpl.cs`, `src/Sorts/QuadSort.cs`

**Upstream reference:** `upstream/quadsort/src/quadsort.c` — `trinity_rotation`, `monobound_binary_first`, `rotate_merge_block`, `rotate_merge`, `quadsort()` allocation logic (swap_size cap growth loop `for (swap_size = 4194304; swap_size * 8 <= nmemb; swap_size *= 4) {}`), `quadsort_swap`.

**Interfaces:**
- Produces:
  ```csharp
  internal static void TrinityRotation<T, TC>(Span<T> a, Span<T> swap, int swapSize, int nmemb, int leftLen, TC cmp);
  internal static int MonoboundBinaryFirst<T, TC>(Span<T> array, ref T value, int top, TC cmp);
  internal static void RotateMergeBlock<T, TC>(Span<T> a, Span<T> swap, int swapSize, int lblockLen, int rightLen, TC cmp);
  internal static void RotateMerge<T, TC>(Span<T> a, Span<T> swap, int swapSize, int nmemb, int block, TC cmp);
  internal static void QuadsortWithScratch<T, TC>(Span<T> a, Span<T> swap, int swapSize, TC cmp); // upstream quadsort_swap
  ```
- `SortSpan` final form: full `quadsort()` logic — `< 32` tiny path, `QuadSwap`, swap-size policy (`nmemb` capped by the growth loop), `GC.AllocateUninitializedArray<T>(swapSize)` inside `try { } catch (OutOfMemoryException) { fallbackSpan = 512-elem scratch; }`, then `QuadMerge`/`RotateMerge` per upstream.
- `Sort<T,TC>(Span<T>, TC)` public adapter: wraps `TC : struct, IComparer<T>` into an internal `ComparerAdapter<T, TC> : IIsLess<T>` (`IsLess => _c.Compare(x, y) < 0`) and calls `SortSpan` with scratch `default` (SortSpan allocates). This adapter lives in `Comparers.cs` and is shared by all three algorithms.

- [ ] **Step 1: Port rotate machinery + wire final SortSpan**

- [ ] **Step 2: Add fallback-path tests (force tiny scratch through QuadsortWithScratch)**

Add to `tests/Sorts.Tests/QuadsortInternalTests.cs`:
```csharp
[Fact]
public void RotateMergePathSortsCorrectlyWithTinyScratch()
{
    var a = DataGen.Ints(Distribution.Random, 5000, 42);
    var expected = a.OrderBy(x => x).ToArray();
    QuadsortImpl.QuadsortWithScratch<int, ComparableCmp<int>>(a, new int[64], 64, new());
    Assert.Equal(expected, a);
}
```
(64 < 5000 forces `rotate_merge` for the deep merges; also add a `RandomD20` variant to hit duplicate-heavy rotations.)

- [ ] **Step 3: Run full suite + commit**

Run: `dotnet test -c Release`
Expected: ALL tests pass (all algorithms' guard tests, DataGen; QuadSort fully green; GlideSort/DriftSort still RED by design — those are the only failures).

```bash
git add -A
git commit -m "feat(quadsort): buffer cap, rotate-merge fallback, comparer adapter"
```

---

### Task 7: GlideSort — small_sort (48-element network)

**Files:**
- Create: `src/Sorts/Glidesort/GlideSmallSort.cs` (`internal static class GlideSmallSort`), `src/Sorts/Glidesort/GlideMerge.cs` (empty placeholder this task)
- Create: `tests/Sorts.Tests/GlideSmallSortTests.cs`

**Upstream reference:** `upstream/glidesort/src/small_sort.rs` — `small_sort`, `sort4_raw`, `SortSmallState` (`new`, `set_new_dst`, `swap_src_dst`, `sort_groups_of_four_from_src_to_dst`, `final_merge_from_dst_into`, `double_merge_from_src_to_dst`), `sort4_into`, `sort8_into`, `sort16_into`, `sort32_into`, `partial_sort_into`, `InsertionGapGuard` (`new`, `insert`), `block_insertion_sort`. Constant `SMALL_SORT = 48` from `lib.rs:35`.

**Interfaces:**
- Produces:
  ```csharp
  internal static class GlideSmallSort
  {
      internal const int SmallSort = 48; // upstream SMALL_SORT
      // el = input slice (len <= 48); sorts in place using only its own locals —
      // upstream signature takes only el + is_less (no scratch).
      internal static void Sort<T, TC>(Span<T> el, TC cmp) where TC : struct, IIsLess<T>;
      internal static void Sort4Into/Sort8Into/Sort16Into/Sort32Into<T, TC>(...); // src/dst halves per upstream
      internal static void BlockInsertionSort<T, TC>(...);
  }
  ```

**Porting guidance:**
- The Rust typestate `MutSlice<Brand, T, Init/Uninit>` exists only to prove read-before-write discipline; in C# the same discipline holds by construction in the ported code — use plain `Span<T>` (dst spans are scratch the caller provides; they are written before read, mirroring upstream).
- `const N: usize` generics → concrete methods per N (4/8/16/32), hand-specialized, `[MethodImpl(AggressiveInlining)]`.
- `sort4_raw`'s optimal 5-comparison network: port comparison-for-comparison.
- `InsertionGapGuard::drop` writes elements back — the Rust `drop` glue becomes an explicit `Flush()` the caller invokes; do NOT rely on finalizers.

- [ ] **Step 1: Write failing unit tests**

`tests/Sorts.Tests/GlideSmallSortTests.cs`:
```csharp
using Sorts;
using Sorts.TestData;
using Xunit;

public class GlideSmallSortTests
{
    [Fact]
    public void SortsAllSizesUpTo48()
    {
        for (int n = 0; n <= 48; n++)
        {
            var a = DataGen.Ints(Distribution.Random, Math.Max(n, 1), n + 3)[..n];
            var expected = a.OrderBy(x => x).ToArray();
            GlideSmallSort.Sort<int, ComparableCmp<int>>(a, new());
            Assert.Equal(expected, a);
        }
    }

    [Theory]
    [InlineData(Distribution.RandomD20)]
    [InlineData(Distribution.Descending)]
    [InlineData(Distribution.AllEqual)]
    public void HandlesAdversarialPatterns(Distribution d)
    { /* n = 48, all three patterns, seed 9; assert sorted ascending */ }
}
```

- [ ] **Step 2: Run — verify RED, port small_sort.rs, run — verify GREEN**

Run: `dotnet test -c Release --filter "FullyQualifiedName~GlideSmallSort"`

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(glidesort): 48-element small-sort network"
```

---

### Task 8: GlideSort — branchless/physical merges + eager fallback sort

**Files:**
- Modify: `src/Sorts/Glidesort/GlideMerge.cs`
- Create: `tests/Sorts.Tests/GlideMergeTests.cs`

**Upstream reference:** `upstream/glidesort/src/branchless_merge.rs` (bidirectional interleaved merges), `upstream/glidesort/src/physical_merges.rs` — `physical_merge`, `physical_triple_merge`, `physical_quad_merge`, ping-pong merge machinery; `upstream/glidesort/src/merge_reduction.rs` — merge splitting for small scratch. `gap_guard.rs` is **skipped by design**: it guards against scratch overlapping input, which cannot happen in our C# layout (separate allocations); document this divergence in a code comment.

**Interfaces:**
- Produces:
  ```csharp
  internal static class GlideMerge
  {
      // All take (input spans..., scratch, cmp). Scratch length >= half the total
      // input length (upstream contract). Returns void; result lands in the left
      // (destination) span — mirror upstream return semantics: they return the
      // merged MutSlice; in C# the destination IS the left span, callers slice.
      internal static void PhysicalMerge<T, TC>(Span<T> left, Span<T> right, Span<T> scratch, TC cmp);
      internal static void PhysicalTripleMerge<T, TC>(Span<T> a, Span<T> b, Span<T> c, Span<T> scratch, TC cmp);
      internal static void PhysicalQuadMerge<T, TC>(Span<T> a, Span<T> b, Span<T> c, Span<T> d, Span<T> scratch, TC cmp);
      // bidirectional + interleaved merge loops from branchless_merge.rs, incl.
      // the ping-pong buffer ping-ponging and unrolled 2-way interleave.
      internal static void BidirectionalMerge<T, TC>(...); // port of the interleaved ping-pong merge
  }
  internal static class EagerSort // merge-only eager mode (upstream quicksort depth fallback)
  {
      // Sorts v with small_sort runs of length <= 48 then bottom-up physical merges,
      // guaranteeing O(n log n) — the introsort-style shield used by Task 9's quicksort.
      internal static void Sort<T, TC>(Span<T> v, Span<T> scratch, TC cmp);
  }
  ```

- [ ] **Step 1: Write failing merge unit tests**

`tests/Sorts.Tests/GlideMergeTests.cs`:
```csharp
[Fact]
public void PhysicalMergeMergesTwoSortedRuns()
{ // two random-sorted halves, sizes (0,8),(8,0),(1,1000),(1000,1),(500,500),(48,48)
  // → PhysicalMerge; assert whole thing ascending + permutation of input }

[Fact]
public void PhysicalQuadMergeMergesFourSortedRuns()
{ // four sorted quarters, incl. all-equal and interleaved-range cases }

[Fact]
public void EagerSortSortsRandomDataAllSizes()
{ // n in {0,1,2,47,48,49,100,4096,20000} random + RandomD20; assert ascending + permutation }
```

- [ ] **Step 2: Run RED → port merges + EagerSort → run GREEN**

Run: `dotnet test -c Release --filter "FullyQualifiedName~GlideMerge|FullyQualifiedName~EagerSort"`

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(glidesort): physical/triple/quad + bidirectional merges, eager fallback"
```

---

### Task 9: GlideSort — stable quicksort (bidirectional partition)

**Files:**
- Create: `src/Sorts/Glidesort/GlideQuicksort.cs`
- Create: `tests/Sorts.Tests/GlideQuicksortTests.cs`

**Upstream reference:** `upstream/glidesort/src/stable_quicksort.rs` (read the full ASCII-art header comment first — the bidirectional partition invariant), `upstream/glidesort/src/pivot_selection.rs`.

**Interfaces:**
- Produces:
  ```csharp
  internal static class GlideQuicksort
  {
      // Sorts v (may be unsorted); scratch >= v.Length. Depth limit per upstream
      // (limit -= 1 per level; on 0 → EagerSort.Sort fallback).
      internal static void Quicksort<T, TC>(Span<T> v, Span<T> scratch, int limit, TC cmp);
      internal static int ChoosePivot<T, TC>(Span<T> v, TC cmp); // pivot_selection.rs
      // The bidirectional partition: port of partition() + helper state.
      internal static void Partition<T, TC>(Span<T> left, Span<T> right, Span<T> dest, Span<T> scratch, ref T pivot, TC cmp);
  }
  ```

**Porting guidance:**
- The forward/backward scans write to dest-front/scratch-front and scratch-back/dest-back; recursion `partition(a, b', concat(a,b), concat(a',b'))` and `partition(c', d, concat(c,d), concat(c',d'))` per the header ASCII art. In C#, the "concat" spans are constructed with `Span<T>.Slice` on the two underlying arrays when adjacent — represent (left,right) as two spans and dest/scratch likewise; recursive helper takes (Span left, Span right, Span dest, Span scratch) where each may be a two-piece logical slice. Upstream handles this with `MutSlice` two-piece views; in C# define a small `readonly struct TwoPieceSpan<T> { Span<T> A, B; }` with `Length`, `Slice`, and ref-accessor helpers — this is the one structural addition the port needs.
- `is_less` calls stay `TC.IsLess(in T, in T)`.
- Rust `#[inline(never)]` on `quicksort` → `[MethodImpl(MethodImplOptions.NoInlining)]` (upstream does this for binary size / i-cache; C# equivalent reasoning holds for the recursive driver).

- [ ] **Step 1: Write failing quicksort tests**

```csharp
[Fact]
public void QuicksortSortsAllSizesAndPatterns()
{ // n in {49..200 sweep, 1000, 4096, 20000} × {Random, RandomD20, Zipfian, AllEqual, Descending}
  // assert ascending + permutation (reuse SortContract oracle helpers) }

[Fact]
public void QuicksortDepthLimitFallsBackToEager()
{ // adversarial organ-pipe-of-organ-pipes killer input 100k elements that biases pivots;
  // limit artificially 1 → still sorts correctly (fallback exercised) }
```

- [ ] **Step 2: Run RED → port → run GREEN** (`--filter GlideQuicksort`)

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(glidesort): stable quicksort with bidirectional partition"
```

---

### Task 10: GlideSort — main loop (powersort) + public wiring

**Files:**
- Create: `src/Sorts/Glidesort/GlidesortImpl.cs` (LogicalRun, MergeStack, main loop, powersort)
- Modify: `src/Sorts/GlideSort.cs` (SortSpan final wiring)
- Create: `src/Sorts/Powersort.cs` (shared by GlideSort and DriftSort)

**Upstream reference:** `upstream/glidesort/src/glidesort.rs` — `LogicalRun` (enum + `create` + `logical_merge` + `physical_sort`), `MergeStack`, `glidesort()` main loop, `run_length_at_start`; `upstream/glidesort/src/powersort.rs` — `merge_tree_scale_factor`, `merge_tree_depth` (port the whole comment block — the proof matters); `upstream/glidesort/src/lib.rs` — `glidesort_alloc_size` (n.min(1MiB/sizeOf) max (n/2).min(1GiB/sizeOf) max n/8 max SMALL_SORT).

**Interfaces:**
- Produces:
  ```csharp
  // src/Sorts/Powersort.cs — shared, both algorithms use identical math
  internal static class Powersort
  {
      internal static ulong MergeTreeScaleFactor(int n);     // ceil(2^62 / n)
      internal static byte MergeTreeDepth(int left, int mid, int right, ulong scale); // LZCNT((f*x)^(f*y))
  }
  // C# note: BitOperations.LeadingZeroCount — single LZCNT instruction.

  // GlidesortImpl.cs
  internal readonly struct LogicalRun // (start, length, kind) — kinds: Unsorted, Sorted, DoubleSorted(mid)
  internal sealed class ... // MergeStack: 64-slot arrays (leftChildren, desiredDepths) + len
  internal static class GlidesortImpl
  {
      internal static void Sort<T, TC>(Span<T> v, Span<T> scratch, TC cmp, bool eagerSmallsort);
  }
  ```
- `GlideSort.SortSpan` final: `int alloc = GlidesortAllocSize<T>(v.Length)` (upstream formula with `Unsafe.SizeOf<T>()`); scratch = `GC.AllocateUninitializedArray<T>(alloc)`; call `GlidesortImpl.Sort`. Note upstream's sanity: scratch < SMALL_SORT never happens given the formula's `.max(SMALL_SORT)`.
- C# mapping of `LogicalRun::create`: the Rust `eager_smallsort` flag comes from `lib.rs` (`glidesort_with_max_stack_scratch` uses eager when scratch is stack-sized); port the default path (`eager_smallsort: false`) and the flag through — Task 15 benchmarks may A/B it.

- [ ] **Step 1: Port powersort with a direct unit test**

```csharp
[Fact]
public void PowersortDepthMatchesRust()
{ // hand-computed: n=100, runs at (0,40),(40,40),(40,60): scale = ceil(2^62/100);
  // depth((0,40,80)) vs depth((40,80,100)) — compute expected via the formula in
  // a comment and assert; also assert depth strictly increases along i for a
  // random partition of [0,1000) into 20 runs (the stack invariant). }
```

- [ ] **Step 2: Run full GlideSort suite — ALL theories must go GREEN**

Run: `dotnet test -c Release --filter "FullyQualifiedName~GlideSort|FullyQualifiedName~Glide"`
Expected: 100% pass across 4 types × 12 distributions × all sizes/seeds (incl. `RandomS95` which exercises logical-run laziness and `Zipfian` which exercises quicksort-heavy paths).

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(glidesort): powersort main loop — full sort works"
```

---

### Task 11: DriftSort — smallsort + merge

**Files:**
- Create: `src/Sorts/Driftsort/DriftSmallSort.cs`, `src/Sorts/Driftsort/DriftMerge.cs`
- Create: `tests/Sorts.Tests/DriftSmallSortTests.cs`, `tests/Sorts.Tests/DriftMergeTests.cs`

**Upstream reference:** `upstream/driftsort/src/smallsort.rs` — `SmallSortTypeImpl` (threshold 32 for unmanaged ≤16-byte T, else 16; C# selects via `typeof(T).IsValueType && Unsafe.SizeOf<T>() <= 16 && !RuntimeHelpers.IsReferenceOrContainsReferences<T>()` — cache in a `static readonly bool` inside a `static class SmallSortConfig<T>`), `sort_small_general`, `insert_tail`, `insertion_sort_shift_left`, `sort4_stable`, `sort8_stable`, `merge_up`, `merge_down`, `bidirectional_merge`, `select` (branchless pointer select); `MIN_SMALL_SORT_SCRATCH_LEN = 50`. `upstream/driftsort/src/merge.rs` — `merge`, `merge_up`, `merge_down` (scratch ≥ n/2 contract).

**Interfaces:**
- Produces:
  ```csharp
  internal static class DriftSmallSort
  {
      internal const int MinSmallSortScratchLen = 50;
      internal static int Threshold<T>(); // 32 or 16 per config
      // v.Length <= Threshold; scratch.Length >= MinSmallSortScratchLen
      internal static void SortSmall<T, TC>(Span<T> v, Span<T> scratch, TC cmp);
      internal static void InsertionSortShiftLeft<T, TC>(Span<T> v, TC cmp, int start = 1);
  }
  internal static class DriftMerge
  {
      // scratch >= v.Length - (v.Length / 2) — upstream contract
      internal static void Merge<T, TC>(Span<T> v, Span<T> scratch, int leftLen, TC cmp);
  }
  ```

**Porting guidance:**
- `sort4_stable` / `sort8_stable`'s `select(cond, if_true, if_false)` pointer select: in C#, `ref T` locals + `ref T pick = ref cond ? ref a : ref b;` (C# supports conditional ref expressions) — compiles to cmov. `ManuallyDrop`/`MaybeUninit` handling disappears: our scratch is a real `T[]`/`Span<T>` and we maintain write-before-read.
- The `#[inline(never)]`-style tuning: keep `SortSmall` aggressively inlined, `Merge` normally.

- [ ] **Step 1: Failing tests (sizes 0..32 sweep × 3 patterns; Merge on (500,500)/(1,999)/(999,1) sorted halves)**
- [ ] **Step 2: Run RED → port → GREEN** (`--filter DriftSmallSort|DriftMerge`)
- [ ] **Step 3: Commit** — `feat(driftsort): smallsort networks + n/2 merge`

---

### Task 12: DriftSort — stable quicksort + pivot (ancestor tracking)

**Files:**
- Create: `src/Sorts/Driftsort/DriftQuicksort.cs`
- Create: `tests/Sorts.Tests/DriftQuicksortTests.cs`

**Upstream reference:** `upstream/driftsort/src/quicksort.rs` — `stable_quicksort`, `stable_partition` (+ `PartitionState::new`/`partition_one`, the 4× unrolled loop for `sizeof(T) <= 16`), ancestor-pivot equal-partition logic; `upstream/driftsort/src/pivot.rs` — `choose_pivot`, `median3_rec`, `median3`.

**Interfaces:**
- Produces:
  ```csharp
  internal static class DriftQuicksort
  {
      internal static void StableQuicksort<T, TC>(Span<T> v, Span<T> scratch, int limit, T? leftAncestorPivot /* null = None */, TC cmp);
      internal static int StablePartition<T, TC>(Span<T> v, Span<T> scratch, int pivotPos, bool pivotGoesLeft, TC cmp);
      internal static int ChoosePivot<T, TC>(Span<T> v, TC cmp);
  }
  ```
- C# mapping decisions (document in code comments):
  - `Option<&T>` ancestor pivot → `T?` for nullable value types / `(bool has, T value)` tuple to also support non-nullable-struct T: use a small `readonly struct PivotRef<T> { bool Has; T Value; }`.
  - `has_direct_interior_mutability` check: C# equivalent — treat ALL T as "may have interior mutability", i.e. take the safe branch upstream provides (no pivot copy aliasing optimization); cost is one `T` copy per partition — negligible vs upstream's own comment that this affects at most a redundant partition round. ALSO: always copy pivot into a local before partitioning (upstream `pivot_copy`) since our `in T` params can't be held alive across the loop otherwise anyway.
  - `intrinsics::abort` on bad scratch contract → `ThrowHelper.ThrowInvalidOperationException` (contracts violated = our bug, tests catch it).
  - `PartitionState.partition_one` body maps to: `scratchRev = ref Unsafe.Subtract(ref scratchRev, 1); ref T dst = ref towardsLeft ? ref scratchBase[numLeft] : ref Unsafe.Add(ref scratchRev, numLeft); dst = *scan; numLeft += towardsLeft ? 1 : 0; scan = ref Unsafe.Add(ref scan, 1);` — conditional `ref` selects and boolean arithmetic keep it branchless.
  - The equal-partition path calls `stable_partition` with an inverted `is_less` (`|a, b| !is_less(b, a)`) — in C#, implement as a second loop parameter `invert: bool` inside `StablePartition` (avoids allocating a delegate wrapper per call): `bool towardsLeft = invert ? !cmp.IsLess(in pivot, in cur) : cmp.IsLess(in cur, in pivot);` — port upstream semantics exactly (`pivot_goes_left` for the pivot slot itself stays independent of invert).
  - Depth limit fallback → Task 13's `DriftImpl.Sort(eager: true)` — for this task's standalone tests, fallback target is a local `EagerFallback` = `DriftSmallSort`-runs + `DriftMerge` bottom-up (same shape as Task 8's EagerSort but using drift's merge; the real main loop replaces it in Task 13 — keep the seam: `static Func<Span<T>, Span<T>, TC, ...>`? NO — simple `internal static Action`-free approach: DriftQuicksort takes an `bool eagerFallback` and calls `DriftImpl.Sort(v, scratch, eager: true, cmp)` once Task 13 exists; for Task 12 tests, temporarily throw on limit==0 EXCEPT one test that pins the seam with the eager fallback implemented locally in DriftQuicksort. Cleanest: implement eager fallback inline in DriftQuicksort as `EagerSortImpl` (runs of ≤ threshold via SortSmall, then bottom-up DriftMerge) — ~30 lines, used by both quicksort limit-0 and (later) nothing else since main loop has its own. Do that.)

- [ ] **Step 1: Failing tests:**
```csharp
[Fact]
public void StableQuicksortSortsAllSizesAndPatterns()
{ // sizes 33..300 sweep + {1000, 4096, 20000} × {Random, RandomD20, Zipfian, AllEqual, Descending} }

[Fact]
public void AncestorPivotGivesEqualBatchingOnAllEqual()
{ // AllEqual n=100k: sorts correctly; (informational — comparisons counting is
  // not observable from C#; assert correctness only) }
```
- [ ] **Step 2: Run RED → port → GREEN** (`--filter DriftQuicksort`)
- [ ] **Step 3: Commit** — `feat(driftsort): stable quicksort with ancestor pivot tracking`

---

### Task 13: DriftSort — main loop + public wiring (suite fully green)

**Files:**
- Create: `src/Sorts/Driftsort/DriftImpl.cs`
- Modify: `src/Sorts/DriftSort.cs`

**Upstream reference:** `upstream/driftsort/src/drift.rs` — `sort` (main loop w/ 66-slot run stack), `logical_merge`, `create_run`, `find_existing_run`, `sqrt_approx`, `DriftsortRun` bitfield (`(len << 1) | sortedBit`), `MIN_SQRT_RUN_LEN = 64` + `min_good_run_len` policy; `upstream/driftsort/src/lib.rs` — top-level `driftsort()` entry: `MAX_LEN_ALWAYS_INSERTION_SORT = 20` (pure insertion for n ≤ 20), scratch policy `max(max(len - len/2, min(len, 8MB/sizeOf)), MIN_SMALL_SORT_SCRATCH_LEN)`, 4096-byte stack buffer first.

**Interfaces:**
- Produces:
  ```csharp
  internal static class DriftImpl
  {
      internal static void Sort<T, TC>(Span<T> v, Span<T> scratch, bool eagerSort, TC cmp);
  }
  ```
- `DriftSort.SortSpan` final wiring: n ≤ 20 → `DriftSmallSort.InsertionSortShiftLeft` (i-cache-friendliness per upstream comment); else compute `allocLen` per policy; small allocs (≤ 4096 bytes → `stackalloc` impossible for generic T; use `[ThreadStatic] T[]? s_stackScratch` of 512 elements as the "stack buffer") else `GC.AllocateUninitializedArray<T>(allocLen)`; call `DriftImpl.Sort(v, scratch, eagerSort: false, cmp)`.
- `MergeTreeScaleFactor/MergeTreeDepth` — reuse `src/Sorts/Powersort.cs` from Task 10 (identical math; note this in a comment referencing both upstream files).
- `DriftsortRun` → `readonly struct RunState { private readonly ulong _v; }` with `RunState.Sorted(len)/.Unsorted(len)/.IsSorted/.Length` mirroring upstream bit ops. 66-slot stacks → two arrays + `stackLen`.

- [ ] **Step 1: Run the FULL suite — every test in the project must be GREEN now**

Run: `dotnet test -c Release`
Expected: 100% pass — all 3 algorithms × 4 types × all distributions × all sizes/seeds, plus internal tests. This is the "implementation complete" gate for the spec's correctness section.

- [ ] **Step 2: Commit**

```bash
git add -A
git commit -m "feat(driftsort): lazy powersort main loop — all three sorts complete, suite green"
```

---

### Task 14: Benchmark project — 3 matrices + baselines

**Files:**
- Create: `benchmarks/Sorts.Benchmarks/Program.cs`, `benchmarks/Sorts.Benchmarks/BenchConfig.cs`, `benchmarks/Sorts.Benchmarks/CoreMatrixBench.cs`, `benchmarks/Sorts.Benchmarks/TypeMatrixBench.cs`, `benchmarks/Sorts.Benchmarks/ScalingBench.cs`, `benchmarks/Sorts.Benchmarks/BaselineBench.cs`

**Interfaces:**
- Consumes: `Sorts.QuadSort/GlideSort/DriftSort`, `Sorts.TestData.DataGen`.
- Produces: runnable `dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter <pattern>` with `--memory` on by default.

- [ ] **Step 1: Write config + program**

`BenchConfig.cs`:
```csharp
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Environments;

public class BenchConfig : ManualConfig
{
    public BenchConfig()
    {
        AddJob(Job.Default
            .WithPlatform(Platform.X64) // Apple Silicon → run as Arm64 if detected:
            // pick via RuntimeInformation.ProcessArchitecture in Program.cs instead
            .WithGcServer(false)
            .WithGcForce(false));
        AddDiagnoser(new MemoryDiagnoser(true));
        AddColumn(StatisticColumn.P90); // jitter tail visibility
        AddExporter(BenchmarkDotNet.Exporters.MarkdownExporter.GitHub);
    }
}
```
`Program.cs`: switch on architecture, `BenchmarkRunner.Run` the four classes with `BenchConfig` via `[Config(typeof(BenchConfig))]` on each benchmark class. Accept `--filter` pass-through.

- [ ] **Step 2: Write the four benchmark classes**

Common pattern (in-place sorts destroy data → fresh clone per iteration):
```csharp
[IterationSetup(Target = "...")] // one per benchmark method — see note below
public void Setup() { _data = (int[])_template.Clone(); }

[Benchmark(Baseline = true)]
public void ArraySort_Generic() => Array.Sort(_data);

[Benchmark] public void QuadSort() => Sorts.QuadSort.Sort(_data);
[Benchmark] public void GlideSort() => Sorts.GlideSort.Sort(_data);
[Benchmark] public void DriftSort() => Sorts.DriftSort.Sort(_data);
```
NOTE on data freshness: `IterationSetup` runs once per *iteration* (many invocations) — sorting an already-sorted array skews results for adaptive sorts. Correct approach for in-place sorts: pre-generate `InvocationCount` unsorted clones in `GlobalSetup` into a ring buffer, and each invocation sorts `_ring[_i++ % _ring.Length]`. Set `[InvocationCount(64)] [OperationsPerInvoke(64)]` with a 64-entry ring, and NO IterationSetup. Implement helper `RingData<T>` in the benchmarks project. For `string[]`/`Pair[]` clone is shallow/ref-copy — still correct (elements are immutable / value structs). Document this in a comment (it is the standard BenchmarkDotNet recipe for destructive benchmarks).

`CoreMatrixBench`: `[Params("all")] Distribution Dist` × `[Params(1_000, 100_000, 1_000_000)] int N` — one benchmark method per distribution (12 methods) each with the 4 impls inside? NO — BenchmarkDotNet params generate the cross product with methods: make ONE class with `[Params] Distribution` + `N` and 4 benchmark methods (baselines + ours). 12×3 params = 36 cases × 4 methods.

`TypeMatrixBench`: 5 types (int, double, string, `Struct16` (two ints + a long, `IComparable` by first key), `Struct128` (fixed junk, comparable by first int)) × `[Params] Distribution {Random, RandomD20, RandomS95, Zipfian}` × N=100_000. Separate nested classes per type (generics don't bind well as direct params): `TypeMatrixInt`, `TypeMatrixDouble`, `TypeMatrixString`, `TypeMatrixStruct16`, `TypeMatrixStruct128`, each with 4+ benchmark methods.

`ScalingBench`: int Random, `[Params(1_000, 2_000, 4_000, ..., 1_048_576, 10_000_000)]` × 4 impls (Array.Sort baseline).

`BaselineBench`: int Random 100k — `ArraySort_Generic` (baseline), `ArraySort_IComparer` (`Array.Sort(_data, _comparer)` with `IComparer<int>`), `ArraySort_Comparison` (`Array.Sort(_data, (Comparison<int>)...)`), `Linq_OrderBy` (`_data.OrderBy(x => x).ToArray()` — the only stable reference), `QuadSort/GlideSort/DriftSort`. Plus one `Pair` variant comparing our sorts vs OrderBy on struct-with-payload.

- [ ] **Step 3: Smoke-run a tiny filter to validate the harness**

Run: `dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*CoreMatrixBench*Random*100000*' --job short`
Expected: completes with sane numbers (QuadSort/GlideSort/DriftSort same ballpark as Array.Sort or better; allocations: Array.Sort 0 B; ours ≤ scratch policy — record what you see; if any of ours shows unexplained per-call allocations beyond scratch, that's a bug to fix BEFORE the full run).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "bench: core/type/scaling matrices, baselines, ring-buffer data freshness"
```

---

### Task 15: Full benchmark run + README report

**Files:**
- Modify: `README.md` (repo root)
- Create: `benchmarks/results/` (exported markdown, committed)

- [ ] **Step 1: Run the three matrices**

```bash
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*CoreMatrixBench*' --job default
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*TypeMatrixBench*' --job default
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*ScalingBench*|*BaselineBench*' --job default
```
Expected: 1–2 h total; if the machine is under load (check `top`), note it and re-run outliers. Keep `BenchmarkDotNet.Artifacts` results; copy the markdown exports into `benchmarks/results/`.

- [ ] **Step 2: Write README.md**

Structure (Chinese, per spec §4):
1. 项目简介 + 定位（Array.Sort 替代评估；契约：正确 + 确定性，不保证稳定）
2. 结果速览表（每算法 vs Array.Sort<T>(T[])：random/ascending/d20/zipf × 100k/1M 的比值；分配对比）
3. 三算法一段式说明 + 上游链接 + 移植要点（branchless 映射、scratch 策略、跳过项：gap_guard、WASM、类型特化 codegen）
4. Benchmark 方法论（矩阵、ring-buffer 方案、复现命令）
5. 使用示例（API 代码片段）
6. 测试说明（差分 + 确定性 harness；如何跑）
7. 致谢与许可（上游三仓库、作者、license；本仓库实现的关系说明）
8. 已知限制（引用类型接口调用与 Array.Sort 同级；86 不做 keys/items 重载——指向 spec 的非目标）

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "docs: README with full benchmark results and usage"
```

---

### Task 16: Subagent review — fidelity + C# performance idioms

**Files:**
- Potentially modify: any `src/Sorts/**` per findings
- Create: `docs/superpowers/reviews/2026-09-18-subagent-review.md` (findings + resolutions log)

- [ ] **Step 1: Launch two review agents in parallel** (Agent tool, `claude` type, each with this repo as cwd)

Agent A — algorithm fidelity. Prompt essentials: "You are reviewing a C# port of three sorting algorithms. Upstream sources are vendored at upstream/{quadsort,glidesort,driftsort}. C# implementations in src/Sorts/. For EACH of the three ports, read the upstream source file(s) and the corresponding C# file side by side, and verify: (1) every algorithmic decision point matches (run thresholds, merge orders, powersort math, pivot selection, depth limits, buffer caps, skip conditions, loop boundaries, off-by-ones); (2) no upstream code path was dropped silently (documented skips are: gap_guard.rs, WASM paths, type-specialization codegen, MaybeUninit machinery); (3) constants match the Global Constraints list. Report findings as a numbered list with file:line for both sides. Do NOT modify code."

Agent B — C# performance idioms. Prompt essentials: "Review src/Sorts/** for .NET performance hazards: interface-devirtualization blockers (virtual calls in hot loops), missing AggressiveInlining on micro-helpers, allocation on steady-state paths beyond documented scratch policy, bounds-check patterns that defeat JIT elimination (non-obvious span indexing where `ref` + Unsafe.Add would be better or vice versa), branchy patterns where conditional-ref/select would be cmov, defensive copies from `in` params on non-readonly structs, and LINQ/boxing anywhere. Cross-check src/Sorts has zero LINQ/boxing in kernels. Report findings as a numbered list with file:line. Do NOT modify code."

- [ ] **Step 2: Triage findings with superpowers:receiving-code-review discipline (verify each against source before accepting; reject with reasons where the reviewer is wrong)**

- [ ] **Step 3: Apply accepted fixes** — each fix: re-run `dotnet test -c Release` (must stay green) and re-run affected benchmark filter (e.g. `--filter '*CoreMatrixBench*Random*' --job short`) before/after to confirm no regression.

- [ ] **Step 4: Write the review log + final commit**

```bash
git add -A
git commit -m "review: subagent fidelity + perf-idiom findings, fixes applied"
```

---

## Self-Review (already performed)

- **Spec coverage:** contract tests (§2, Task 3), API surface (§5, Tasks 3/6/10/13), all three ports (§6, Tasks 4–13), test matrix (§8, Tasks 2–3 + per-task internal tests), benchmark matrices (§9, Task 14), subagent review (§10, Task 16), environment (§11, Task 1), README (§4, Task 15). Non-goals respected (no keys/items, no NuGet, no parallel).
- **Type consistency:** `IIsLess<T>` + `ComparableCmp/InterfaceCmp/ComparisonCmp` defined Task 3, consumed identically in Tasks 4–13; `Powersort` shared by Tasks 10 & 13; kernel entry `SortSpan<T,TC>(Span, Span, TC)` stable across all three classes; `Pair` defined Task 2, reused in benchmarks Task 14 (plus `Struct16`/`Struct128` defined there).
- **Placeholder scan:** implementation-detail bodies marked `{ ... }` exist only where the plan delegates to the vendored upstream source with exact function lists + C# signature mapping (porting tasks) — every such block names the upstream file and functions to port; infrastructure tasks contain full code.
