# csharp-sorts：ipnsort 的 C#/.NET 10 移植

**[ipnsort](https://github.com/Voultapher/sort-research-rs)**（sort-research-rs）的 C#/.NET 10 移植，目标是对 `Array.Sort` 家族做替代评估。移植原则为「架构忠实 + C# 性能惯用法」：算法结构、优化决策与上游一一对应，落地时采用 .NET 的值类型 comparer 特化、`Span<T>`、`ref` 游标等惯用法。

## 1. 项目简介与定位

本项目以**性能为第一指标**评估：ipnsort 移植到 C# 后，能否在 .NET 10 上替代 `Array.Sort`。

验证主战场是最通用入口 `Array.Sort<T>(T[])`（`IComparable<T>` 默认比较路径）：值类型 `T` 上 JIT 将 `CompareTo` 特化为内联的非虚调用，与上游 Rust 单态化处境对等；引用类型 `T` 走接口虚调用，与 `Array.Sort` 内部 `Comparer<T>.Default` 路径同级开销，对比公平。

**排序契约**（与 `Array.Sort` 对齐）：

| 属性 | 承诺 |
|---|---|
| 正确性 | 输出是输入的升序排列（多重集相等） |
| 确定性 | 同一输入 → 每次排序结果逐元素一致；算法无随机性、无隐藏状态 |
| 稳定性 | **不保证**（如同 `Array.Sort`）；ipnsort 是原地不稳定排序 |
| 异常行为 | null 数组 → `ArgumentNullException`；range 越界 → `ArgumentOutOfRangeException`；n ≤ 1 直接返回 |

## 2. 算法与移植要点

**Ipnsort**（上游 [sort-research-rs/ipnsort](https://github.com/Voultapher/sort-research-rs)）——高效、通用、鲁棒的不稳定原地排序。结构：

- 长度 ≤ 20 直接插入排序（通用代码里小 codegen 比快排更值）。
- 否则先做一次 run 检测：整段升序直接返回、整段严格降序原地反转；其余进入快排。
- 快排：伪中位数（`median3_rec`，采样递归阈值 64）选 pivot，并对前驱 pivot 的等值祖先做一次反转分区批处理（大量重复值的主路径）；**分支无关（branchless）Lomuto 循环分区**处理 ≤ 96 字节的元素（每元素固定 1 次比较 + 2 次写入，无分支误判），更大元素走分支 Hoare 以减少搬移；递归深度超限（2·⌊log2 len⌋）时 heapsort 兜底，保证 O(n log n) 最坏情况。
- 小排序（≤ 32）按类型分派：integer 网络（sort9/sort13 最优调度 + 双向归并）、general 路径（sort8 + 归并 ping-pong）、fallback 插入排序（阈值 16）。
- 完全原地：值类型零分配（`stackalloc` scratch），引用类型走 `ArrayPool`（稳态零分配）。

移植要点（架构忠实 + C# 性能惯用法）：

- **branchless 比较映射**：上游 Rust 用 `T: Ord` 单态化出无分支比较；C# 侧以 `IIsLess<T>` 结构体接口 + 值类型 comparer（`ComparableCmp<T>` 等）让 JIT 特化并内联，`where TC : struct` 约束消除接口虚分派。
- **类型特化 codegen**：.NET 泛型即上游单态化的等价物——`Sort<T, TC>` 每个值类型组合独立 JIT，比较器内联后与上游 `#[inline]` 同效；`InvertedCmp<T, TC>` 把「取反 + 换参」折叠成单独单态化，对应上游不同的闭包类型。
- **数组访问**：`ref T` 基引用 + `nint` 游标（`Unsafe.Add`），对应上游裸指针 + `usize`：JIT 消除边界检查，x64 上避免逐元素 `movsxd`。
- **跳过项**：上游 `Drop`/panic-unwind 的写回设施（C# 无 `Drop`：比较器抛异常时数组内容未定义，但绝不内存不安全）；上游 criterion 基准基建（本仓库用 BenchmarkDotNet 复刻，见 §3）。

## 3. Benchmark 方法论与结果

- **矩阵**：
  - 主矩阵 `CoreMatrixBench`：12 分布 × {1k, 100k} × {Array.Sort, Ipnsort}，`int[]`。
  - `TypeMatrixBench`：{int, double, string, Struct16, Struct128} × {Random, RandomD20, RandomS95, Zipfian} × 100k。
  - `ScalingBench`：Random `int[]`，10 → 512k 共 16 个规模。
  - `BaselineBench` / `PairBench`：`Array.Sort` 各入口（generic / `IComparer` / `Comparison`）的入口税对照；Pair（8 字节键值记录）作为不可特化 comparer 的载荷结构点。
  - 小数组：`SmallShapeBench`（14 形状 × 10–200）、`SmallTransitionBench`（256–2048 过渡区）、`SmallTypeShapeBench`（{int, string, Struct16} × 3 形状 × {32, 128}）、`SmallArrayBench`（Random 10–400）。
  - 外部对照：`DotnetPerfSortBench` / `StructComparerBench` 复刻 dotnet/performance 官方用例；`UpstreamBench{Int,U64,String,1K}Matrix` / `UpstreamExtrasBenchMatrix` 按上游 criterion 的 pattern 名、尺寸（0…10k 档）和元素类型（i32/u64/string/1KiB）逐 case 对齐。
- **分布**：`DataGen` 按上游 sort-research-rs 的 `patterns.rs`/`bench.rs` 重写（映射表见 `tests/Sorts.TestData/DataGen.cs` 头部），并用 `Distribution` 枚举补充近乎有序族（SortedSwap1/3、RandomSnl）与结构性/对抗性形状（Plateau、Stagger、Median3Killer），共 18 种。
- **数据新鲜度（pool）**：原地排序会破坏输入；固定模板还会让分支预测器「记住」单一输入，系统性偏袒分支多的实现。`InitPool` 预生成多份不同 seed 的未排序模板，每次 invocation 拷贝下一份进工作数组（对齐上游 `patterns::use_random_seed_each_time` 的意图）；该拷贝在所有被测方法间一致。`DotnetPerfSortBench` 例外：为与 dotnet/performance 官方用例逐 case 对齐，保留其固定 seed 12345 的数据集。
- **配置**：所有矩阵共用一个 `BenchConfig`（单 job、Workstation GC、不强制 GC、MemoryDiagnoser + P90 列 + GitHub markdown 导出），所有 case 都以默认精度跑。
- **复现命令**：

```bash
# 主矩阵（默认精度；多个 glob 用空格分隔）
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*CoreMatrixBench*'
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*TypeMatrixBench*'
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*ScalingBench*' '*BaselineBench*' '*PairBench*'
# 小数组 / 上游对齐（全量较贵，用 --filter 缩小到关心的格子）
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*SmallShapeBench*' '*SmallTransitionBench*' '*SmallTypeShapeBench*'
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*UpstreamBench*'
```

最近一次主矩阵导出（markdown + csv）在 `benchmarks/results/`；速览数值随 CPU/运行时版本变化，引用前请以本机重跑为准。

## 4. 使用示例

```csharp
using Sorts;

Ipnsort.Sort(intArray);                                   // 整个数组（IComparable<T> 路径）
Ipnsort.Sort(intArray, index, length);                    // 区间排序
Ipnsort.Sort(stringArray);                                // 引用类型同样支持

// 显式比较器（null ⇒ Comparer<T>.Default）与委托
Ipnsort.Sort(intArray, Comparer<int>.Default);
Ipnsort.Sort(objArray, (a, b) => a.Key.CompareTo(b.Key));

// 高性能附加内核：结构体 comparer → JIT 值类型特化 + 内联（对标上游单态化）
readonly struct ReverseCmp : IComparer<int>
{
    public int Compare(int a, int b) => b.CompareTo(a);
}
Ipnsort.Sort<int, ReverseCmp>(array.AsSpan(), default);
```

## 5. 测试说明

xUnit 差分 + 确定性测试（先于实现编写）：

- **差分测试**：18 种分布 × 规模扫描（含边界 0/1/2 与 2 的幂/非 2 幂）× 随机种子，输出与 .NET 参考排序逐元素比对（多重集相等 + 升序不变量）。
- **确定性测试**：同一输入排序两次，结果逐元素一致（捕捉隐藏状态/随机化）。
- **单元层**：各内部构件（branchless/分支 Hoare 分区、小排序网络与 general 路径、run 检测、pivot 深度数学、heapsort 兜底）有独立测试。

```bash
dotnet test tests/Sorts.Tests
```

## 6. 致谢与许可

- **[ipnsort](https://github.com/Voultapher/sort-research-rs)**（sort-research-rs）— Lukas Bergdoll，MIT OR Apache-2.0。

本仓库为 ipnsort 的 C# 移植（2026）：算法与优化决策忠实对应上游，代码以 C#/.NET 惯用法重写（`Span<T>`、泛型特化、`ref` 游标等），非逐行翻译。各文件头部保留上游出处标注。

## 7. 已知限制

- **不稳定排序**：与 `Array.Sort` 同类；需要稳定排序的调用方请另选实现（如 LINQ `OrderBy`）。
- **引用类型接口调用与 `Array.Sort` 同级**：引用类型 `T` 的 `IComparable<T>.CompareTo` 为接口虚调用，与 `Array.Sort` 内部 `Comparer<T>.Default` 路径开销同级——该场景无免费午餐。
- **不做 `Sort<TKey,TValue>(keys, items)` 键值对重载**：设计非目标（见 `docs/superpowers/plans/2026-09-19-ipnsort-extension.md`）。
- **比较器异常语义**：比较器抛出异常时数组内容未定义（对应上游 panic 语义；不会内存不安全）。
- **性能数据**：本仓库不追踪历史运行；`benchmarks/results/` 是最近一次主矩阵导出，不同 CPU/运行时版本的数值不可直接横比，结论请以本机重跑为准。
