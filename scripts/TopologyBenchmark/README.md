# 250×250 基架动态连通性离线基准

比较友人提供的 `references/TopologyLab/GridTopology.cs`、HLT（也常写作 HDT）分层动态连通性，以及复用缓冲区的全量 BFS。只测独立数据结构，不引用游戏 DLL，不启动游戏，不改动 Mod 运行时代码。实测结论见 [RESULTS.md](RESULTS.md)。

## 复现

需要 .NET 10 SDK，以及本地 `references/TopologyLab/GridTopology.cs`。该参考目录被仓库忽略，换机器需另外提供同一文件；结果清单保存其 SHA256。基准工程直接链接编译原文件，未复制或修改算法。`run.ps1` 优先使用仓库 `.local-tools/dotnet/dotnet.exe`，没有则用 PATH 中的 SDK。

在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/TopologyBenchmark/run.ps1 -Quick
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/TopologyBenchmark/run.ps1 -TestOnly
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/TopologyBenchmark/run.ps1 -Output scripts/TopologyBenchmark/results/my-run
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/TopologyBenchmark/run.ps1 -Events 1200 -Repeats 5 -Seed 20261102 -Scenario bridge_toggle
```

默认 1200 个单格事件，批量场景 600 个事件，5 次独立初始化重放；`-Quick` 使用 120/60 个事件、2 次重放。每批多格计一个事件，CSV 同时给出实际格变更数和每格摊销。事件数取偶数用于删/补成对的场景。`-Scenario` 可限定下表的一个名称。任何编译、分区或内部不变量检查失败均返回非零退出码。

每次输出：

- `environment.json`：CPU、系统、运行时、计时器频率、随机种子、源码/程序集 SHA256、工作负载摘要及其哈希、正确性检查数量。
- `samples.json`：每次重放的逐事件耗时、初始化、内存、GC、查询、分区导出及算法计数器。
- `summary.json` / `summary.csv`：平均、p50/p95/p99、最大值、首次事件、删除/添加分项和重复间波动。

## 三种实现及公平性

**TopologyLab** 使用前景四邻接、背景八邻接，维护欧拉数、孔洞数和两套并查集。真实断裂会扫描受影响的前景区域；补格可能扫描背景。保留节点池整理和批量兜底，全部计入更新耗时。分两种调用方式：

- `Topology-single`：每批按顺序调用原 `Add` / `Remove`。
- `Topology-batch`：多格事件调用原 `AddBatch` / `RemoveBatch`；单格仍用单格接口。仅在多格场景增加这一行。

原文件没有公开前景单点连通查询；适配器在构造时用反射绑定 `_fgNode` 和 `_fg`，计时区直接调用原并查集的 `Find`。不会把一次全图 `CanonicalLabels()` 的成本误记成一次点查询，也没有改写原算法。前景标签导出读取同一套真实存储。参考实现还维护了孔洞信息，HLT/BFS 只维护前景；这是针对基架断裂所需接口的工程成本对比，并非额外功能完全相同的库比较。

**HLT-ETT** 是本目录实现的分层森林算法，依据 Holm、de Lichtenberg、Thorup 的[原论文](https://www.cs.princeton.edu/courses/archive/fall07/cos521/handouts/poly.pdf)和[实现研究](https://people.csail.mit.edu/rahul/papers/dyncon-jea2001.pdf)：

- 边从第 0 层进入，`F_i` 包含层级至少为 i 的树边。
- 删除树边后，在每层选较小一侧，先提升该侧的本层树边，再查找替代非树边；内部非树边也提升，跨割边转成树边。
- Euler-tour tree 维护子树顶点数，以及本层树边/非树边存在标记。查找候选通过树聚合定位，没有把全分量 BFS 冒充 HLT。
- 格子增删转为最多四条边的增删，空格保留为不计入前景分量数的孤立顶点。固定顶点数是整张地图的 62,500；邻接表针对四度网格存储。

动态树使用固定种子的随机隐式 treap，支持切分、合并和根查询。**这是 HLT 层级算法的期望平衡 ETT 实现，不具备原论文使用确定性平衡树时的完整确定性时间保证**；没有实现论文更复杂的加速查询结构，也没有并行化。结论针对这份经对拍的 C# 实现，不代表所有 HLT 实现的最佳性能。图更新 API 在线执行，不使用未来事件信息。

**BFS-rebuild** 是独立前景四连通基线，复用标签和队列数组，每个外部事件（含整批）只重建一次。它不计算孔洞，稳定状态直接查标签。它对应现有“变更后重算”的算法量级，**不等于直接测量游戏中的 `ShipRegistry`**：没有 Verse 回调、HashSet、归属筛选及搬图费用。

所有算法消费相同初态和变更序列，每次事件结束都必须提供准确的分量数、点对连通性和全图分区。边界内空格/非空格、四邻接语义完全一致。测试中的多船是相隔的几何块，没有测试相邻不同 ownerId 的控制域；那需要按归属另建索引或额外设计，不能直接以本基准的单层占用图替代。

## 工作负载

以下均为 250×250 的确定性合成地图，不宣称来自实际存档统计。建筑、墙和屋顶不参与基架连通；“舱室”不会被误画成地板孔洞。

| 名称 | 初态与变更 | 定位 |
| --- | --- | --- |
| `small_ship` | 40×24 完整基架；随机命中一格再补回 | 小船稀疏占图 |
| `nine_ships` | 九块分离的 40×24 基架；随机单格删补 | 多船同图 |
| `dense_plate` | 230×230 基架；随机单格删补 | 高占用上界压力 |
| `blast_repair_3` | 120×70 基架；3×3 成片删除再补回，船边裁切 | 小爆炸与维修 |
| `blast_repair_9` | 同上，改为 9×9 | 大批量压力 |
| `battle_erosion` | 120×70 基架；从仍存活格选中心，半径 1–3 圆形持续删除 | 累积战损、碎片增多 |
| `bridge_toggle` | 两大甲板由一格桥连接，反复删补该桥 | 真断裂最坏形状与摊销行为 |
| `ring_toggle` | 一格厚的大环，反复开闭缺口 | 前景不裂、背景大孔洞反复变化 |
| `random59` | 全图 59% 独立随机占用，再随机翻格 | 碎片压力，不视为典型船体 |

前五个删补场景每次恢复原始状态；`battle_erosion` 不恢复。没有用重复删除已空格来稀释性能数字。正确性专测另外包含幂等操作和批内重复格。

## 验证和测量口径

1. 对全部 512 个 3×3 初始形状，逐格翻转再恢复。检查空/满图、1×1、单行、单列、四角相碰和孤块。
2. 四个固定种子的矩形随机序列混合单格、批量、插入、删除与重复格；每步用独立 BFS 检查分量数和**全部格子的分区双射**，不只比较数量。另测点查询。
3. 小图每步验证 HLT 的层级尺寸约束、非树边两端连通、ETT 父指针/堆序/聚合，以及 TopologyLab 的孔洞和背景不变量。节点池回收测试实际超过阈值并断言发生整理。
4. **每个 250×250 正式场景的每个事件也做完整前景分区对拍**；大型内部不变量检查放在初末态。验证不在计时区。
5. Release、单线程、x64、禁用分层 JIT；每个算法先预热，再以新实例重放完整输入。重复轮转算法执行次序。初始化单列，第一轮结构提升、节点回收和运行中 GC 均包含在更新成本里。
6. `Update*Us` 是 `Apply + Count` 每外部事件的实测微秒。计时器为 100 ns 分辨率的环境中，亚微秒结果只作量级判断，不据此解释几十纳秒的差异；计时调用成本未扣除。p 分位数合并各次重放的事件样本，最大值含操作系统抢占，不是算法最坏时间保证。
7. `QueryNs`：终态预热后 4096 个固定点对重放 8 次，半数从存活格抽样、半数从整图抽样，避免稀疏图全部空格早退。它是稳定终态查询，不是边更新边查询的游戏负载。
8. `ExportUs`：终态导出全部 62,500 格分区的时间。BFS 已经拥有完整标签，只需复制；TopologyLab 展开并查集，HLT 遍历 Euler tour。并不包括构造 Gravship 搬运清单。若应用每次变化都强制导出全分区，就应考虑这项，不能仅引用增量更新数字。
9. 分配量使用当前线程分配计数器；`InitMiB/FinalMiB` 是强制 GC 后的托管存活内存增量估计，不是进程峰值 RSS。GC 发生次数只覆盖更新区间。原 `StatCompactions` 只统计 `CompactAll`，不含仅前景的兜底重建次数；相关扫描已包含在 `RepairCells` 与耗时中。

源码分工：`Grids.cs` 适配和独立 BFS，`HltGrid.cs` 分层策略，`EulerTourForest.cs` 动态森林，`Workloads.cs` 输入，`Validation.cs` 对拍，`Program.cs` 测量与输出。

## 应用边界

本实验只回答数据结构在这些 250×250 输入上的表现。不测游戏 TPS/FPS、Harmony、结构事件扇出、碰撞缓存、归属变更、主驾驶台继任、跨图搬运或保存恢复，也不把未变化期间的现有缓存命中误当作每 tick 全图扫描。大图长寿命节点池峰值和真实存档负载仍应另测；本轮小图已检查整理正确性，但短正式序列未必触发大图整理。
