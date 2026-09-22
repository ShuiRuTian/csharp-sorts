# csharp-sorts 优化调查报告：超越 1:1 移植的分析结论

日期：2026-09-22。状态：调查完成，待用户裁决后进入实施（worktree）。
基线：用户 6 个 commit 后的单引擎 Ipnsort（Quad/Glide/Drift 已移除），22,374 测试绿。
证据：两份调查报告（.superpowers/investigation-layerAB.md、investigation-layerCD.md）、
JitDisasm（/tmp/ipndisasm-out/）、当日 Default 档趋势基准、rust-array-sort 跨语言数据。

## 0. 先校准事实（两项推翻旧结论）

1. **Random 已 0.24x**（938μs vs Array.Sort 3843μs @100k）——不是旧数据的 0.40x。用户 6 个
   commit 的累计效果：random int 上**赢 Array.Sort 4.1 倍**。
2. **1k/2k 的"12–20x 悬崖"基本是测量伪影**：旧 ring rig（InvocationCount=64 钉死）从未离开
   Tier0-minopts 阶段（~11x 慢的间接 cell-call 形态）；9c49fa8 修复后的真实数字：**1k = 0.89x
   （赢）**。真实残余：n∈(32,150] 慢 5–15%（n=50 1.14x，n≈200 平价）。

真实输面只剩两个：**S95/Tail 型（append+sort）1.7–2.3x 慢**；未特化值类型（如 Pair）的
comparer 税 1.40x。其余全面领先或平价（string Zipf 0.58x、结构化 0.06–0.09x）。

## 1. Layer A（比较热路径 codegen）：已到可达最优，无需再动

JitDisasm 审计（Tier1+PGO）：
- typeof 特化完全折叠：ComparableCmp<int>.IsLess = 4 指令（ldr;ldr;cmp;cset gt）
- InvertedCmp 折叠取反+换参为 `cset le`，分区循环 240 字节逐字节相同，零成本
- 分区每元素：单 cmp+cset、无需 store-select（计数算术消掉了）、2x 展开、零边界检查、
  pivot 在寄存器。**没有分支混回来**。tmp.md 的 conditional-ref 担忧仅剩 >16B 分支臂。

残余（低价值/外部）：SwapIfLess 付 bool 物化再测试（~3 指令/swap，属 JIT 短板，可提
issue）；FullOpts/无 PGO 形态下分派不折叠（见 P3）。

## 2. Layer B（小输入路径）：真相与有限机会

- **无 per-call 分配**（值类型 T 全程零分配；ArrayPool 仅引用 T）。原设想的
  "n<256 轻路径跳分配/分派"无分配可跳、分派已热折叠——**价值 MODERATE/低**（估 2–6%
  @n=50–150）。唯一独特价值是冷调用者（~10^5 次调用的 Tier0 暖机墙，真实终端用户成本，
  固定 benchmark 看不见）。
- 叶子（SmallSortNetwork）的真实固定成本：localsinit 清零 16x stp（Rust 付零）+ 4 个间接
  调用 + 间接 CopyTo ≈ 24 元素叶子的 25–30%（IpnSmallSort.cs:148,196）。
  **P2：`[SkipLocalsInit]` + 内联 copy-back + 可选内联 BidirectionalMerge/InsertTail**
  → 1–2k 处 ~3–6%、100k ~1%，低风险，顺带缩小 Tier0 冷形态。
- Pair 类 1.40x = CompareTo 不为 branchless 消费者折叠（Comparers.cs 记录的极性两难），
  是**最大的按类型残余差距**：同 typeof 特化手法扩展到常见用户 struct 不可行（开放类型），
  但可文档化"实现 IEquatable 风格原始比较接口"或提供 SortPrimtive<T> 类入口——设计决策待定。

## 3. Layer C（算法结构）：唯一高价值项 = S95/Tail 的 run→merge 路径

**问题机制**（已查明，非移植 bug）：上游 ipnsort 同样在此输给分支 Hoare（rust 侧 1.43x）。
95% 有序时 introsort 分支全预测+几乎零交换（AS 在 S95 上比 Random 快 7.7 倍），而
branchless Lomuto 无论顺序每元素固定付 1 读 2 写；且检出的 95% run 被丢弃。
Ipnsort 对 S95 与 Random 等速（899 vs 938μs）——顺序无关是设计本性。

**方案（P1，两报告汇合的同一结论）**：run 检测已存在（find_existing_run）→ 加一条
`runLen >= 3n/4` 分支：头部已有序（降序则反转）→ `IpnImpl.Sort(v[runLen..])` 排尾部 →
原地不稳定归并（SymMerge 旋转式，~60–80 行，NoInline；或 ArrayPool n/2 scratch 若放宽零分配）。
- 预期：S95@100k 899μs → 300–600μs = **平价到反超**（AS 499μs）；@1k 6.1→~2.5μs（赢）；
  @1M 估 3–5ms vs AS 11.9ms（赢）。driftsort 参照上限 61.5μs（带 n/2 scratch 的 run+sort+merge）。
- 边界选择 3n/4：保 OrganPipe（run=n/2，今日 quicksort 赢 2.7x）和 Sawtooth（n/5）在
  quicksort 路径，捕获 ≥75% 预排序。边界成本 = 1 次比较。
- 风险：原地归并正确性（不稳定归并的轮转算术）；代码体积（上游拒绝此特性的原因：
  lib.rs:162-164 "makes the implementation a lot bigger"）；Struct128 边际薄（现 1.16x，
  需 gate 在 SizeOf 或实测）。
- **这是结构性偏离上游的第一刀**（上游明示接受此弱点）——归因文档从 "ported from" 升级为
  "derived from + modified"。

## 4. Layer D（混合引擎）：基本已存在，D 就是 C 的 run→merge + 现状

单混合入口的形态今天已 90% 成立：n≤20 插入 → run==n 早退（升/降序 0.06–0.09x 的来源）→
run≥3n/4 归并路径（=P1，唯一缺口）→ 其余 typeof 特化 quicksort。typeof 形状分派已 JIT 常量
折叠（IpnSmallSortConfig + Comparer 臂）。**实施 P1 即完成 D。**
- 单入口 vs Rust 双入口（sort/sort_unstable）：.NET 形态是单不稳定 Array.Sort，单混合匹配
  它；稳定伴随引擎（复用 SmallSortPrimitives 的 60%）推迟到语义需求出现。
- 预扫描在 random 上的代价：期望 run≈2，~2–3 次比较（实测上限 0.4ns/元素）。

## 5. 实施建议（优先级、均待批准；worktree）

| # | 内容 | 预期 | 风险 | 备注 |
|---|---|---|---|---|
| P1 | run≥3n/4 → 排尾 + 原地归并 | S95/Tail 平价→赢 | 中 | 唯一结构项；偏离上游需改 attribution |
| P2 | 叶子去税（SkipLocalsInit+内联） | 小尺寸 3–6% | 低 | 独立可先行 |
| P3 | 分派无 PGO 折叠（typeof 臂替代 static-readonly Kind switch） | 仅 AOT/无 PGO 场景 | 低 | Tier1+PGO 下零收益 |
| P4 | JIT issue 三连（bool-retest/conditional-ref/static-readonly 枚举折叠） | 外部收益 | 零 | basecases/ 已是现成复现集 |
| — | n<256 轻路径 | 2–6%（仅冷调用者价值大） | 低 | 优先级低于 P1/P2 |
| — | 稳定伴随引擎 | 语义需求出现前不做 | — | C.3 分析 |

测试盲区（若实施需补）：255/256/257 阈值边界、降序 run+随机尾（12 分布中无此形状）、
新路径的零分配断言。验证按用户规则：short 档迭代，定稿全精度；代码一律 worktree。
