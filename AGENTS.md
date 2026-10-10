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
- 实现用户指定方案时，若遇到素材契约不明、原版机制证据不足或实测结果与方案不符，优先向用户说明具体问题并提问；不得自行猜测或换用另一方案。
- 热路径日志必须先检查 `DeltaVLog.Enabled`，只在状态边沿或用户事件输出，禁止每帧/tick 无条件刷屏。
- 当前阶段优先清晰的现有契约，不为未来兼容制造额外抽象。
- Delta-V 贴图生成、分层与对齐施工时加载仓库 `.agents/skills/delta-v-art/SKILL.md`；资源清单与施工进度只在 `doc/美术资源施工表.md` 维护。

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
- 一张 Map 只由一个 SpaceObject 承载，但可含多艘船。对接把船放进目标图，实际出力时从同图提取控制域并分离；被动阻力和纯漂移不触发分离。`ShipRegistry.StateOf` 的 `Docked/FreeFlight` 只由当前同图船数及天然地面等事实即时推导，不是存档状态。独立口袋图中的单艘物化敌舰通常为 `FreeFlight`，不得预设为 `Docked`。
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

当前已实现但仍需游戏内系统验证：飞船/SpaceObject 解耦、一图多船和归属域、驾驶与自动驾驶、对接/分离、球面星区、POI 生命周期、远距整图归档、shuttle 起飞/驾驶/普通对接/侵入、月池语义与渲染。太空战和敌舰 AI 的核心流程已由用户游戏内验证，未发现严重阻塞问题；具体边界项仍按 `doc/TODO.md` 逐项验收。

通用地图 prefab（导出/重建，2026-09-30）已实测通过；能力边界与已知限制见 README 与 `doc/TODO.md`。

项目未发布，测试以新档为准。具体待验证项和未完成内容只在 `doc/TODO.md` 与 README 已知限制维护。

## 20. 当前代码架构地图（单实现入口）

- `UniverseManager`：objects、ID 分配、Map→SpaceObject、聚焦、tick/update 总装和帧动作队列。
- `OrbitSphere`：球面移动、弧距、切空间基和 sphere↔tile。
- `SpaceTransform`：全部坐标、速度、刚体点和停放锚转换。
- `ShipRegistry`：船、控制域、状态、船体锚、朝向和派系查询。
- `SubstructureOwnership`：基架归属、连通认领和结构版本；质量报告、控制域与碰撞面共用其结构版本失效判定及设置页重扫间隔。基架与归属同步移除（`RemoveFoundation`/`RemoveGravshipTerrainUnsafe` postfix → `NotifyFoundationRemoved`），「归属仅在有基架的格上有意义」恒成立。
- `ShipPhysics`：质量/惯量、推进器、分配器、阻力和积分。
- `PilotTarget` / `PilotService` / `ShipControlService` / `ShipController`：普通船与 shuttle 的统一驾驶层；每个船归属由实例注册表持有一个玩家或 NPC controller，基类统一调度齐射、转向、点防与炮手事件。主驾驶台保存玩家火控模式（默认自动），`SpaceAimController` 只在手动集火且为聚焦图所选船时提供输入和预瞄。
- `ShipAutoFireControl` / `ShipAutoManeuverControl`：玩家与 NPC controller 共用的自动选敌、齐射意图和战斗站位类；玩家自动火控只筛敌对 Ship tag，自动机动开关为运行时状态且不触发 NPC 撤退或跳帮。
- `ShipAutopilot`：固定/随动/追踪任务；制导经 `IAutopilotGuidance`。
- `ShipFlightTransitionService`：分离、跨图转移、对接发起和源图善后。
- `DockingCompletion`：对接完成收尾的模板方法基类；shuttle 子类只注入落地冷却与标签，运行时键由容器注销统一清理；公共源图处置/聚焦交接/通知不得分叉。
- `LandingApproachController`：唯一对接计划、落点注册、复核和完成触发。
- `GravshipInterop`：原版 Gravship 私有逻辑镜像、designator 适配、预览和 `CaptureDomain/StripDomain/PlaceIntoMap`。
- `SpaceShuttleService`：shuttle 起飞、注册／读档时虚拟推进器注入、侵入状态和事件驱动的口袋清理；驾驶、物理、自动驾驶与普通飞船共用现有入口，普通对接不在此另实现。
- `SpaceWeaponService` / `SpaceWeaponShot`：跨容器开火总装与唯一 Prepare→Launch→Commit 事务；逐发弹药、装弹机基础脉冲、沿物理链逐段预留的加速脉冲、电容放电与末端炮口在此一次性冻结，成功发射后统一扣费；缺电加速段跳过且不增加弹速，后段仍继续检查。附件电容独立储能、限速充电，仅充电需求变化时更新 `CompPowerTrader.PowerOutput`。锁定弧与发射轴分离，全部武器统一以实例 `fireDeviationDeg` 检查目标与实际炮口/固定轴夹角。Traverse 无独立逐 tick 扫描，只按火控或原版图内目标命令转向；太空炮手由 Job 进入／结束登记并供原版 Mannable 查询。Crew/Traverse/Magazine/Attachment/加速件以正交 Comp/能力接口参与，第三方不得建立家族特判。
- `SpaceWeaponAttachmentSlots` / `SpaceRailgunConstruction`：武器 Def 声明的插件槽位、允许挂件与串联加速件的选中建造按钮及槽位提示唯一入口；加速段到装弹机的反向关联从 Def 注册反查。轨道炮由装弹机的状态 Comp 管理已解析加速段并统一规划能量、分发颜色；加速段颜色 Comp 不独立轮询。两者经原版 `ThingWithComps.DrawColor → ThingComp.ForceColor` 给 `CutoutComplex`／`_m` 红色蒙版提供主色；绿色通道才对应 `DrawColorTwo`。状态变化经 `Notify_ColorChanged` 刷新 MapMesh，不要求建筑专用 `thingClass`；不切换整张贴图，也不使用 `CompGlower`。状态与预瞄都复用 `SpaceWeaponShot` 的能量规划，安全路径复用同一判定。
- `Building_SpaceTurretGun` / `DefModExtension_SpaceTurretArt`：替换指定炮塔的旋转顶部材质，位置、角度和绘制继续走原版 `Building_TurretGun`/`TurretTop`；Def 固定为仅点防的 1×1 激光炮不触发原版同图索敌，以免发射原版机炮弹。
- `CompMissileAutoloader` / `SpaceMissileMagazineGeometry` / `CompLoadedProjectileGraphic`：唯一 1×3 无人导弹架使用原版 `CompChangeableProjectile` 的一发待发状态和弹种筛选；前一格嵌墙、后两格在船内。1×2 气密导弹仓只可与船内两格同向并排，左右槽位由发射架 Def 声明；原版设施 Comp 登记链接，建造与运行时复用同一几何判定，同归属同派系才自动送入实体导弹。开火、库存、筛选、供电和归属事件合并到下一游戏 tick 按发射架 ID 顺序补弹，双架共享仓不重复取弹；仅库存被原版预留而受阻时保留条件性 60 tick 重试。发射架保留三层整图，以 `Patch_MissileLauncherWallLink` 使原版墙在 `LinkGrid` 中与其相连。导弹物品通过 `DefModExtension_SpaceMissileAmmo` 指向宇宙弹体，三层绘制中层读取同一 `SelectMagazine()` 结果；不改共享 `GraphicData`。
- `SpaceProjectileService` / `SpaceCollision` / `SpaceImpactEffects`：含冷发射程序的宇宙弹体、统一碰撞与联合命中效果链；弹道工作对象与弹体小图分别池化，SpaceObject 身份每发新建；炮口闪光、点火尾焰/烟迹和命中视觉只由弹体服务登记并在前台层绘制，不另建弹道。
- `ShellRenderer` / `MapOverlayRenderer` / `ShellRenderRuleDef` / `DockingGhostRenderer` / `ShipSilhouette`：整壳分层、地图内容收集、Thing 外壳绘制配置、幽灵和剪影。入壳 Thing 的静态层走原版 `Thing.Print`，实时层经限定作用域捕获原版 `Graphics.DrawMesh`；替代图为有序整组替换。门在外壳上仅同步全开／全关终态。炮塔旋转炮身由 `IShellTurretTopSource` 提供当前材质和角度，规则决定仅绘炮身并置于船壳上层；轨道装弹机用规则排除，加速段内构与外壳入壳，不修改碰撞分类。
- `ThrusterEffects` / `ThrusterAudio`：只读真实分配油门的表现。
- `MoonPoolUtility` / `Patch_MoonPool` / `MoonPoolGraphics` / `SectionLayer_MoonPool`：月池判定、无顶密封和连接视觉；每图月池格缓存首次查询建立，此后随地形变化逐格维护，不按 tick 重扫。
- `SpaceLifecycleService`：生成、休眠、物化、归档和销图。无快照 marker 首次物化的内容链 = 类型 prefab 池（seed 确定性选取，入队测尺寸与盖章同一解析），**容器先注册、结构后铺设**（驾驶台 `ClaimOnSpawn` 认领要求 FrameOf 已解析；归属名 = marker 生成时决定的完整名，经 envLabel/归档 carriedLabel 传承）。池空在生成期拦截；池失效在入队/物化期报错并取消该 POI（prefab-only，不回退程序生成）。
- `NpcShipAiService` / `NpcShipController`：NPC 战术资格、战损撤退、退场及跳帮仍在 NPC 层；选敌和站位调用上述共用类。已选目标的射击、点防与自动驾驶仍逐 tick，开火走共同 controller 和 `SpaceWeaponShot`。炮手补位由岗位结束、武器状态和 Pawn 可用性事件触发，NPC 不再每 60 tick 重派。AI 资格门禁与 dev 诊断共用同一判定，Combat 日志只输出状态变化。`ArchivedMapStore` 是远距普通口袋容器的原版 `Map.ExposeData` 整图序列化与运行时卸载/恢复入口，归档内容随 `SpaceObject` 落档；远距归档与恢复按设置周期全量检查，撤退航程按实际经过的游戏 tick 推进。该路径尚待游戏内存读档和引用完整性验收。
- `SectorContentService` / `SectorContentTypes`：星区首次生成的幂等记账与按类型生成循环；marker 身份在生成时一次性决定——随机派系（可见非玩家派系，不做类型→派系映射）+ 完整名（`DeltaV_NamerSpacePoi` RulePack 词干 + 类型名 + (派系名) + #ID）烘焙进 envLabel/faction，物化时直接应用。`SectorContentTypes` 是任意深度的分类树（星区架构 §11 分类），分支只分组、生成与设置只遍历叶子；规则读 `DeltaVSettings.sectorRules`（`SectorTypeRule`：保底数/分段额外量/prefab 池；缺条目用注册表默认，读路径不静默改写设置）。
- `QueueFrameAction`：离开当前 tick/加载调用栈的唯一帧边界延迟机制。

通用地图工具包（上述坐标/渲染/搬运/生命周期/生成条目同属一个将来通用的地图工具包；后续地图能力一律扩展这些入口，不建平行实现）：

- `MapContentSpawner`：Thing 记录回放（def/stuff/quality/hp/stack/faction → `GenSpawn`）、Scribe 恢复实例落位、基架盖章包裹器与设施重链（`RelinkGravshipComponents`）的唯一入口；`PoiSnapshot`/`ShipProceduralLayout`/`MapPrefab` 的落物一律经此。
- `MapPrefab` / `MapPrefabDocument` / `MapPrefabThingState`：通用地图 prefab 的唯一门面、文档与逐实例状态入口。地形及旋转锚继续复用原版 `PrefabUtility`，建筑/物品通过原版 `Thing.ExposeData` 导出并整批 Scribe 读取；基架/屋顶另存侧车，锚点掩码只纳入 Building+Item。重建顺序为 foundation（含指定归属）→ terrain → 整批解析内部引用、重分配 Thing ID、指定派系与旋转落位 → roofs → 重链 → marker 展开；外部旧地图引用不随 prefab 复制。驾驶台源身份在落位前清理，对接口通道格按新锚换算。文件落 mod 根 `Prefabs/`，新文件格式要求重新导出旧 prefab；地形 Def 读入走原版 XML 交叉引用管线。prefab 池仍由 `LoadRelative`、`ListPrefabFiles`、`ResolvePoolPath` 维护。具体契约见 `doc/Prefab状态重建.md`。
- `CompPrefabMarker` / `CompPrefabPawnSpawner`：prefab 重建标记框架——只有 `MapPrefab.Spawn` 在整片放置完成后调用 `Expand(PrefabSpawnContext)`（context 含 Map/Faction/OwnerId/Root/Rotation/Universe，为后续 SpaceObject 展开器预留）；普通读档与 `PoiSnapshot.ApplyTo` 恢复不调用展开。新展开器 = 新子类 + CompProperties，`TryGetComp` 基类匹配零修改纳入。当前仅实现 Pawn 标记：每个标记生成一名 Pawn，Def 指定 PawnKind 优先，其次目标派系基础成员、原版 `Faction.RandomPawnKind`，最后由 Def 指定后备种类；原格优先、附近可站立格兜底，成功后销毁标记。人形船员的真空服与头盔统一由 `SpacePawnEquipment` 在入图前穿戴，程序测试船员同用此入口。SpaceObject 标记延期。
- `ShipProceduralLayout` / `ShipPocketMapFactory` / `ShipTestScene`：口袋图生成与程序生成布局。程序生成仅存于 `ShipTestScene`（MapPrefab 导入失败时的唯一回退特例）；星区 POI 物化不走程序生成。

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
