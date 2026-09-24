# 小排序网络的 C# 代码生成笔记（RyuJIT vs LLVM）

- 日期：2026-09-25
- 对象：`src/Sorts/Ipnsort/IpnSmallSort.cs` 的 `Sort9Optimal` / `Sort13Optimal` 与交换原语
- 结论：C# 网络慢**不是算法**，而是两处 codegen（RyuJIT 不提升数组元素到寄存器；`?:` 生成胖 cmov）。两处改写后 C# 网络的代码质量追平 LLVM。

## 1. 问题一：`?:` 生成的 cmov 很胖

同一个「比较-交换」：

| 写法 | RyuJIT 反汇编 | 指令数 |
|---|---|---:|
| `sw ? b : a` | `cmp; setg; movzx; test; cmov; test; cmov`（bool 物化 + 每个 select 各重测一次） | ~17 |
| `Math.Min/Max` | `cmp; mov; cmovle; cmovl`（**一次 cmp 同时喂两个 cmov**） | ~11 |
| LLVM（上游指针选择） | `cmp; cmovg; cmovl` + 2 load/store | 9 |

**根因**：RyuJIT 把 `bool` 当一等值——先 `setcc`+`movzx` 物化，再对每个 `select` 单独 `test`；它没有 LLVM 那种「把 cmp 标志位折回 cmov」的优化。`Math.Min/Max` 是内在函数，直接映射到 `cmp`+`cmov`，所以紧凑。

**修复（B2）**：`SwapIfLess` 对**整数基元 + 自然序比较器**（`ComparableCmp<int>` 等）用 `Math.Min/Max`；其余（自定义/接口/反转比较器、`char`/`nint` 无 `Math.Min` 重载、`float`/`double` 的 NaN 语义、非基元 T）回退值三元，保证语义一致。

## 2. 问题二：数组元素不提升到寄存器

网络原来在数组上原地比较-交换（`SwapIfLess(ref vBase, aPos, bPos)`），每次比较都要从数组 **load 两个元素 + 写回两个元素 + 寻址**。RyuJIT **无法证明 `Unsafe.Add` 的访问不别名**，因此不会把元素提升到寄存器；LLVM 有 `mem2reg`/SROA + 别名分析，会提升。

| 方法 | 数组形式 | 局部变量形式 | LLVM 等价物 |
|---|---:|---:|---:|
| `Sort9Optimal` | 261 | **135** | 131 |
| `Sort13Optimal` | 606 | **231** | — |

**修复（B3）**：`Sort9Optimal`/`Sort13Optimal` 先把 9/13 个元素读进 `v0..vN` 局部变量，在寄存器上跑同一套比较-交换（`CompareSwap(ref a, ref b)`），最后写回。

局部变量版反汇编开头就是 9 条 `mov` 一次性读入、中间 25 次全 `cmp;mov;cmovle;cmovl`（零访存）、最后 9 条 `mov` 写回。

## 3. 性能（`SwapLeafBench`，int，ratio vs `Array.Sort`，网络叶子临时启用）

| 用例 | 插入叶子 | 网络+三元 | 网络+MinMax | 网络+局部 |
|---|---:|---:|---:|---:|
| Random/32 | **1.03** | 1.95 | 1.68 | 1.11 |
| Random/64 | **1.16** | 1.92 | 1.64 | 1.31 |
| Random/128 | 0.98 | 1.63 | 1.47 | **0.97** |
| FewUnique/32 | **0.61** | 2.44 | 2.09 | 1.36 |
| SortedSwap3/32 | **0.87** | 3.63 | 3.11 | 2.03 |

- MinMax 相对三元：网络整体再快 ~14–20%。
- 局部变量相对 MinMax：Random/128 由 1.47 → **0.97**（反超插入），Random/32–64 只差 ~8–13%。
- 但**结构化/近有序**小 N（FewUnique/SortedSwap3/RandomSnl）网络仍明显输——这是**非自适应**的固有代价，与代码质量无关。

## 4. 结论与局限

- C# 网络代码质量已可追平 LLVM（135 vs 131）；差距根因是「RyuJIT 不提升数组元素 + `?:` 胖 cmov」，均可通过改写消除。
- 网络仍**非自适应**，小 N/结构化场景下插入叶子更优，故**默认叶子保持插入**；网络实现保留、可测试。
- `sort13` 的 13 个活跃值对 8 字节 T 可能寄存器溢出（仍远好于数组形式的 606 条）。
- 若将来做「混合叶子」（近有序检测 → 插入，否则局部变量网络），可同时吃到两边的胜利。
