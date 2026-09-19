# Task 16 — Dual-Review Fix Wave (8 findings)

All 8 findings applied. Build 0 warnings; full suite 67,044/0/0. Commit cc4f50e (+342/-76).

## Per-finding changes

1. **B1 — GlideMerge.cs MergeOneAtBegin/End (~:176-216):** conditional-ref store-select +
   integer cursor advance (DriftMerge.MergeUp/Down idiom); ties preserved (begin→left,
   end→right). Counters use `Unsafe.As<bool, byte>` (see Codegen note).
2. **B2 — GlideQuicksort.cs:** `PartitionBidirNBurst` (+BurstForward/Backward): base-ref +
   int-cursor partition step modeled on DriftQuicksort.PartitionState/PartitionOne; per-element
   SplitOffBegin/Slice machinery replaced by one view shrink per batch; upstream double-store
   ported (sizeOf(T) <= IntPtr.Size), conditional-ref select otherwise. Pivot tracking hoisted
   to batch level (step-index math). Fallback per-element path retained for the public
   Partition seam's disjoint layouts (TwoPieceSpan.TryAsContiguousSpan added). Invert folded
   into the burst via the inverted comparison — hand-traced, 22,348 Glide tests green.
3. **B3 — GlideSmallSort.cs:** one 96-slot scratch per call ([0..32) chunk dst, [32..96)
   network workspace) passed into PartialSortInto; inner Rent/Return(64) removed.
4. **B4 — QuadsortImpl.cs:** 5 branchy cursor sites -> ternary increments.
5. **B5 —** DriftImpl.cs FindExistingRun base-ref rolling-cursor loop (semantics preserved);
   GlideMerge.cs ShrinkStableMerge descending scan ref cursor; GlideQuicksort.cs
   CopyTwoPieceToTwoPiece bulk CopyTo when both sides contiguous. RunLengthAtStart skipped.
6. **B6 — Comparers.cs:** AggressiveInlining on InterfaceCmp/ComparisonCmp.IsLess.
7. **A1 — GlideSort.cs SortSpan:** n < 48 fast path to GlideSmallSort.Sort; 512-element
   ThreadStatic ScratchCache for alloc <= 512 (DriftSort pattern, lazy init).
8. **A2 — DriftSmallSort.cs IsFreezeLike:** `!IsValueType || !IsReferenceOrContainsReferences`
   — reference types take the network (threshold 32) like upstream Freeze; ref-containing
   structs stay on insertion sort. SortSmallGeneral verified free of unmanaged-only ops.
   Tests updated (threshold pins; string path swept 0..=32).

## Benchmarks (ratio vs Array.Sort; clean sequential run 2026-09-19 11:20)

GlideSort Random 100k: **2.58x -> 1.86x** (8,861 -> 6,385 us, -28%).

| Dist (GlideSort) | 100k before | 100k after | 1M before | 1M after |
|---|---|---|---|---|
| Random | 2.58x | 1.86x | 2.56x | 1.76x |
| RandomD20 | 2.46x | 1.31x | 2.39x | 1.29x |
| RandomP5 | 1.83x | 0.94x | 1.72x | 1.04x |
| RandomS95 | 0.65x | 0.40x | 0.53x | 0.45x |

Pure random gains = B2 burst (single quicksort pass); duplicate-heavy gain most from
B1 merges + A1/B3 small-sort paths (RandomP5 now BEATS Array.Sort). DriftSort/QuadSort
int: 1.43→1.46x / 1.63→1.67x (+1-3%, within run StdDev — no real regression).

String 100k: GlideSort Random 1.37→1.10, D20 1.12→0.83, Zipf 0.85→0.71.
DriftSort string flat-to-better (Random 1.21→1.16, D20 0.77→0.77, Zipf 0.63→0.64) —
A2's network path is hazard-free and marginally faster than insertion sort; no regression.

## Codegen note (B1/B2)

JitDisasm (Arm64 Tier1, PartitionBidirNBurst): ternary counters already compiled to
cset+add; `Unsafe.As<bool, byte>` materialization shortened each step 6→4 instructions
and removed re-branching risk. Remaining data-dependent branches are the inlined
`int.CompareTo` overflow-safe cascade inside the comparer — identical before/after,
capping int gains at the observed ~28%. String cases (comparison-dominated) show the
full B1/A1 effect. Follow-up candidate (next wave): same byte-materialization in
DriftMerge.MergeUp/Down + DriftQuicksort.PartitionOne counters.

## Notes

- Intermediate runs today were contaminated by a concurrent JIT-disasm harness; reported
  numbers are from the final clean run.
- README regeneration + full benchmark matrix re-run: separate later dispatch (out of scope).
