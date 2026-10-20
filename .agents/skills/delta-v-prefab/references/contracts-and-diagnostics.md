# 存档契约与导入诊断

制作流程与坐标解释分别见同目录 `workflow.md`、`geometry.md`。运行时重建次序和入口仍以仓库 `doc/Prefab状态重建.md`、`doc/代码入口索引.md` 及当前代码为准。

## Def 引用与实例状态

- 按实际类型核对引用：`TerrainDef`、`ThingDef`、`ColorDef`、`RoofDef` 分别建表，排除抽象基类。只搜索任意 `defName` 会把模板误当成可用 Def。
- 地毯属于生成 Def：查 `TerrainDefGenerator_Carpet`，不要把 `TerrainTemplateDef` 的 `Carpet` 写成地形。1.6 中 `Carpet` + `Structure_GreenPine` 生成 `CarpetGreenPine`。
- 优先使用游戏实际导出的同 Def 状态原型。手写或裁剪时读该类和全部 Comp 的 `ExposeData`／`PostLoadInit`；不要只保存当前看起来需要的数值。
- **缺失列表节点不等于空列表**。`Scribe_Collections.Look` 可以把初始化过的列表改为 null，随后读取类直接遍历它。`ThingFilter` 应有 `allowedDefs` 与 `disallowedSpecialFilters`；没有特殊筛选也写空节点。手写 Pawn 归属 Comp 时核对 `assignedPawns`、`uninstalledAssignedPawns` 等读取契约。
- 存储筛选器与发射架弹种筛选器都要检查。导弹真正落进弹仓会调用 `ThingFilter.Allows(Thing)`；只验证筛选器 XML 存在或建筑能生成不能覆盖该路径。
- 核对实际 `turretGunDef`、Verb 状态与 ID、BillStack、容器和 Comp 列表。嵌套枪不能沿用其他炮塔原型的 gun Def。
- 同 prefab 的内部引用保留；外部引用清理和旧船身份重分配复用现有重建器。回收完整游戏导出时，不在制作脚本里通过全局字符串替换删除 ID、map 或引用字段；手工克隆新实例时再按引用契约处理身份。
- 外层放置元数据与内层 Scribe 状态都可能含 Def、pos、rot、stuff、数量；编辑后核对两层实际语义。外层 pos 为局部 root，内层 pos 可以保留源地图绝对坐标；`MapPrefab.Spawn` 使用外层落位，`Building_DockingPort.RebasePrefabLocalState` 使用二者之差换算通道。不能为让文本相等而统一重写内层坐标。
- 原版默认值可省略序列化字段，例如旋转 North、守卫模式；先查相应 `ExposeData` 默认值再解释，不能把缺失字段当成损坏。未启用 `useHitPoints` 的物品可能导出 `hitPoints=-1`，不要统一要求所有条目的生命值为正。

## 地形底层与电网

- 验证地板、foundation 与 `BaseTerrainAt` 三层。基架先于地板铺设仍不足以保证底层正确。
- `TerrainGrid.SetTerrain` 在不可通行的 Space 上铺可叠地板时，会使用生成器的 `defaultUnderGridTerrain`，缺省则回填 Sand。飞船生成器应与原版 Space 生成器一样使用 Space 底层；已有可通行的天然底层仍应保留。
- 船每次出力都脱离、地图 ID 持续增加时，读 `ShipRegistry.StateOf`／`RestsOnNaturalGround` 与实际底层。不能隐藏成功提示、强制 pocket 图永远自由飞行或禁用 NPC 出力来掩盖循环。
- `transmitsPower` 电池、发电机和太阳能板等建筑占地本身导电，别再在脚下放第二个导电节点。普通墙下电线与导电建筑下电线是不同情况。
- 原版 `PowerNetMaker` 沿建筑四邻接连接，不保证重叠节点归为同一网。检查导电建筑占地无重叠，并用原版组网校验全网连接；设备供电仍走已有电网收尾入口。

## 从日志区分首因和连锁错误

从 `Player.log` 的第一条失败开始，保留完整内部堆栈、条目索引、Def、位置与旋转。不能只记录异常类型和摘要。

| 日志或现象 | 先查什么 |
|---|---|
| `No TerrainDef named ...` | 引用类型、模板/生成 Def、拼写和启用的内容 |
| `cannot place ...` | 原版占地、操作格、PassThroughOnly、宿主/插件尚未落位的中间状态 |
| `ThingFilter.Allows` 空引用 | 筛选器列表是否通过 Scribe 读成 null，实际库存是否进入正确储物格 |
| `under terrain there` | 基架/地板顺序，以及是否在已铺了一半的 prefab 上叠加程序回退船 |
| `Two power nets on the same cell` | 导电设备与隐藏电线是否重叠 |
| 脱离提示重复且新图 ID 增长 | 底层是否被回填 Sand，状态派生是否持续判为天然地面停靠 |
| `MapDrawer.Dispose` 后继续网格渲染异常 | 是否在原版绘制初始化回调结束前销毁新图 |

文件加载失败时目标图尚未改动，双船测试可以在空图回退程序布局。开始落位后不能把另一艘程序船叠到半成品上。基架写入复核 `FoundationAt`，建筑放不下不能只记警告后宣称成功；沿现有入口报告失败，由调用方处置已修改目标图。

新图的 `Map.FinalizeInit` 会排队绘制初始化。生成长事件内同步销图可能令 `MapDrawer.Dispose` 遇到未初始化的 sections，之后回调又读已释放网格。测试失败的销图和容器注销走 `UniverseManager.QueueFrameAction`，不另加原版补丁或吞异常代替。

## 验证证据

区分静态检查、实际原版函数的离线 fixture 和游戏验收。按变更验证原版四向占地、操作格、Scribe 读后非空列表和筛选条件、原版电网连接，以及失败回退和帧边界清理。离线 fixture 不等于执行完整 `SpawnSetup`。

安装版程序集必须配套能支持其 .NET／Unity 类型的测试运行环境；从本机游戏或已有工具依赖读取程序集，不给 Mod 增加新的运行时依赖。隔离绘制等无关副作用时，保留正在验证的原版逻辑。

完整导入的第一次成功不代表四向、存读档、地板替换/移除和真实天然地面停靠已全部验证；按用户实测范围更新 `doc/TODO.md`。
