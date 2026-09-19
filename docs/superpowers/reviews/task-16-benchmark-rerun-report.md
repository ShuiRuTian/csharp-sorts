# Task 16 — Benchmark re-run + README regeneration after fix wave cc4f50e

## Per-matrix run status (sequential, default accuracy, background)

| Matrix | Cases | Wall time | Status |
|---|---|---|---|
| CoreMatrixBench | 144 | 21 min 00 s | OK |
| TypeMatrixBench | 80 | 12 min 37 s | OK |
| ScalingBench + BaselineBench | 55 | 1 h 33 min 27 s | OK |

Notes: results dir wiped before re-runs (fix wave's intermediate runs at 10:33/11:01 had contaminated partial exports; stale wait-loop from prior session killed). Matrix 3 first attempt used repeated `--filter` flags — BDN rejects duplicate options (error hidden by tail pipe); re-ran with space-separated globs. Full suite NOT re-run per dispatch instruction (fix report already documents 67,044 green).

## Headline: before → after (vs Array.Sort<T>(T[]), int[])

| Cell | before | after | Delta |
|---|---|---|---|
| GlideSort Random 100k / 1M | 2.58x / 2.58x | 1.79x / 1.81x | -30% |
| GlideSort D20 100k / 1M | 2.46x / 2.39x | 0.97x / 1.35x | -60% / -44%, 100k now WINS |
| GlideSort Zipfian 100k / 1M | 2.92x / 2.43x | 1.43x / 1.42x | -51% / -41% |
| GlideSort RandomP5 100k / 1M | 1.83x / 1.75x | 1.02x / 0.96x | ~parity / now WINS |
| GlideSort FewUnique 100k / 1M | 1.94x / 1.67x | 0.96x / 0.92x | now WINS |
| DriftSort Random 100k / 1M | 1.43x / 1.42x | 1.43x / 1.40x | noise |
| QuadSort Random 100k / 1M | 1.63x / 1.57x | 1.65x / 1.61x | noise |
| DriftSort ascending/OrganPipe/etc | 0.04–0.26x | 0.03–0.26x | slightly better, OrganPipe 1M now 0.03x |
| DriftSort RandomP5 100k | 0.55x | 0.59x | still wins |
| DriftSort FewUnique 100k / 1M | 0.89x / 0.75x | 0.91x / 0.78x | still wins |

String 100k: GlideSort Random 1.37→1.11x, D20 1.12→0.81x, Zipf 0.85→0.65x, S95 0.28→0.27x (A2 Freeze-widening = real material string improvement, documented in README type-matrix note). DriftSort string Random 1.21→1.18x, Zipf 0.63→0.63x, D20 0.77→0.76x.

Scaling plateau (random int): DriftSort ~1.4x, QuadSort ~1.6x, GlideSort ~1.8x from 16k through 10M; DriftSort 0.97x at 8k. Baseline zoo: ArraySort_Generic 3.65 ms; IComparer 4.12 (+13%); Comparison 5.56 (+52%); LINQ 5.83; DriftSort 4.93 (beats Comparison+LINQ); QuadSort 5.78; GlideSort 6.31.

## README regenerated

All 8 sections kept. Updated: 速览 speed table (all 24 cells), environment stamp (fix-wave cc4f50e noted), reading-guidance bullets (GlideSort ~2.6x→~1.8x; duplicate-value bullet now documents D20/P5/FewUnique flips to beating Array.Sort — headline-worthy per dispatch), type-matrix table + A2 note, scaling plateau + small-N loss range (~1.7–22x, worst 1k RandomP5 GlideSort 27x — small-N got WORSE for GlideSort: 1k Random 9.5→22.5x, P5 32→27x; B3/A1 trades small-N for large-N), baseline zoo numbers, 已知限制 + eager-fallback run-detection/powersort-merge-order deviation (parked Agent A finding). Allocations unchanged (verified: 0 / 400KB–4MB / 401KB–2MB / 400KB–4MB).

Files regenerated: benchmarks/results/ — all 16 (8 github.md + 8 csv) replaced.

Commit: f9fd2ab "docs: regenerate README and benchmark results after perf fix wave" (17 files, +645/-644).

## Concerns

- GlideSort 1k regression is real (sub-1% StdDev): Random 9.5→22.5x, D20 7.4→24.1x, Zipfian 6.9→21.2x vs Array.Sort. README small-N bullet reports the honest new range/worst-cases. If 1k latency matters, the A1 small-sort fast path (n<48) + B3 single-scratch trade should be revisited.
- QuadSort Struct128 Random 1.95x is now the worst 100k random cell (was tied before) — within its historical range, reported as-is.
- Matrix 3 needed a relaunch (repeated --filter rejected); ~3 min lost, no data impact.
- PairBench again not run (outside the dispatch's three commands; consistent with previous round).
