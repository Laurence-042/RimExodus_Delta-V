---
name: delta-v-prefab
description: Design, author, validate, and troubleshoot RimExodus Delta-V map prefabs and their layout diagrams. Use for floor plans, completed ship layouts, Scribe state authoring, and prefab import failures; use delta-v-art for sprite work.
---

# Delta-V 地图 prefab

让预制图、示意图和实际导入内容一致。布局范围服从用户：只标地板时不擅自放建筑；要求实装时完成建筑、原版落位检查和游戏验收。

## 依据与入口

先读仓库 `AGENTS.md`、`README.md` 与 [Prefab 状态重建](../../../doc/Prefab状态重建.md)。这些文件和现有代码是运行时契约的权威位置；本 skill 指导制作与诊断，不另建放置、物理或状态判定实现。

从当前 Def 与继承链读取尺寸、旋转、默认朝向、交互点、地形需求、Comp 和插件槽位。涉及原版行为时查 `AGENTS.md` 指定的对应版本参考源码；当前统一参考目录为 `C:\Users\Laure\Documents\workspace\rimworld\_refernces`。不要依据截图或记忆补字段。

地图内容仍通过 `MapPrefab`、`MapPrefabThingState`、`MapContentSpawner` 重建；运行时变更先核对 `AGENTS.md` §20 的已有入口。贴图工作另读相邻的 `delta-v-art` skill。

## 布局与示意图

- 用同一份格位记录生成 XML 和图；建筑框表示真实占地，另标绘制外延。改布局时同时更新图和预制图，避免旧图对应新 XML。
- 建筑 `Position` 是原版 root，不能普遍当成左下角。朝北的 2×2 root 在西南格，奇数边与偶数边的旋转补偿不同。调用实际游戏程序集的 `GenAdj.OccupiedRect`、`PrefabUtility.GetAdjustedThingLocalPosition`、`GetAdjustedLocalPosition` 验证四向，不用自写几何检查作为唯一证据。
- 不可旋转建筑还要读 `defaultPlacingRot`；占地用 `size`，不是 `graphicData.drawSize`。
- `PassThroughOnly` 可通行但不能当作操作格。用原版 `GenConstruct.InteractionCellStandable` 与 `NotBlockingAnyInteractionCells` 检查工作台、炮手等交互点；连通通道检查不能代替这两项。
- 检查批量生成的中间状态：宿主先落位、插件随后安装、电网最后结算。最终有自瞄不代表早期操作格已解除保护。太阳能板等建筑应留开操作格，不能靠插件尚未生效时的特例通关。
- 外置太阳能板保持无顶；推进器按原版排气禁区检查。护盾投射器的保护、船内密封、屋顶支撑、补弹与维护通道均需落实到格位。
- 舰船动力读取 `ShipPhysics` 的真实分配预览，并明确质量、载荷与推进器工作状态假设；不从喷口数量直接断言机动性能。稀有逆重电芯是否使用、武器数量与电源预算服从用户需求。

## Def 引用与存档状态

- 按实际类型核对每个引用：`TerrainDef`、`ThingDef`、`ColorDef`、`RoofDef` 分别建表，排除抽象基类。只搜索任意 `defName` 会把模板误当成可用 Def。
- 地毯是原版生成 Def：查 `TerrainDefGenerator_Carpet`，不要把 `TerrainTemplateDef` 的 `Carpet` 写成地形。1.6 中 `Carpet` + `Structure_GreenPine` 生成 `CarpetGreenPine`。
- 优先使用游戏实际导出的状态作为同 Def 原型。手写或裁剪状态时逐项读该类和所有 Comp 的 `ExposeData`／`PostLoadInit`，避免只保存当前看起来需要的数值。
- **缺失列表节点不等于空列表**。`Scribe_Collections.Look` 可以把初始化过的列表改成 null；读取类之后可能直接遍历它。已确认的例子：`ThingFilter` 必须有 `allowedDefs` 与 `disallowedSpecialFilters`；没有特殊筛选也写空节点。手写 Pawn 归属 Comp 时也核对 `assignedPawns`、`uninstalledAssignedPawns` 的读取契约。
- 存储筛选器与发射架弹种筛选器都要校验。导弹真正落进弹仓会调用 `ThingFilter.Allows(Thing)`；仅检查筛选器 XML 存在或建筑能生成不能覆盖此路径。
- 保留武器实际 `turretGunDef` 与对应 Verb 的合法状态、容器内容及内部引用；清除外部地图、旧船 ID、Pawn／任务等源实例引用。不要让新 gun、BillStack 或 Comp 列表缺失后由运行时异常暴露。

## 地形底层与电网

- 验证三层地形：实际地板、foundation、`BaseTerrainAt`。基架先于地板铺设仍不足以保证底层正确。
- `TerrainGrid.SetTerrain` 在不可通行的 Space 上铺可叠地板时，可能使用生成器的 `defaultUnderGridTerrain`，缺省则填 Sand。飞船生成器应与原版 Space 生成器一样使用 Space 底层；天然地面已有可通行底层时仍应保留它。
- 若船每次出力都脱离、地图 ID 不断增加，读 `ShipRegistry.StateOf`／`RestsOnNaturalGround` 与实际底层；不能通过隐藏成功提示、强制 pocket 图永远自由飞行或禁用 NPC 出力掩盖循环。
- 电池、发电机和太阳能板等 `transmitsPower` 建筑占地本身导电，别再在其脚下放第二个导电节点。普通墙下电线与导电建筑下电线是不同情况。
- 原版 `PowerNetMaker` 沿建筑四邻接连接，不能假定重叠电线会归入同一网。检查导电建筑占地无重叠，并用原版组网验证全网连接；消费者供电仍由已有电网收尾入口处理。

## 导入失败诊断

从 `Player.log` 的**第一条失败**开始，保留完整内部堆栈与条目索引、Def、位置和旋转。后续基架、电网或绘制报错可能是失败善后的连锁结果。

- 文件加载失败时目标图尚未改动；双船测试可在空图回退程序布局。落位已经开始后不可把另一艘程序船叠到半成品上。
- 基架写入要复核 `FoundationAt`；建筑放不下不能只记警告后宣称成功。沿既有入口报告失败，让调用方处置已修改的图。
- 新图的 `Map.FinalizeInit` 会排队做绘制初始化。生成长事件内同步销图可能令 `MapDrawer.Dispose` 遇到尚未初始化的 sections，稍后回调又读已释放网格。测试失败的销图／容器注销走 `UniverseManager.QueueFrameAction`，不以额外原版补丁或吞异常代替。

## 验证与交付

分清静态文件检查、原版函数的离线 fixture 和实际游戏验收；离线 fixture 不等于执行完整 `SpawnSetup`。按变更选择检查：类型引用、原版四向占地、操作格、Scribe 读取非空列表与筛选条件、原版电网连接，以及失败回退和帧边界清理。

离线加载安装版程序集时，运行环境要能支持该版 .NET／Unity 类型；程序集应来自本机游戏或已有工具依赖。不要为测试给 Mod 增加新的运行时依赖。测试只隔离绘制等无关副作用，保留正在验证的原版逻辑。

`Prefabs/` 是被忽略的本地输出，`ListPrefabFiles` 会递归枚举其中的 `.xml`。辅助数据不要伪装成 prefab XML；用 `.probe` 等其他扩展名或放到输出目录之外。遵守已有入库规则，不把测试 DLL、存档或参考源码强行纳入提交。

需要用户启动游戏验收时，先完成并保存生成文件、示意图及必要编译，再结束当前工作、明确停止读写，留出完整测试窗口。用户报告结束或提供新日志后再恢复。XML 更新可重新导入；Def／DLL 更新需要重启，错误测试图需要重建。

只有用户实测确认的部分才记为通过。跨图旋转、地板移除、存读档等未测试边界留在 `doc/TODO.md`。提交仍遵从当次用户授权。
