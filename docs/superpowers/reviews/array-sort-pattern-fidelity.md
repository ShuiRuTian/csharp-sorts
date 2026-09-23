# 用例生成保真审计与对齐（vs sort-research-rs）

- 日期：2026-09-24
- 参照：`upstream/sort-research-rs/sort_test_tools/src/patterns.rs`、`benches/bench.rs`（`pattern_providers` 默认集 + `extra_pattern_providers`）
- 目标：`E:\Code\csharp-sorts-wt\smalln`（分支 `improve/smalln`）
- 结论：原 `DataGen` **不是**上游规律的忠实移植；已按上游公式重写，并补了生成器不变量测试。

## 1. 上游实际跑什么

`bench.rs:67-75` 的**默认**分布只有 7 个：

`random`、`random_z1`(zipf 1.0)、`random_d20`、`random_p5`、`random_s95`、`ascending`、`descending`。

`saw_ascending / saw_descending / saw_mixed / pipe_organ / random_d* / random_p* / random_z* / random_s* / random_snl_* / random_m*` 全在 `extra_pattern_providers`，**仅设 `EXTRA_PATTERNS` 时才跑**（`bench.rs:78-252`）。关键点：这些结构化分布几乎都是「**先取随机样本，再对每个 run 排序**」造出来的，不是确定性斜坡。

## 2. 审计：旧 `DataGen` vs 上游

| 旧分布 | 上游 | 问题 |
|---|---|---|
| Random | `rng.gen::<i32>()`（全 i32，含负数） | 用 `Random.Next()`，只有 `0..int.MaxValue`，**无负数**、值域减半 |
| RandomD20 | `random_uniform(len, 0..20)` = 0..=19 | `Next(21)` = 0..=20，**差一** |
| RandomP5 | 精确 95% 零 + 5% 随机，再 shuffle | 每位置独立 5% 随机（近似，非精确） |
| RandomS95 | `random_sorted(95)`：随机值，前 95% 排序 | 前缀是 `a[i]=i` 的**递增斜坡**，值分布完全不同 |
| RandomTail | 上游无此名；最近 `random_snl_*`/`random_merge` | **与 RandomS95 完全同代码同 seed，是重复分布** |
| Sawtooth | `saw_ascending(len, round(log2 len))`：随机值、齿数 log2 n | `i % (n/5+1)`，固定 5 齿、周期小值 |
| OrganPipe | `pipe_organ`：随机值，前半升序、后半降序 | `0..n/2` 确定性斜坡上下 |
| Zipfian | `random_zipf(1.0)`（zipf crate，1..=n） | `n/(1+u²n)` 近似，范围 0..n |
| AllEqual | 常数 66 | 42（无影响） |
| FewUnique | `random_d4` = `0..4` | `Next(4)*1000`，基数同、取值不同 |
| — | `random_merge`、`random_snl_*` | 缺失 |

影响：`Random/Ascending/Descending` 的结论不受影响；`RandomS95/RandomTail` 的比值不可与上游 `random_s95` 对比；Sawtooth/OrganPipe 是自造规律。

## 3. 对齐后的映射（`tests/Sorts.TestData/DataGen.cs`）

| 名称 | 上游公式 |
|---|---|
| Random | `patterns::random`（全 i32） |
| Ascending / Descending | `patterns::ascending` / `descending` |
| Sawtooth | `patterns::saw_ascending(len, round(log2 len))` |
| OrganPipe | `patterns::pipe_organ` |
| RandomD20 | `patterns::random_uniform(len, 0..20)` |
| RandomP5 | `random_x_percent(len, 5.0)` |
| RandomS95 | `patterns::random_sorted(len, 95.0)` |
| **RandomMerge**（原 RandomTail） | `patterns::random_merge(len, 95.0)` |
| Zipfian | `patterns::random_zipf(len, 1.0)`（逆变换精确采样 1..=n） |
| AllEqual | `patterns::all_equal`（66） |
| FewUnique | `patterns::random_uniform(len, 0..4)` |

非 int 变换也对齐上游：string = `shift_i32_to_u32(val)` 的 10 位十进制（`bench.rs` FFIString）；double = `extend_i32_to_u64(val)` 位重解释为 f64、NaN→0（`F64NonNanCmp`）。Pair 上游无对应，保留。

`RandomTail` 重命名为 `RandomMerge`（避免与 `RandomS95` 重复），更新 `Distribution`、`MatrixBase`、README。

## 4. 方法学差异（保留）

- 上游每次基准用**新随机种子**（`use_random_seed_each_time` + `ensure_true_random`）；此处用固定 seed `20260918` 保证可复现（分布规律一致，具体实例不同）。
- 上游用 criterion 现场生成；此处 BDN `GlobalSetup` 预生成 + 每 invocation 拷贝模板。

## 5. 验证

- `tests/Sorts.Tests` 新增 9 条生成器不变量（全 i32 含负数、D20 值域、P5 零占比、S95 前缀有序、Merge 两段有序、Sawtooth 分块有序、OrganPipe 升降、Zipf 重尾、AllEqual 常数）→ **22383 passed / 0 failed**。
- 重跑 `CoreMatrixBench`（12 分布 × {1k, 100k}）与 `SmallArrayBench`/`SmallTuneBench`，结果见 `bdn_corematrix_faithful.txt` 等。
