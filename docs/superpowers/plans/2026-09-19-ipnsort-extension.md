# ipnsort Extension Plan (csharp-sorts)

> **For agentic workers:** Execute via subagent-driven-development, task-by-task. Parent spec: `docs/superpowers/specs/2026-09-18-csharp-sorts-design.md` — ALL its Global Constraints, conventions, and rulings apply unchanged (contract: correct + deterministic, no stability promise; depth: architecture-faithful + C# idioms; kernel `IIsLess<T>`; per-task review with upstream fidelity verification; ≤120-line output discipline).

**Goal:** Port ipnsort (Voultapher's unstable in-place sort, Rust std `sort_unstable` candidate) as `Sorts.Ipnsort` — the Array.Sort-ecosystem twin (unstable + in-place + zero-allocation), completing the Array.Sort replacement matrix.

**Upstream:** vendored at `upstream/sort-research-rs/ipnsort/src/` (1,548 lines: lib.rs 217, quicksort.rs 381, smallsort.rs 783, pivot.rs 90, heapsort.rs 77). License: sort-research-rs repo — MIT OR Apache-2.0 (verify LICENSE file at `upstream/sort-research-rs/LICENSE`).

**Architecture:** pdqsort-family introsort: n≤20 insertion → find_existing_run early exit → quicksort (ancestor-pivot equal-partition) with depth limit → heapsort fallback. **In-place, zero allocation** — SortSpan takes scratch: default and ignores it. Two partition impls: `partition_lomuto_branchless_cyclic` (sizeof≤96, the novel Bergdoll/Peters branchless Lomuto with cyclic permutation) and `partition_hoare_branchy_cyclic` (large T). Smallsort: Network (int-like: sort9/sort13 optimal networks + parity-style bidirectional merge) / General (Freeze, same family as DriftSmallSort) / Fallback (insertion).

**Reuse (verified in-repo code):** `DriftQuicksort.ChoosePivot` (upstream pivot.rs is near-identical — only an added len<8 abort that our port handles in-bounds), `DriftImpl.FindExistingRun` (make it accessible or copy with cross-ref), `DriftSmallSort` General-family helpers where signatures match.

## Tasks

### Task 17: skeleton + RED baseline
- Commit vendored upstream (done in working tree — `upstream/sort-research-rs/`; keep `.git` removed).
- `src/Sorts/Ipnsort.cs`: public API (5 overloads, Array.Sort-shaped, attribution header), `SortSpan<T,TC>(Span<T> v, Span<T> scratch, TC cmp)` throws NIE (scratch ignored — in-place kernel), `Sort<T,TC>(Span<T>, TC)` wired via ComparerAdapter (in-place, no TODO — direct).
- 4 concrete harness classes: IpnsortTests/IpnsortDoubleTests/IpnsortStringTests/IpnsortPairTests (copy Glide pattern).
- ApiContractTests: extend the 3 registries with Ipnsort.
- RED: all Ipnsort theories fail NIE; everything else green.

### Task 18: smallsort — Network + General + Fallback dispatch
- Port `small_sort_network` (sort13_optimal, sort9_optimal with their codegen'd comparison schedules — hand-port the swap sequences exactly; swap_if_less branchless), `small_sort_general` family (REUSE DriftSmallSort's sort8_stable/merge_up/merge_down where identical — verify via upstream diff; cross-reference), `small_sort_fallback` (reuse InsertionSortShiftLeft).
- Dispatch `choose_unstable_small_sort`: Network = (Copy-like: unmanaged value type) && efficient in-place swap && sizeof*32 ≤ 4096; General = Freeze-like && sizeof*48 ≤ 4096; Fallback else. Thresholds 16/32/32, scratch lens 48/32.
- File: `src/Sorts/Ipnsort/IpnSmallSort.cs`. Tests: sizes 0..32 sweeps × patterns; dispatch-class tests incl. BigStruct128 → Fallback, string → General, int → Network.

### Task 19: partition + heapsort (the heart)
- Port `partition` (swap pivot to front → inst_partition → swap to num_lt), `partition_lomuto_branchless_cyclic` (loop_body with gap-value cyclic permutation, unroll 2 for sizeof≤16, the cleanup loop), `partition_hoare_branchy_cyclic` (left/right scans, cyclic first-swap GapGuard).
- GapGuard panic-recovery → C#: for the Lomuto path, the gap value bookkeeping must be maintained as EXPLICIT writes (no Drop); on comparer exception, duplicate/lost elements acceptable per contract but NO memory unsafety. Verify write-before-read discipline.
- Port `heapsort` + `sift_down` (branchless child select).
- File: `src/Sorts/Ipnsort/IpnPartition.cs` + `IpnHeapsort.cs`. Tests: partition unit tests (num_lt correctness, pivot placement, all-equal/already-partitioned edges), heapsort standalone, in-place-ness (zero alloc assert via GC.GetAllocatedBytesForPrecision around a sort of n=10_000 — allow small constant for test scaffolding).

### Task 20: quicksort driver + main entry → FULL suite green
- Port `quicksort` driver (ancestor equal-partition with inverted comparator — same invert: bool pattern as DriftQuicksort; recurse-left/loop-right; limit handling → heapsort), `unstable_sort` entry (n≤20 insertion; find_existing_run full-slice early exit; limit = 2·ilog2(len|1)).
- Reuse DriftQuicksort.ChoosePivot + DriftImpl's FindExistingRun (expose internal or copy with cross-ref comment — implementer's choice, report it).
- Wire SortSpan. GREEN: full suite (67,044 + ~5,580 Ipnsort theories) all green.

### Task 21: benchmarks
- Add Ipnsort method to: MatrixBase (Core), all 5 TypeMatrix classes, ScalingBench, BaselineBench, PairBench. Baseline comparison stays Array.Sort_Generic.
- Smoke: CoreMatrix Random 100k + string matrix — record vs Array.Sort AND vs the three stable ports.

### Task 22: full matrix re-run + README
- Re-run all matrices (sequential, default accuracy). README: new Ipnsort column in 速览, updated 阅读指引 (the unstable in-place story: direct Array.Sort competitor), 归属 section.

### Task 23: dual review (fidelity + perf) + fixes + affected-benchmark re-run
- Same as Task 16: Agent A fidelity (upstream cross-check, entry-to-entry), Agent B perf idioms (branchless verification of the Lomuto cyclic — THE critical hot loop; zero-alloc claim audit).
- Fix wave + scoped re-review + affected benchmark re-run + README touch-up if numbers move.
