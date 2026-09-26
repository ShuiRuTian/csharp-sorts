# 小数组优化研究：借鉴 Array.Sort 的叶子与分区策略（10–400）

> **状态：结论已作废（2026-09-26）。** 本文的两条主结论来自**固定模板**基准：
> ①“插入叶子可全局启用”、②“整个小数组（≤256）用 Hoare 分区更快”。
> 改用「每次调用换一份数据」（`InitPool`，对齐上游 `use_random_seed_each_time`）后，
> fresh-data 全矩阵显示两者在随机/重复数据上更慢（叶子：网络 ≥ 插入 约 21/24 例；
> 分区：随机数据 N=1024 慢约 2–2.8×，仅近乎有序数据受益），均已回退。
> 保留本文仅作调查过程记录，**不要据此实现**。

- 日期：2026-09-22
- 目标：`E:\Code\csharp-sorts`（ipnsort 移植）小数组性能
- 分支：`improve/smalln`
- 参考实现：dotnet/runtime `5edb3a2`，物化源码见 `E:\Code\runtime-src\`
- 机器：AMD Ryzen 9 7945HX，.NET 10.0.12 x64（AVX512）

## 1. 动机

日常数据绝大多数是小数组。基准显示 ipnsort 在 n≥10k 已大幅领先 `Array.Sort`（随机 100k ≈ 0.39×），但在 n=30–400 区间反而落后 1.5–2.5×。

## 2. 优化前基线（`SmallArrayBench`，Random int，ratio = Ipnsort ÷ `Array.Sort<T>(T[])`）

| N | 10 | 20 | 30 | 50 | 80 | 100 | 200 | 400 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| ratio | 0.76 | 0.83 | **2.46** | **2.25** | **2.06** | **2.01** | **1.51** | **1.49** |

悬崖在 20→30：`MaxLenAlwaysInsertionSort=20` 之上进入 ipnsort 驱动（run 扫描 + 伪中位数 pivot + 分支无关 Lomuto 分区 + 上游整型网络叶子）。

## 3. 根因（隔离实验）

用快速脚手架 + BDN 逐项隔离：

1. **叶子用「网络」而非插入排序是主因。** 上游整型网络（`sort9/13_optimal` + 双向归并 + 回拷）在 C# 里每次比较**无条件写两个元素**，再加上归并 scratch 与回拷，在叶子规模下开销压过其比较次数优势。把标量类型的叶子换成 `InsertionSortShiftLeft`（阈值 16）后，n=50–400 从 ~1.5–2.25× 降到 ~0.95–1.15×。这正是 `Array.Sort` 的叶子策略（`IntrosortSizeThreshold = 16` + 插入排序）。
   - 大 n 影响：100k 随机 0.40 → **0.41（中性）**。插入叶子可以安全全局启用。

2. **极小/小数组用 Hoare 分区更快。** 分支无关 Lomuto 每元素写两次，长随机序列下靠「无分支」取胜；但小数组里搬移开销占主导，`Array.Sort` 的 branchy Hoare 更快。
   - 但**不能**对「大数组里的小分区」启用 Hoare：实测 100k 随机从 0.41 退化到 **0.52**（分支误判累积）。
   - 于是把条件设为**整个待排数组 ≤ 256 时**才全程用 Hoare，大数组一路 Lomuto。这样小数组受益、大 n 不受影响。

3. **运行时 bool 会污染大 n 的 codegen。** 最初把「是否小数组」当作运行时 `bool` 沿递归下传，100k 随机从 0.41 退化到 **0.49**（约 20%）。改为**泛型模式标记** `IPartitionMode`（`PartitionAuto` / `PartitionHoare`）产生两套 JIT 单态化后，大 n 路径与原始代码完全同构，恢复到 **0.41**。

## 4. 落地改动（全部在 `src/Sorts/Ipnsort/`）

- `IpnSmallSort.cs`：新增 `ScalarLeafThreshold = 16`、`LeafThreshold<T>()`、`SmallSortLeaf<T,TC>()`。标量类型（Network 分派类：≤8B 非托管值类型）的驱动叶子改为插入排序；其余分派类保持上游 `SmallSort`。上游网络实现与 `IpnSmallSortConfig` 保持不变（仍被单元测试覆盖）。
- `IpnQuicksort.cs`：驱动阈值改用 `LeafThreshold<T>()`/`SmallSortLeaf`；`Quicksort` 增加 `TMode` 泛型参数。
- `IpnPartition.cs`：新增 `IPartitionMode` 标记（`PartitionAuto`/`PartitionHoare`）、`SmallArrayHoareMax = 256`；`Partition` 增加 `TMode`，`PartitionAuto` 在 `sizeof(T)≤96` 时用 Lomuto（与原始 codegen 同构），`PartitionHoare` 全程 Hoare；保留二参 `Partition<T,TC>` 重载供测试使用。
- `IpnImpl.cs`：`len ≤ 256` 走 `Quicksort<T,TC,PartitionHoare>`，否则 `Quicksort<T,TC,PartitionAuto>`。

## 5. 优化后（`SmallArrayBench`，同机同 runtime）

| N | 10 | 20 | 30 | 50 | 80 | 100 | 200 | 400 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 改前 | 0.76 | 0.83 | 2.46 | 2.25 | 2.06 | 2.01 | 1.51 | 1.49 |
| 改后 | **0.64** | 0.84 | **0.92** | **0.93** | **0.80** | **0.91** | **0.96** | **1.01** |

**大 n 回归校验**（`CoreMatrixBench` Random 100000）：改前 0.40，改后 **0.41**（中性）。其它分布 100k（RandomD20 0.25、RandomP5 0.21、RandomS95 2.19、RandomTail 2.22）与基线一致，未计入本次范围的最大亏损 RandomS95/Tail 仍待 P1（超长前缀 run 合并）处理。

## 6. 复现

```bash
dotnet test tests/Sorts.Tests                                                  # 22374 passed
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*SmallArrayBench*'
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*CoreMatrixBench*Random,*100000*'
```

快速迭代脚手架（非仓库内）：`C:\Users\15898\AppData\Local\Temp\opencode\sortbench`。

## 7. 注意与后续

- **上游保真偏离（有意，均以测量为准）**：标量叶子不再使用上游网络；小数组分区用 Hoare。网络实现与分派配置仍保留并测试，只是不在默认小数组路径上。若需可加开关回退。
- 200（0.96）与 400（1.01）已基本打平；若要进一步压到明显 <1，可考虑把 `SmallArrayHoareMax` 下调/上调做细调，或在 ≤256 路径内也试「分区到更小阈值 + 插入」的叶子组合。
- S95/Tail 的大 n 亏损与本次无关，见 P1。

## 8. 第二轮调优（2026-09-23，BDN 默认精度，同机）

在既有补丁之上继续用 BDN 做 A/B（`SmallTuneBench`，Random int，200–2048；`SmallArrayBench`，10–400）。跨 run 的 ratio 噪声约 ±0.03，故只采纳跨尺寸一致、且大于噪声的效应。

**已采纳：`SmallArrayHoareMax` 512 → 1024。** 整个数组 ≤1024 时全程 Hoare；2048+ 仍走 Lomuto。

| N | 512（原） | 1024（新） |
|---|---:|---:|
| 768 | 1.02 | **0.98** |
| 1024 | 1.09 | **0.99** |
| 2048 | 1.02 | 1.03 |

把阈值提到 2048 反而使 2048 从 1.02 退化到 **1.10**（大数组上 Hoare 的分支误判开始压过其省下的搬移），所以上界取 1024。≤512 的路径与原来完全一致（小数组数值不变）。

**`InsertTail` 加 `[AggressiveInlining]`（保留）。** JitDisasm 证实原来 `InsertTail` 是独立编译的方法（叶子插入排序每元素一次 `call`）；强制内联后消失。但 BDN 显示 200–2048 绝对耗时与 ratio 均无变化（调用开销被循环体摊平），属中性改动，仅为与上游单态化内联行为一致。

**负结果（均实测，不再走这些方向）：**
- 标量叶子阈值 16 → 32：明显更差（512 1.13、1024 1.17、2048 1.23），确认 16 接近最优。
- 小数组路径改用 median-of-three（诊断，全局关闭伪中位数）：400 1.04→1.09、1024 0.99→1.07、2048 1.03→1.15，**更差**。伪中位数不是瓶颈，保留。
- 400–1024 残留的 ~0–8%（512 最明显，~1.08）来自分区/驱动的常数项，不是 pivot；没有低风险的低垂果实。

**100k 回归校验（同 session main vs smalln）：** Random 100k `main` 0.43 vs 补丁 0.44（无回退）；n=1000 `main` 1.43 → 补丁 ~0.99。RandomD20/P5 100k 0.26/0.22 → 0.22/0.21。

复现：`dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*SmallTuneBench*'`（及 `'*SmallArrayBench*'`）。

## 9. 第三轮：非 int 叶子改自适应插入（2026-09-24）

小尺寸 × 类型矩阵（`SmallTypeShapeBench`）暴露：int 的叶子早已是插入排序，但 `string`/`Struct16`（General 分派）仍走上游 `SmallSortGeneral`（sort8 网络 + 双向归并 + 回拷），在 32/128 上亏损，**近乎有序最惨**（Str SortedSwap3@32 = 1.67×、Struct16 = 2.02×）。原因：网络**非自适应**（无视已有顺序，做满 O(n log n) 比较）+ 引用类型的 `ArrayPool` 往返 + 归并回拷。

A/B（ratio vs `Array.Sort`，QuickBenchConfig）：

| 用例 | baseline（网络叶子, thr32） | A（插入, thr32） | **B（插入, thr16）** |
|---|---:|---:|---:|
| Str Random@32 / @128 | 1.26 / 1.02 | 1.71 / 1.27 | **1.15 / 0.98** |
| Str FewUnique@32 / @128 | 1.62 / 0.93 | 1.97 / 0.73 | **0.82 / 0.79** |
| Str SortedSwap3@32 / @128 | 1.67 / 1.53 | 1.06 / 0.91 | **1.13 / 1.25** |
| Struct16 Random@32 / @128 | 1.16 / 1.33 | 1.22 / 1.26 | **0.92 / 1.03** |
| Struct16 FewUnique@32 / @128 | 1.48 / 1.13 | 1.41 / 0.65 | **0.66 / 0.67** |
| Struct16 SortedSwap3@32 / @128 | 2.02 / 1.87 | 0.87 / 0.71 | **0.87 / 1.01** |
| int（各档） | — | — | 不变（0.64–1.01） |

**B 全面优于 baseline（≤1.25，无回退）**；A 对近乎有序过拟合、却把随机 string 打到 1.7–2.0（阈值 32 下插入排序比较次数过多）。

100k 回归（`LargeTypeLeafBench`，默认精度）：B 与 baseline 逐项一致 → 叶子改动在大 N **中性**：

| 用例 100k | baseline | B |
|---|---:|---:|
| Str Random | 1.09 | 1.08 |
| Str RandomS95 | 1.49 | 1.52 |
| Struct16 Random | 1.51 | 1.53 |
| Struct16 RandomS95 | 2.92 | 3.05 |

落地：`IpnSmallSort.SmallSortLeaf` 所有分派类改插入排序；`LeafThreshold<T>()` 统一 16（`DriverLeafThreshold`）。`SmallSort`/网络实现保留并测试，不在默认路径。

**同时暴露（未修，优先级更高）**：非 int 在 100k 仍亏——Struct16 Random 1.5×、Struct16 RandomS95 **2.9–3.0×**、Str RandomS95 1.5×。根因是 **16B 元素仍走 branchless Lomuto（每元素写两次 ×16B）** + `RandomS95` 丢弃 95% 前缀 run。对应合成报告 P1/P2，比叶子改动影响更大。

## 10. 检查：`stackalloc` 零初始化 / `[SkipLocalsInit]`（2026-09-24）

背景：另一 agent 指出 C# 会对 `stackalloc` 做零初始化（上游 Rust 的 `MaybeUninit` 不清零），问是否有收益。

**机制与定位**：`IpnSmallSort.cs` 仅两处 `stackalloc`——`SmallSortNetwork`（`NetworkScratchLen(32)×sizeof(T)`，int 级 256B）与 `SmallSortGeneral`（`GeneralScratchLen(48)×sizeof(T)`，Struct16 级 768B）。二者只被 `IpnSmallSort.SmallSort` 调用；而 §9 之后默认叶子是插入排序，`SmallSort` **已不在默认路径**（仅测试调用）。引用类型走 `ArrayPool.Rent` + `Return(clearArray:true)`，`[SkipLocalsInit]` 管不到。

**对照实验**（`SmallTypeShapeBench`，临时回退到网络叶子 + 给两个方法加 `[SkipLocalsInit]`，测试 23951 全绿，证明未初始化 scratch 仍 write-before-read）：

| 用例（network 叶子） | baseline | +SkipLocalsInit | 插入叶子 B |
|---|---:|---:|---:|
| Struct16 SortedSwap3@32 / @128 | 2.02 / 1.87 | 1.81 / 1.64 | **0.87 / 1.01** |
| Struct16 Random@128 | 1.33 | 1.26 | **1.03** |
| Str SortedSwap3@32 | 1.67 | 1.65 | **1.13** |
| Str Random@32 | 1.26 | 1.28 | **1.15** |
| int（各档） | — | 不变 | 不变 |

**结论**：只有 Struct16（唯一走 stackalloc 的类型）有 ~5–10% 微弱改善，且在 QuickBenchConfig 噪声内；string（ArrayPool）与 int 无变化。`[SkipLocalsInit]` 在当前默认路径**收益为零**；即便保留网络叶子，也远不如插入叶子（同一批亏损 2.0→0.87 vs 2.0→1.81）。已移除实验属性；网络/General 的 scratch 仍 write-before-read，测试覆盖。
