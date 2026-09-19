# csharp-sorts：glidesort / quadsort / driftsort 的 C# 移植

三个现代自适应排序算法——**DriftSort**、**GlideSort**、**QuadSort**——的 C#/.NET 10 移植，目标是对 `Array.Sort` 家族做替代评估。移植原则为「架构忠实 + C# 性能惯用法」：算法结构、优化决策与上游一一对应，落地时采用 .NET 的值类型 comparer 特化、`Span<T>`、`ThreadStatic` 缓冲等惯用法。

## 1. 项目简介与定位

本项目以**性能为第一指标**评估：把 [driftsort](https://github.com/Voultapher/sort-research-rs)（sort-research-rs 中的 driftsort）、[glidesort](https://github.com/orlp/glidesort)、[quadsort](https://github.com/scandum/quadsort) 移植到 C# 后，能否在 .NET 10 上替代 `Array.Sort`。

验证主战场是最通用入口 `Array.Sort<T>(T[])`（`IComparable<T>` 默认比较路径）：值类型 `T` 上 JIT 将 `CompareTo` 特化为内联的非虚调用，与上游 Rust 单态化处境对等；引用类型 `T` 走接口虚调用，与 `Array.Sort` 内部 `Comparer<T>.Default` 路径同级开销，对比公平。

**排序契约**（与 `Array.Sort` 对齐）：

| 属性 | 承诺 |
|---|---|
| 正确性 | 输出是输入的升序排列（多重集相等） |
| 确定性 | 同一输入 → 每次排序结果逐元素一致；算法无随机性、无隐藏状态 |
| 稳定性 | **不保证**（如同 `Array.Sort`）。当前移植因源算法设计而构造性稳定，但契约不承诺 |
| 异常行为 | null 数组 → `ArgumentNullException`；range 越界 → `ArgumentOutOfRangeException`；n ≤ 1 直接返回 |

## 2. 结果速览

环境：Apple M3 Pro（Arm64）、.NET 10.0.5、单线程、Workstation GC、BenchmarkDotNet 0.15.4 默认精度（2026-09-19 性能修复波 cc4f50e 之后实测）。数据为 `CoreMatrixBench` 全矩阵中 `int[]` 的结果；比值为**算法耗时 ÷ `Array.Sort<T>(T[])` 耗时**，< 1.00 即快于 `Array.Sort`。完整矩阵（12 分布 × 3 规模 × 4 方法）见 `benchmarks/results/`。

**速度比值**（耗时 ÷ `Array.Sort<T>(T[])`，越低越快）：

| 分布 \ 规模 | QuadSort 100k | QuadSort 1M | GlideSort 100k | GlideSort 1M | DriftSort 100k | DriftSort 1M |
|---|---:|---:|---:|---:|---:|---:|
| Random（全随机）        | 1.65 | 1.61 | 1.79 | 1.81 | 1.43 | 1.40 |
| Ascending（升序）       | **0.26** | **0.07** | **0.15** | **0.10** | **0.10** | **0.07** |
| RandomD20（值域 0..20） | 1.81 | 1.68 | **0.97** | 1.35 | 1.09 | 1.08 |
| Zipfian（重尾）         | 1.93 | 1.78 | 1.43 | 1.42 | 1.23 | 1.13 |

`Array.Sort<T>(T[])` 绝对耗时（随机 int）：100k ≈ 3.45 ms，1M ≈ 43.8 ms。

**分配对比**（每次排序的托管分配；`Array.Sort` 内省排序原地完成，零分配）：

| 算法 | 100k（int） | 1M（int） | 说明 |
|---|---:|---:|---|
| `Array.Sort<T>(T[])` | 0 B | 0 B | 原地内省排序，无 scratch |
| QuadSort | ~400 KB | ~4 MB | 全量 n 的归并 scratch（Descending 走原地翻转路径，0 B） |
| GlideSort | ~401 KB | ~2 MB | 上游 `glidesort_alloc_size`：8MB 上限内取 n，超限取 n/2 |
| DriftSort | ~400 KB | ~4 MB | 上游 scratch 策略 `max(n/2, min(n, 8MB/元素大小))` |

即：三者的自适应与重复值处理能力以 O(n) 级 scratch 缓冲 + 相应的拷贝带宽为代价——这是设计使然（上游同理），不是移植缺陷；但 .NET 侧 `Array.Sort` 的零分配原地内省排序把这个差距摆在了明面上。

**怎么读这些数字**（诚实结论）：

- **全随机 int 上我们输了，但差距收窄**：DriftSort 慢 ~1.4x、QuadSort ~1.6x、GlideSort ~1.8x（性能修复波前 GlideSort 为 ~2.6x）。`Array.Sort` 的内省排序原地完成、零 scratch、零拷贝；三个稳定自适应排序为换来自适应与重复值处理，付出 O(n) scratch 分配 + 全量拷贝带宽的代价（见上表分配对比）。这是设计使然（上游 Rust 版同样需要 scratch），不是移植缺陷。
- **自适应模式大胜**：升序 0.07–0.26x（**快 4–14 倍**）、OrganPipe 0.03–0.07x（**最大自适应胜幅**，1M 时 DriftSort 0.03x = 快 30 倍）、Sawtooth 0.16–0.38x、RandomS95/RandomTail 0.27–0.45x、AllEqual/Descending 0.04–0.18x。数据越有结构，赢得越多。
- **重复值：修复波后多个格子反超 `Array.Sort`**：RandomD20 100k 上 GlideSort **0.97x**；RandomP5（95% 重复）上 DriftSort **0.59x–0.66x**、GlideSort/QuadSort ~0.94–1.02x（打平）；FewUnique（4 个不同值）上 DriftSort **0.78–0.91x**、GlideSort **0.92–0.96x**；Zipfian 上 DriftSort 1.13–1.23x。
- **规模越大差距越稳**：1M 与 100k 的比值基本一致（缓存效应被 O(n) 主导摊平）。
- **规模扩展**（随机 int，1k → 10M）：`ScalingBench` 显示从 16k 起三算法与 `Array.Sort` 的比值进入平台期——DriftSort 稳定在 ~1.4x、QuadSort ~1.6x、GlideSort ~1.8x，直到 10M 无恶化（10M 时 DriftSort 1.45x）。8k 附近 DriftSort 达到 **0.97x**（与 `Array.Sort` 打平）。1k–2k 的小数组上三算法明显劣势（~1.7–22x，最差 1k RandomP5 GlideSort 达 27x、1k FewUnique 达 17x），与上游"小输入用插入排序"的取舍一致——小数组不值得复杂算法，但此时 `Array.Sort` 的内联插入排序更快。
- **`Array.Sort` 基准动物园**（int Random 100k）：`ArraySort_Generic` 3.65 ms；`IComparer` 入口 4.12 ms（+13%，接口税）；`Comparison` 委托入口 5.56 ms（+52%，委托税）；`Linq_OrderBy`（稳定参照）5.83 ms + 1.5 MB 分配。即 DriftSort（4.93 ms）已快于 `Array.Sort` 的 `Comparison` 委托入口与 LINQ OrderBy，QuadSort（5.78 ms）与 LINQ 相当，GlideSort（6.31 ms）逼近——**同为稳定排序时，DriftSort 胜出**。

**类型矩阵**（100k，vs `Array.Sort<T>(T[])`；引用类型 `string[]` 走接口虚调用，与 `Array.Sort` 同级开销）：

| 类型 \ Random比值 | DriftSort | GlideSort | QuadSort | 备注 |
|---|---:|---:|---:|---|
| int        | 1.42x | 1.77x | 1.66x | |
| double     | 1.39x | 1.69x | 1.53x | |
| Struct16   | 1.73x | 2.32x | 1.80x | 16 字节值类型 |
| Struct128  | 1.72x | 1.67x | 1.95x | 大元素：拷贝代价放大 |
| string     | 1.18x | **1.11x** | 1.19x | 接口调用主导，差距缩小 |

`string[]` 的结构化模式差距同样收窄甚至反超：Zipfian 上 DriftSort **0.63x**、GlideSort **0.65x**（快于 `Array.Sort`）；RandomD20 上 DriftSort **0.76x**、GlideSort **0.81x**。Struct128 的 D20/S95/Zipfian 上 GlideSort 以 0.56–1.02x、DriftSort 以 0.51–1.08x 胜出居多。元素越大、比较越贵、数据越有结构，移植的相对优势越明显。`string[]` 数字较修复波前的提升来自 A2：`IsFreezeLike` 扩宽到引用类型（Rust `Freeze` 含 `String`/`&T`/`Box`），使 `string[]` 走小排序网络而非插入排序。

## 3. 三算法说明与移植要点

**DriftSort**（上游 [sort-research-rs/driftsort](https://github.com/Voultapher/sort-research-rs)，Rust）——面向未来的稳定通用排序：小输入用插入排序（i-cache 友好），大输入先做一次"drift"扫描识别已有顺序结构，再用 quicksort 分区 + powersort 合并树调度归并。对真实世界数据（部分有序、大量重复）自适应能力最强。

**GlideSort**（上游 [glidesort](https://github.com/orlp/glidesort)，Rust）——"可以像标准库 sort 一样通用，却快得像专门优化过的 sort"：稳定、分支减少的小排序网络 + 借鉴 driftsort 的归并调度，在随机数据上也非常快。

**QuadSort**（上游 [quadsort](https://github.com/scandum/quadsort)，C）——最自包含的稳定自适应归并排序：四路交换网络小排序 + 四路归并，无随机化、无最坏情况陷阱，对小数组和结构化数据表现出色。

**移植要点**（架构忠实 + C# 性能惯用法）：

- **branchless 比较映射**：上游 Rust 用 `T: Ord` 单态化出无分支比较；C# 侧以 `IIsLess<T>` 结构体接口 + 值类型 comparer（`ComparableCmp<T>` 等）让 JIT 特化并内联，`where TC : struct` 约束消除接口虚分派。
- **scratch 策略**：DriftSort 沿用上游"最多 len/2、且不超过 8MB/元素大小"的临时缓冲策略，上游的 4096 字节栈存储换成每-(T, 线程) 512 元素 `ThreadStatic` 缓冲，超出部分堆分配；QuadSort 以 64 元素 `ThreadStatic` 小缓冲服务 n < 32 路径（稳态零分配）；GlideSort 按上游 `glidesort_alloc_size` 每次调用堆分配。`Sort<T,TC>(Span<T>, TC)` 内核均可由调用方自带 scratch。
- **类型特化 codegen**：.NET 泛型即上游的 Rust 单态化等价物——`Sort<T, TC>` 每个值类型组合独立 JIT，比较器内联后与上游 `#[inline]` 同效。
- **跳过项**：`gap_guard`（上游检测 scratch 与输入重叠的保护——C# 端 scratch 独立分配，重叠不可能发生，故跳过）；`tracking.rs` 调试设施（上游专用断言基建，不影响正确性路径）；WASM 目标路径（.NET 无对应物）；powersort 深度数学共享为 internal `Powersort` 类。
- Benchmark 工程注记：BenchmarkDotNet 0.15.4 移除了独立的 `[OperationsPerInvoke]` 特性（改为 `[Benchmark]` 的属性，总操作数 = InvocationCount × OperationsPerInvoke），矩阵基准因此显式设置 `InvocationCount`。

## 4. Benchmark 方法论

- **矩阵**：`CoreMatrixBench`（12 分布 × {1k, 100k, 1M} × 4 方法，`int[]`）；`TypeMatrixBench`（{int, double, string, Struct16, Struct128} × {Random, RandomD20, RandomS95, Zipfian} × 100k）；`ScalingBench`（1k → 10M 随机 `int[]` 12 个规模）；`BaselineBench`/`PairBench`（`Array.Sort` 变体入口与 Pair 键值结构对照）。
- **分布**：Random、Ascending、Descending、Sawtooth（5 齿）、OrganPipe、RandomD20（值域 0..20）、RandomP5（95% 零 + 5% 随机）、RandomS95（95% 有序 + 5% 随机尾部）、Zipfian（s≈1 重尾）、AllEqual、FewUnique（4 个不同值）、RandomTail（升序 + 末 5% 随机）。
- **数据新鲜度（ring buffer）**：原地排序会破坏输入，而 `IterationSetup` 每个 iteration 只跑一次（一个 iteration 含多次 invocation），后续 invocation 会在已排序数据上重排——对自适应排序是毒药。方案是 `GlobalSetup` 预生成 64 份未排序克隆进环形缓冲，每次 invocation 排序下一格；游标回卷时全部从不可变模板重拷。不变量数学：InvocationCount = RingSize = 64，环恰在批次边界回卷，每个克隆每批恰被排序一次。重拷成本为每 64 次排序 64 次 `Array.Copy`（内存带宽 vs O(n log n) 比较），占比百分之几，且对所有被测方法一致。
- **配置**：单 job、Arm64、Workstation GC、不强制 GC（观察真实分配行为而非隐藏它）、MemoryDiagnoser、P90 列、固定种子 20260918。
- **复现命令**：

```bash
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*CoreMatrixBench*'
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*TypeMatrixBench*'
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*ScalingBench*' '*BaselineBench*'   # 多个 glob 空格分隔，'|' 不被支持
```

完整导出（markdown + csv）在 `benchmarks/results/`。

## 5. 使用示例

三个算法 API 同构：

```csharp
using Sorts;

// 与 Array.Sort<T>(T[]) 同形的主入口（IComparable<T> 路径）
DriftSort.Sort(intArray);                    // 整个数组
QuadSort.Sort(intArray, index, length);      // 区间排序
GlideSort.Sort(stringArray);                // 引用类型同样支持

// 显式比较器（null ⇒ Comparer<T>.Default）与委托
DriftSort.Sort(intArray, Comparer<int>.Default);
QuadSort.Sort(objArray, (a, b) => a.Key.CompareTo(b.Key));

// 高性能附加内核：结构体 comparer → JIT 值类型特化 + 内联（对标上游单态化）
readonly struct ReverseCmp : IComparer<int>
{
    public int Compare(int a, int b) => b.CompareTo(a);
}
GlideSort.Sort<int, ReverseCmp>(array.AsSpan(), default);
```

## 6. 测试说明

xUnit 差分 + 确定性测试（先于实现编写）：

- **差分测试**：12 种分布 × 规模扫描（含边界 0/1/2 与 2 的幂/非 2 幂）× 随机种子，输出与 .NET 参考排序逐元素比对（多重集相等 + 升序不变量）。
- **确定性测试**：同一输入排序两次，结果逐元素一致（捕捉隐藏状态/随机化）。
- **单元层**：各算法内部构件（quicksort 分区、小排序网络、归并、powersort 深度数学）有独立测试。

```bash
dotnet test tests/Sorts.Tests
```

## 7. 致谢与许可

- **[quadsort](https://github.com/scandum/quadsort)** — Igor van den Hoven，公有领域（public domain / unlicense）。
- **[glidesort](https://github.com/orlp/glidesort)** — Orson Peters，MIT OR Apache-2.0。
- **[driftsort](https://github.com/Voultapher/sort-research-rs)**（sort-research-rs）— Orson Peters & Lukas Bergdoll，MIT OR Apache-2.0；设计文档见 sort-research-rs 的 driftsort writeup。

本仓库为上述三个项目的 C# 移植（2026）：算法与优化决策忠实对应上游，代码以 C#/.NET 惯用法重写（`Span<T>`、泛型特化、`ThreadStatic` 等），非逐行翻译。各文件头部保留上游出处标注。

## 8. 已知限制

- **引用类型接口调用与 `Array.Sort` 同级**：引用类型 `T` 的 `IComparable<T>.CompareTo` 为接口虚调用，本移植与 `Array.Sort` 内部 `Comparer<T>.Default` 路径开销同级——该场景无免费午餐，速览表的 `string[]` 行体现这一点。
- **不做 `Sort<TKey,TValue>(keys, items)` 键值对重载**：设计文档明确的非目标，列为验证成立后的后续项（见 `docs/superpowers/specs/2026-09-18-csharp-sorts-design.md`）。
- **`ThreadStatic` 小缓冲的重入性注意**：上游 Rust 的 4096 字节小缓冲存于**调用栈**（每次调用独立）；C# 端以每-(T, 线程) 的 `ThreadStatic` 512 元素缓冲代替（懒初始化）。若自定义比较器回调在同一线程上递归调用同一 `T` 的排序、且外层规模小到用该缓冲，内层会覆写外层正在使用的 scratch 导致数据损坏。数组足够大时 scratch 走堆分配、每次调用独立，无此问题。请勿在比较器中递归排序同一 `T`。
- **eager-smallsort A/B 未做**：上游对"先完整小排序再归并"vs"边扫边合"的取舍做过基准 A/B；本端口直接沿用上游结论，未在 .NET 上复测该 A/B。
- **eager 回退路径丢失 run 检测与 powersort 合并序**：递归深度限制耗尽的罕见最坏情况路径上，本端口把区间切成固定宽度的 run 做朴素两两归并；上游在该路径仍调用完整 eager 主循环（含 `find_existing_run` 与 powersort 合并树）。输出仍正确且稳定，仅该罕见路径的自适应性与合并次序偏离上游。
