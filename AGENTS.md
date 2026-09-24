# AGENTS.md — RimExodus: Delta-V 开发约束与架构事实

为了验证此文件确实完整读入，你需要在对话开始给出这个文件内容的标题目录，如果你觉得内容似乎被截断了，你需要补充读取这个AGENTS.md被截断的部分（这是个mm豆测试）。

它只记录后续开发必须遵守的规则、当前仍有效的架构不变量，以及少量容易复犯且难从代码表面看出的约束；§20 是当前代码架构地图（单实现入口索引）、§21 是单实现/单计算纪律的事故教训汇总。功能介绍、设置项、兼容性、已知限制、历史尝试、回归清单和待办不在这里重复维护。

开始工作前必须完整阅读本文件和 `README.md`。如果工具输出截断，继续分段读取到文件末尾；首次回复中给出上述目录，以确认读取的是正确仓库。长期信息应写入本文件、对应专题文档或代码注释，不要依赖 agent/harness 的临时记忆。

## 1. 信息归属与维护规则

按以下顺序确定事实归属：

- `README.md`：对外功能、设置项、Mod 兼容性、已知限制、源码目录结构的单一事实源。
- 主设计 Story：项目总体目标、玩家流程、技术路线和阶段结果。
- `doc/` 下专题文档：复杂机制的权威设计与调研结论。
- `doc/TODO.md`：未完成工作和待验证项；不得把其中排期当成已实现状态。
- `AGENTS.md`：当前仍有效、后续 agent 必须遵守的架构不变量和开发纪律。
- 代码注释：只承载紧贴实现、离开代码就容易误改的局部约束。

维护时遵守：

- 不在 `AGENTS.md` 保留“旧方案 + 覆盖说明”。方案被替换时直接删除旧结论；有复盘价值的过程移到对应阶段文档。
- 同一事实只保留一个权威位置，其他地方用链接和一句摘要引用。
- 功能改动落地后同步相关专题文档。
- 对外功能、设置、兼容性和已知限制变化同步 `README.md`。
- 玩家可见文本统一使用 Keyed 翻译，不在代码中散写中英文 UI 文本。
- 当前仍处于早期开发阶段时，不要为了“未来兼容”提前制造复杂抽象；先保证现有架构契约清晰。

## 2. 提交、发版与工作方式

- 未经用户明确指示，不得 `git commit` 或 `git push`。
- 提交授权逐次有效：用户的一次"提交"只覆盖当时已完成、用户明确认可的那批工作；此后新增的修复、调研结论或后续任务产生的改动，必须再次等待用户明确要求提交，不得沿用早先授权连续提交。
- 需要把用户不认可的提交挪离当前分支时，先建独立分支保存，再回退当前分支；不丢弃、不改写已提交历史。
- 完成实现后只做与风险相称的编译、静态检查和必要验证，等待用户实测并明确要求提交。
- 用户要求“提交”时检查玩家可见变更是否需要写入 `Changes.txt`。
- 不主动修改版本号，也不自行创建新版本节；版本 bump 由用户明确发起。
- 开工前检查工作树。用户已有改动必须保留，不能为了方便 reset、checkout 或覆盖无关文件。
- 查询优先使用环境提供的源码检索工具。涉及 RimWorld 内部行为时，先查仓库已有反编译源码或参考源码，不凭印象施工。
- **诊断日志禁止挂在每帧/tick 热路径上无条件输出**（2026-09-19 教训：gizmo 每帧经 `ShouldShowGizmo`/`ValidateDisabledState` 轮询 `CanLaunch`/`CanStartRitualNow`，一次选中复现刷出 767 条）。热路径（OnGUI/gizmo 验证/getter/MapMesh）埋点必须：先查 `DeltaVLog.Enabled` 再构造字符串、且只按状态变化（边沿触发）输出；优先把埋点挂在用户事件（点击、浮单、job 下发）等天然低频时机。
- 新功能优先通过明确的小型接口接入现有架构，不为一个局部症状复制另一套坐标、生命周期或状态系统。
- **同一功能只允许一份实现（铁律，不变量 #21）**：动代码前先查 §20 架构地图与既有入口；已有实现（本 mod 的唯一入口，或原版/参考源码的公共调用链）必须直接复用，不得并行写第二份等价实现或"镜像"。复用原版私有逻辑时只在唯一入口做一次显式镜像并注明来源（如 `GravshipInterop` 的原版设计器输入格适配）；调用点禁止自行组合换算。教训：自组合 root↔中心锚格使脚印偏移几十格；误认设计器输入格与 root 恒等又造成偶数尺寸一格偏移；速度基变换点乘也曾在两处各写一份。
- 唯一推荐构建命令：`dotnet build Source/DeltaV.csproj -c Release`（net48 + NuGet `Krafs.Rimworld.Ref`，输出到 `1.6/Assemblies/`；构建产物不入库）。

## 3. 外部源码与 RimExodus 参考

涉及 RimWorld、Harmony 或其他 Mod 内部行为时，以仓库中已有的 `references/` 源码或反编译结果为施工依据。

如果缺少需要调查的具体版本源码：

- 先明确告知用户缺少可靠参考；
- 不凭旧记忆假定签名或内部流程；
- 不为了完成任务擅自引入未经确认的新依赖。

RimExodus 是本项目最重要的架构参考之一，尤其包括：

- 多 Map 生命周期管理；
- Pawn 跨 Map 转移；
- 聚焦地图切换；
- 跨 Map 状态保留；
- Projectile 和命中后效处理；
- 非当前地图内容的额外渲染；
- Harmony patch 与兼容层经验。

但 **Delta-V 的空间模型与 RimExodus 地表无缝模型不同**。

RimExodus 的：

- 世界 tile；
- 六边形邻接；
- `NeighborLink.offset`；
- 接缝带；
- 地表连续地形；

都不是太空坐标模型。

可以复用机制和经验，不能把地表邻接语义直接推广成宇宙空间。

同时，其修改应该兼容RimExodus——RimExodus应该不会动宇宙空间和口袋地图，但是要避免这个mod在地面上生效影响RimExodus

## 4. 总体架构：Map 是刚体的局部坐标系

Delta-V 的核心模型：

> 每艘飞船拥有自己的 RimWorld Map；Map 是飞船内部的局部坐标系，飞船作为整体在统一连续宇宙坐标中运动。

RimWorld Map 本身：

- 不连续移动；
- 不任意角旋转；
- 不因飞船转向而重排所有 Thing；
- 不因飞船转向而批量修改 `Rot4`；
- 继续保持原生矩形整数格子结构。

飞船在宇宙中的状态独立保存，例如：

```text
SpacePose
- universePosition
- velocity
- angle
- angularVelocity
```

飞船内部所有 Pawn、Thing、建筑、Room、Region、PathGrid、电网等仍只认识自己的 Map 局部坐标。

**禁止把飞船的宇宙运动实现为批量修改 `Thing.Position` 或 `Thing.Rotation`。**

这样做既没有必要，也会破坏 RimWorld 和第三方 Mod 对稳定 Map 坐标系的假设。

## 5. 统一宇宙坐标与坐标变换

飞船、太空 POI 和太空阶段 Projectile 使用同一个连续宇宙坐标系。

任何系统都不得把以下概念混为一谈：

1. RimWorld Map cell；
2. 飞船局部连续坐标；
3. 宇宙绝对坐标；
4. 当前聚焦参考系中的显示坐标；
5. 屏幕坐标。

统一转换链（universe 坐标 = 轨道层球面上的单位方向向量，球面绝对坐标）：

```text
Map local / Thing local
        ↓
Ship local continuous coordinate
        ↓ Ship SpacePose（球面方向 + 该处切空间基）
Universe sphere direction
        ↓ inverse(FocusedObject.SpacePose)
Focused-frame coordinate
        ↓
Render / screen coordinate
```

球面几何（切空间基、球面移动、弧距、sphere↔tile 纯函数）唯一入口是 `Source/Core/OrbitSphere.cs`。不存在任何持久化的坐标对齐锚：sphere↔tile 互推是纯函数，旧实现的 hostMap 锚 / floating origin / anchorDisplacement / 重锚机制已删除，不得回退。

应建立集中坐标 API，例如：

```text
LocalToUniverse
UniverseToLocal

UniverseToFocused
FocusedToUniverse
```

命名可以随代码演化，但语义必须唯一。

### 坐标纪律

- 不在调用方手写散落的旋转 + 平移公式。
- 地图连续点 ⇄ 球面轨道位置只经所属 `SpaceObject` 位姿与 `SpaceTransform.MapPointToUniverse/UniverseToMapPoint`；口袋图与真实轨道图同一规则，真实轨道图由 `PoiState(NativeParent)` 承载，不存在合成 `MapFrame`。
- 停放 root（格角 + Rot4）与船体帧原点（口袋图几何中心）的换算唯一入口是 `SpaceTransform.ShipOriginFromPlacementRoot`；原版 designator 输入格 ⇄ 最终 root 的整数适配唯一入口是 `GravshipInterop`。调用方不得自行组合 `PrefabUtility.GetRoot` / designator 半跨度偏移 / `engineToCenter`。
- 不建立第二套“战斗专用坐标”和第三套“渲染专用坐标”。
- Projectile、碰撞、外壳渲染和跳帮位置换算必须复用同一坐标定义。
- Renderer 输出只是表现结果，绝不能反过来成为宇宙状态的数据源。
- 宇宙绝对坐标不因切换当前聚焦地图而改变。
- 尽量使用浮点连续坐标保存宇宙状态，不把宇宙位置提前量化为 `IntVec3`。
- 位置存球面单位方向向量（Vector3）；角度/速度是"该处切空间基"下的 2D 量（x=东、y=北），与旧平面系约定连续。

## 6. 聚焦地图与参考系

任意时刻有一张当前聚焦 Map。

**核心不变量：当前聚焦 Map 永远使用 identity transform。**

即：

- 当前 Map 始终按自身 North 正常显示；
- 当前 Map 不因宇宙姿态发生视觉旋转；
- RimWorld 原生鼠标、选择、Targeter、Designator、Pawn 寻路等都继续工作在标准 Map 坐标中。

宇宙画面的参考系为：

```text
DisplayTransform = inverse(FocusedObject.SpacePose)
```

因此：

- 玩家飞船旋转时，看起来是宇宙和其他飞船反向旋转；
- 实际只是显示参考系改变；
- 玩家飞船 Map 内任何 Thing 均没有旋转。

如果切换聚焦到敌舰：

- 敌舰成为 identity transform；
- 敌舰自身 Map 按标准朝向显示；
- 玩家飞船转而成为带相对位置和相对角度的外部代理。

### 勿回退

不得为了让敌舰看起来旋转而：

- 旋转敌舰真实 Map；
- 批量修改敌舰 Thing 的 `Rot4`；
- 批量搬运 Thing 到旋转后的格子；
- 让 Pawn / Region / Room / PowerNet 理解任意角坐标。

非聚焦飞船应通过外部代理表现。

## 7. 飞船 Map

每艘可进入飞船对应一张独立 RimWorld Map。

飞船 Map 内部继续使用原版：

- ThingGrid；
- Pawn；
- Region / Room；
- PathFinder；
- 建造系统；
- Zone；
- 电网；
- 工作系统；
- 存档。

飞船可活动结构之外使用透明不可通行区域承载地图边界。

### 船外真空区域纪律（原 Void 纪律，2026-09-17 用户决策改版）

飞船/POI 口袋图的结构外区域填充**原版 Space 真空**（`GenStep_DeltaVShipFill`；取代旧
DeltaV_Void）：甲板可经原版逆重机制向外扩建（Substructure 可铺在 Space 上）、残骸附近
空位可停靠降落，为 EVA 维护扩展留出地形基础。旧 Void 纪律中「不是宇宙空间」的约束全部保留：

不得：

- 把飞船 Map 的结构外区域当成可飞行宇宙；
- 让远距离 Projectile 通过结构外区域横穿整张 Map 来模拟太空；
- 使用 Map 边长定义宇宙距离；
- 把宿主/显示地图边界当成宇宙世界边界。

宇宙空间在逻辑上独立于所有 RimWorld Map。

## 8. 飞行与建造模式

飞行时只修改飞船的宇宙状态。

驾驶输入预计包括：

- W/S：前后推力；
- A/D：侧向平移；
- Q/E：旋转。

物理模型由统一的飞船状态系统更新：

```text
thrust → acceleration
acceleration → velocity
velocity → universePosition

torque → angularAcceleration
angularAcceleration → angularVelocity
angularVelocity → angle
```

具体质量、转动惯量和推进器贡献公式可以在后续专题文档中演化，不在 AGENTS 中锁死。

### 建造 / 固定轨道模式

进入固定轨道或建造状态时，可以要求飞船宇宙角度吸附到四个正交方向之一。

这只是：

```text
SpacePose.angle
```

的约束。

**不是对飞船内部所有 Thing 执行 Rotate。**

玩家在飞船内部建造时仍在标准 RimWorld Map 坐标中工作。

## 9. 非聚焦飞船与外壳代理

非聚焦飞船初期不做完整内部 Map 旋转渲染。

使用独立的外壳代理表现，例如：

- 船体轮廓；
- 外墙；
- 外露建筑；
- 推进器；
- 武器硬点；
- 破损；
- 必要特效。

代理需要支持：

- 连续位置；
- 任意角旋转；
- 相对于当前聚焦参考系显示；
- 缓存；
- 飞船结构变化后的 dirty / rebuild。

### 外壳代理是缓存，不是事实源

真实飞船结构始终来自其 Map。

外壳代理：

- 可以丢弃；
- 可以重新生成；
- 默认不应独立持久化可以从 Map 重建的数据；
- 不得反向决定建筑、碰撞或飞船内部状态。

如果之后增加 LOD，也必须保持这一原则。

### 非聚焦地图内部渲染

除非后续经过明确调研与性能验证，否则不要为了“看见敌舰窗户里的人”重新引入完整任意角 remote Map renderer。

先解决外壳代理能够满足的需求。

### 整壳分层机制（2026-09-20）

重叠船壳的层序由**命令缓冲**保证，一条管线双挂载：背景缓冲（相机 BeforeForwardOpaque）按 objectId 升序整组绘制非进场壳、组间清深度，全部画完再清深度使聚焦图内容无条件覆盖；前台缓冲（AfterForwardAlpha）承载进场/选点悬停壳，聚焦图画完之后再画、临时覆盖聚焦图（目标图）。**不得回退到海拔/深度排序方案**：正交俯视相机下 `Graphics.DrawMesh` 透明队列按 bounds 距离排序、x/z 分量压过海拔差，负海拔高度带已实测证伪（两船仍逐建筑交叠）。火焰命令的 MaterialPropertyBlock 必须每命令独立池化（共享块会被同帧后收集的火焰覆盖）。

## 10. 专用宿主口袋图与降落图

没有"宿主/显示大地图"。原版口袋图的 sourceMap 引擎契约（storyteller/财富/移除级联从口袋图重定向）由一张**专用无 tile 宿主口袋图**统一承担（`UniverseHostParent_DeltaV`）：

- `Tile` 恒 `PlanetTile.Invalid`，只存在于 `Find.World.pocketMaps`，绝不进 `Find.WorldObjects`（否则 SpawnSetup 的 FastTileFinder 越界崩溃）；
- 永不被聚焦（`Game.CurrentMap` setter 拒绝切入）、永不出现在世界视图、tick 常冻结、殖民者栏无分组；
- 内容是空 Void 图（无事实价值，可随时重建，不序列化引用——按 def 重扫恢复）；
- 所有 Delta-V 口袋图（飞船/POI）的 `sourceMap` 统一指向它，且口袋图一律不占轨道 tile。

**降落与起飞**（星区模型语义，权威设计见 `doc/星区架构.md` §7）：

- 战术降落（对接）目标 = 飞船球面位置所在 SpaceObject 的内部投影（**任何已物化对象均可**，含移动中的有动力飞船——2026-09-21 阶段4 修订，见 §12 与 `doc/星区架构.md` §7）；落点 = 投影对应位置（原版降落选点的初始落点）；深空无目标则拒绝固定，不再凭空生成空画布。
- 原版 gravship launch = 跨星区战略转移：降落到目标 worldtile 上 SpaceObject 的内部（worldtile 地图；tile 上无现成对象时按需生成空画布锚对象）。
- 起飞来源 = 任意宿主 SpaceObject 的内部地图（`ship.dockedMap`；任意真实轨道图，含原版发射到任意 POI tile 生成的图，都能 bootstrap 进入自由飞行）；起飞后位姿 = 宿主对象位姿 ⊕ 起飞点内部坐标。
- 与宿主口袋图完全解耦；worldtile 不与任何一张图一一对应、不参与战术几何，其弧长由 `CellsPerTile` 定义。

## 11. 太空对象与 POI 生命周期

远距离对象不应为了存在于宇宙中就永久持有完整活动 Map。

逻辑上区分：

```text
Universe State
    ↓
Map loading / generation
    ↓
Loaded Map
    ↓
Active / interactable
    ↓
Dormant / unload
```

Universe State 至少负责保存：

- 宇宙位置；
- 朝向；
- 速度；
- 必要身份；
- Map / WorldObject 对应关系；
- 需要跨卸载保存的战斗或 POI 状态。

具体休眠和卸载策略在性能测试后定夺。

### 生命周期纪律

- 当前没有 Map 不代表 SpaceObject 不存在。
- Map 是否加载与对象是否存在是两个不同状态。
- 不把 `Find.Maps.Contains(map)` 当作宇宙存在性的唯一判断。
- 可重建的渲染缓存不应决定对象生命周期。
- 加载 / 卸载必须有单一管理入口，避免 POI、战斗和 UI 各自操作 Map 生命周期。

## 12. 对接、跳帮与进入他船（2026-09-21 阶段4 定案；旧 BoardingConnection 方案退役）

进入另一艘船**不建立跨图 live 连接、不自研自由 Pawn 跨图转移**。两条途径：

- **对接（接舷）**= 整机战术降落到目标 SpaceObject 内部图（移动 ↔ 固定互转管线）：
  对接计划（目标对象 + Map 内落点 + 幽灵船位置提示）+（可选）随动自动驾驶 + 几何
  完成判据（与预定落点位姿重合保持可配置 tick 数）。**自动驾驶 = 「驾驶此船」的特殊状态**：占用驾驶位与按键抑制，按下任意驾驶键即打断自动驾驶并接管手操（打断不取消计划）。
  **选点阶段零消费**（游戏照常运行——2026-09-21 用户决策移除选点暂停）：预览用空洞 Gravship（私有无参构造器 + 只读填充
  Foundations/Bounds/驾驶台/推进器，供原版选点/合法性/禁触链路读取）——严禁在此
  阶段做 Gravship(engine) 消费式冻结（会删除 zone、收缴地板指令、清算区域限制与
  储物组）。**真实消费式冻结唯一发生在完成时刻**（CaptureShipForCompletion），与
  FinalizeShipCapture 移除、重放 InitiateLanding 重建同一调用栈（原版起飞同款毫秒
  级窗口）——对接期间的建造/扩建/zone 变更全部随捕获带走；完成前以新鲜脚印复核
  落点合法性。**落点注册单一数据源**：选点确认时经 ComputeLandingPose 一次性注册
  （host ⊕ 偏移 + 相对角 + 地图锚 cell/rot），此后停靠剪影、对齐判据、最终落位
  （WriteDockedState 直写注册值）全部读同一份数据，**不得二次计算**；停靠剪影
  锚 = 位姿锚（口袋图几何中心），视觉重叠即判据本身。对接后两船同图（建筑拓扑
  共享、基架归属分离），Pawn 移动/战斗/追击全走原版同图机制。
- **跳帮舱**= 载人制导飞行器：导弹式追踪命中敌舰外壳、开孔、密封舱落位堵孔、
  乘员在目标图落点生成（经容器搬运送达）。RimExodus 的 transfer 实现不再直接复用于
  本主题，但其多图共存、聚焦切换、Harmony patch 与兼容处理经验仍是通用参考。

纪律：

- ShipAutopilot 是**纯自动驾驶任务编排器**（固定航点 / 随动目标两类），控制律只经
  `IAutopilotGuidance` 接入；两者均不得与对接完成
  语义耦合——对接完成判据是几何的（位姿窗口 + 保持 tick），不依赖自动驾驶信号。
- 幽灵船是无图、只带船壳快照图的**派生渲染缓存**：不参与降落判定、拾取/聚焦、
  生命周期、星区记账与剪影扫描，无推进器火焰；不入 `universe.objects` 权威列表。
- 对接计划为运行时数据不存档（2026-09-22 修订：不再全程禁存档）：**计划飞行期可正常
  存档**——存档不含计划与自动驾驶任务，读档后计划丢弃、船按已保存速度漂移、不自动
  续接（读档 = 组件重建自动丢弃）；
  仍禁存档的窗口 = 选点阶段（原版深序列化 marker.gravship 与控制器
  landingMarker，空洞预览 Gravship 不具备落盘语义）、到达重放（转移中状态）与跳帮舱
  飞行（乘员在运行时容器，落盘即丢失）。玩家同时最多一个计划；超出可配置距离
  （小于休眠距离）自动取消。

### 聚焦切换与 Pawn 移动分离

Pawn 移动（对接后两船同图，不存在跨图行走）和玩家相机切换地图是两个不同事件；
聚焦切换只经现有 CurrentMap 通道（点击船壳/面板/殖民者栏），任何转移逻辑不得硬编码
跟随切换。多人分队、无人机、远程观察等场景以后都依赖这两个概念保持分离。

## 13. 太空战与 Projectile

太空飞行阶段不要求普通 RimWorld Projectile 永远存在于某张 Map 中。

基本模型：

```text
Map 内武器发射
        ↓
读取原版 weapon / projectile 参数
        ↓
创建 SpaceProjectile
        ↓
在 Universe Layer 飞行
        ↓
与飞船 / POI 碰撞
        ↓
宇宙命中点 → 目标 Map 局部坐标
        ↓
在目标 Map 产生原版或适配后的命中后效
```

### SpaceProjectile

SpaceProjectile 是太空阶段的权威状态。

至少可能包含：

```text
position
velocity
source
launcher
projectileDef
payload
age
```

实际字段按实现需要确定。

### 战斗纪律

- Projectile 宇宙轨迹不依赖当前聚焦 Map。
- 切换聚焦对象不得改变 Projectile 真实位置、方向或剩余飞行状态。
- Renderer 只投影 Projectile，不模拟 Projectile。
- 命中检测在统一宇宙坐标中完成。
- 命中后再转换到目标飞船局部坐标。
- 最终伤害、爆炸、火焰等 Map 内后效尽量交回 RimWorld 原生机制。
- 不使用“把弹丸放在一张 Map 上，然后不断移动整个 Map”作为物理事实源；若后续使用中转 Map，只能是表现或兼容实现细节。

制导弹、点防御、护盾、beam 等高级能力在基本直射链稳定后再设计。

## 14. 碰撞与飞船几何

必须区分：

- 渲染外壳；
- 宇宙碰撞体；
- Map 内实际建筑格。

初期三者可以由同一份船体数据生成，但不要在 API 上默认它们永远完全等价。

宇宙命中点映射为目标 Map 坐标时必须通过目标 `SpacePose` 的逆变换，不根据当前屏幕旋转或代理 Mesh 顶点反推。

如果未来实现：

- 护盾；
- 分区装甲；
- 穿透；
- 船体断裂；
- 可脱落模块；

仍应保持 Universe → Ship Local → Map 的转换方向。

## 15. 缓存、存档与可重建状态

默认原则：

> 能由持久状态重建的运行时缓存不序列化。

典型缓存：

- 外壳 Mesh；
- 相对显示 Transform；
- 当前参考系投影结果；
- 空间索引；
- 临时碰撞加速结构。

这些缓存需要：

- 明确的 dirty 机制；
- 换档后重建；
- Map 删除时释放；
- SpaceObject 删除时释放。

禁止只持久化：

```text
built = true
```

却不持久化其对应缓存内容。

若缓存不存档，其 built/stale 状态同样默认不存档，并提供读档后的明确自愈入口。

需要持久化的是无法从其他状态可靠恢复的游戏事实，例如飞船绝对姿态、速度或已经发生的长期 POI 状态。

## 16. 与 RimExodus 的边界

Delta-V 可以使用 RimExodus 的通用跨图经验或基础设施，但两者的空间职责必须分离。

RimExodus：

```text
Surface world tile
↔ neighboring Surface world tile
```

Delta-V：

```text
Ship-local frame
↔ continuous Universe frame
↔ another ship-local frame
```

因此禁止：

- 用 `NeighborLink.offset` 表示两艘飞船的空间关系；
- 把宇宙中的飞船强行解释为相邻世界 tile；
- 用地表接缝带模拟接舷；
- 让六边形世界几何参与太空飞行坐标；
- 因为“RimExodus 已经有跨图 renderer”就直接把 translation-only 逻辑拿来承担任意刚体姿态。

真正适合复用的是：

- transfer 状态捕获与恢复；
- Map 生命周期经验；
- Harmony patch 模式；
- 原生 / Mod 兼容处理；
- 聚焦切换经验；
- Projectile 后效回交原版的思路。

## 17. Harmony、兼容层与验证

Harmony 规则：

- 能用 Prefix/Postfix、参数修正或状态收尾完成的功能，不优先写 transpiler。
- 必须写 transpiler 时先核对真实游戏 IL。
- 不凭方法名猜重载。
- Harmony 按参数名绑定时必须使用真实参数名；不确定则明确核实签名。
- 手动反射绑定兼容层必须允许目标 Mod 缺失。
- 兼容层初始化失败不得杀死 Delta-V 主 Mod。

兼容策略：

- 第三方 Mod 未安装：零行为、零错误。
- 签名漂移：Warning + 安全退出对应兼容层。
- 不为了兼容一个 Mod 污染核心宇宙坐标模型。
- 需要支持特殊 Projectile / Verb 时，在其自然公共入口适配，而不是复制其整套战斗算法。

验证顺序：

1. 编译；
2. Harmony 绑定检查；
3. 游戏启动确认 Mod 正常实例化；
4. 最小游戏内回归；
5. 用户实测后再提交。

涉及坐标和参考系的改动至少验证：

- 当前聚焦船保持 identity；
- 非聚焦船相对位置正确；
- 非聚焦船相对角度正确；
- 切换聚焦前后绝对 Universe State 不变；
- 连续多次切换无明显漂移；
- 存档读档后姿态恢复一致。

## 18. 当前最重要的架构不变量

如果其他文档尚未读完，至少不能违反下面这些规则：

1. **飞船 Map 永远是稳定的局部 RimWorld 坐标系，不随宇宙姿态真实旋转。**
2. **飞船宇宙姿态保存在 Map 外部的统一连续坐标系。**
3. **当前聚焦 Map 永远使用 identity transform。**
4. **非聚焦飞船默认显示外壳代理，而不是完整旋转内部 Map。**
5. **切换聚焦只是参考系变化，不修改任何对象的绝对宇宙状态。**
6. **禁止通过批量修改 Thing.Position / Rot4 实现飞船转向。**
7. **专用宿主口袋图只是口袋图 sourceMap 锚点，不是显示画布、不是宇宙边界、不可聚焦；降落目标与起飞来源都与宿主无关。**
8. **所有跨层坐标转换必须收口到统一 Transform API。**
9. **SpaceProjectile 在 Universe Layer 中拥有独立权威状态，显示层不能反向驱动物理。**
10. **命中流程始终是 Universe → target ship local → target Map。**
11. **Pawn 跨 Map 与相机聚焦切换是两个独立机制。**
12. **RimExodus 的地表邻接语义不得泄漏到 Delta-V 的连续宇宙模型。**
13. **可重建缓存不是存档事实，也不能成为游戏逻辑权威状态。**
14. **未经用户实测和明确要求，不提交代码。**
15. **universe 坐标是球面绝对坐标（OrbitSphere 唯一入口；几何与尺度按 PlanetLayer 参数化、缓存分键，CellsPerTile 是设置项而非常量）；不得重新引入任何坐标对齐锚、floating origin 或把口袋图挂到真实 tile 上。**
16. **星区（worldtile）只是 coarse address 与 SpaceObject 管理/生成记账维度；碰撞、降落选点等一切战术几何判定一律在星球轨道坐标中完成，不由 tile 几何决定。**
17. **每个可进入地图都属于一个 SpaceObject；内部地图坐标 ⇄ 轨道坐标的换算唯一通道是所属对象（对象位姿 ⊕ 内部局部坐标），不存在图级坐标锚。**
18. **星区无实体、无图；不存在「与星区对应的 Map」。**
19. **进入他船只经对接（模式互转 + 原版管线）或跳帮舱容器投放；不建跨图 live 连接、不自研自由 Pawn 跨图转移；ShipAutopilot 完成语义不与对接耦合（对接完成判据是几何的：位姿窗口 + 保持 tick）。**
20. **SpaceObject 的运动只属于游戏时钟（tick 驱动）：自动驾驶、跳帮舱、未来弹道与一切宇宙状态积分一律按游戏 tick 推进（暂停 = 无 tick = 冻结；倍速 = 加速）。模拟路径禁止 `Time.deltaTime`/帧驱动积分——那是硬造时间。表现层（火焰粒子/音频/帧表）只读真实时间、不移动对象，不受此限（2026-09-21 用户定案）。**
21. **同一功能只有一份实现：先查 §20 架构地图复用既有入口（本 mod 或原版调用链）；原版私有逻辑的镜像只做一次并收口在唯一入口；调用点禁止自组合换算。**
22. **飞船不是 SpaceObject（解耦架构 §16.1–3）：SpaceObject = 地图容器（一图一刚体、每对象只积分一次）；船 = 主驾驶台认领的归属 + 同归属四连通控制域（ShipRegistry 唯一查询）；船不保存可与容器分叉的第二套宇宙积分状态，停靠船位姿 = 容器位姿 ⊕ 船体锚派生。**
23. **玩家与 NPC controller 必须是同一 ShipController 基类的两个子类（解耦架构 §5.4/§16.17）；出力前公共检查与拆分入口（RequestThrust）独立于 controller，玩家确认用原版 forcePause 弹窗；被动阻力与纯漂移不拆船，实际积分与 MPC 预测用同一阻力因子。**

## 19. 星区模型（核心已实现，待游戏内实测）

权威设计见 `doc/星区架构.md`（2026-09-16 六稿评审通过后施工）。§7–§9 核心与 §11.1 左侧面板/星球仪（含 §12.2 星区地图 = 星球仪连续缩放、LOD 分层、图标/剪影点击聚焦）已落地：尺度重标定（CellsPerTile 设置项默认 20000 + 位姿即时重标定）、可降落判定（任何已物化 SpaceObject 内部投影，含移动目标；空间图落点规则 = 纯太空可停但禁触异主基架）、**基架归属体系**（每个基架格记录归属 SpaceObject ID，`SubstructureOwnership` MapComponent；引擎范围/承载、逆重发射与自由飞行捕获一律按「自己 ID」过滤，`GetConnectedSubstructure` 归属过滤 prefix 单点收口；建造按邻接规则继承/保持未占有(0)/拒建多归属冲突，未占有域相连时整域吸收、争议桥保持未占有，未占有(0) 是真实归属域（可 bootstrap 成船）；剪影按 Map+归属 ID 双键、朝向配对经 ShipSilhouette.OrientationForCurrentMap（口袋图纹理配船姿态角、停靠图纹理配宿主帧角，不得自行组合）；「修改基架归属」命令在 Orders 分类，支持新建独立 ID；详见 `doc/星区架构.md` §7 基架归属模型）、口袋图降落/起飞（战术降落 = 选点 → 自动驾驶 → 清除原图 Substructure → 落点生成 Substructure：选点只冻结不搬运的 Gravship 数据快照、船体全程留在口袋图，控制权夺取复用原版 cutscene 级联、目标图正常显示，船壳走标准非聚焦外壳渲染，落点反向映射「宿主帧 ⊕ 落点局部，角度 = 宿主帧角 ⊕ 最终放置朝向」后交给轨道层通用自动驾驶 `ShipAutopilot`（TrackTo 随动 + 失败信号，可复用）飞行；保持判据满足后**直接放置**完成真转移（2026-09-24 解耦重构：GravshipInterop.CaptureDomain 按当前控制域捕获 → StripDomain 剥离源图 → PlaceIntoMap 放入目标图，与移除同一调用栈；不再重放原版 InitiateLanding，不进 cutscene/mask/LandingEnded 链路）；落地后同图建筑拓扑但基架归属分离（落点盖章为船 ID），空白空画布不作战术降落目标；**对接口**（门类建筑，真空屏障科技，朝向决定延伸方向；已停靠落定后自动铺设密封对接通道至首个障碍，通道非基架、原版房间机制原生密封，端口移除同步收回）、星区生成记账服务（`SectorContentService`）、空画布与被占用宿主图纳管、SpaceObject 派系字段、推进器动力来源接口（`ThrusterPower`，默认模型读推进器自身实际状态：故障/排气堵塞/断电，不查引擎连接）、统一状态面板。未实现：星区内容类型占位、伴生物、VEF/电推与 VGE 燃料的 Model 替换实现（仅接口）。补充约束：

- 星球轨道坐标系必须可按星球参数化/实例化，不得烧死「只有一个轨道层」（恒星系扩展性）。
- 星区内容密度数值（保底/分段概率）仍属 `doc/星区架构.md` §12.1 待定项；当前默认残骸保底 1、无额外量，只经 `SectorContentService.Rules` 调整。
- **自动驾驶出力必须走刚体动量管理**（2026-09-18 用户决策）：制导期望力经 `ShipPhysics.ForceToCommand` 归一化后由联合分配器 `Allocate` 求真实逐推进器油门与可达力，积分使用可达值（玩家船/敌舰/未来舰船 AI 同构，机动受推进器布局硬约束，禁止绕过分配器直接合成力）；**虚空动力**仅限停靠终段收敛辅助（`FlyTo` 的 `allowVoidThrust`，按距离渐变补齐「期望 − 可达」差值），不写油门、不进火焰。推进器火焰表现层（聚焦地图内与非聚焦船壳两条路径）只读分配器真实油门（`MassReport.throttlesWrittenAt` 0.25s 新鲜期防幽灵火焰）。
- **已停靠→自由飞行转换源图善后**（`HandleDepartedSourceMap`，只服务已停靠→自由飞行与读档清扫）：worldtile 图不受转换影响（去留只由锚对象配置与距离生命周期决定），非 worldtile 图空白立刻删除（遗留基架转无主残骸）、非空白走距离生命周期；**禁止按对象身份（空画布锚/玩家 Settlement/站点）特判去留，转换不得成为 worldtile 图的销毁者**。
- **逆重发射离场 = 星区驱动标准流程**（2026-09-20 用户决策，取代按源图身份分支的多重实现——旧实现从 wreck 等停靠源发射时星区 worldtile 图无人处理、wreck 图被 CurrentMap/非空白守卫跳过、旅行期间距离评估因无玩家参考点整体短路，双图均不删）：① 任意地图触发起飞，战略源地址 = 地图归属星区 worldtile；② 飞船自有飞行口袋图源总是销毁口袋图（遗弃滞留者、遗留基架转无主残骸）；③ 归属星区 worldtile 图按逆重锚决定去留（无锚 → 原版 `AbandonMap` 弃置 + 基架转残骸，有锚/有其他停靠船保留；无图跳过）——**任意源类型统一执行，禁止只对特定源跑锚规则**；④ 星区内其余 SpaceObject 地图（含停靠源图）一律按超删除距离处理（`SpaceLifecycle.PackDepartedSectorPois` → TryPack：休眠 + 快照收纳/删除、对象保留为 packed，守卫照常生效）——**旅行期间 `MinPlayerDistance` 为 +Infinity 且评估整体跳过，不得依赖自然距离触发，离场时刻必须显式执行**。捕获口径不变（口袋图发射 = 整船捕获，停靠图发射 = 力场内捕获，两口径并存不得合并）；无 tile 源的星区弃置确认弹窗对自有口袋图与停靠源统一；善后统一排在 TravelTo 放行后的 Postfix + 帧边界（排队过早会卡守卫——旧 bug 勿回退）。
- **归属/对象 ID 一律经 `UniverseManager.AllocateOwnerId` 单一计数器分配**（2026-09-20 撞号教训：对象注册与裸 ID 双计数器互不可见 → 域名反查错对象、异主域同号合并被捕获吞并）：单调递增；**注销对象必须推高计数器**（被注销 ID 仍被驾驶台/图域持有，绝不再发）；读档对齐到 max(对象, 全部图归属格, 全部驾驶台身份)；**禁止任何调用点自行扫 objects 取 max+1 发号**。**飞船身份常驻驾驶台**（`CompDeltaVPilotConsole.shipId` 持久，创建/首次解析时领取，解析链与重生 BFS 重盖章见 `doc/星区架构.md` §7）；不得再造停泊/恢复等一次性身份搬运。
- **shipId 写入场景（2026-09-24 解耦重构修订）**：驾驶台 spawn 认领（ClaimOnSpawn：无身份的新台按脚下归属领主/备用、未占有域领新 ID 只认领连通片）与随 comp 消亡；战略起飞不写归属数据（身份随 comp 飞行，容器销毁）。归属 ID 与对象 ID **语义分离**（共用计数器防撞号，禁止 ObjectById(ownerId) 反查）。重生重盖章的承载图必须含飞船自有口袋图（无 tile，勿再用 `IsZeroGravity` 一类要求有效 tile 的守卫拦掉）。**飞船进自由飞行时按 ID 整清源图旧域**（`SubstructureOwnership.ClearOwner`；基架不重叠，直接清零归属即可）。
- **战术降落（自由飞行→已停靠）豁免原版降落结局与场景落地钩子**（2026-09-20 用户决策）：LandingEnded 的降落结局掷骰与 `Scenario.PostGravshipLanded`（机械族追击等 ScenPart）在战术降落窗口（`LandingApproachController.TacticalLandingEnding` = 到达重放 ArrivalPending 或降级立即降落窗口）内一律跳过；不得让战略仪式残留的 `launchInfo.doNegativeOutcome=true` 在战术降落二次生效。战术流程不启动/不延长逆重引擎冷却（冷却唯一启动点 = 战略发射 `ConsumeFuel`，降落期唯一写入 = "引擎过热"结局）；战略到达的结局、场景钩子与冷却保持原版。
- 设计与实现的唯一权威仍是 `doc/星区架构.md`；后续修订以该文档为准并同步 `README.md`。


## 20. 当前代码架构地图（单实现入口索引，2026-09-24 解耦重构版）

后续开发动代码前先在此查入口；同一功能不得出现第二份实现（铁律/不变量 #21）。架构演化时同步本节。

**流程状态实例归属（2026-09-22 迁移定案；2026-09-24 解耦重构后服务清单）**：一切可变
游戏流程状态收进 UniverseManager 持有的**不序列化实例服务**（`Approach`/`Autopilot`/
`BoardingPods`/`ShipTransitions`/`Lifecycle`/`Generation`，惰性属性）——
读档时原版反序列化器重建组件（`Scribe_Collections.Look(..., LookMode.Deep)` →
`Activator.CreateInstance`），实例字段自动归零，「读其他档/退主菜单残留旧流程」不可表示。
**禁止再造可变静态游戏状态**（新代码要写 static 可变字段前先自问它是否属于某个
universe 实例）。静态白名单仅四类：无状态纯过程/纯函数（`SpaceTransform`、
`GravshipInterop` 原版边界镜像）；输入层快速门（`PilotKeyBridge.SuppressVanillaKeys`/
`ActivePilotKeys`——按键补丁在主菜单也被轮询，必须免判空）；渲染/反射/几何缓存（材质、
纹理、FieldInfo、`OrbitSphere`——§15「换档重建」）；同调用链重入卫兵与日志沿检测计数
（`Patch_GravshipUtility_PreLaunchConfirmation.chaining`、`lastLoggedFx` 系列）。
dev `ResetObjects` 在**同一 universe 实例**上换 objects，必须显式调各服务 `ResetFlow()`
（见该处注释）。渲染缓存与按键抑制门仍为静态、读档经 `LoadedGame` 清理。

### 驱动节奏
- `UniverseManager.GameComponentTick`（唯一 tick 总装，游戏时间语义）：`Autopilot.Tick() → BoardingPods.Tick() → Approach.Tick(this) → 容器物理积分循环`（自动驾驶中的容器被积分循环按 `IsActive` 跳过，无双驱动；对接组合体随宿主容器惯性漂移 + 被动阻力）。
- `UniverseManager.GameComponentUpdate`（每帧）：只做渲染/输入分发（船壳分层、星球仪/面板、驾驶状态维护、火焰/音频、跳帮舱标记投影）——模拟路径禁止入帧路径（不变量 #20）。

### Core/（数据与算法）
- `OrbitSphere`：球面几何唯一入口（`Basis/Move/Offset/ArcCells/TileFromDirection`；`Offset` 是 `Move` 的局部球面反解；按 PlanetLayer 参数化、缓存分键）。
- `SpaceTransform`：全项目唯一坐标变换 API——2D 旋转/`WrapAngle`；Map⇄对象局部（`ShipLocalOrigin/MapToShipLocal/ShipLocalToMap`）；Map 连续点⇄universe（`MapPointToUniverse/UniverseToMapPoint`，统一消费地图所属容器位姿）；停放 root→船体帧原点（`ShipOriginFromPlacementRoot/GravshipBoundsCenterFromEngineRoot/BoundsCenterOfCells`）；速度换基 `TransportVelocity`、刚体点位姿 `RigidPointPose`；显示层参考系与投影。原版 designator 输入格⇄放置 root 只在 `GravshipInterop` 适配。
- `SpaceObject/PoiState/SpacePose`：宇宙状态（存档事实）。**SpaceObject = 地图容器**（具体类；objectId/pose/map + 环境 faction，label 为派生拼接名/环境名虚属性）；`ShipState` 已删除（解耦重构），飞船不再持久化为宇宙对象。`PoiState` 特化 POI（NativeParent/SnapshotPocket + envLabel/快照）。
- `ShipRegistry`（新，飞船查询唯一入口）：`Ship` 句柄（ownerId/map/主台 comp）与 `ShipsOnMap/ShipByOwner/OwnershipHasMainConsole` 枚举；**控制域 = 主台脚下同归属四连通**（缓存经 `SubstructureOwnership.GlobalStructureVersion` + TTL 失效）；**残留块 = 剩余基架物理四连通**（两语义不得混用，§8.1）；状态派生 `StateOf`（自由飞行 = 唯一船 + 非天然地面接触 `RestsOnNaturalGround`（基架下非 Space 真实地面）+ 非地表图）；位姿派生 `ShipPose/HeadingAngle`（容器 ⊕ 船体中心锚 ⊕ σ=AngleFromRot4(主台朝向)，控制台 East ⇔ 船帧对齐图帧）；`FactionOf/Contains/EngineOn`（引擎查询仅供战略发射）。
- `UniverseManager`：`objects` 权威列表与 `RegisterObject/UnregisterObject`、`ObjectById`、`AllocateOwnerId`（对象/结构归属共用唯一计数器；**语义分离，禁止反查**）、结构名注册表 `NoteStructureName/RenameStructure/NewWreckName/LabelForOwner`（改名唯一出口，同步本船引擎 RenamableLabel）、专用宿主口袋图、`FrameOf`、`SetFocused`（聚焦 = CurrentMap 派生，不再持久化 ID）、`ResetRuntimeServices`、tick/update 总装（积分对象 = 容器）。
- `ShipPhysics`：质量/惯量/联合分配器/积分（刚体动量管理唯一实现；`GetReport(Map)`、`Integrate(SpaceObject,…,pilotFrameAngle)` 含**被动阻力**半衰期衰减，`PassiveDragFactors` 与 MPC 预测同一因子）；`BuildControlModel` 只读预览与真实 `Allocate` 共用同一分配核心，只有 `Allocate` 写油门。`DenseAdmmQpSolver`：无游戏依赖的小型凸 QP 唯一实现。
- `SubstructureOwnership`：基架归属 MapComponent（归属 = 结构 ID）；`GlobalStructureVersion` 域缓存失效源；`ClaimConnectedDomain` 唯一归属算法；`ResolveBuilt` 多归属邻接保持未占有（不拒建）。`DeltaVSettings/DeltaVMod/DeltaVDefOf/DeltaVLog`。

### Interaction/（玩法流程）
- **ShipController 基类 + PlayerShipController/NpcShipController 双子类**（解耦架构 §5.4 强制）：统一驾驶契约（`CanEnter/Enter/Exit/ComposeCommand` + 确认边界 `PresentSplitConfirmation/PresentLeftBehindConfirmation`）；玩家子类 = 手操输入 + 自动驾驶交互 + 拆船/留置确认；NPC 子类内持 `INpcShipAi`（接入边界，本轮空实现 NoopNpcShipAi）。`PilotService` 是路由服务（universe.`Pilot`）：控制状态派生、按键维护与抑制、controller 生命周期；驾驶状态为运行时数据不跨档。
- `ShipFlightTransitionService`（universe.`ShipTransitions`）：转换与拆分的唯一编排——**出力前公共检查与拆分入口 `RequestThrust`**（仅推进器出力触发；玩家经 controller 弹 forcePause 确认窗，NPC 直接执行；取消后结构版本不变不重复弹窗）、`SplitResiduals`（公共拆分：残留块 → 各自新容器+残骸归属+「残骸 #N」）、`TransferDomainToNewPocket`（无引擎搬运核心：CaptureDomain → StripDomain → 新口袋图 → PlaceIntoMap → 新容器注册，脱离/拆分共用）、`BeginDocking`（源图残留事前确认 → 选点）、`CompleteTransferIntoHost/FinishDocking`（直接放置合图 + 源图处置）、`BeginFreeFlight/BeginFreeFlightFromConsole`（脱离：通道收回 + 断片/留置汇总确认 + 控制域转移）、战略离场适配（`HandleVanillaTakeoffFromDeltaVSource/HandleStrategicTravelDeparture`，星区标准流程保留）。`GravshipInterop` 是原版私有字段镜像的唯一收口（设计器输入格换算、`BuildPreview`（按控制域）、**`CaptureDomain`/`StripDomain`**（按 cells 的捕获/移除段镜像，无引擎搬运基础）、`PlaceIntoMap`、cutscene 压零）。
- `LandingApproachController`（universe.`Approach`）：对接计划编排——`DockingPlan` 状态对象（含 structureId 落点盖章；构造即完整，唯一事实源 `ActivePlan`）、`TryConsume`（原子发布 + marker 复核引用保留）、`Tick`（守卫/位姿窗口/保持计数）、`CompleteDocking`（新鲜脚印复核 → 完成时刻捕获 → 直接放置 → 帧边界 FinishDocking）、`CancelSelectionOrPlan`。
- `ShipAutopilot`（universe.`Autopilot`）：任务按归属 ID 键控、物理作用于容器（`FlyTo`/`TrackTo`，出力前经 `RequestThrust` gate）。制导唯一边界 `IAutopilotGuidance`：`OnlineMpcGuidance`（六状态 MPC，**预测含与积分同口径的被动阻力**）、`BrakingGuidanceFallback` 数值回退。
- `BoardingPodFlight`（universe.`BoardingPods`）：跳帮舱（目标 = 地图容器，整图即跳帮面；tick 驱动纯追踪、命中开孔、密封舱落位、乘员投放）。
- `FreeFlightBootstrap`（重力门禁查询）、`Designator_ReassignSubstructure`（修改基架归属命令，**主台脚下格保护 + 汇总提示**）、`PlaceWorker_DeltaVSubstructureOwnership`（多归属邻接不拒建，仅邻接着色提示）、`ShellPicking`（外壳命中唯一入口，每容器一壳）。

### Rendering / Lifecycle / Generation / World / Buildings / Harmony / Debug
- `ShellRenderer + ShellLayer + MapOverlayRenderer`：整壳分层唯一管线（背景/前台命令缓冲，**每容器一壳**、按 objectId 层序）；`DockingGhostRenderer`：对接幽灵船（按控制域烘焙，不入 objects）；`ShipSilhouette`：剪影纹理唯一入口（朝向配对 = 船所在图容器角，口袋/宿主同一规则）；`PlanetGlobeRenderer`（星球仪/星区地图：容器图标/标注 + 逐船剪影标记）；`WorldShipMarkerRenderer`（世界剪影，按船 = 归属渲染）；`ThrusterEffects/ThrusterAudio`（火焰/工作音，只读容器报告油门）；`ShipStatusPanel`（统一左侧面板：**多船列表 + 当前查看/操作船切换** `TryGetSelectedShip`）。
- `SpaceLifecycleService`（universe 实例持有；POI 距离三段、休眠/唤醒、地图销毁与缓存释放唯一管理入口）、内部 `MapDormancyService`、`SpaceMapGovernance`（MustStayActive 按「图上有玩家主台派系船」保护）、实例 `IncrementalMapGenerationService`、`PoiSnapshot`、`PocketMapHostService`。
- `ShipPocketMapFactory`/`ShipStructureStamp`/`ShipTestScene`（口袋图与测试场景；测试敌舰含驾驶台 = 有效 NPC 船）；`UniverseHostParent_DeltaV`/`ShipPocketParent_DeltaV`/`SpaceHostParent_DeltaV`/`SectorContentService`（世界对象与星区记账）。
- `CompDeltaVPilotConsole`（船身份与认领锚点：shipId/isMain 持久、ClaimOnSpawn/继任/主备切换/改名 gizmo）、`Building_DockingPort`（对接口+通道；脱离编排 `RetractChannelsFor/CollectChannelLeftBehind`）、`Building_BoardingPodLauncher/Building_BoardingPodLanded`（跳帮舱发射管/落位密封舱）。
- Harmony 按文件分域（LandingApproach/DockingPlanOrders/GravshipLanding*/GravshipCaptureScope（仅到达画面冻结，口袋发射整域例外已退役）/PoiDormancy/KeyBindingSuppression/SubstructureOwnership/**SubstructureBuildRules（太空图基架范围放开 + 引擎改名反向同步）**/WorldCameraRotation/…）；`DebugActions_DeltaV`（dev 设施：测试场景/阻力开关/强制拆分/宇宙 dump 等）。


## 21. 单实现/单计算纪律（2026-09-21 对接调试连环事故的教训汇总）

§2 铁律与不变量 #21 的展开；每条都来自实测事故，违者复犯：

- **注册即唯一**：任何"确认时刻"产生的数据（落点、目标位姿、锚点）只计算一次、存入状态对象，此后显示/判据/写回**全部只读注册值**；写回路径禁止重算（事故：完成时用真实清单把落点位姿重算了一遍，与注册数据分叉，落位偏移）。
- **显示锚 = 逻辑锚**：给玩家的视觉对齐参考（停靠剪影/幽灵/标记）必须锚定在对应逻辑量的锚点上（事故：剪影按脚印包围盒居中、位姿锚在口袋图几何中心，手操视觉对准但落位偏了两个中心之差；自动驾驶锚对锚反而正确，长期掩盖了 bug——两条路径"都对"本身就是坏味道）。
- **喂原版 API 前先抄原版调用方**：`CanDesignateCell` 收设计器鼠标输入格，`InitiateLanding` 收最终放置 root。`GetRoot(GetSizeRotAdjustedCell(loc))` 对偶数尺寸不恒等（40×24 North：输入 72,54 → root 73,55）；计划只存 root，复核经 `GravshipInterop.RootToDesignatorInput` 反编码。旧的「root 恒等」断言已被实测推翻，不得复用。
- **复核 = 重走主路径**：验证/复核代码必须调用与主路径**相同的入口**在当前数据上再跑一遍；另写一份"近似检查"就是第二实现，必错（同一次落点复核先后踩了三个坑：读错 Find.CurrentMap、漏设预览 Rotation、自组合换算——全在第二实现上，主路径从没错过）。
- **流程状态 = 单一对象**：多字段流程状态收敛为构造即完整的状态对象（DockingPlan 模式），对外唯一事实源（ActivePlan 模式）；禁止散落静态字段 + 从 ship.docking 等标志推断拼装状态（事故：「对接中但目标未知」成为可表示、可观测的状态）。
- **模拟只认游戏时钟**（不变量 #20 的由来）：任何宇宙运动一旦脱离游戏 tick 就会在暂停期继续动——发现"暂停时东西还在动"不要修表象，删掉帧驱动/deltaTime，回到 tick。
