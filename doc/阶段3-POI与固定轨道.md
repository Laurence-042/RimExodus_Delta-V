# 阶段3：POI 生命周期 + 球面宇宙坐标与宇宙视野 + 驾驶台 + 固定轨道模式

对应 [story.md](story.md)「当前阶段计划 §3」+ 用户决策（2026-09-13）：

- **范围 = 四块全做**：最简太空 POI 与地图生命周期、更远宇宙视野（放宽缩放）、驾驶台（正式进入驾驶方式）、固定轨道/建造模式（逆重降落整船搬运）。
- **POI 生命周期 = 活跃/休眠/删除三段**（universe 距离而非 tile 距离，用户定夺）：近了正常 tick、远了冻结但保留、更远删除收快照。
- **宇宙视野 = 放宽缩放**（用户定夺）：远处对象的外壳代理由 ShellRenderer 画在世界空间（`Graphics.DrawMesh` + `disableMapClippers`），本就不受地图矩形限制，唯一约束是相机缩放上限。
- **球面宇宙坐标（2026-09-14 重构，用户定夺）**：universe 位置 = 轨道层球面上的单位方向向量；sphere↔tile 互推为纯函数，锚机制（hostMap 锚、floating origin、`anchorDisplacement`、重锚）整体退役。同批落地：专用无 tile 宿主口袋图、全部 Delta-V 口袋图去 tile 化、起降流程与宿主解耦。

**状态：阶段3收口已实现，待游戏内验证（2026-09-16：通用分帧生成、轨道 POI 自动治理、完整休眠与地图代理）。**

## 实现概览

| 文件 | 职责 |
|---|---|
| `Source/Core/SpaceObject.cs` | 宇宙对象基类：id/label/pose/map（AGENTS §11 Universe State） |
| `Source/Core/ShipState.cs` | 飞船状态：+ isPlayerShip、primaryConsole、docked 停靠状态与恢复数据 |
| `Source/Core/ShipOrientation.cs` | Map 姿态、主驾驶台局部朝向与玩家所见航向的集中换算 |
| `Source/Core/PoiState.cs` | POI 状态：NativeParent / SnapshotPocket 两类，前者保存 MapParent 引用，后者保存 seed + 快照 |
| `Source/Lifecycle/SpaceLifecycle.cs` | Delta-V 距离调度：加载/唤醒、休眠、延迟原版删除 |
| `Source/Lifecycle/SpaceMapGovernance.cs` | 轨道 MapParent 开放式纳管判定 + 自动注册/主动销毁同步 |
| `Source/Lifecycle/MapDormancyService.cs` | 通用完整休眠：Map 三入口 + TickManager Thing 表的停止/恢复 |
| `Source/Lifecycle/PoiSnapshot.cs` | 结构级快照（foundation/表层地形、屋顶、建筑、物品）平行列表序列化 |
| `Source/Generation/PoiTemplate.cs` | 残骸模板（seed 决定，同 seed 重生成一致）——**2026-09-30 移除**：物化改 prefab-only（池空/失效报错取消），程序布局仅存于双船测试场景 |
| `Source/Generation/IncrementalMapGenerationService.cs` | 通用单飞生成队列、GenStep adapter、原版准备/收尾与失败清理 |
| `Source/Generation/PocketMapHostService.cs` | 可抽取的公共无 tile 口袋图宿主接口与实现 |
| `Source/Rendering/MapOverlayRenderer.cs` | 通用 17×17 section 地图代理缓存；地形/静态 Thing 绘制与命中 |
| `Source/Harmony/Patch_PoiDormancy.cs` | 休眠、被动删除、生成冲突、存档阻止、代理 dirty 与销毁清理桥接 |
| `Source/World/ShipPocketParent_DeltaV.cs` | 船/POI 口袋图 WorldObject：覆写 `WorldCameraPosition`（连续球面视角） |
| `Source/World/SpaceHostParent_DeltaV.cs` | 降落图 WorldObject：背景视角 = 停靠在本图的船的连续球面位置 |
| `Source/World/UniverseHostParent_DeltaV.cs` | 专用宿主口袋图 WorldObject：无 tile、仅作全部口袋图 sourceMap 锚 |
| `Source/Core/OrbitSphere.cs` | 球面几何唯一入口：切空间基/球面移动/弧距/sphere↔tile 纯函数 |
| `Source/Core/UniverseManager.cs` | objects 权威列表、专用宿主惰性入口、真实空间图 tile 帧、缩放放宽、SpaceLifecycle 挂载、平面旧档迁移 |
| `Source/Buildings/CompDeltaVPilotConsole.cs` | 驾驶台 Comp：进/出驾驶 gizmo + 主驾驶台规则 |
| `Source/Interaction/MobileShipBootstrap.cs` | 普通地图新建 Gravship 接入移动模式；Orbit 启动、地面重力限制（任意真实轨道图可起飞，无宿主前置） |
| `Source/Interaction/ShipModeService.Docking.cs` | 固定轨道停靠/起飞（逆重搬运管线 + 角度吸附 + 落点校验/回滚） |
| `Source/Generation/ShipPocketMapFactory.cs` | 飞船口袋图单一生成入口；起飞时按当前 Gravship 实际边界动态定尺寸并居中 |
| `Source/Harmony/Patch_GravshipLanding.cs` | 原版确认降落事件 → Delta-V docked 状态的最小桥接 |
| `Source/Harmony/Patch_WorldCameraRotation.cs` | 给背景 WorldCamera 补聚焦参考系 roll；不旋转 Map 相机/内容 |
| `Source/Rendering/ShellRenderRuleDef.cs` | 外壳 Thing 纳入／排除及 `render` 配置（贴图来源、表面层级、替代图和 zIndex） |
| `Source/Rendering/WorldShipMarkerRenderer.cs` | 星球轨道层的玩家飞船剪影标记；由 ValidSubstructure 生成可重建纹理缓存 |
| `Source/Rendering/DeltaVLandingAnimator.cs` | 保持镜头缩放的起始姿态 → 目标姿态降落插值 |
| `Source/Harmony/Patch_DeltaVLandingAnimation.cs` | 仅在 Delta-V 降落期间替换原版镜头/飞入表现并补原版收尾前置状态 |
| `1.6/Patches/DeltaV_PilotConsole.xml` | 向 Odyssey 原版 PilotConsole 追加 Delta-V Comp，保留原版 Gravship 身份与设施兼容 |

## 核心设计定稿

### 球面宇宙坐标（2026-09-14 取代“动态锚”）

- `SpacePose.position` = 轨道层球面上的**单位方向向量**（Vector3）；`velocity` 为该处切空间速度（x=东、y=北），`angle` 为船局部系到切空间基的旋转。切空间基/球面移动/弧距/sphere↔tile 全部收口 `OrbitSphere`（尺度常量 `CellsPerTile=100` 配合 `AverageTileSize` 与球面半径换算格↔弧度）。
- **没有锚、没有 floating origin**：sphere↔tile 是纯函数（`TileFromDirection` 线性扫描取最大点积，仅降落时调用；`TileCenterDirection` 反向），任何状态都不需要对齐基准。长途航行沿球面推进，绕星球一周回到原点，坐标无漂移也无界问题。
- **全部 Delta-V 口袋图（船/POI）无 tile**：`Tile` 恒 `PlanetTile.Invalid`，只存在于 `Find.World.pocketMaps`（绝不进 `Find.WorldObjects`——无 tile WorldObject 的 SpawnSetup 会在 FastTileFinder 越界崩溃）。`sourceMap` 统一指向专用宿主口袋图。聚焦口袋图时 `Map.Tile.Layer` 回落地表层，但 `BackgroundWorldCamera*` 视差参数地表/轨道层共用 `PlanetLayerDef` 类默认值（两层 XML 均未覆盖），无视觉差异。
- **口袋图不是玩家主基地**（Patch_MapPlayerHome.cs，2026-09-14）：原版生态普遍假设主基地图持有有效 tile（DateNotifier 两个助手的 tile 守卫一个缺失一个因 `&&` 短路顺序失效、Ideo 仪式历法读 `AnyPlayerHomeMap.Tile`、`TileTemperaturesComp` 按 tileId 索引按层缓存数组；`AnyPlayerHomeMap` 仅原版就有 94 处调用）。飞船口袋图因 `GravshipUtility.PlayerHasGravEngine` 回退会被判为 home 而触发上述全部路径的越界。覆写 `Map.IsPlayerHome` 对 Delta-V 口袋图返回 false——飞行中与原版 gravship 航行同形态（无 home，原版自带空值分支）；降落后逆重引擎随船体落到真实降落图，该图经同一条引擎回退自然成为 home（与原版 gravship 降落一致）。事件侧闭环：口袋图 def 无 `IncidentTargetTags`，`TargetAllowed` 标签交集恒为空，storyteller 不会选它为事件目标。
- **旧档迁移**：平面存档以旧 `hostMap` tile 为切平面原点把绝对偏移换算成球面方向（角度/速度基约定不变直接沿用）；旧档口袋图残留的真实 tile 用反射直写私有字段清空（走 `WorldObject.Tile` setter 置 Invalid 有 Surface 层 FastTileFinder 越界风险）。`hostMap`/`anchorDisplacement` 的 `Look` 只在非写档阶段调用（LoadingVars 注册 + ResolvingCrossRefs 消费两阶段都是 Verse 契约要求；2026-09-16 修复了只门 LoadingVars 导致旧档 loadID 无人消费的 "Not all loadIDs consumed" 警告）。
- **背景位置注入点 = `MapParent.WorldCameraPosition`**（船图：该图宇宙对象的球面点；降落图：停靠在本图的船的球面点——两图切换聚焦背景无跳变）；**背景角度注入点 = `WorldCameraDriver.ApplyMapPositionToGameObject` Postfix**（对象图随聚焦姿态；真实空间图随停靠主船姿态）。后者只给父级 WorldCamera 补局部 roll（子级 WorldSkyboxCamera 自动继承），因此星球与星空相对聚焦船反向旋转，而 Map Camera/Thing/鼠标/寻路仍为 identity。
- **缩放放宽**：聚焦 Delta-V 图（对象图/真实空间图）时幂等设置 `CameraDriver.config.sizeRange.max = universeZoomMax`（默认 400），离开恢复记录的原值。相机高度公式与 farClip(65.5) 按同一 max 归一化，正交投影下放宽安全，无需 patch。
- **星球视图玩家船标记**：仅在世界视图选中轨道层时绘制；位置直接取 `SpacePose.position` 的连续球面点，姿态经 `SpaceTransform.UniverseWorldMarkerRotation` 对齐当地东/北切空间基。剪影在每次成功进入移动模式时从对应逆重引擎 `ValidSubstructure` 生成一次透明纹理，此后不再扫描结构，战损/建造变化不会改变标记；下次进入移动模式才以当时船体刷新。读档后因运行时缓存不存档，第一次显示时惰性补建一次。移动船叠加 `SpacePose.angle`；固定模式沿用最近一次移动模式剪影及姿态。纹理/材质均为可重建运行时缓存，不创建或占用轨道 tile 的 `WorldObject`。

### POI 三段生命周期

- 距离参考 = 全部玩家阵营船的最小球面弧距（`OrbitSphere.ArcCells`，格）；阈值 `poiActiveRadius`（默认 300）/ `poiDeleteRadius`（默认 1200）。生成、休眠、删除三段共用这一个距离判定，与聚焦/相机状态无关。
- **注册**：扫描全部 `Tile.LayerDef.isSpace` 的 `MapParent`，类型判定保持开放以覆盖第三方子类；排除 Delta-V 内部 parent、已由 ShipState 表示的图和纯空白 Space 降落画布。NativeParent 的绝对位置始终由轨道 tile 中心纯函数导出，`map == null` 不等于对象消失。占用排除必须先判 `parent.Map != null`（2026-09-16 修复：无图 POI 的 `parent.Map == null` 与飞行船 `dockedMap == null` 形成 null == null 恒真，曾把全部无图 POI 永久拒绝纳管并被 stale 清理静默移除——orbit 层只剩唯一有图 POI 的根因）。
- **Active**：无图时进入按距离排序的全局单飞队列。原生 POI 保留真实 tile parent，使用自己的 `MapGeneratorDef` / `ExtraGenStepDefs`；SnapshotPocket 才使用公共无 tile 宿主并在完成后盖模板/快照。默认每帧 8ms（设置 1–16ms），Space/DeltaV 填充有 adapter，其他步骤原子执行并记录超预算诊断。入队参数被拒与 `mapBeingGenerated` 被外部持续占用（>5s）都会输出必要日志，激活链无静默断点（2026-09-16）。
- **Dormant**：图保留，除三个 Map 更新入口早退外，还从 `TickManager` 全局 Thing tick 表摘除；唤醒遍历 spawned Things 恢复一次。当前图、生成中图、玩家 Pawn 所在图、玩家家园与停靠玩家船所在图强制保活。NPC Pawn 不阻止休眠。
- **删除**：`MapParent.CheckRemoveMapNow` 只拦截受治理 POI 的“人走即删”；超过删除距离后调度器直接调用其虚方法 `ShouldRemoveMapNow`，原样采用 `alsoRemoveWorldObject` 与 `forceRemoveWorldObjectWhenMapRemoved`。暂不可删则保持休眠并重试；任务主动销毁不经此被动入口，立即清状态和缓存。SnapshotPocket 仍走结构快照删除。
- **生成隔离**：半成品图不 tick、不渲染、不作为可进入 Map，并持续从 Thing tick 表摘除；生成中手动保存对话框和底层 SaveGame 都会用 Keyed 消息拒绝。外部同步 GenerateMap 会先收完当前分帧任务，避免静态状态交错；安全可绑定的第三方 Postfix 在原版 finally 等价时点复放，Prefix/Transpiler 只警告不伪造状态。
- **快照保真边界**：foundation + 非 Void 表层地形、屋顶、建筑（def/stuff/pos/rot/hp/faction）、物品（含 stack）。不保 pawn、comp 内部状态（电力开关等）、燃气/雪/区带。序列化 = 平行列表（格列表与 def 列表一一对应），保存/加载共用同一组 `Scribe_Collections.Look`，无不对称空间。

### 驾驶台与主驾驶台规则

- 直接扩展原版 `PilotConsole`（3×2、rotatable、需 Substructure 甲板、Odyssey 建造分类）：XML patch 只追加 `CompDeltaVPilotConsole`，保留原版 `CompPilotConsole`/`CompProperties_GravshipFacility`。因此原版 `GravshipComponents`、`ThingDefOf.PilotConsole` 身份判断、发射方向回退和第三方兼容均走原生路径。原版 Gravship 抵达 Orbit 后仍保持固定模式；玩家必须选中具体驾驶台点击“进入移动模式”，`MobileShipBootstrap` 才以该台连接的引擎与 `ValidSubstructure` 精确捕获一艘船。**任意真实轨道图（含原版发射降落到任意 POI tile 生成的图）都可起飞**——初始球面位置由该图 tile 帧纯函数换算，宇宙中已有其他对象也不冲突（绝对坐标天然一致）。地面图保留灰化入口，悬停显示重力限制；后端仍重复校验以防非 UI 路径绕过。
- `Exploriters.siiftun1857.LaunchIntoEmptySpace` 是运行依赖：它只向 Orbit Layer 追加空白 tile 的 raycast/选择图层，不提供需要调用的 C# API；Delta-V 因此只声明依赖与加载顺序，不复制其选取逻辑。
- 主台规则（story §固定轨道）：第一台自动成为主（SpawnSetup 且现主台失效时）；仅显式 gizmo 可换主；主台被拆转交船上最老的一台。
- Toggle 快捷键保留（无驾驶台也可驾驶，dev/测试便利）。

### 模式状态与转换

- **固定模式**：真实船体在降落图（真实轨道 tile 图）上，一图可有多艘船（同 tile 降落即同图）；选船只能从具体驾驶台发起。允许原版定点移动和建造。
- **移动模式**：真实船体在独占口袋图，一图恒为一船；`SpacePose` 持续积分。HUD 无论是否驾驶都显示当前船方向、速度、转速、质量、辅助和控制状态，并提供驾驶切换与固定模式按钮。
- **驾驶模式**：移动模式中的输入开关；开启才采样 W/S/A/D/Q/E 和飞行辅助，关闭后不搬图、已有速度继续漂移。
- 转换只有 `固定 --(选中目标驾驶台)--> 移动`、`移动 --(HUD 选点降落)--> 固定`，以及移动模式内部的 `未驾驶 <--> 驾驶`。固定进入移动后默认未驾驶。

### 白名单外壳

- 不再重放 `SectionLayer_Terrain` 或整层 `SectionLayer_ThingsGeneral`。每帧从真实 Map 读取可见地形、墙门、气密建筑与自然岩石；其他 Thing 由 `ShellRenderRuleDef` 显式纳入，规则也可排除自动纳入的实心建筑。所有 SpaceObject 使用同一收集路径。
- 规则的 `render` 把 `source`（`Thing` 或 `TurretTop`）、`layer`（`Hull`／`Exposed`／`Effects`）、可选 `alternateGraphic` 和 `zIndex` 收在一起。替代图仍采用原版 `GraphicData` XML；`zIndex` 以 `Altitudes.AltInc` 为单位，仅调整所在表面层内的深度。层间清深度保色，顺序由命令缓冲决定。
- 当前规则显式纳入 `LargeThruster`，七种点防／小型／中型炮塔只绘旋转炮身于 `Exposed` 层；轨道炮装弹机／加速段明确排除。驾驶台、逆重引擎和其他室内设备默认不绘制。
- POI 与飞船共用同一变换、点击和绘制规则；`TerrainDef.dontRender` 的 Space 背景天然跳过，Pawn/Projectile/Mote/Gas/Filth 和动态特效不进入普通船壳内容。
- 代理按原版 17×17 section 缓存源内容；Terrain/Things/Buildings 的 `MapMeshDirty` 只令相关及必要相邻 section 失效。缓存不存档，并在地图删除、换档、Def 缓存刷新时释放。

### 固定轨道 / 建造模式（逆重搬运）

- 测试船配置复刻参考存档 map2：`GravEngine` 3×3 的 `Thing.Position` 锚点位于船体偏移 `(17,12)`，四角布置 4× `GravFieldExtender`（引擎 SubstructureSupport 500、延展器各 +250）→ 支持上限 1500 ≥ 960 格甲板，`ValidSubstructure`（foundation floodfill ∩ footprint 并集）覆盖全船。生成时引擎先以玩家阵营生成，延展器/驾驶台/推进器随后生成并在批量盖章末尾经原版 `CompAffectedByFacilities.Notify_ThingChanged` 重连；玩家船墙、门和设备全部归玩家占有。不得把 3×3 引擎锚点误作其他坐标语义。
- **停靠**：以 `SpacePose.angle + 主台局部朝向` 得到真实航向并吸附 90° → `GenerateGravship` → 只设置原版 `Gravship.Rotation` 的初始值 → 直接调用 `GravshipUtility.ArriveExistingMap`。移动/旋转选点、合法性预览、确认 UI、捕获/遮罩、实际放置与落地收尾仍是原版；仅替换表现为固定镜头缩放下的“降落前 universe 位置/Map 角度 → 所选落点/Rot4”动态对齐，不再从视野外飞入。源图被清空前缓存 `ShipPhysics.MassReport`，选点确认后分别以实际定向净推力/质量和实际净力矩/转动惯量得到线加速度 `a` 与角加速度 `α`，按静止到静止的 `T=2√(distance/acceleration)` 计算平移/旋转时间并取较大者（0.35–15 秒表现边界）；两轴共用对称恒加速—恒减速位移曲线，较短轴自动降低有效出力同步到达。整船绘制仍回调原版 `GravshipRenderer.DrawGravship`，因此推进器火焰、耀斑、尾烟及引擎辉光沿用原版。承载目标 = 船当前球面位置经 `TileFromDirection` 得到的真实轨道 tile 图（`GetOrGenerateMap`：有图复用、没图按 `landingMapSize` 生成，图中心补一格 Substructure 满足原版选点合法性）；敌舰/POI 是无 tile 口袋图，不挡路。
- **落点回写（当前坐标规则）**：记录口袋图几何中心落到目标图后的 `dockedOrigin`；目标图所属 `SpaceObject` 经统一地图点⇄球面变换给出 `SpacePose.position`。真实轨道图所属对象为 `PoiState(NativeParent)`，不再使用合成 `MapFrame`。当前对接计划与到达流程以 `doc/星区架构.md` §7 为准。
- 降落图 `WorldCameraPosition`（自建 def 与原版 `SpaceMapParent` 经 Harmony Postfix）读取停靠在本图的船的球面位置，所以非中心落点不会让星球背景与降落图坐标脱节。
- Delta-V 绕过原版起飞仪式，因此在生成 Gravship 前补默认安全 `LaunchInfo(quality=1, doNegativeOutcome=false)`；原版 `LandingEnded` 会无条件读取该对象，缺失正是首轮实测 NRE 的原因。LandingEnded Prefix 另有同样的兼容兜底。
- **口袋图生命周期**：降落动画仍读取旧口袋图中心，所以在 `LandingEnded` Postfix（原版 Gravship/捕获已释放）才销毁旧图并置 `ship.map = null`；停靠期间真实船体只存在于降落图。旧版 docked 存档若仍带空口袋图，会在加载收尾迁移销毁。
- **固定 → 移动**：必须选中目标船驾驶台，并从原版 `WorldComponent_GravshipController.InitiateTakeoff` 发起。原版负责截图捕获、`PreSwapMap`、`GenerateGravship`、源图注销与 `TakeoffEnded`；Delta-V 把可见起飞动画时长压到零，仅在 `TakeoffEnded → GravshipUtility.TravelTo` 扩展点把世界旅行改接到独占口袋图。由于原版十秒动画通常会让源图 `PowerNetManager` 自然结算延迟注销，零动画路径在新图放置前显式调用同一个原生结算入口，不能重新实现或逐 Comp 特判电网。随后规范主驾驶台为口袋图 East（局部 +X），按旋转后的 `Foundations` 实际边界生成新图（每边 15 格结构余量、单轴最小 80，并校正奇偶），再用原版 `GravshipPlacementUtility` 放置。非对称扩建导致中心变化时同步改记 `SpacePose.position`；进入后默认未驾驶。生成失败则以 North 原布局放回固定图，不吞船。起飞成功后对离港地图做**空降落图清理**（帧边界延后执行，脱离原版 TakeoffEnded 调用栈）：太空层地图若已彻底为空——无人、无建筑/物品、无其他停靠船、只剩 Filth/Gas/Mote 残迹——直接 `DeinitAndRemoveMap + parent.Destroy` 删除；空画布走 POI 的休眠/快照机制没有意义。任何遗留内容（哪怕一件掉落物品）都保留地图（原版持久语义）。读档后同样清扫一次（`UniverseManager.SweepEmptyDepartedMaps`），处理历史会话的积累。
- **宿主口袋图 Colonist Bar**：宿主无 pawn 本就不会生成分组，`CheckRecacheEntries` Postfix 仍防御性移除宿主图整组（识别 `UniverseHostParent_DeltaV`），与 RimExodus 自身过滤可叠加。降落图有真实船员，走原版分组。
- **停靠船体归属**：停靠后 `ship.map == null`，宿主图上的驾驶台通过所属逆重引擎的 `ValidSubstructure` 反查 `ShipState`；因此固定轨道期间新建的第二台驾驶台仍可设为主台，多个独立停靠结构之间不会串台。
- **前进方向**：W/S、A/D 与飞行辅助都定义在主驾驶台参考系；`ShipOrientation` 集中换算为 Map 局部指令。HUD 航向同样显示 `SpacePose.angle + 主台局部方向`，无主台才回退历史 +X。
- 停靠/选点船：物理 tick 跳过。选点中由原版 marker 表示；停靠后在降落图由真实建筑表示，在其他对象图从降落图 `ValidSubstructure` 实时生成外壳代理，且不重复叠加 `SpacePose.angle`。

## 游戏内验证清单

1. **背景**：直线驾驶 → 星球背景连续移动；Q/E 旋转 → 星球与星空同步反向旋转、Map 内容不转；聚焦 飞船图 ↔ 降落图 切换背景无跳变；长时间同向飞行（跨多 tile、近半圈）后位置/背景无漂移、无 `GetTileCenter` 红字。
2. **宇宙视野**：敌舰移远 300/600/1000 格（dev），zoom out 外壳可见、方位正确；离开 Delta-V 图后缩放恢复原版上限；打开星球视图并切到轨道层，所有玩家船在连续球面位置显示实际船体剪影，移动/固定四向均与真实朝向一致，切到地表层不显示。
3. **POI 全链**：`POI: create derelict 400 cells east` → 60 tick 内生成图+代理+消息 → zoom out 看到外壳 → 点击聚焦进入 → 拆走物资/破坏墙 → 传送主船 1500 格（跨休眠/删除段）→ 消息"map destroyed (snapshot)"→ 传送回来 → 消息"restored from snapshot"、物资/破坏状态恢复。
4. **POI 休眠**：距离 300–1200 之间图保留但冻结（放入 pawn 的场景 pawn 不 tick——POI 模板无 pawn，验证日志即可）；聚焦休眠 POI 图立即唤醒。
5. **存读档**：POI 三种状态分别存读一致；Packed 恢复正确；docked 船读档后仍停靠且 `ship.map == null`；动画中存读档会等恢复后的 `LandingEnded` 再销毁旧图；**平面坐标旧档**读档自动迁移（宿主图/锚记账消失、位置换算成球面、口袋图残留 tile 清空）且无红字；专用宿主口袋图读档后复用不重建。
6. **驾驶台/模式**：首台自动主；第二台不取代；gizmo 换主生效；拆主台自动转最老台；地面新建船的移动模式 gizmo 灰化且悬停显示重力限制；**原版逆重飞船发射到任意 POI/orbit 图后**，选中某船驾驶台即可进入移动模式（该图不获得任何宿主身份、宇宙中已有其他船也不拒绝），默认未驾驶；固定→移动期间确认走原版 `InitiateTakeoff`，带电器/电线/电池及设施 Comp 无重复注册红字；移动 HUD 始终显示状态，驾驶开关不搬图，多船共享图不出现全局转换按钮。
7. **固定模式**：在 0°/90°/180°/−90° 分别进入 → 目标为当前球面位置对应的真实轨道图并出现原版移动/旋转/合法性预览/确认选项；**同位置重复降落复用同一张图**（飞走再飞回落回原图）；近距离/小角差对齐明显快于远距离/大角差，平移与旋转均从零速度加速后减速至零；确认后镜头位置与缩放不变且无 `LandingEnded` 红字，动画结束后旧口袋图消失；切到敌舰/POI 图仍可看到固定船代理；在降落图扩建/缩减后从具体驾驶台进入移动模式会生成匹配当前边界的新口袋图，Universe 位置保持落点；宿主口袋图在世界视图不可见、殖民者栏无分组、不 tick（dev 日志核对）；**起飞后原降落图若彻底为空则被删除**（世界视图不再残留空轨道图），地图上留一件物品则保留；四向均保持固定时航向，W 永远向主台正面。
8. **不变量回归**：聚焦 identity；切换聚焦不改绝对宇宙状态；连续多次切换无漂移；敌舰位姿直写系列照常工作。
9. **原生 POI 与生成器**：同 seed/parent/尺寸比较同步与分帧结果；小行星、SpaceSite、SpaceSettlement、第三方轨道 MapParent 未访问也自动注册，入圈按近到远串行生成且不重复，排队后离圈取消；未知重步骤准确报告耗时。生成中手动/自动保存被拒绝，成功/失败/换档后无半成品与 MapGenerator 静态锁残留。
10. **原生休眠/删除/代理**：离开真实 POI 后近距不被立即删除，中距 Map 与 Thing 全停 tick，返回只恢复一次；远距严格服从各 parent 的 ShouldRemoveMapNow 输出，强制保活条件和任务主动销毁分别生效。小行星/平台地形、矿物、建筑与物品可见，Space 背景无矩形；采矿/建拆仅更新相关 section；与 RimExodus 同装时两套休眠集合不串图且 Delta-V 不接管地表。

## 明确不做（本阶段）

- 不 patch 相机 clamp、不做投影重锚、不做屏幕边缘方向标记；背景位置/roll 之外仍沿用原版 PlanetLayer 与视差参数。
- POI 无降频层（tick 只有活跃/冻结两档）；快照不保 comp 状态/pawn（有 pawn 不删只休眠）。
- 未提供 adapter 的第三方/原版 GenStep 仍是单帧原子步骤，可能产生一次尖峰；日志会给出步骤名和耗时，后续只需新增 adapter。
- 敌舰停靠（对等架构允许，但未专门验证）；POI 的任务内容仍由其原版/第三方 parent 自身负责。
- Changes.txt 与版本号（按提交纪律，由用户发起）。

## 风险与已知问题

- **EastSign 手性**与阶段2 遗留验证项同源：背景移动方向若与飞行方向相反，翻转 `OrbitSphere` 的 `EastSign` 一个常量。
- **切空间近似**：两对象相对量按观察者基点积×半径换算，跨数十 tile 尺度误差可忽略；若未来出现超远距显示需求再升级为精确测地线公式。
- **ValidSubstructure 覆盖**：stamp 布局（engine 居中 + 艏艉 extender）按 footprint 半径推算覆盖全船，需实测 `Dump universe state` + 停靠全船搬运验证；不满足时的保底方案 = 用 ShipPhysics 船格集仿写收集。
- **快照体积**：POI 图 90×90、模板内容有限；大量 POI 同时 Packed 时的存档增量需关注。
- Current.Game.Gravship 借用窗口内异常（LongEventHandler 出错）会走 ErrorWhileGeneratingMap 顶层报告，docked 不置位、状态可重试；无自动回滚之外的补偿。
