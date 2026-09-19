# Task 16 Agent A — Algorithm Fidelity Review
## Verdict: FINDINGS (2 Important, 2 Minor; no Critical — no wrong-algorithm behavior shipped)

## Findings

1. **Important — GlideSort entry drops upstream's two allocation-free fast paths.**
   Port: src/Sorts/GlideSort.cs:52-59 (SortSpan). Upstream: upstream/glidesort/src/lib.rs:132-139, 272-295.
   Upstream dispatches n < SMALL_SORT(48) straight to small_sort (no scratch, no merge-stack
   machinery) and uses a 4096-byte stack scratch when 4096/sizeOf(T) >= n/2
   (glidesort_with_max_stack_scratch). The port always computes GlidesortAllocSize and
   heap-allocates: for 2 <= n < 48 it allocates a 48-element buffer plus a MergeStack (sealed
   class + LogicalRun[64] + byte[64]; GlidesortImpl.cs:68-71,115 — upstream stack-allocates it)
   and enters glidesort->stable-quicksort before the base case reaches the same small_sort.
   Output order and comparison count are identical (the base case precedes pivot selection), so
   this is a pure allocation/i-cache entry-profile deviation — but tiny sorts are the most common
   real-world calls for an Array.Sort replacement, and all n <= 2*(4096/sizeOf(T)) lose upstream's
   allocation-free band. README documents "per-call heap alloc" without noting the missing fast
   paths. Fix: `if (v.Length < GlideSmallSort.SmallSort) { GlideSmallSort.Sort(v, cmp); return; }`
   at the top of SortSpan; optionally a per-(T,thread) small-scratch cache for the stack-scratch band.

2. **Important — DriftSort Freeze mapping demotes all managed types from network small-sort (threshold 32) to insertion sort (threshold 16).**
   Port: src/Sorts/Driftsort/DriftSmallSort.cs:32, 46-56, 311-317. Upstream: upstream/driftsort/src/smallsort.rs:50-63; lib.rs:154-161.
   Rust Freeze means "no interior mutability" — String/Box/&T ARE Freeze, so upstream runs
   sort_small_general (branchless network, SMALL_SORT_THRESHOLD=32) for managed element types.
   The port's IsFreezeLike (value type containing no managed references) maps them to insertion
   sort, threshold 16 — changing the quicksort base-case boundary, the eager boundary (2*thr),
   the eager run length, and comparison counts for string[] and every reference-carrying T. The
   mapping is documented at DriftSmallSort.cs:304-310, but the consequence is not called out
   anywhere (README §8's reference-type note covers only interface-call cost). The network's
   "compares on outdated copies" hazard does not apply to C# reference types (a copied reference
   is the same object), so widening IsFreezeLike is safe and more faithful. Fix: IsFreezeLike =
   true for all T (or at least reference types), or document the deviation in README §8.

3. **Minor — eager fallbacks lose run detection and powersort merge order.**
   Port: src/Sorts/Glidesort/GlideMerge.cs:649-684 (EagerSort); src/Sorts/Driftsort/DriftQuicksort.cs:117-148 (EagerSortImpl).
   Upstream: upstream/glidesort/src/stable_quicksort.rs:403; upstream/driftsort/src/quicksort.rs:32
   (both call the full eager main loop — run detection + powersort merge tree). On recursion-limit
   exhaustion the port chops into fixed-width runs with no find_existing_run and merges naively
   pairwise. Output stays correct and stable; only the rare worst-case path's adaptivity and merge
   order deviate. Documented in the port code headers, not in README.

4. **Minor — GlideSmallSort ports only the may_call_ord_on_copy branch, i.e. upstream's `unstable` feature build.**
   Port: src/Sorts/Glidesort/GlideSmallSort.cs:256-259, 287-296. Upstream: upstream/glidesort/src/util.rs:155-184; small_sort.rs:137, 189 (guarded loops at :141-160/:200-219).
   In the default (stable) upstream build may_call_ord_on_copy() is false for ALL types, so
   upstream runs the imbalance-guarded loops. For a valid comparator both branches move elements
   identically in the symmetric fixed-count merges; the difference is confined to broken
   comparators (the port may read across the adjacent left/right span boundary — still in-bounds
   of the underlying buffer, as its own comment notes). Documented; no action needed beyond
   noting the doc comment cites unstable-build semantics.

## Documented-divergence inventory confirmation
- gap_guard.rs skipped as a type: confirmed — success-path copies inlined (GlideMerge.cs:6-15); overlap impossible with separate Span allocations.
- tracking.rs skipped: confirmed — debug instrumentation only, zero algorithmic effect, absent from the port.
- MaybeUninit→Span collapse: confirmed — write-before-read by construction; glidesort SMALL_SORT sanity fallback replaced by Debug.Assert (GlidesortImpl.cs:109-112).
- Pivot-snapshot in drift quicksort: confirmed as documented (DriftQuicksort.cs:7-15) — pivot copied to a local, re-copied to its scratch slot unconditionally (upstream's interior-mutability branch, taken universally).
- ThreadStatic scratch patterns: confirmed — QuadSort 64-element SmallScratch (QuadSort.cs:13-28), DriftSort 512-element stand-in for the 4096-byte stack buffer (DriftSort.cs:77-112), README §3/§8 with the reentrancy caveat.
- Known-minor drift MIN_SMALL_SORT_SCRATCH_LEN 50 vs upstream 49: confirmed documented (DriftSmallSort.cs:24-28); satisfies len+17 for len <= 32.
- Quadsort gcc/QUAD_CACHE branchy variants skipped (clang forms only): confirmed (QuadsortImpl.cs:2, 611; ParityMerge).
- Rust Drop/panic-recovery guards not ported (comparator exception ⇒ unspecified buffers): confirmed, uniform policy in every file header.
- Glide LeftIfNewPivotEqualsCopy (Copy-type strategy) skipped: confirmed (GlideQuicksort.cs:252-254) — equivalent for valid comparators (pivot lands in geq).
- Drift has_direct_interior_mutability — safe branch taken universally: confirmed (DriftQuicksort.cs:8-11).
- QuadReversal defensive len<3 handling: confirmed documented (QuadsortImpl.cs:320-322).

## Checked-but-clean
- QuadSort entry flow: guards → TailSwap(n<32) / QuadSwap → 4MiB-capped swapSize growth loop → QuadMerge → RotateMerge, OOM stack[512] fallback, QuadSwap==1 descending-run early-out, quadsort_swap n<=96 — all match quadsort.c:1065-1117.
- QuadSort stability at every merge site: head tie→left, tail tie→right (QuadsortImpl.cs:36-52), cross_swap tie→left-lower (:824-831, :973-982), parity finals, monobound lower-bound — stable, matches quadsort.h:38-108.
- quad_swap analyzer state machine (ordered/reversed/not_ordered, Duff tail switch, in-order-flag pair swaps, 32-block parity pass) matches quadsort.c:257-414.
- cross_merge / partial_forward / partial_backward / rotate_merge_block / trinity_rotation / tail_merge structure, comparisons, 65536 cap match quadsort.c:416-1052.
- Glidesort main loop, 7-arm logical_merge ordering, CreateRun thresholds (run^2 >= len/2, strict-descending reverse) match glidesort.rs:30-285.
- BranchlessMergeState: begin tie→left, end tie→right, DrainRemainder left-then-right; finish_merge/interleaved loop shapes; num_safe_merge_ops — match branchless_merge.rs.
- merge_reduction (shrink/crossover/splitpoints) strict-less polarity and binary-search invariants match merge_reduction.rs.
- physical_merge/triple/quad data movement incl. small-scratch degradation paths matches physical_merges.rs.
- GlideQuicksort: two-piece pivot indices, median3_rec thresholds, partition polarities + inverted equals-batch, take() four buckets, pivot landing bookkeeping, overlapped both-small smallsorts, strategy dispatch — match stable_quicksort.rs / pivot_selection.rs.
- Driftsort main loop (66-slot run stack, sqrt_approx, MIN_SQRT_RUN_LEN=64, dummy-run collapse), logical_merge scratch-fit guard, create_run eager/lazy, find_existing_run — match drift.rs.
- DriftMerge merge_up tie→left / merge_down tie→right, cursor arithmetic, Drain — match merge.rs (stable).
- DriftQuicksort stable_partition (reverse-side copy-back, unroll-4 sizeOf<=16 gate), choose_pivot/median3, equal-partition inversion — match quicksort.rs / pivot.rs.
- Powersort shared math identical to both upstream copies (ceil(2^62/n) scale factor, CLZ-of-XOR depth).
- Constants all match upstream: glide 48/32/64/1MiB/1GiB; drift 20/8MB/64/64; quadsort 262144/4194304/65536/512/96/32s.
- Kernel discipline: zero raw CompareTo/.Compare calls outside Comparers.cs; cmp(a,b)>0 ⇒ IsLess(b,a) and cmp(a,b)<=0 ⇒ !IsLess(b,a) at every comparison site read.
- Scratch invariants: quicksort only ever sees runs <= scratch.Length (glide concat guard; drift canFitInScratch); drift merge min-side <= scratch — no seam can throw for a conforming caller.
