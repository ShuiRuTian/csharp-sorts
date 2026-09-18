# csharp-sorts 设计文档：glidesort / quadsort / driftsort 的 C# 移植与 Array.Sort 替代评估

- 日期：2026-09-18
- 状态：已批准（用户确认移植深度「架构忠实 + C# 性能惯用法」、性能第一、Array.Sort 同款契约）
- 上游来源：
  - https://github.com/scandum/quadsort （C）
  - https://github.com/orlp/glidesort （Rust）
  - https://github.com/Voultapher/driftsort （Rust；设计文档见 sort-research-rs writeup）

## 1. 目标与定位

以**性能为第一指标**，将三个现代排序算法移植到 C#/.NET 10，最终目标为替代 `Array.Sort` 家族。验证主战场是最通用入口 `Array.Sort<T>(T[])`（`IComparable<T>` 默认比较路径）：值类型 T 上 JIT 将 `CompareTo` 特化为内联的非虚调用，与上游 Rust 单态化处境对等；引用类型 T 走接口虚调用，与 Array.Sort 内部 `Comparer<T>.Default` 路径同级开销，对比公平。

## 2. 排序契约（与 Array.Sort 对齐）

| 属性 | 承诺 |
|---|---|
| 正确性 | 输出是输入的升序排列（多重集相等） |
| 确定性 | 同一输入 → 每次排序结果逐元素一致；算法无随机性、无隐藏状态 |
| 稳定性 | **不保证**（如同 Array.Sort）。当前移植因源算法设计而构造性稳定（归并列稳定，零成本），但契约不承诺；为性能破坏稳定性的优化合法 |
| 异常行为 | null 数组 → ArgumentNullException；range 越界 → ArgumentOutOfRangeException；n ≤ 1 直接返回 |

注：稳定性在这些设计中的实际代价集中于 glidesort/driftsort 的 stable partition（全量过 N 缓冲 vs 不稳定原地交换）；归并路径的稳定性免费（`<=` vs `<` 零成本差）。

## 3. 非目标

- `Sort<TKey,TValue>(keys, items)` 键值对版本（列为验证成立后的后续项）
- 并行排序、NuGet 打包、LINQ 集成

## 4. 项目结构

```
csharp-sorts/
├── README.md                      # 中文结果报告 + 使用说明
├── Directory.Build.props          # net10.0, nullable, AllowUnsafeBlocks, Optimize
├── src/Sorts/                     # 类库（发布库）
│   ├── Quadsort/                  #   QuadSort.cs（完整移植，最自包含）
│   ├── Glidesort/                 #   GlideSort.cs + 小排序网络等
│   └── Driftsort/                 #   DriftSort.cs
├── tests/Sorts.Tests/             # xUnit 差分/确定性测试（先于实现）
└── benchmarks/Sorts.Benchmarks/   # BenchmarkDotNet
```

## 5. API 面（三个算法同构）

```csharp
public static class QuadSort / GlideSort / DriftSort
{
    // 主验证入口，与 Array.Sort<T>(T[]) 同形
    void Sort<T>(T[] array)                        where T : IComparable<T>;
    void Sort<T>(T[] array, int index, int length) where T : IComparable<T>;
    void Sort<T>(T[] array, IComparer<T> comparer);            // null ⇒ Comparer<T>.Default
    void Sort<T>(T[] array, Comparison<T> comparison);
    // 高性能附加内核
    void Sort<T, TC>(Span<T> span, TC cmp)         where TC : struct, IComparer<T>;
}
```

`Sort<T,TC>` 是性能关键路径（结构体 comparer → JIT 值类型特化 + 内联）；其余重载按需包装为 struct comparer 或走接口路径，包装成本与 Array.Sort 同级。

## 6. 移植深度决策

「架构忠实 + C# 性能惯用法」：完整保留核心架构（run 检测、stable quicksort、branchless 归并网络、powersort 策略、introsort 盾等），微观层换用 C# 最优等价物。跳过 Rust/C 专有部分（WASM 路径、codegen 生成的极端类型特化）。预计每算法 800–2000 行 C#。

### 6.1 Quadsort（最接近逐行移植）

- quad-swap 分析器：8 元素一组，4 次两两比较形成位掩码（0–15），3 次额外比较定序，块内输出排序的 8 元素块；反序 run 线性翻转。
- ping-pong 四路归并：4 块同时归并（2 进 swap、1 回主存），块 8→32→128→512→2048…
- parity 双向无哨兵归并（等长数组，完全展开、branchless、双向内存并行）。
- cross merge（不等长/有序数据收益）+ tail merge（尾块）+ 已有序跳过。
- scratch：n 元素 swap；分配失败回落原地旋转（n log²n）。

### 6.2 Glidesort

- powersort 主循环：线性扫描识别升序/严格降序 run，**不**急切排序乱序区（产生可未排序的逻辑 run）。
- 逻辑归并：双未排序 run 直接拼接；双已排序 run → "double sorted"；已排序 × 未排序 → 对未排序 run 施加 stable quicksort。
- stable quicksort（借鉴 fluxsort）：双向交错 branchless 稳定分区。
- 交错 ping-pong 归并（多 run 并发 + 双向，内存/指令级并行）。
- 大归并二分拆分 → 适配任意小 scratch。
- 小排序：插入排序网络处理 ≤20 元素。
- scratch 策略：≤1MiB 用 n；≤1GiB 用 n/2；更大 n/8。

### 6.3 Driftsort（glidesort 的精简演化）

- 懒归并 + powersort 合并策略（核心循环同源）。
- run 检测：min-run ≈ √N，检测失败不在 min-run 区内重试。
- stable quicksort：√N 采样近似中位数 pivot；祖先 pivot 追踪 → 等值批处理 → 期望 O(N·log K)（低基数/Zipf 大幅提速）。
- branchless 单向稳定分区（指针选择填缓冲两端）。
- introsort 盾：递归深度 2·log₂N 回落归并（强制小 run），保证 O(N·log N)。
- 小输入（≤20）内联插入排序，避免 i-cache 污染。
- scratch：≤8MB 用 n，否则 n/2。

## 7. C# 性能技术映射

| 上游惯用法 | C# 等价物 |
|---|---|
| Rust 单态化 / C 宏 | 泛型 + 结构体 comparer（JIT 值类型特化、去虚化、内联） |
| 裸指针遍历 | `ref T` + `Unsafe.Add`/`MemoryMarshal`、`Span<T>` 切片 |
| `#[inline]`/宏展开 | `[MethodImpl(AggressiveInlining)]` + 手工展开关键循环 |
| branchless ternary | min/max/三元模式诱导 JIT 生成 cmov |
| `MaybeUninit` scratch | 小尺寸 `stackalloc`；大尺寸按算法原生分级分配（见 6.x），`ArrayPool` 备选 |
| 排序网络（codegen 生成） | 手写等价固定网络（≤20 元素） |

## 8. 正确性与确定性测试（xUnit，先于实现）

- **差分正确性**：对拍 `Array.Sort<T>(T[])`，断言多重集相等（不比较相等元素相对顺序）。
- **确定性**：同一输入的两份独立拷贝分别排序 → 结果逐元素一致（排除实例身份依赖）；多 seed × 全分布 × 对抗输入。
- 矩阵：3 算法 × 4 类型（int / double / string / (key,payload) struct）× 12 分布 × 尺寸 0–64 全遍历 + 随机尺寸 × 200 seeds。
- 对抗输入：触发 depth shield、merge splitting、全等值、NaN（double 语义随 Array.Sort）、锯齿、反序拼接等冷路径。
- 异常行为：null / 越界 range / 空 / 单元素。

## 9. Benchmark 矩阵（BenchmarkDotNet）

- **类型**（各测一个成本轴）：int（控制流）、double（数值比较）、string（昂贵比较）、16B struct（中等拷贝）、128B struct（拷贝主导）。
- **分布**：random / ascending / descending / sawtooth / organ-pipe / random_d20 / random_p5 / random_s95 / zipfian / all-equal / few-unique / random-tail。
- **基线**：`Array.Sort<T>(T[])`（主报告行）、`Array.Sort + IComparer<T>`、`Array.Sort + Comparison<T>`、`OrderBy`（稳定现状参照）、非泛型 `Array.Sort(Array)`（仅参考）。
- **分层控制总量**：① core 矩阵 int × 全分布 × {1k, 100k, 1M}；② type 矩阵 5 类型 × 4 代表分布；③ size-scaling 1→10M 阶梯。
- 固定 seed 可复现；`MemoryDiagnoser` 报告分配；支持 `--filter` 局部运行；全量预计 1–2 小时。

## 10. 子 agent 审查（实现全绿后）

并行两路：① **算法忠实度**（对照上游源码核对逻辑偏差）；② **C# 性能惯用法**（去虚化/内联/分配/分支）。修复后重跑测试与关键 benchmark，结果汇总进 README。

## 11. 环境与工具

- .NET 10 SDK（本机已装 10.0.201；动工前核对最新 feature band，必要时经官方 dotnet-install 脚本更新）。
- xUnit（测试）、BenchmarkDotNet（基准）。

## 12. 风险与缓解

| 风险 | 缓解 |
|---|---|
| JIT 下 branchless 模式退化为分支 | benchmark 实测验证，必要时改写为 JIT 友好形式 |
| 零初始化数组 scratch 的清零成本 | 分级分配 + `MemoryDiagnoser` 监控；大数组评估 `ArrayPool` |
| 引用类型接口调用开销 | 与 Array.Sort 同级（公平），struct comparer 路径另测 |
| 移植逻辑偏差 | 差分测试全覆盖 + 忠实度审查 agent |
| 全量 benchmark 时间过长 | 分层矩阵 + `--filter` 局部运行 |
