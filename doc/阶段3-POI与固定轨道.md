# 阶段3：POI 生命周期 + 动态锚与宇宙视野 + 驾驶台 + 固定轨道模式

对应 [story.md](story.md)「当前阶段计划 §3」+ 用户决策（2026-09-13）：

- **范围 = 四块全做**：最简太空 POI 与地图生命周期、更远宇宙视野（放宽缩放）、驾驶台（正式进入驾驶方式）、固定轨道/建造模式（逆重降落整船搬运）。
- **POI 生命周期 = 活跃/休眠/删除三段**（universe 距离而非 tile 距离，用户定夺）：近了正常 tick、远了冻结但保留、更远删除收快照。
- **宇宙视野 = 放宽缩放**（用户定夺）：远处对象的外壳代理由 ShellRenderer 画在世界空间（`Graphics.DrawMesh` + `disableMapClippers`），本就不受地图矩形限制，唯一约束是相机缩放上限。
- **动态锚（用户定夺）**：宿主图的位置记录随玩家主船同步平移（不移动地块）；背景相机按连续坐标移动，并按聚焦姿态反向旋转。

**状态：已实现，待游戏内验证（2026-09-13）。**

## 实现概览

| 文件 | 职责 |
|---|---|
| `Source/Core/SpaceObject.cs` | 宇宙对象基类：id/label/pose/map（AGENTS §11 Universe State） |
| `Source/Core/ShipState.cs` | 飞船状态：+ isPlayerShip、primaryConsole、docked 停靠状态与恢复数据 |
| `Source/Core/ShipOrientation.cs` | Map 姿态、主驾驶台局部朝向与玩家所见航向的集中换算 |
| `Source/Core/PoiState.cs` | POI 状态：seed + 快照引用（Packed 时只有轻量状态） |
| `Source/Lifecycle/SpaceLifecycle.cs` | **POI 生命周期单一管理入口**：三段判定、生成/休眠/唤醒/删除/恢复 |
| `Source/Lifecycle/PoiSnapshot.cs` | 结构级快照（foundation/表层地形、屋顶、建筑、物品）平行列表序列化 |
| `Source/Generation/PoiTemplate.cs` | 残骸模板（seed 决定，同 seed 重生成一致） |
| `Source/Harmony/Patch_PoiDormancy.cs` | 休眠拦截：Map MapPreTick/MapPostTick/MapUpdate Prefix + CurrentMap setter 唤醒 + Storyteller 目标剔除 |
| `Source/World/ShipPocketParent_DeltaV.cs` | 船/POI 口袋图 WorldObject：覆写 `WorldCameraPosition`（连续球面视角） |
| `Source/World/SpaceHostParent_DeltaV.cs` | 宿主图 WorldObject：背景视角 = 主船（动态锚）的连续球面位置 |
| `Source/Core/UniverseLayerMapping.cs` | + `AbsoluteSpherePoint`（绝对偏移 → 背景球面点，背景专用入口） |
| `Source/Core/UniverseManager.cs` | objects 权威列表、动态锚维护（floating origin 平移）、缩放放宽、SpaceLifecycle 挂载 |
| `Source/Buildings/CompDeltaVPilotConsole.cs` | 驾驶台 Comp：进/出驾驶 gizmo + 主驾驶台规则 |
| `Source/Interaction/DockController.cs` | 固定轨道停靠/起飞（逆重搬运管线 + 角度吸附 + 落点校验/回滚） |
| `Source/Generation/ShipPocketMapFactory.cs` | 飞船口袋图单一生成入口；起飞时按当前 Gravship 实际边界动态定尺寸并居中 |
| `Source/Harmony/Patch_GravshipLanding.cs` | 原版确认降落事件 → Delta-V docked 状态的最小桥接 |
| `Source/Harmony/Patch_WorldCameraRotation.cs` | 给背景 WorldCamera 补聚焦参考系 roll；不旋转 Map 相机/内容 |
| `Source/Rendering/ShellRenderRuleDef.cs` | 外壳 Thing 白名单；可选 alternateGraphic 与 zIndex |
| `Source/Rendering/DeltaVLandingAnimator.cs` | 保持镜头缩放的起始姿态 → 目标姿态降落插值 |
| `Source/Harmony/Patch_DeltaVLandingAnimation.cs` | 仅在 Delta-V 降落期间替换原版镜头/飞入表现并补原版收尾前置状态 |
| `1.6/Patches/DeltaV_PilotConsole.xml` | 向 Odyssey 原版 PilotConsole 追加 Delta-V Comp，保留原版 Gravship 身份与设施兼容 |

## 核心设计定稿

### 动态锚与背景相机

- **两个换算入口不得混用**（UniverseLayerMapping 类注释）：
  - tile 投影（重锚/生成落点）用**相对**偏移 `pose.position`——口袋图挂载 tile 表示相对宿主的方向，长途航行后仍落在宿主附近；
  - 背景球面视角（`AbsoluteSpherePoint`）用**绝对**偏移 `pose.position + anchorDisplacement`——平移时刻绝对值不变，背景无跳变。
- **floating origin**：主船离原点超过 `originShiftThreshold`（默认 1000 格）时全体 pose 一次性平移（含 `preDockPose`），`anchorDisplacement` 累计。相对位置、绝对偏移都不变 → 外壳投影与背景均无感。
- **背景位置注入点 = `MapParent.WorldCameraPosition`**；**背景角度注入点 = `WorldCameraDriver.ApplyMapPositionToGameObject` Postfix**。前者保持连续球面位置，后者只给父级 WorldCamera 补局部 roll（子级 WorldSkyboxCamera 自动继承），因此星球与星空相对聚焦船反向旋转，而 Map Camera/Thing/鼠标/寻路仍为 identity。
- **缩放放宽**：聚焦 Delta-V 图（对象图/宿主图）时幂等设置 `CameraDriver.config.sizeRange.max = universeZoomMax`（默认 400），离开恢复记录的原值。相机高度公式与 farClip(65.5) 按同一 max 归一化，正交投影下放宽安全，无需 patch。

### POI 三段生命周期

- 距离参考 = 全部玩家阵营船的最小 universe 距离；阈值 `poiActiveRadius`（默认 300）/ `poiDeleteRadius`（默认 1200）。
- **Active**：无图 → LongEventHandler 生成口袋图（复用 DeltaV_ShipPocket 管线，tile=投影附近空闲 tile）→ 有快照回放快照（玩家改动恢复，快照用后丢弃），否则模板 + seed 盖章。
- **Dormant**：图保留、tick 冻结（三个 Harmony Prefix 早退；RimExodus Patches_Dormancy 同款模式，判定集合只含自己的 POI 图，与 RimExodus 互不干扰）。聚焦中的 POI 图强制活跃；进入休眠图（CurrentMap setter，覆盖殖民者栏/外壳点击等全部路径）同步唤醒。休眠集合不存档，读档后先活跃、下次扫描重新分段。
- **Packed**：图上**无 pawn 才删**（原版 AnyPawnBlockingMapRemoval 同语义；有 pawn 只休眠）→ `PoiSnapshot.Capture` → `PocketMapUtility.DestroyPocketMap`。
- **快照保真边界**：foundation + 非 Void 表层地形、屋顶、建筑（def/stuff/pos/rot/hp/faction）、物品（含 stack）。不保 pawn、comp 内部状态（电力开关等）、燃气/雪/区带。序列化 = 平行列表（格列表与 def 列表一一对应），保存/加载共用同一组 `Scribe_Collections.Look`，无不对称空间。

### 驾驶台与主驾驶台规则

- 直接扩展原版 `PilotConsole`（3×2、rotatable、需 Substructure 甲板、Odyssey 建造分类）：XML patch 只追加 `CompDeltaVPilotConsole`，保留原版 `CompPilotConsole`/`CompProperties_GravshipFacility`。因此原版 `GravshipComponents`、`ThingDefOf.PilotConsole` 身份判断、发射方向回退和第三方兼容均走原生路径；Delta-V Comp 在非 Delta-V 地图上不显示额外行为。
- 主台规则（story §固定轨道）：第一台自动成为主（SpawnSetup 且现主台失效时）；仅显式 gizmo 可换主；主台被拆转交船上最老的一台。
- Toggle 快捷键保留（无驾驶台也可驾驶，dev/测试便利）。

### 白名单外壳

- 不再重放 `SectionLayer_Terrain` 或整层 `SectionLayer_ThingsGeneral`。每帧直接从真实 Map 读取逆重基架 foundation 和 Thing：基架、`ThingDef.IsWall` / `IsDoor` 自动纳入，其他 Thing 只有命中 `ShellRenderRuleDef.thingDef` 才绘制。
- 规则的 `alternateGraphic` 使用原版 `GraphicData` XML 结构，可提供 `Graphic_Single`/`Graphic_Multi`、`texPath`、`drawSize`、shader 等；省略则沿用原 Thing Graphic。`zIndex` 以 `Altitudes.AltInc` 为单位叠加，控制代理遮挡次序。
- 默认只显式加入 `LargeThruster`；驾驶台、逆重引擎、延展器和室内设备不在白名单。该列表可由兼容补丁追加，不要求改核心渲染器。

### 固定轨道 / 建造模式（逆重搬运）

- 测试船配置复刻参考存档 map2：`GravEngine` 3×3 的 `Thing.Position` 锚点位于船体偏移 `(17,12)`，四角布置 4× `GravFieldExtender`（引擎 SubstructureSupport 500、延展器各 +250）→ 支持上限 1500 ≥ 960 格甲板，`ValidSubstructure`（foundation floodfill ∩ footprint 并集）覆盖全船。生成时引擎先以玩家阵营生成，延展器/驾驶台/推进器随后生成并在批量盖章末尾经原版 `CompAffectedByFacilities.Notify_ThingChanged` 重连；玩家船墙、门和设备全部归玩家占有。不得把 3×3 引擎锚点误作其他坐标语义。
- **停靠**：以 `SpacePose.angle + 主台局部朝向` 得到真实航向并吸附 90° → `GenerateGravship` → 只设置原版 `Gravship.Rotation` 的初始值 → 直接调用 `GravshipUtility.ArriveExistingMap`。移动/旋转选点、合法性预览、确认 UI、捕获/遮罩、实际放置与落地收尾仍是原版；仅替换表现为固定镜头缩放下的“降落前 universe 位置/Map 角度 → 所选落点/Rot4”平滑插值，不再从视野外飞入。整船绘制仍回调原版 `GravshipRenderer.DrawGravship`，因此推进器火焰、耀斑、尾烟及引擎辉光沿用原版。承载目标始终为宿主图；敌舰/POI 保持独立 Map，只以代理投影到宿主图。
- **落点回写**：记录口袋图几何中心落到宿主图后的 `dockedOrigin`，再经确认前 HostFrame 将它换回新的 `SpacePose.position`。停靠期间 HostFrame 由 `pose.position + hostMapCenter - dockedOrigin` 反推，因此确认前后 POI/敌舰代理不跳位；宿主图真实船体与 Universe 位置保持同构。
- 宿主图 `WorldCameraPosition` 同样读取 HostFrame（而非直接读取主船 pose），所以非中心落点不会让星球背景与宿主图坐标脱节。
- Delta-V 绕过原版起飞仪式，因此在生成 Gravship 前补默认安全 `LaunchInfo(quality=1, doNegativeOutcome=false)`；原版 `LandingEnded` 会无条件读取该对象，缺失正是首轮实测 NRE 的原因。LandingEnded Prefix 另有同样的兼容兜底。
- **口袋图生命周期**：降落动画仍读取旧口袋图中心，所以在 `LandingEnded` Postfix（原版 Gravship/捕获已释放）才销毁旧图并置 `ship.map = null`；停靠期间真实船体只存在于宿主图。旧版 docked 存档若仍带空口袋图，会在加载收尾迁移销毁。
- **起飞**：不播放起飞动画；`GenerateGravship(engine@dockedMap)` 后先规范主驾驶台为口袋图 East（局部 +X），再按旋转后的 `Foundations` 实际边界生成新口袋图（每边 15 格结构余量、单轴最小 80，并校正奇偶使船体几何中心与 Map 中心精确重合），最后用原版 `GravshipPlacementUtility` 搬入。非对称扩建导致中心变化时，先将宿主图上的新船体中心换算为 `SpacePose.position`，保证所有建筑的绝对 Universe 位置在起飞前后不变；`SpacePose.angle` 写入主驾驶台在降落图上的绝对方向。新图生成失败则以 North 原布局放回宿主图，不吞船。
- **宿主图 Colonist Bar**：参考 RimExodus 的休眠/无人图过滤方式，在原版 `CheckRecacheEntries` 完成后移除没有停靠船的宿主图整组，并重建连续 group 与绘制缓存。起飞状态提交后立即标脏，空宿主图不再占据顶部栏；再次停靠时恢复原版分组。该过滤只识别当前 Delta-V 宿主图，可与 RimExodus 自身过滤叠加。
- **停靠船体归属**：停靠后 `ship.map == null`，宿主图上的驾驶台通过所属逆重引擎的 `ValidSubstructure` 反查 `ShipState`；因此固定轨道期间新建的第二台驾驶台仍可设为主台，多个独立停靠结构之间不会串台。
- **前进方向**：W/S、A/D 与飞行辅助都定义在主驾驶台参考系；`ShipOrientation` 集中换算为 Map 局部指令。HUD 航向同样显示 `SpacePose.angle + 主台局部方向`，无主台才回退历史 +X。
- 停靠/选点船：物理 tick 跳过、tile 不重锚。选点中由原版 marker 表示；停靠后在宿主图由真实建筑表示，在其他对象图从宿主图 `ValidSubstructure` 实时生成外壳代理，且不重复叠加 `SpacePose.angle`。

## 游戏内验证清单

1. **背景**：直线驾驶 → 星球背景连续移动；Q/E 旋转 → 星球与星空同步反向旋转、Map 内容不转；聚焦 飞船图 ↔ 宿主图 切换背景无跳变。
2. **宇宙视野**：敌舰移远 300/600/1000 格（dev），zoom out 外壳可见、方位正确；离开 Delta-V 图后缩放恢复原版上限。
3. **POI 全链**：`POI: create derelict 400 cells east` → 60 tick 内生成图+代理+消息 → zoom out 看到外壳 → 点击聚焦进入 → 拆走物资/破坏墙 → 传送主船 1500 格（跨休眠/删除段）→ 消息"map destroyed (snapshot)"→ 传送回来 → 消息"restored from snapshot"、物资/破坏状态恢复。
4. **POI 休眠**：距离 300–1200 之间图保留但冻结（放入 pawn 的场景 pawn 不 tick——POI 模板无 pawn，验证日志即可）；聚焦休眠 POI 图立即唤醒。
5. **存读档**：POI 三种状态分别存读一致；Packed 恢复正确；docked 船读档后仍停靠且 `ship.map == null`；动画中存读档会等恢复后的 `LandingEnded` 再销毁旧图；宿主图关系不断。
6. **驾驶台**：首台自动主；第二台不取代；gizmo 换主生效；拆主台自动转最老台。
7. **固定模式**：在 0°/90°/180°/−90° 分别进入 → 目标始终为宿主图并出现原版移动/旋转/合法性预览/确认选项；确认后镜头位置与缩放不变，船从确认前实际位置/角度平滑转到落点/角度且无 `LandingEnded` 红字，动画结束后旧口袋图消失；切到敌舰/POI 图仍可看到停靠船代理；在宿主图扩建/缩减后返回飞行会生成匹配当前边界的新口袋图，Universe 位置保持落点；起飞后宿主图 Colonist Bar 分组消失、再次停靠后恢复；四向均保持降落时航向，W 永远向主台正面。
8. **不变量回归**：聚焦 identity；切换聚焦不改绝对宇宙状态；连续多次切换无漂移；敌舰位姿直写系列照常工作。

## 明确不做（本阶段）

- 不 patch 相机 clamp、不做投影重锚、不做屏幕边缘方向标记；背景位置/roll 之外仍沿用原版 PlanetLayer 与视差参数。
- POI 无降频层（tick 只有活跃/冻结两档）；快照不保 comp 状态/pawn（有 pawn 不删只休眠）。
- 敌舰停靠（对等架构允许，但未专门验证）；POI 自动生成/任务化（dev action 创建）。
- Changes.txt 与版本号（按提交纪律，由用户发起）。

## 风险与已知问题

- **EastSign 手性**与阶段2 遗留验证项同源：背景移动方向若与飞行方向相反，翻转 `UniverseLayerMapping.EastSign` 一个常量。
- **ValidSubstructure 覆盖**：stamp 布局（engine 居中 + 艏艉 extender）按 footprint 半径推算覆盖全船，需实测 `Dump universe state` + 停靠全船搬运验证；不满足时的保底方案 = 用 ShipPhysics 船格集仿写收集。
- **快照体积**：POI 图 90×90、模板内容有限；大量 POI 同时 Packed 时的存档增量需关注。
- Current.Game.Gravship 借用窗口内异常（LongEventHandler 出错）会走 ErrorWhileGeneratingMap 顶层报告，docked 不置位、状态可重试；无自动回滚之外的补偿。
