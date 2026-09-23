# 与 dotnet/performance 排序用例对比（512 个唯一随机值）

- 日期：2026-09-22
- 参照：[dotnet/performance](https://github.com/dotnet/performance) `99531dc`
  - `src/benchmarks/micro/libraries/System.Collections/Sort.cs`
  - `src/benchmarks/micro/libraries/System.Collections/DataTypes.cs`（IntStruct/IntClass/BigStruct）
  - `src/benchmarks/micro/libraries/System.Collections/Utils.cs`（`DefaultCollectionSize = 512`）
  - `src/harness/BenchmarkDotNet.Extensions/ValuesGenerator.cs`（`ArrayOfUniqueValues<T>`，seed 12345）
- 复刻实现：`E:\Code\csharp-sorts-wt\smalln\benchmarks\Sorts.Benchmarks\DotnetPerfSortBench.cs`
  （原样照搬其类型定义、数据生成器、Size=512；BCL `Array.Sort<T>(T[],0,Size)` vs `Sorts.Ipnsort`）
- 机器：AMD Ryzen 9 7945HX，.NET 10.0.12 x64

## 1. 默认比较器路径（`Sort.cs` 的 `Array` 用例）

ratio = Ipnsort ÷ `Array.Sort<T>(T[], 0, 512)`，<1 为快（`SmallArrayHoareMax` 已从 256 提到 **1024**，n=512 因此全程走 Hoare）：

| 类型 | BCL Array.Sort | Ipnsort | ratio |
|---|---:|---:|---:|
| `int`（4B 值类型） | 2.597 µs | 2.851 µs | **1.10** |
| `string`（引用） | 30.75 µs | 32.07 µs | **1.04** |
| `IntStruct`（4B 值类型） | 2.676 µs | 3.262 µs | **1.22** |
| `IntClass`（引用） | 12.36 µs | 16.93 µs | **1.37** |
| `BigStruct`（32B 值类型） | 3.196 µs | 4.307 µs | **1.35** |

**结论：在他们这个 n=512 的默认路径用例上，我们仍略落后（1.04–1.37×），但比阈值=256 时已大幅收窄**（尤其 `BigStruct` 2.74×→1.35×、`IntClass` 1.64×→1.37×、`string` 1.16×→1.04×）。原因：n=512 之前落在 `SmallArrayHoareMax=256` 之上，走了 Lomuto（每元素搬 2 次）对中等/大元素吃亏；提到 512 后 n=512 全程 Hoare，与 `Array.Sort` 的分区方式一致，剩下的差距来自 pivot 采样与驱动常数项。`int`/`IntStruct` 仍差 10–22%。

## 2. 结构体比较器用例（`Array_ComparerStruct`）

| 方式 | 耗时 | ratio |
|---|---:|---:|
| BCL `Array.Sort(int[],0,n, new Cmp())`（struct IComparer 装箱 → 接口调用） | 9.095 µs | 1.00 |
| Ipnsort `Sort<int, Cmp>(span, default)`（struct 比较器单态化 + 内联） | 3.229 µs | **0.36** |

**我们快约 3×。** 这正是「比较器特化」真正兑现的地方：BCL 的 `Array.Sort<T>(T[],int,int,IComparer<T>)` 会把 struct 比较器**装箱**成接口，比较无法去虚化；我们的 `Sort<T,TC>(…, TC) where TC : struct` 把比较器单态化并内联。
（注：BCL 在 `MemoryExtensions.Sort<T,TComparer>(Span<T>, TComparer)` 上也对值类型比较器做了同样去虚化——优势并非我们独有，而是取决于用哪个入口。）

## 3. 复现

```bash
# 需先临时移除嵌套 worktree 里的同名工程（BDN 要求工程名唯一），否则 BDN 报
# "Found more than one matching project file for Sorts.Benchmarks"
dotnet run -c Release --project benchmarks/Sorts.Benchmarks -- --filter '*DotnetPerfSortBench*' '*StructComparerBench*'
```

## 4. 后续可做的改进（按价值）

1. **n≈512 的中间区间**：已把 `SmallArrayHoareMax` 从 256 提到 **512，再提到 1024**（整数组阈值，对大 n 无影响）；上表即阈值=512 时的结果；后续 BDN 实测 768/1024 在阈值=1024 下再降到 0.98/0.99（详见 `array-sort-smalln-study.md` §8）。100k Random 复测 `main` 0.43 vs 补丁 0.44，无回退。
2. **剩余差距（int/IntStruct ~1.1/1.22）**：主要来自 pivot 采样（`Median3Rec`）与驱动常数项；若要继续压缩，需要为小-中规模单独设计更省的 pivot/驱动，收益有限。
3. **中等大小非托管结构体**：阈值提到 512 后 `BigStruct` 从 2.74× 降到 1.35×；更大规模（如 2k–10k）仍值得单独 A/B。
4. 补做 `Sort.cs` 的其余用例：`Array_Comparison`、`List.Sort`、LINQ（我们有对应入口可对拍）；`runtime/Span/Sorting.cs` 只是自研快排基准，参考价值低。
