# Struct16（实测 24B）分区阈值实验：大元素走 Hoare vs Lomuto（BDN A/B）

- 日期：2026-10-10
- 触发：Struct16 @100k 长期亏损（Random ~1.4×、RandomS95 ~2.6–2.9×），怀疑上游 `MAX_BRANCHLESS_PARTITION_SIZE = 96` 在 C# 侧偏大
- 结论：**实验证实 16B 走 Hoare 全面更优（S95 快 ~57%），但决定保持上游 96B 阈值**（架构忠实），本文作为已知残余项归档

## 1. 背景与假设

上游 ipnsort `quicksort.rs:151`：`sizeof(T) <= 96` 走无分支 Lomuto 循环分区（每元素固定 1 比较 + 2 次写），否则走分支 Hoare（搬移少）。C# 实测 Struct16（**实测布局 24B**：`int Key + 4B 填充 + long A + int B` 的顺序布局；`BenchTypes.cs` 注释曾误写 16B，已修正）恰落在 Lomuto 区间且亏损；假设其写放大在 24B 下越过交叉点，Hoare 应更优。

## 2. 方法（含度量纪律教训）

- 临时把 `IpnPartition.MaxBranchlessPartitionSize` 从 96 改为 **8**（只影响 9–96B 类型，当前矩阵中仅 Struct16；int/double/string/Pair ≤8B 不动，Struct128 本就 >96 走 Hoare）；
- BDN 完整 `TypeMatrixStruct16`（4 分布 × 2 方法，默认精度、同一 job）；
- **touch + DLL 哈希校验**：`Copy-Item` 会保留旧 mtime，MSBuild 增量检查因此可能**跳过重编**（实测：stale 时构建 0.85s no-op、哈希不变；touch 后哈希变化）。过程中曾因此产出过一份假的 thr96 基线（数字与 thr8 相同），已废弃；本文所有关键数字均来自显式 touch 后的构建。

## 3. 结果（100k，ratio = Ipnsort ÷ `Array.Sort<T>(T[])`）

| 分布 | thr96（Lomuto，现状） | thr8（Hoare） | 变化 |
|---|---:|---:|---:|
| Random | 1.42–1.46 | **1.21** | 快 ~15% |
| RandomD20 | 0.88–1.01 | **0.74** | 快 ~17% |
| RandomS95 | 2.58–2.91 | **1.15** | 快 ~57% |
| Zipfian | 1.32–1.45 | **1.08** | 快 ~20% |

- thr96 有两次独立完整运行（会话间波动已计入区间）；thr8 为一次完整运行，其批次内 `Array.Sort` 基线（3.86/2.05/1.70/3.20 ms）与 thr96 批次一致，对比有效。
- 2026-10-11 **net11 RC.1 复验**（迁移后同会话、默认 job、DLL 直跑）：thr96 = 1.43/1.03/2.93/1.37（Random/D20/S95/Zipfian ratio），thr8 = **1.17/0.69/1.04/1.05** —— 结论不变，S95 改善更大（2.93→1.04）。

## 4. 归因

1. **写放大**：16B 下 Lomuto 的 2 写/元素（32B）对 Hoare ~1 写/元素（16B），写带宽开始主导——与上游论文的观察一致（"1k 类型 Hoare 最优，因为最少元素拷贝"；u64 超出 L2 后也被单写实现反超）；
2. **S95 叠加因素**：`FindExistingRun` 检测到 95% 前缀 run 后丢弃（上游明示不做部分 run 合并），全量快排 + 16B 写成本放大；Hoare 下重排几乎不发生，4.98ms→1.95ms；
3. **对照**：128B（>96，Hoare）在 Random 上 1.03×、结构化全胜，支持"阈值两侧行为分界"的归因；
4. 交叉验证（同 session、touch 校验）：把阈值临时改为 8 后 int/double 的既有结果不涉及 9–96B 参数，无回归面。
5. **注意**：§4 的"写放大主导"不完整，见 4b——真正的主因是结构体比较器的代码形态。

## 4b. 追查修正（2026-10-10）：主因是结构体比较器的代码生成

对 `PartitionLomutoBranchlessCyclic[S16, ComparableCmp<S16>]` 的 TC=0 反汇编（对比 int 实例）：

- **int 实例**：比较器折算为单条 `cmp + setle`，分区循环真正无分支（204B）；
- **S16 实例**：`Struct16.CompareTo → int.CompareTo` 内联为 -1/0/1 的**两分支体**，每个被扫描元素带 **2 个数据相关分支**（`jl`/`jle`，随机数据 ~50% 误判），函数 421B；
- 已试验并排除的修法：`Key.CompareTo`、显式三目链 `Key > o.Key ? 1 : (Key < o.Key ? -1 : 0)`、回退极性翻转 `x.CompareTo(y) < 0`——JIT 都不折叠用户 `CompareTo`（后两者仍是 2 分支，仅代码略小）。

**BCL 对照（runtime main 源码 + 本机 .NET 10.0.12 反汇编）**：`GenericArraySortHelper<T>.LessThan` 对基元类型用原始 `<` 特化臂（与本移植的 typeof 臂同法），对结构体回退 `left.CompareTo(right) < 0`；`Array.Sort<Struct16>` 的 `IntroSort` 同样生成两分支比较（全函数 19 个 jcc、0 个 setcc）。即：**该比较器税是共享的**，不是本移植独有的劣势。对结构体而言，无分支分区失去比较器层面的收益，拷贝/循环开销成为与 BCL 竞争的决定项——这就是 thr8 能显著改善 Struct16 的机制。运行时侧：`Int32.CompareTo` 仍是两分支实现（让其 branch-free 的 PR #90156 已关闭）；#81220（issue，非 PR）触发的 JIT 冗余分支折叠早期尝试 #83859（2023）未合并即关闭、#88527（draft）搁置；最终 **#126587（"JIT: dominating redundant branch opt"，2026-04-09 合并，随 .NET 11 preview4+/RC 发布）** 合并（issue 评论贴出修复 asm）。本机 TC=0 全优化探针（2026-10-10 静态反汇编）把边界钉死：**孤立的同形状方法在 RC.1 都折叠**——`M3Ret`（`CompareTo>0` 返回）net10=18B/RC=9B；`M3Sel`（select 消费）net10=两分支 16B/RC=`cmp+cmov` 10B；`S16Sel`（struct 包装+select）RC=`cmp+cmov` 12B。**但真实分区上下文不折**：`PartitionLomutoBranchlessCyclic[S16, ComparableCmp<S16>]` 仍 419B/7 个条件跳转，两个扫描循环的比较仍以 -1/0/1 分支体物化（`cmp;jg`→`mov ±1;jmp`），仅尾部直筒比较出现 `setg/cmovl`。折叠边界是**分区循环的 CFG/访存上下文**（比较分支与 csel 消费之间夹元素搬运/多分支），不是类型包装或消费形状。**结论修正：结构体路径的比较器税不会随已发布的 JIT 折叠自动消失（至少至 RC.1）；保持 96 是纯上游忠实决策，不寄望"未来 JIT 恢复无分支前提"；若未来出现覆盖循环上下文的折叠，以 cmpdisasm 复验后再重估 96。**

机制上这解释了为什么"小结构体复制更快"的经验在 C# 结构体上失效：上游 Rust 的 `derive(Ord)`/`i32::cmp` 在单字段/契约形状上都编译为 `cmp+setcc`（多字段派生见本节附），"便宜比较 + 小拷贝 ⇒ branchless Lomuto 赢"的前提成立；C# 用户结构体的 `IComparable.CompareTo` 不折叠，branchless 分区的前提被破坏，剩下 2 写/元素与比较误判，Hoare（拷贝更少）反而胜出。旁证：`Pair`（8B、同一回退路径）亏损 1.46×，而 `double`（8B、已特化）0.43× —— 与尺寸无关，由比较器形态决定。

**附：为什么 Rust 没问题（本机 rustc 1.100 -O3 同布局反汇编，2026-10-10）**——单字段 24B 探针：`a < b` → `movl;cmpl;setl;ret`（4 条，完全无分支）；契约形 `a.cmp(b) == Greater` → `cmp+setg`；select 消费 → `cmp+cmov`；**带 load/select/store 的 64 元素循环 → 全程无分支，每迭代仅 1 条可预测回边**（`cmpl/setl` 算 is_less、`cmovge` 选值、`addq` 计数、`jne` 仅作循环回边）。对照：RyuJIT 在 RC.1 的孤立 select 形状已同形（`S16Sel` = `mov;cmp;mov;cmovle`，12B），但分区循环上下文仍以 -1/0/1 分支体物化（419B/7 jcc）——**差距在折叠的上下文鲁棒性**：LLVM 的 if-conversion 是结构性 IR 化简，RyuJIT 的 RBO 折叠是定向模式匹配，遇到"比较分支与 csel 消费之间夹访存/多分支"的循环 CFG 不触发。另一半差异在源形状：上游契约是布尔 `<`（自然降级 setcc、无需物化 ±1），本移植被 `Array.Sort` 契约锁在 int 返回的 `CompareTo`（恢复无分支完全依赖 JIT 折叠）。另注：多字段派生 Ord 时 LLVM 也用分支，但选的是 `!=` 等值早退（近 100% 可预测），区别于 C# `int.CompareTo` 测试 `<`/`>`（随机 ~50/50 数据相关）——比较形状层面的额外不可移植差异。

因此：thr8 的收益（§3）依然成立，但它是**在比较器已变 branchy 的前提下**减少拷贝的补救；真正的杠杆是结构体比较器的代码生成，属 JIT 限制（已记录于 `Comparers.cs` 的极性说明），非本移植可改。

## 5. 决定与后续

> 注：本节"保持 96"的初版决定已于 2026-10-11 修订为"能力判定"规则（见 §7）。

**保持 96（上游忠实）**。理由：该常量是上游跨类型标定的架构参数，本项目原则为"架构忠实 + C# 性能惯用法"；改 96→8 属于算法参数偏离，收益明确但应作为"有意偏离"独立决策（需同步注释、README 参数描述，并用 BDN 重跑 int/double 回归）。

**已知残余（保持 96 的代价）**：Struct16 Random ~1.4×、RandomS95 ~2.6×。若未来需要消除，改动点（`IpnPartition.cs` 常量）与全部对照数据即本文。

## 6. 附：C#/RyuJIT vs Rust/LLVM 非循环差异（反汇编对照，2026-10-11，net11 RC.1）

同布局 24B 探针（单字段/三字段），直线（无循环）形状；C# 取 TC=0 FullOpts 与默认 tiering（Tier0/InstrT0/Tier1+PGO）三阶段；Rust 用 rustc 1.100 -O3。

| 直线形状 | Rust/LLVM | C# Tier1+PGO（稳态） | C# TC=0 |
|---|---|---|---|
| `OP 0` 极性 select（`<`/`<=`/`>=`/`==`） | 四极性全 `cmp+cmov` | 仅 `{>, <=, ==}` 折 cmov；**`{<, >=}` 仍分支**（16B；极性不对称） | 同（14B 分支 vs 12B cmov） |
| 扇出：比较结果两用 | 1×cmp + 2×cmov，**零分支** | -1/0/1 菱形：**2 jcc + 2 jmp**（43B） | 同 Tier1 |
| 多字段 CompareTo（链式/早退式） | 内联 + `jne` 等值早退（可预测）+ setcc + cmov | 内联但**全分支树（5 jcc，含方向分支）** | **不内联**（`call`） |
| 返回 bool `<` | `cmp+setcc` | `cmp+setcc`（一致） | 同 |
| 三路消费 | setcc×2 + 2 等值分支 | setcc+cmov×2 + 1 分支 | 同 |

结论：除循环上下文外，差距集中在 ①契约形状（int `CompareTo` vs bool `<`，恢复依赖 JIT 折叠）；②RyuJIT 折叠的**极性不对称**（`{<, >=}` 不折）与**单用途假设**（扇出不折）；③多字段比较器的内联策略与分支形态（Tier1 内联但 5 分支；LLVM 等值早退+setcc/cmov）；④tiering 三阶段（Tier0/InstrT0 远差于 Tier1；稳态 Tier1 与 TC=0 结论一致——简单探针形状相同，多字段仅"是否内联"不同）。均属 RyuJIT 实现特性，非语义/硬件限制。

**极性不对称的机制（Tier1 实测 + 对照探针）**：内联的 `int.CompareTo` 是"先 `a<b`、后 `a>b`"的两比较菱形。RBO/断言传播这类推理**只能使用支配性（dominating）比较**，而只有第一个比较 `a<b` 支配使用点（`a>b` 只出现在 `a<b` 为假的分支上）。谓词若等价于支配比较（`<0` ⟺ `a<b`；`>=0` ⟺ `!(a<b)`）→ 折叠以**复用该分支**（jump-threading/断言消解 select）完成 → 得到分支；若由第二个比较或组合决定（`>0`/`<=0`/`==0`）→ 支配推理不适用 → 走值折叠路径（单 `cmp` + 保留 select）→ cmov。对照：纯 bool 直比五种极性**全部** cmov（select 降低本身与算子无关）；`!(CompareTo >= 0)` 与 `CompareTo < 0` 同形（**语义性**，非语法）。初始 relop 折叠与 select 消解的精确 pass 需 checked JIT 的 IR dump 钉死（发布版 JIT 不支持 JitDump）。

**循环内不折的定位（探针 + 上游确认）**：同一消费形状 `x + (a.CompareTo(b) > 0 ? 1 : 0)`——直线版**无分支**（34B，setcc+cmov 物化，TC=0 与 Tier1 同形）；放进最小循环（数组 load + 累加，**无 store、无共享比较、单用途**）即退回 -1/0/1 分支体（每迭代 2 个数据相关分支，函数 171~201B/10 jcc）——**循环上下文本身即可击穿折叠**。上游确认：**#96380（open，.NET 12 milestone）**，jakobbotsch："We do not emit conditional select instructions inside loops today because we do not have the necessary framework in place to make a proper prediction on whether that is profitable or not. Branches can (and do in real world examples) outperform conditional select instructions due to branch prediction."；RC1 仍在生效——runtime 自己在 RC1 用"禁用 `Random.InternalSample` 内联"规避（#131714："If inlined into a caller with a loop we lose the if conversion and performance suffers"）。RBO 侧对应限制：#48115（open，"handle cases where the redundant compare block has side effects … beyond our reach"）、#127181（不处理 ignorable side effects）。

**8B 非基元与引用类型的 Hoare A/B（2026-10-11，net11，同会话，阈值 96=Lomuto vs 0=Hoare）**：`PairBench`：Ipnsort Random 5.043→**4.390ms**（ratio 1.44→**1.29**，−13%）；`TypeMatrixString`：Random 23.75→**21.07**（1.37→1.22）、D20 8.14→**6.44**（0.75→0.61）、S95 21.27→**15.56**（2.01→1.46，−27%）、Zipfian 18.39→**16.51**（1.23→1.11）——**8B 结构体与 8B 引用同样应走 Hoare**。结合 Struct16（9–96B，−18~−64%），"branchless 仅限特化标量（12 个 typeof 臂）、其余类型全 Hoare"的**能力判定规则获得完整数据支撑**；`96→8` 只覆盖 9–96B，会留下 8B 类 −10~−13%（struct）/ −10~−27%（string）在桌上。

## 7. 决定修订（2026-10-11）：能力判定规则取代 96

**新规则**：branchless Lomuto 仅用于"比较可折成单条原生比较"的类型——默认入口 `ComparableCmp<T>` 的 12 个特化标量臂；其余一切（用户结构体/引用/枚举）走 Hoare。调用式 comparer 入口（`IComparer`/`Comparison`/struct adapter）暂留旧阈值（scope 1b，待补测）。

**数据（net11 RC.1，同会话 A/B）**：Struct16 thr96→Hoare：1.43/1.03/2.93/1.37 → **1.17/0.69/1.04/1.05**（−18~−64%）；Pair：1.44→**1.29**（−13%）；string：1.37/0.75/2.01/1.23 → **1.22/0.61/1.46/1.11**（−10~−27%）。

**实现**：`IIsLess<T>.PreferLomuto(int)`（static abstract，按实例化 JIT 折叠）——`ComparableCmp<T>` 12 个 typeof 臂；`InvertedCmp<T,TC>` 继承内层；`InterfaceCmp`/`ComparisonCmp`/`ComparerAdapter` 返回 `elementSize <= 96`；`IpnPartition.Partition` 决策点改为 `TC.PreferLomuto(Unsafe.SizeOf<T>())`；`MaxBranchlessPartitionSize` 降级为 legacy `internal` 常量。**验证**：JitDisasm——`int` 分区 204B 字节不变；S16/S16B 的 Lomuto 实例消失、Hoare 出现；全量测试 23962 全绿。

**依据**：§4b（折叠只在孤立形状；分区循环内 if-conversion 被禁用 #96380；RBO 不跨副作用 #48115）+ §6（非循环差异）。**后续**：显式 comparer 入口补测（决定是否扩到 scope 1a）；JIT 轨道（上游 issue / nightly 复验）。

**采用后复测（caprule_bench，net11 RC.1，同批 26 用例）**：Struct16 ratio 1.19/0.70/1.08/1.04（vs thr96 1.43/1.03/2.93/1.37）；Pair 1.30（vs 1.44）；string 1.27/0.62/1.43/1.06（vs 1.37/0.75/2.01/1.23）；int 0.39/0.33/0.98/0.34（代码字节不变，差异为 BDN/运行时世代）。四组报告已更新至 `benchmarks/results/`。

**尺寸扫描（1k/10k/100k，旧规则 vs 新规则，同会话 A/B）**：Struct16 Random 1.38/1.43/1.44 → **1.19/1.19/1.17**，S95 2.33/2.98/2.84 → **1.11/1.41/1.05**，D20 1.22/1.09/1.01 → **0.85/0.74/0.68**；Pair 1.48/1.46/1.47 → **1.34/1.27/1.27**；string Random 1.30/1.32/1.37 → **1.29/1.32/1.22**，D20 1.13/0.86/0.73 → **0.99/0.77/0.61**，S95 1.69/1.72/1.85 → **1.48/1.57/1.48**，Zipfian 1.31/1.09/1.11 → **1.20/1.04/1.08**；**int 对照全尺寸 Δ≤0.01**（代码不变）。结论：规则收益**全尺寸成立**（N=1k 起即完整，非大数组专属）；n ≤ 32 本就不经过分区。
