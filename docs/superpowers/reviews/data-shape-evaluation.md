# 数据形状（distributions / patterns）评估报告 —— 聚焦小数组

- 日期：2026-09-24
- 目标：`E:\Code\csharp-sorts-wt\smalln`（分支 `improve/smalln`）
- 审计对象：`tests/Sorts.TestData/DataGen.cs`、`benchmarks/Sorts.Benchmarks/*`
- 上游参照：`upstream/sort-research-rs/sort_test_tools/src/patterns.rs`、`upstream/sort-research-rs/benches/bench.rs`
- 已有审计：`docs/superpowers/reviews/array-sort-pattern-fidelity.md`（保真对齐已做，本报告不重复其结论）
- **未运行任何 `dotnet build/test/run`**。`E:\Code\csharp-sorts\bdn_smalldist.txt` 存在（951,632 B）但不含 `BenchmarkRunner: End`、无汇总表，最后几行是 `WorkloadWarmup`——那是**正在跑的 `SmallDistBench`**（`// Found 216 benchmark(s)` = 12 分布 × 9 尺寸 × 2 方法）。按约束跳过其数值，本报告不引用任何未完成的测量。

---

## 0. 结论先行（我的判断）

1. **保真度没问题，但“保真的对象”本身不是为小 N 设计的。** 12 个形状都是 `patterns.rs`/`bench.rs` 的忠实移植；上游默认集（`bench.rs:67-75`）在小 N 上同样退化（见 §2），上游不在乎是因为它扫描 0…10M（`bench.rs:327-330`）主要看大 N。我们把它整包搬到了 10–200，于是把上游的退化也搬了进来。
2. **12 个形状里有 3 个在小 N 直接退化成别的形状**（`RandomP5`→AllEqual；`RandomS95`/`RandomMerge`→Ascending），且 `RandomMerge` 在 N≤30 与 `RandomS95` 逐元素相同（§2）。
3. **`AllEqual` 对 ipnsort 不是“重复值压力”，而是“已排序快路径压力”**，与 `Ascending` 高度冗余：`IpnImpl.FindExistingRun`（`IpnImpl.cs:51-88`）对全等序列返回 `runLen==len`、`wasReversed=false`，直接原样返回（`IpnImpl.cs:24-35`）。真正的重复值压力来自 `FewUnique`/`RandomD20`/`RandomP5`/`Zipfian`。
4. **最大的真实场景缺口是“近乎有序”（incremental perturbation）**：`3sort`/`+sort`/`%sort`/`dither`/`snl`（sorted + 固定 k 个扰动）一个都没有。这正是小数组最常出现的形态（插入/删除/更新后重排）。（注：`array-sort-smalln-study.md:48` 的 `RandomS95 100k = 2.19×` 是**旧 ramp 生成器**下的数；忠实生成器重跑后 100k `RandomS95 = 0.94`，真正的 100k 亏损是 `RandomMerge = 1.24`，见 `bdn_corematrix_faithful.txt`。所以风险区更应盯 `RandomMerge`。）
5. **次大缺口是“小 N × 非 int 类型”的组合**：小数组路径的叶子策略按类型分派（int 走 16 插入；`string`/`Struct16` 走 32 的 general；`Struct128` 走 16 回退，`IpnSmallSort.cs:95-116`），所以 int 的小 N 结论**不能外推**到 string/结构体。当前 `SmallDistBench` 只有 int；`DotnetPerfSortBench` 有 5 类型但只有 unique-random 一种形状。
6. **没有任何对抗性 pivot 形状**（`!sort` median-of-3 killer 等）。ipnsort 用递归伪中位数 + heapsort 兜底（`IpnPivot.cs:20-38`、`IpnQuicksort.cs:41-45`），`!sort` 不会像打普通 M3 那样致命，但它是低成本的鲁棒性探针，且能同时压 `Array.Sort` 的 M3。
7. 本套件缺的是**广度**（形状家族的单点采样 + 无序度参数），不是**保真**。修法很便宜：加 2–3 个近乎有序形状、去掉小 N 退化/冗余形状、补一个小 N 类型点。

---

## 1. 逐个形状评估

判定列含义：**真实?** = 在 N≤200 是否模拟真实负载；**冗余/退化** = 与其它形状重复或在小 N 塌缩。

| 形状 | 生成位置 | 压的算法性质 | 现实场景 | N≤200 真实? | 冗余/退化（含小 N） |
|---|---|---|---|---|---|
| **Random** | `DataGen.cs:36-37,77-82` | 分区平衡、pivot 质量、通用吞吐 | 任意键的小批量排序 | ✅ 是 | 无。全 i32 含负数，标准基线 |
| **Ascending** | `DataGen.cs:39-41` | `FindExistingRun` 全升序快路径（`IpnImpl.cs:24-35`） | 已排序 / 追加后无变化 | ✅ 是 | 与 `AllEqual` 对 ipnsort 冗余（都走快路径） |
| **Descending** | `DataGen.cs:42-44` | 全降序快路径 + `Reverse` | 逆序输入 | ✅ 是 | 无 |
| **AllEqual** | `DataGen.cs:66-68` | **对 ipnsort 是快路径**（非降序、run 到 len） | 全部同值 | ✅ 是 | ⚠️ 对 ipnsort 与 `Ascending` 冗余；对 `Array.Sort` 才是重复值压力。**小 N 下几乎免费** |
| **FewUnique (D4)** | `DataGen.cs:69-71,85-89` | 祖先 pivot 等值批处理（`IpnQuicksort.cs:53-69`）、分区 | 枚举/类别/标志，4 个值 | ✅ 是 | N=10 时可能只有 3 个不同值（给定 dump 已观察） |
| **RandomD20** | `DataGen.cs:51-53` | 同上，中等基数 | 小整数码 / 20 档 | ✅ 是 | 小 N 与 `FewUnique` 部分重叠（N=10 约 8 个不同值） |
| **RandomP5** | `DataGen.cs:54-56,92-100` | 极端重复 + 稀疏离群，**shuffled 故不走快路径** | 稀疏更新（95% 0 + 5% 非零） | ⚠️ N≥50 真实；N≤30 退化 | ❌ **N=10 全 0 = AllEqual**；N=20/30 仅 1 个随机值（见 §2） |
| **RandomS95** | `DataGen.cs:57-59,103-108` | 已排序前缀 run + 小乱尾，插入叶子 | 已排序表追加少量新元素 | ⚠️ N≥50 真实；N≤30 退化 | ❌ **N=10 全排序 = Ascending**；N=20/30 仅 1 个乱元素 |
| **RandomMerge** | `DataGen.cs:60-62,111-117` | 两段各自有序 | 两个有序段待合并 | ⚠️ 同 S95 | ❌ **N=10 全排序**；**N≤30 与 `RandomS95` 逐元素相同**；ipnsort **不做 run 合并**（`IpnImpl.cs:31-34`），但忠实实测 100k：S95 0.94 vs Merge 1.24，故仅 **N≤30** 时≈S95，大 N 不冗余 |
| **Sawtooth** | `DataGen.cs:45-46,121-132` | 多段短 run，pivot 跨 run 采样 | 轮转/交错流 | ⚠️ 中：N=10 是 4 段长 3,3,3,1，更像“局部有序噪声” | 无（但与 Go 的确定性 sawtooth 构造不同） |
| **OrganPipe** | `DataGen.cs:47-49,135-142` | bitonic，pivot 采样 | 两有序段合并 / 山形 | ✅ 是 | 无 |
| **Zipfian** | `DataGen.cs:63-65,146-178` | 重尾重复 + 随机序 | 按热度/频次排键 | ⚠️ 小 N 下≈“1 个主导值 + 尾巴”，与 P5/FewUnique 重叠 | 小 N 冗余 |

**关键洞察（与保真审计不同的角度）：**
- `AllEqual` 走的是 `IpnImpl` 的全序快路径，**没有进入重复值批处理代码**。把它当“重复值用例”会高估覆盖。
- `RandomMerge` 在 ipnsort 里几乎等于 `RandomS95`：ipnsort 只用 `find_existing_run` 判断“整段有序/整段逆序”，**不利用部分 run 做原地合并**（`IpnImpl.cs:31-34` 明确说“users can use a stable sort for that”）。所以“两段有序”与“95% 前缀有序”对 ipnsort 的差别只在乱元素落在头/尾，对 `ChoosePivot` 的 `[0,n/8) [4n/8,5n/8) [7n/8,n)` 采样（`IpnPivot.cs:22-33`）有轻微影响。

---

## 2. 小 N 退化：逐尺寸账本

用 `SplitLen(len, pct) = round(len/100*pct, AwayFromZero)`（`DataGen.cs:182-183`）算出的实际数量：

| 形状 | 量 | N=10 | 20 | 30 | 40 | 50 | 80 | 100 | 150 | 200 |
|---|---|---|---|---|---|---|---|---|---|---|
| `RandomP5` | 随机元素个数 | **0** | 1 | 1 | 2 | 2 | 4 | 5 | 7 | 10 |
| `RandomS95` | 乱尾长度 | **0** | 1 | 1 | 2 | 2 | 4 | 5 | 7 | 10 |
| `RandomMerge` | 第二段长度 | **0** | 1 | 1 | 2 | 2 | 4 | 5 | 7 | 10 |
| `Sawtooth` | 段数 / 段长 | 4/3 | 4/5 | 6/6 | 8/8 | 7/8 | 7/13 | 8/14 | 8/21 | 8/25 |
| `OrganPipe` | 半长 | 5 | 10 | 15 | 20 | 25 | 40 | 50 | 75 | 100 |

由此得到三个**精确**结论（不只是“取整到 100%”）：

1. `RandomP5` 在 N=10 是 **10 个 0**，与 `AllEqual` 完全同构（shuffle 全 0 仍是全 0）；N=20/30 是“95% 零 + 1 个随机值”，本质是 AllEqual + 1 个离群。
2. `RandomS95` 在 N=10 是**全排序**（`sortedLen=10`），与 `Ascending` 同构；N=20/30 是“29/30 有序 + 末尾 1 个乱值”。
3. `RandomMerge` 在 N≤30 与 `RandomS95` **逐元素相同**：`firstRunLen=N`（或 `N-1`）时第二段长度 ≤1，排序单元素是 no-op，与“不排序尾巴”等价。N≥40 才分开。

**影响**：`SmallDistBench`（`SmallDistBench.cs:13-21`）的 12 形状 × 9 尺寸里，N=10 这一列的 12 个点中有 3 个是重复/塌缩的；N=20、30 两列各有 2 个塌缩。也就是说 **108 个 case 里约 9–12 个在测同一件事**，而这些预算本可换成近乎有序形状。退化本身继承自上游（`bench.rs:71-72` 的 `random_p5`/`random_s95` 在 `test_len=10` 同样塌缩），**不是移植 bug**，但“在小 N 矩阵里保留它们”是选择问题。

`DataGenTests.cs` 的不变量（`:56-108`）全部用 N=10000/100000，**永远不会触发上述小 N 退化**，所以退化既没被测、也没被文档化。

---

## 3. 覆盖度对比

### 3.1 我们 vs 各语言标准套件

| 形状家族 | 我们 | CPython `sortperf` | Go `sort_test` | OpenJDK JMH | B&M 祖师爷 |
|---|---|---|---|---|---|
| 随机 | ✅ Random | `*sort` | `rand` | RANDOM | ✅ |
| 升 / 降 | ✅ | `/sort` `\sort` | `copy/reverse` | — | ✅ |
| 全等 | ✅ AllEqual | `=sort` | — | — | — |
| 少量重复值 | ✅ FewUnique(D4) | `~sort` | — | REPEATED(4) | — |
| **增量扰动（sorted+few）** | ❌ | **`3sort`/`+sort`/`%sort`** | **`dither`/`reverse1`/`reverse2`** | — | ✅ 讨论 |
| **M3 killer** | ❌ | **`!sort`** | — | — | ✅ |
| sawtooth | ✅（随机值版） | — | ✅（确定性版） | — | ✅ |
| **stagger** | ❌ | — | **✅** | STAGGER `(i*3)%n` | — |
| **plateau** | ❌ | — | **✅** | — | — |
| **shuffle（交错两半）** | ⚠️ 近似（OrganPipe/RandomMerge） | — | **✅** | SHUFFLE(奇偶交错) | — |
| bitonic / pipe organ | ✅ OrganPipe | — | — | — | ✅ |
| Zipf 重尾 | ✅ | — | — | — | — |
| 基数为 D 的 sweep | ❌ 仅 D4/D20 | — | — | REPEATED 固定 4 | — |
| 有序度百分比的 sweep | ❌ 仅 P5/S95/M95 | — | — | — | — |
| 固定 k 的乱尾 `snl(k)` | ❌ | `+sort`(k=10) | — | — | — |
| 尺寸覆盖 | 10–200 | 100–1025 | 16–1024 | 10–2M | — |

**缺失清单（按重要性）**：
1. **近乎有序族**：`3sort`（升序 + k 次随机交换）、`%sort`（升序 + 1% 随机替换）、`dither`（升序 + 小噪声）、`snl(k)`（升序 + 末 k 个乱）。这是小数组最高频的真实形态，也是决策最相关的一族。
2. **`!sort` median-of-3 killer**：低成本鲁棒性探针。
3. **`stagger`**（模运算确定性排列）：产生大量中尺度逆序，和随机/锯齿都不同。
4. **`plateau`**（上升坡 + 大平台 + 下降坡）：**同时**含结构与重复值，是 `OrganPipe`（有结构无重复）与 `FewUnique`（有重复无结构）的交集，正好压 `IpnQuicksort.cs:53-69` 的等值批处理。
5. **`shuffle`**（交错两半）：近似已有（OrganPipe），但确定性交错比随机值 bitonic 更能复现。
6. **基数/有序度 sweep**：上游 `EXTRA_PATTERNS`（`bench.rs:78-248`）有 `d2..d1024`、`p1..p99`、`s5..s99`、`m5..m99`、`snl_1/2/5/10`、`z1.05..z4`。我们每个家族只取一个点，看不到“在哪个基数/有序度上翻盘”的拐点——对“替代 Array.Sort”的阈值决策很关键。

### 3.2 我们哪些是“非标准”的

“非标准”分两层：

- **形状本身**：`RandomP5`（精确 95% 零）、`RandomS95`、`RandomMerge`、`Zipfian`、`RandomD20`、以及**随机值版**的 `Sawtooth`/`OrganPipe` 都是上游 criterion 套件专有（`bench.rs`），CPython/Go/OpenJDK 没有。它们都合理（稀疏、近有序、重尾、小字母表），但 `RandomP5`/`S95`/`Merge` 的小 N 退化使其在小矩阵里性价比低。
- **选择/参数化**：真正非标准的是**采样方式**——每个家族固定一个参数点（zipf 只 1.0、D 只 4/20、P 只 5、S/M 只 95），而不是像上游那样扫一族。因此我们的 12 个形状覆盖的是“上游 extra 集的一条对角线”。

---

## 4. 对“小数组替代 `Array.Sort`”最有决策价值的形状

决策问题是：**在 n≤200（并延伸到当前仍落后的 200–1024）区间，把 `Array.Sort<T>(T[])` 换成 `Ipnsort` 是否安全、是否值得？** 目前 ipnsort 在小 N 经 Hoare/插入叶子改造后已在 0.8–1.0× 附近（`array-sort-smalln-study.md:43-46`），但结论**只建立在 int + Random 上**（`SmallArrayBench.cs:18` 只喂 `Distribution.Random`）。

按决策价值排序：

| 排序 | 形状 | 为什么对决策关键 |
|---|---|---|
| 1 | **Random** | 唯一通用基线，所有比值都要它做分母 |
| 2 | **近乎有序（新）** | 最高频真实形态；ipnsort 无部分 run 合并，`Array.Sort` 也无；谁更快决定“日常替代”成败 |
| 3 | **RandomMerge / RandomS95 / snl(k)** | 忠实生成器 100k：`RandomS95 = 0.94`（不亏）、**`RandomMerge = 1.24`（最大亏损）**。小 N 是否同样亏？这是最大未验证风险（旧文档的 2.19× 是旧 ramp 生成器的数） |
| 4 | **AllEqual / Ascending / Descending** | ipnsort 快路径的“免费胜利”；确认快路径没被小 N 改造破坏 |
| 5 | **FewUnique / RandomP5 / RandomD20** | 重复值批处理是 ipnsort 的设计卖点；确认小 N Hoare 路径仍兑现 |
| 6 | **OrganPipe / Sawtooth** | 结构化输入，ipnsort 的 pivot 采样 vs `Array.Sort` 的 M3 |
| 7 | **Zipfian** | 小 N 与 P5/FewUnique 重叠，可降级 |
| 8 | **类型维度（string / Struct16）** | 叶子策略按类型分派（`IpnSmallSort.cs:95-116`），int 结论不可外推；copy 成本直接决定 Hoare 是否划算 |
| 9 | **`!sort`** | 鲁棒性底线，但 ipnsort 有 heapsort 兜底，优先级低 |

### 4.1 建议的最小但站得住的小尺寸矩阵

**A. 形状 × 尺寸（int，`QuickBenchConfig` 筛查，`[Config(typeof(QuickBenchConfig))]`）**

10 形状 × 6 尺寸 = 60 case（比现有 108 更便宜）：

| 形状 | 10 | 16 | 32 | 64 | 128 | 200 | 备注 |
|---|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Random | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | 基线 |
| Ascending | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | 快路径 |
| Descending | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | 快路径 |
| AllEqual | ✓ | | ✓ | | | ✓ | 与 Ascending 冗余，抽样即可 |
| FewUnique (D4) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | 重复值 |
| RandomP5 | | | ✓ | ✓ | ✓ | ✓ | **N≥30 才有意义** |
| **SortedPlusK (新, k=1,3)** | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | **补关键缺口** |
| RandomS95 | | | | ✓ | ✓ | ✓ | **N≥40 才有意义**；或换 `snl(k)` |
| OrganPipe | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | 结构 |
| Sawtooth | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | 结构 |

- **删掉**小矩阵里的 `RandomMerge`（N≤30 与 S95 相同、一般≈S95）、`RandomD20`（与 FewUnique 重叠）、`Zipfian`（与 P5/FewUnique 重叠）——这三者在**大 N** `CoreMatrixBench` 里保留。
- 尺寸选取对齐算法阈值：**16**（标量叶子 `ScalarLeafThreshold`）、**32**（general 叶子阈值 `GeneralThreshold`）、**64**（pivot 递归阈值 `PseudoMedianRecThreshold`）、128/200（驱动区），另加 10 覆盖极小区。
- **B. 过渡区（当前仍落后的 200–1024）**：单独 4 形状 × 3 尺寸（{256,512,1024}）＝12 case：`{Random, SortedPlusK, RandomS95, FewUnique}`。这是决定 `SmallArrayHoareMax=1024`（`IpnPartition.cs:58`）是否该保留的地方。
- **C. 小 N × 类型点（新 `SmallTypeShapeBench`）**：3 类型 × 3 形状 × 2 尺寸 ＝ 18 case。
  - 类型：`int`（Network→16 插入）、`string`（General→32）、`Struct16`（General→32）。
  - 形状：`Random`、`SortedPlusK`、`FewUnique`。
  - 尺寸：{32, 128}。
  这样能回答“小 N 的 Hoare/叶子决策在引用类型/结构体上是否也成立”，而这是当前完全空白的格子。

**总成本**：60 + 12 + 18 ≈ 90 case，和现有 `SmallDistBench` 的 108 同量级，但覆盖了真实场景、去掉了退化，并补了类型维度。

### 4.2 新形状的实现草案（放 `DataGen.cs`，接 `Distribution`）

```csharp
// 近乎有序：升序基准 + k 次随机交换（CPython 3sort 思路；k 固定而非百分比，
// 这样在小 N 下仍是“固定个数的逆序”，不会随 N 塌缩）。
case Distribution.SortedPlusSwap:  // k 由参数或枚举档位给（如 1、3）
    for (int i = 0; i < n; i++) a[i] = i;
    for (int t = 0; t < k; t++) {
        int x = r.Next(n), y = r.Next(n);
        (a[x], a[y]) = (a[y], a[x]);
    }
    break;

// 升序 + 末 k 个随机（上游 random_sorted_not_last，bench.rs:51-54）
case Distribution.RandomSnl:       // 固定 k，比 S95 的百分比更真实
    FillRandom(a, seed);
    int sorted = Math.Max(n - k, 0);
    Array.Sort(a, 0, sorted);
    break;

// plateau：上升坡 + 平台 + 下降坡（Go plateau 思路；结构 + 重复值）
case Distribution.Plateau: ...     // 上升 0..h, 平台常数 c, 下降 h..0

// stagger：模运算确定性排列（OpenJDK/Go 思路）
case Distribution.Stagger:         // a[i] = (i*3) % n 之类
```

> `!sort`（median-of-3 killer）建议照抄 CPython `Tools/scripts/sortperf.py` 的构造，不要自己发明；它主要是鲁棒性探针，可放在最后的“对抗”小集里。

---

## 5. 生成器正确性 / 保真性问题

按严重度排列。总体：**没有发现会导致错误排序结论的正确性 bug**；以下是保真与复现性问题。

| # | 严重度 | 位置 | 问题 | 建议 |
|---|---|---|---|---|
| G1 | 低 | `DataGen.cs:182-183` vs `bench.rs:31-36,74,100` | **乘法结合序不同**：我们 `len/100.0*pct`，上游 `random_sorted`/`random_merge` 是 `len*(pct/100.0)`。代数等价但浮点可能差 1 ULP。我已逐位分析：本矩阵的 .5 边界尺寸（10/30/50/150）两式**恰好同值**，故当前无影响；但任意 N 下可能跨舍入边界。 | 加一条针对 N=10..200 全扫的 `SplitLen` 边界测试；或直接按上游分组用两种结合序 |
| G2 | 低 | `DataGen.cs:124-126` | `SawAscending` 对 `chunkSize<1` 做了保护，上游 `chunks_mut(0)` 会 panic。N≥10 时该分支不可达（死代码），是**有意的健壮性偏离** | 保留，但注释里点明“上游会 panic” |
| G3 | 低 | `DataGen.cs:124` | `Math.Round(Math.Log2(...))` 默认 `ToEven`，上游 `f64::round` 是 away-from-zero。整数 n 的 `log2` 不落在 .5 上，**实际无分歧** | 可加 `MidpointRounding.AwayFromZero` 求心安 |
| G4 | 低 | `DataGen.cs:146-178` | `Zipf` 用精确逆变换，上游用 `zipf` crate 的拒绝采样。大 N 同律，**小 N（如 10）的精确 pmf 可能与 crate 实现有偏差** | 若要在小 N 对比上游，注明“律一致、实现不同” |
| G5 | 低 | `DataGen.cs:103-142` | `RandomS95`/`RandomMerge`/`SawAscending`/`PipeOrgan` 内部调用 **`Array.Sort`** 生成数据，因此输出依赖 .NET 运行时版本（非纯 RNG）。上游同样用 `sort_unstable`。 | 文档已提“固定 seed 可复现”，应补充“仅限同一运行时” |
| G6 | 低 | `DataGen.cs:77-82` | `FillRandom` 用 `System.Random`，其算法在 .NET 6 变过；跨运行时版本序列不保证一致 | 同上，属复现性 caveat |
| G7 | 信息 | `DataGen.cs:199-204` | `ToF64` 的 NaN→0 会产生约 0.05% 的额外相等对（`u64∈[2^63-2^52,2^63)` 落入 NaN 区间）。忠实于上游，但“Random double”并非全不同 | 无需改；评估 double 重复值时心里有数 |
| G8 | 中（覆盖） | `DataGenTests.cs:56-108` | 所有形状不变量只测 N=10000/100000，**从不测小 N 退化**；`RandomMerge`/`RandomS95` 在 N≤30 相同也无人断言 | 加小 N 不变量：`P5(n=10)` 全等、`S95(n=10)` 全序、`Merge(n=20)==S95(n=20)` 等，把退化“钉死”成已知事实 |
| G9 | 信息 | `DataGen.cs:228-234` | `Pair` 只按 Key 比较（`Pair.cs` 无 payload tiebreak），`IsDeterministic` 靠逐元素相等间接查确定性；`IsCorrectPermutation` 的 `Canonical` 才含 payload | 现状合理，无需改 |

**关于 `bdn_smalldist.txt`**：存在但未完成（无 `BenchmarkRunner: End`、无 markdown 汇总表；内容为 `SmallDistBench` 216 个 case 的进行中输出，尾部是某 case 的 `WorkloadWarmup`）。按约束**未纳入**本评估。它跑完后可用于验证 §2 的退化预测（N=10 的 RandomP5/AllEqual、RandomS95/Ascending 应给出接近的绝对耗时）。

---

## 6. 一句话给上游/下游

- 对**保真审计**：`DataGen` 忠实，无需再动形状定义；要动的是**形状选择**与**小 N 参数化**。
- 对**决策**：先补“近乎有序 + 小 N × 类型”两类格子，再删小矩阵里塌缩/冗余的 3 个形状——用同样的预算得到对“替代 `Array.Sort`”真正有信息量的矩阵。
