# AGENTS.md — RimExodus: Delta-V 开发约束与架构事实

本文件只记录当前有效的开发约束、架构不变量和单实现入口。历史过程、事故复盘、测试清单与待办分别属于专题文档和 `doc/TODO.md`。

开始工作前必须完整阅读本文件和 `README.md`。首次回复给出以下标题目录；若输出截断，继续读取到文件末尾。

## 目录

1. 信息归属与维护规则
2. 提交、构建与工作方式
3. 外部源码与 RimExodus 参考
4. 总体架构：Map 是刚体局部坐标系
5. 统一宇宙坐标与坐标变换
6. 聚焦地图与参考系
7. 飞船 Map
8. 飞行、控制与物理
9. 非聚焦对象与外壳代理
10. 宿主口袋图、降落图与战略移动
11. SpaceObject 与 POI 生命周期
12. 对接、shuttle 与进入他船
13. 太空战与 Projectile
14. 碰撞与飞船几何
15. 缓存、存档与可重建状态
16. 与 RimExodus 的边界
17. Harmony、兼容层与验证
18. 核心架构不变量
19. 当前项目状态
20. 当前代码架构地图（单实现入口）
21. 单实现、单计算纪律

## 1. 信息归属与维护规则

- `README.md`：玩家可见功能、设置、兼容性、已知限制和目录结构。
- `doc/story.md`：总体目标、玩家流程和阶段路线。
- `doc/` 专题文档：复杂机制的权威设计；太空战以 `doc/太空战架构.md` 为准。
- `doc/TODO.md`：未完成和待验证事项，不代表已实现。
- `AGENTS.md`：当前架构事实和开发纪律。
- 代码注释：只记录紧贴实现的局部约束。

同一事实只保留一个权威位置，其他位置用引用和简短摘要。功能落地后同步专题文档；玩家可见变化同步 README。UI 文本使用 Keyed 翻译。

Mod 尚未发布，不做旧开发快照的存档兼容、迁移键或兼容分支；正常的新档存取必须工作。

## 2. 提交、构建与工作方式

- 未经用户当次明确授权，不得 `git commit` 或 `git push`，也不得修改版本号。
- 开工前检查工作树并保留用户已有改动；禁止为方便而 reset、checkout 或覆盖无关文件。
- 唯一推荐构建命令：`dotnet build Source/DeltaV.csproj -c Release`。
- 涉及 RimWorld 内部行为时先查 `references/` 中对应版本源码，不凭记忆猜签名或流程。
- 热路径日志必须先检查 `DeltaVLog.Enabled`，只在状态边沿或用户事件输出，禁止每帧/tick 无条件刷屏。
- 当前阶段优先清晰的现有契约，不为未来兼容制造额外抽象。

## 3. 外部源码与 RimExodus 参考

RimWorld、Harmony 和其他 Mod 行为以仓库中的源码或反编译结果为依据。缺少可靠参考时先说明，不猜测、不擅自增加依赖。

RimExodus 可参考多 Map 生命周期、跨图状态、聚焦切换、Projectile 后效和 Harmony 模式；不得复用其地表 tile 邻接、六边形、接缝带或平移连续地图语义来表示宇宙空间。Delta-V 行为只在宇宙与口袋图语境生效，不影响 RimExodus 地面地图。

## 4. 总体架构：Map 是刚体局部坐标系

每个可进入的宇宙对象拥有稳定的 RimWorld Map；Map 是刚体内部局部坐标系。宇宙运动只修改 Map 外的 `SpacePose`：位置、速度、角度和角速度。

禁止通过批量修改 `Thing.Position`、`Thing.Rotation`、Room、Region 或地图格来实现飞船宇宙运动和任意角转向。

## 5. 统一宇宙坐标与坐标变换

坐标链为：Map/Thing 局部 → 船体连续局部 → `SpacePose` → 球面 universe → 聚焦参考系 → 屏幕。

- 球面几何唯一入口：`OrbitSphere`。
- Map 连续点与 universe 互转唯一入口：`SpaceTransform.MapPointToUniverse/UniverseToMapPoint`。
- 停放 root 与船体帧原点换算唯一入口：`SpaceTransform.ShipOriginFromPlacementRoot`。
- 原版 designator 输入格与最终 root 适配唯一入口：`GravshipInterop`。
- 速度换基、刚体点速度和相对速度统一走 `SpaceTransform`。

禁止调用方自行拼接旋转、平移、中心偏移、designator 偏移或速度基变换。不得恢复 host-map 锚、floating origin、图级坐标锚或战斗专用坐标系。

## 6. 聚焦地图与参考系

当前聚焦 Map 永远以 identity transform 显示。切换聚焦只改变显示参考系，不改变任何 universe 绝对状态。

非聚焦对象通过外壳代理显示；不得旋转真实 Map、批量旋转 Thing，或要求原版寻路/房间系统理解任意角坐标。

聚焦切换和 Pawn 移动是独立事件。相机连续性交接统一使用 `UniverseManager.SetFocusedWithCamera`。

## 7. 飞船 Map

飞船、shuttle 和 POI 内部继续使用原版 ThingGrid、Pawn、Room、Region、PathFinder、电网、建造与存档系统。

结构外区域使用原版 Space 真空，可铺设 Substructure；它不是宇宙模拟空间。不得用 Map 边长、地图边界或 Map 内 Projectile 远距离移动代替 universe 几何。

## 8. 飞行、控制与物理

- 所有宇宙运动由游戏 tick 驱动；暂停即冻结，禁止用 `Time.deltaTime` 积分。
- `ShipPhysics` 是质量、质心、惯量、推进器分配、被动阻力和刚体积分的唯一实现。
- 玩家、自动驾驶和 NPC 控制都经 `RequestThrust → ForceToCommand → Allocate → Integrate`。
- `ShipController` 必须保持玩家与 NPC 两个子类；`PilotTarget` 统一表示普通船或 shuttle。
- 飞船 = 主驾驶台认领的归属控制域；SpaceObject 只是承载 Map 和运动的容器。
- 已停靠船首次实际推进器出力时经统一分离门禁；被动阻力和纯漂移不触发分离。
- shuttle 虚拟推进器由 XML `position + force` 整表定义，数量、位置、方向和推力不得写死。

## 9. 非聚焦对象与外壳代理

外壳代理是可丢弃缓存，真实结构来自 Map。它支持连续位置、任意角旋转、结构 dirty/rebuild 和命令缓冲分层。

重叠外壳按 objectId 整壳排序；背景壳在聚焦图下，进场/选点壳在聚焦图上。不得退回依赖海拔或透明队列的逐建筑排序。推进器火焰命令的 `MaterialPropertyBlock` 每命令独立。

## 10. 宿主口袋图、降落图与战略移动

`UniverseHostParent_DeltaV` 只承担原版 pocket-map `sourceMap` 契约：无 tile、不可聚焦、不进 WorldObjects、无游戏事实。

战术对接目标是已物化 SpaceObject 的内部 Map；深空无目标时拒绝固定。战略 gravship 发射使用 worldtile 星区地址。worldtile 只是 coarse address，不参与战术距离、碰撞或落点几何。

## 11. SpaceObject 与 POI 生命周期

`SpaceObject` 是一张 Map 的刚体容器，一图只允许一个承载对象。飞船不是 SpaceObject；同一 Map 可有多艘由归属域区分的船。

POI marker 无图；物化后成为普通 SpaceObject。地图加载、休眠、归档与销毁统一由 `SpaceLifecycleService` 管理。对象存在性不得以 `Find.Maps.Contains` 代替。

对象 ID 与结构归属 ID 共用 `UniverseManager.AllocateOwnerId` 防撞号，但语义分离，禁止通过 ownerId 反查 SpaceObject。

## 12. 对接、shuttle 与进入他船

进入他船只有两条路径：普通对接合图，或 shuttle 侵入撞入。不开跨图 live 连接，不自研自由 Pawn 跨图行走。

普通对接流程：目标选择 → 原版 Gravship 选点/合法性 → 注册唯一 `DockingPlan` → 随动自动驾驶或手操 → 位姿窗口保持 → 新鲜脚印复核 → `CaptureDomain → StripDomain → PlaceIntoMap` → 帧边界清理源口袋图。

落点 root、旋转、宿主局部锚和相对角只在确认时计算并写入计划；幽灵、自动驾驶、完成判据和最终放置只读计划值。

shuttle 在降落语义上是“驾驶台与基架合一的单建筑飞船”：预览、落点位姿、捕获、剥离和放置必须走普通飞船同一套 Gravship 链。它只在以下方面特殊：不搬地形、不盖结构归属、落地后启动原版 shuttle 冷却、可切换侵入模式、起飞后立即进入驾驶状态（玩家方且聚焦随起飞交接时——常规飞船本就在驾驶状态中出力起飞，无需此机制）。禁止使用 `DeSpawn + GenSpawn` 另写 shuttle 落位。

自动驾驶只负责任务与控制律，不决定对接完成；任意驾驶键可打断自动驾驶但不取消计划。计划不存档，读档后主体按保存速度漂移。

## 13. 太空战与 Projectile

`SpaceProjectile` 是 universe 弹道权威状态。发射、制导、碰撞都在统一宇宙坐标中进行；命中后按 Universe → 目标局部 → Map 把伤害、爆炸和火焰交回原版。

导弹、侵入 shuttle、点防御和护盾复用 `SpaceCollision` 与联合效果链，不建立平行命中系统。Renderer 只显示，不模拟。

## 14. 碰撞与飞船几何

渲染外壳、宇宙碰撞体和 Map 建筑格是不同概念，可由同一结构数据派生但不能互为事实源。命中点只通过目标 `SpacePose` 逆变换到 Map，禁止从屏幕或 Mesh 顶点反推。

## 15. 缓存、存档与可重建状态

能由持久事实重建的内容不序列化，包括 Mesh、剪影、相对显示变换、空间索引、推进器表和临时流程状态。缓存必须有明确的失效、换档重建和 Map/Object 删除释放入口。

持久事实包括 SpacePose、对象身份、结构归属和无法从 Map 重建的长期状态。不得只保存缓存的 `built` 标志。

## 16. 与 RimExodus 的边界

RimExodus 负责地表 worldtile 邻接；Delta-V 负责船体局部 ↔ 连续球面 universe ↔ 另一船体局部。禁止用 `NeighborLink.offset`、六边形邻接或地表接缝带表达太空关系。

## 17. Harmony、兼容层与验证

- 优先 Prefix/Postfix；transpiler 必须基于真实 IL。
- 按参数名绑定时使用真实名称；反射兼容层允许目标 Mod 缺失和签名漂移。
- 第三方 Mod 未安装时零行为、零错误；兼容失败不得杀死 Delta-V 核心。
- 验证顺序：编译 → Harmony 绑定 → 游戏启动 → 最小游戏内回归 → 用户实测。
- 坐标改动至少验证聚焦 identity、相对位置/角度、切换前后绝对状态、重复切换和存读档。

## 18. 核心架构不变量

1. Map 是稳定局部坐标系，宇宙姿态在 Map 外。
2. 当前聚焦 Map 永远 identity；切换聚焦不改绝对状态。
3. 球面几何和跨层转换只走 `OrbitSphere`/`SpaceTransform`。
4. 每个可进入 Map 属于一个 SpaceObject；SpaceObject 每 tick 只积分一次。
5. 飞船由主驾驶台和同归属四连通控制域定义，不保存第二套运动状态。
6. 非聚焦对象使用缓存代理，缓存不是事实源。
7. 对接、shuttle 和原版放置只共用一套 Gravship 转移链。
8. Projectile 在 universe 层独立存在，命中后才进入目标 Map。
9. 模拟只认游戏 tick；帧路径只做输入、渲染和安全延迟动作。
10. 同一功能只能有一个实现和一个计算入口。
11. 未经用户实测和明确要求，不提交代码。

## 19. 当前项目状态

当前已实现但仍需游戏内系统验证：飞船/SpaceObject 解耦、一图多船和归属域、驾驶与自动驾驶、对接/分离、球面星区、POI 生命周期、太空战、shuttle 起飞/驾驶/普通对接/侵入、月池语义与渲染。

项目未发布，测试以新档为准。具体待验证项和未完成内容只在 `doc/TODO.md` 与 README 已知限制维护。

## 20. 当前代码架构地图（单实现入口）

- `UniverseManager`：objects、ID 分配、Map→SpaceObject、聚焦、tick/update 总装和帧动作队列。
- `OrbitSphere`：球面移动、弧距、切空间基和 sphere↔tile。
- `SpaceTransform`：全部坐标、速度、刚体点和停放锚转换。
- `ShipRegistry`：船、控制域、状态、船体锚、朝向和派系查询。
- `SubstructureOwnership`：基架归属、连通认领和结构版本；基架与归属同步移除（`RemoveFoundation`/`RemoveGravshipTerrainUnsafe` postfix → `NotifyFoundationRemoved`），「归属仅在有基架的格上有意义」恒成立。
- `ShipPhysics`：质量/惯量、推进器、分配器、阻力和积分。
- `PilotTarget` / `PilotService` / `ShipController`：普通船与 shuttle 的统一驾驶层。
- `ShipAutopilot`：固定/随动/追踪任务；制导经 `IAutopilotGuidance`。
- `ShipFlightTransitionService`：分离、跨图转移、对接发起和源图善后。
- `DockingCompletion`：对接完成收尾的模板方法基类；飞船/shuttle 子类只注入钩子增量（冷却、运行时键、标签），公共源图处置/聚焦交接/通知不得分叉。
- `LandingApproachController`：唯一对接计划、落点注册、复核和完成触发。
- `GravshipInterop`：原版 Gravship 私有逻辑镜像、designator 适配、预览和 `CaptureDomain/StripDomain/PlaceIntoMap`。
- `SpaceShuttleService`：shuttle 起飞、虚拟推进器注入、侵入状态和口袋清理；普通对接不在此另实现。
- `SpaceProjectileService` / `SpaceWeaponService` / `SpaceCollision` / `SpaceImpactEffects`：太空战链。
- `ShellRenderer` / `MapOverlayRenderer` / `DockingGhostRenderer` / `ShipSilhouette`：外壳、覆盖物、幽灵和剪影。
- `ThrusterEffects` / `ThrusterAudio`：只读真实分配油门的表现。
- `MoonPoolUtility` / `Patch_MoonPool` / `MoonPoolGraphics` / `SectionLayer_MoonPool`：月池判定、无顶密封和连接视觉。
- `SpaceLifecycleService`：生成、休眠、物化、归档和销图。
- `QueueFrameAction`：离开当前 tick/加载调用栈的唯一帧边界延迟机制。

通用地图工具包（上述坐标/渲染/搬运/生命周期/生成条目同属一个将来通用的地图工具包；后续地图能力一律扩展这些入口，不建平行实现）：

- `MapContentSpawner`：Thing 记录回放（def/stuff/quality/hp/stack/faction → `GenSpawn`）与基架盖章包裹器的唯一入口；`PoiSnapshot`/`ShipStructureStamp`/`PoiTemplate`/`HomePrefab` 的落物一律经此。
- `HomePrefab`：居住区预制序列化（Home area 捕获 → JSON 文件 → 回放）；排除推进器、shuttle 与船体外壳环，派系落图时指定。
- `ShipStructureStamp` / `PoiTemplate` / `ShipPocketMapFactory` / `ShipTestScene`：测试布局 stamp 与口袋图生成；测试船布局数据源优先 `HomePrefab` 预制，缺失回退写死表。

流程状态属于 `UniverseManager` 的实例服务，不建立可变静态游戏状态。静态只允许无状态纯函数、输入快速门、渲染/反射缓存和同调用栈重入卫兵。

## 21. 单实现、单计算纪律

- 动代码前先查 §20；已有入口必须直接复用，不得写等价镜像。
- 确认时产生的落点、位姿和锚只计算一次，显示、判据和写回读取同一状态对象。
- 显示锚必须等于逻辑锚。
- 复核必须重走主路径，不写近似检查。
- 调用原版 API 前核对原版调用者的输入语义；designator 输入格不等于最终 root。
- 捕获与剥离使用同一份绝对 cells；不得从旋转后的 `Gravship.Foundations` 反推源格。
- 跨图搬运后旧 `Ship` 快照失效，需要当前状态时按 ownerId 重新查询。
- Map 身份日志统一使用 `UniverseManager.MapTag`。
- 需要帧边界延迟时使用 `UniverseManager.QueueFrameAction`；`ExecuteWhenFinished` 不是通用异步队列。
- 原版 `MoveDesignator()` 缓存绑定旧 Map；战术对接每次显式创建 `Designator_MoveGravship(targetMap, marker)`。
