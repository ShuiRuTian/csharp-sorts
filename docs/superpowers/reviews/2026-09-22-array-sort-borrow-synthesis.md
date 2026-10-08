# 借鉴官方 `Array.Sort` 提升 csharp-sorts：调研汇总与落地

- 日期：2026-09-22
- 参考实现：dotnet/runtime `5edb3a2`（2026-09-22），关键源码物化在 `E:\Code\runtime-src\`
- 目标：`E:\Code\csharp-sorts`（HEAD `9c49fa8`，当前仅剩 ipnsort 移植）
- 方法：4 个子 agent + git worktree 分头探索；分支与报告见文末

> **先更正一个前提**：`README.md` §2 与 `benchmarks/results/` 仍是 driftsort/glidesort/quadsort 时期（`59170d8` 已删除）的数据，描述的是那三个**带 O(n) scratch 的稳定自适应排序**。当前树里的 ipnsort 是**原地零分配**的，随机 int 已经**快于 `Array.Sort` ~2.5×**（见下），并不存在 README 所说的 1.4× 随机亏损。真实待改进项是另外四个。

## 1. 当前实测基线（AMD Ryzen 9 7945HX，.NET 10.0.12 x64，ratio = ipnsort ÷ `Array.Sort<T>(T[])`）

| 分布 / 类型（N=100k） | ratio | 结论 |
|---|---:|---|
| Random int | **0.37–0.39×** | 大胜 |
| Ascending / Descending / AllEqual | 0.07–0.10× | 大胜 |
| RandomD20 / RandomP5 / FewUnique / Zipfian | 0.25–0.30× | 大胜（祖先 pivot 等值批处理） |
| **RandomS95 / RandomTail** | **2.10–2.35×** | **亏损（最大）** |
| n=1000 | 1.37–1.39× | 亏损 |
| **double Random** | ~~1.27×~~ → **0.43×（本次已修）** | 已转为大胜 |
| Struct16 Random | 1.43× | 亏损 |
| string Random / Struct128 Random | 1.06× / 0.95× | 打平 / 微胜 |

## 2. 四个方向的结论

### 2.1 运行时 / JIT 特化（报告：`array-sort-jit-runtime.md`）
`Array.Sort` 真正吃到的 runtime 特性只有一类，且**本项目基本已具备或更强**：
- 运行时构造 `IComparable<T>` 约束 helper（`CreateInstanceForAnotherGenericParameter`）→ 本项目用 `SortSpan<T,TC> where TC : struct` 单态化，**已覆盖且更通用**；
- `typeof(T)==typeof(int)` 常量折叠的 `LessThan/GreaterThan` → `ComparableCmp<T>` **已覆盖**；
- `ref T` + `Unsafe.Add` 去越界检查、`[AggressiveInlining]`、`NoInlining`、`BitOperations.Log2` → **已覆盖**；
- **唯一真正新增的高价值点**：NaN 前移 `SortUtils.MoveNansToFront`（`ArraySortHelper.cs:1111`）+ 之后用原始 `<`/`>` 比较浮点。这正是 `Comparers.cs` 之前显式放弃浮点特化的原因之解。

### 2.2 向量化 / SIMD（报告：`array-sort-simd.md`）
- **明确结论：`Array.Sort`（及整个 BCL 排序）不使用任何 SIMD。** 唯一向量化排序是 GC 的原生 C++ `vxsort`（`src/coreclr/gc/vxsort/`），模板只实例化定宽整数/指针，且 NEON 仅有 `uint32_t` 网络。
- `SpanHelpers` 的向量化用于 `IndexOf/SequenceEqual` 等，**不是排序**。
- 对本项目可行的向量化（按性价比）：① **向量化 run/有序性检测**（低风险，利好自适应行）；② 4 字节基元的小排序网络（x86 上可能受益，M3/NEON 128 位下多半只是噪声）；③ vxsort 式向量分区（上限最高，风险最高，仅 4/8 字节）；`Struct128`/`string[]`/`Struct16` 是死路。

### 2.3 工程与 API 设计（报告：`array-sort-api-design.md`）
本项目相对 BCL 缺失的 API 面（按价值）：
1. **整个 `Span<T>` 扩展面**（`Sort(this Span<T>)` / `+IComparer` / `+Comparison`）——内核已存在，纯薄包装；
2. **`Sort<T>(T[],int,int,IComparer<T>?)`** 与范围越界异常对齐（BCL 用 `ArgumentException(Argument_InvalidOffLen)`，本项目用 `ArgumentOutOfRangeException`）；
3. **键值对 `Sort<TKey,TValue>`**（需双数组交换内核；项目已列为非目标）；
4. 比较器异常翻译（BCL 把 `IndexOutOfRangeException` 转成"坏比较器" `ArgumentException`，其余包成 `InvalidOperationException`）、XML 文档与"不稳定排序"声明。
（不推荐非泛型 `Array.Sort(Array)`/`IComparable` 路径——装箱慢，性能优先场景可直接弃用。）

### 2.4 核心算法与阈值（报告：`array-sort-algo-thresholds.md`）
- `Array.Sort` 关键常数：`IntrosortSizeThreshold = 16`、n==2/3 显式网络、深度上限 `2*(Log2(n)+1)`、median-of-three + Hoare 双指针（pivot 停 `hi-1`）、堆排序兜底。**无 run 检测、无自适应**。
- 本项目的亏损根因**不是** scratch/merge（已是原地零分配），而是：
  1. **`RandomS95`/`RandomTail` 2.1–2.35×**：`FindExistingRun` 找到超长有序前缀后**丢弃**，仍对整段快排；
  2. **n≈1000 的 1.39×**：≤32 叶子走 `SmallSortNetwork`（scratch + copy-back），比 `Array.Sort` 的纯插入排序多内存流量；每层 driver（run 扫描 + pivot 采样）开销；
  3. **`Struct16` 1.43×**：≤96B 一律走 branchless Lomuto（每元素搬 2 次），16B 元素时流量占主导；`Struct128` 走 Hoare 反而 0.95×；
  4. **double**：比较未特化（见 2.1）。

## 3. 已落地：浮点 NaN 前移 + 直接比较（分支 `improve/float-nan`）

对应 2.1 的唯一高价值项，改动 3 处、零新增 API：
- 新增 `src/Sorts/FloatPrepass.cs`——`MoveNansToFront<T>` 的移植（对 double/float/Half 有效，其它 `T` 的 `typeof` 链折叠为空）；
- `Comparers.cs`——`ComparableCmp<T>` 增加 double/float/Half 的原始 `>` 分支（仅在前移保证无 NaN 时成立，已加注释约束）；
- `Ipnsort.cs`——默认可比较入口先做前移、切掉前导 NaN 区，再排序。

**验证**：
- `dotnet test tests/Sorts.Tests` → **22374 passed / 0 failed**（含 double 的 NaN 注入与确定性用例）；
- `TypeMatrixDouble` A/B（同机同 runtime）：

| Dist (double, 100k) | 改前 ratio | 改后 ratio |
|---|---:|---:|
| **Random** | **1.27×** | **0.43×** |
| RandomD20 | — | 0.25× |
| Zipfian | — | 0.25× |
| RandomS95 | — | 2.10×（未改善，见下） |

NaN 语义与 `Array.Sort` 一致（NaN 视为最小、提前到最前；`-0.0==+0.0`），不稳定性符合项目契约。注意：前移是**无条件多一趟 O(n) 扫描**，在接近有序的浮点数据上相对优势会略降——需在 CoreMatrix 的浮点行上补测。

## 4. 后续优先级（建议）

| 优先级 | 动作 | 预期 | 状态 |
|---|---|---|---|
| P0 | 浮点 NaN 前移 + 直接比较 | double Random 1.27× → 0.43× | **已完成（分支待合）** |
| P1 | 利用超长前缀 run：只排尾部再原地归并 | 修 `RandomS95/Tail` 2.1–2.35× | 待做（需原地对称归并，保零分配） |
| P2 | Lomuto/Hoare 尺寸阈值重调（9–96B 走移动更少的 Hoare） | 修 `Struct16` 1.43× | 待做（须保持 int/double 在 Lomuto） |
| P3 | 精简小 n 叶子/driver（显式 n==2/3 网络；17–32 试纯插入） | 修 n≈1000 1.39× | 待做 |
| P4 | API 面补齐：`Span<T>` 扩展、range+comparer、异常对齐、文档 | 可替代性 | 待做 |
| P5 | 向量化 run 检测 | 自适应行小幅 | 可选 |

**不要动**（现有胜势所在）：branchless Lomuto + cyclic 分区、零分配原地、`FindExistingRun` 提前返回 + 降序 `Reverse`、祖先 pivot 等值批处理、伪中位数 pivot、`ComparableCmp` 的 `>0` 极性与 `InvertedCmp` 单态化。

## 5. 分支 / 报告索引

| 分支 | 内容 |
|---|---|
| `explore/jit-runtime` | `docs/superpowers/reviews/array-sort-jit-runtime.md` |
| `explore/simd` | `docs/superpowers/reviews/array-sort-simd.md` |
| `explore/api-design` | `docs/superpowers/reviews/array-sort-api-design.md` |
| `explore/algo-thresholds` | `docs/superpowers/reviews/array-sort-algo-thresholds.md` |
| `improve/float-nan` | 浮点 NaN 前移实现（`FloatPrepass.cs` + `Comparers.cs` + `Ipnsort.cs`），测试全绿 |
