# 教程布局制作源

`build.py` 是生成入口；移民船残骸以用户原始导出的逐字节副本 `arrival_intact.prefab` 为基准，删改逻辑编辑 `arrival_layout.py`，环形船厂与小货船编辑 `shipyard_layout.py`，武器平台与靶舰编辑 `combat_range_layout.py`，12 个星区叶子的内置内容编辑 `sector_layout.py`。生成器读取安装版与项目 Def 的尺寸和继承链，解析占地／操作格后由同一份记录输出：

- `Common/Prefabs/Tutorial/*.xml`：七份正式可打包教程 prefab，运行时不调用布局脚本。
- `Common/Prefabs/Sector/{Craft,Stations}/*.xml`：星区飞行器与站点默认生成池。
- `Common/Prefabs/Sector/Asteroids/*.xml`：小行星带核心与五份带权子资源；只有 `SectorAsteroid.xml` 含批量标记。
- `Source/obj/TutorialContent/*.png`：与 XML 同批记录生成的临时布局图，不入库。
- `Source/obj/TutorialContent/*.records.json`：原版离线检查输入，不入库。

`prototypes.states` 是从本地 Demo／Demo_0 导出中摘取并清除源图、派系、控制台身份、电网引用后的实例状态。空休眠舱按原版 `Building_Casket`／`ThingOwner` 存档契约补齐，并经真实 Scribe 加载验证。不要删掉内部容器的具体 Class 或空列表，也不要把完整 Scribe 替换成普通 PrefabThingData。

`build.py` 同时输出 REF `environment` 层，按布局的环境分组写入有顶格及月池力场顶，默认 21°C、真空度 0；残骸仅货仓为 12°C、真空度 0，其余有顶区域为 −75°C、真空度 1，包含不可通行设备下方的空气格；无室内区域的模板输出空列表。运行时教程不再重设数值，机制与验收见 [Prefab 状态重建](../../doc/Prefab状态重建.md)。

```powershell
python scripts/TutorialContent/build.py
just build 1.6
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/TutorialRegression/run.ps1 -HarmonyPath C:/Users/Laure/.nuget/packages/lib.harmony/2.4.1/lib/net8.0/0Harmony.dll
just build
```

可用 `--data` 指定其他安装目录的 Data。构建依赖 Python Pillow；它只用于制作期示意图，不是 Mod 运行前置。

`python scripts/TutorialContent/build.py --only SectorFighter ... SectorAsteroid AsteroidSteelSmall AsteroidSteelMedium AsteroidComponentsSmall AsteroidMixedMedium AsteroidPlasteelLarge` 可单独重建星区内容。舰船与站体均包含真实驾驶台、电网、维生和用途设备；贸易站状态显式绑定三个锁定供货仓与一个解锁取货仓。小行星带核心和五份子体按原生 asteroid 契约写 `Vacstone_Rough`、Vacstone／部分 Mineable 和 `RoofRockThin`，不写 foundation／environment；核心标记保存带权路径、环带、密度、间距和固定种子，子体没有标记。`TutorialRegression` 检查 12 个注册叶子的默认路径、format-2 文件、Scribe 数字身份、贸易引用和小行星带完整契约；完整 SpawnSetup、人员活动、战斗、交易补货和 cluster 物化仍由新档验收。

`python scripts/TutorialContent/build.py --only Modules` 生成独立舱室 XML，并把图纸、边界格位报告和两种拼缝示意写入 `Source/obj/TutorialContent/`。逐个改动时也可以直接指定 `ModuleResearch` 等模板名。运行上述原版离线检查命令时加 `-ModulesOnly` 检查模块。模块接口和空基架口径见[独立舱室模板](../../doc/Prefab状态重建.md#独立舱室模板)。

`python scripts/TutorialContent/build.py --only AssemblyCombat AssemblyIndustrial` 重建两艘模块试装船。编辑 `module_assemblies.py` 调整组合位置和机务设施。上述 `-ModulesOnly` 回归也检查这两艘船。

用下列命令运行 Workshop 轮廓驱动的离线生成器。CP-SAT 仅用于制作期；先按上文编译 1.6 和运行 `-ModulesOnly`，使生产分配器夹具可用。

```powershell
py -3.14 -m pip install --target Source/obj/TutorialContent/SolverDependencies -r scripts/TutorialContent/ship-search-requirements.txt
$env:PYTHONPATH = 'Source/obj/TutorialContent/SolverDependencies'
py -3.14 -X utf8 scripts/TutorialContent/generate_ships.py --blind-control --output Source/obj/TutorialContent/ShipSearch
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/TutorialRegression/run.ps1 -HarmonyPath C:/Users/Laure/.nuget/packages/lib.harmony/2.4.1/lib/net8.0/0Harmony.dll -ShipSearchDirectory Source/obj/TutorialContent/ShipSearch
```

可重复传入 `--blueprint` 读取本机 BTD 文件；不传时使用本机 Workshop 的 Freighter、Explorer 和 Tier 0 Ebon Hawk，且 Ebon Hawk 示范要求走廊至少有一处交会。可用 `--seed`、`--weights`、`--material-limits`（Def 名至整数上限的 JSON 对象）、`--distance`、`--harbor-distance` 调整输入。 `--batch-seeds N` 统计连续 N 个种子的功能抽取，再对每个任务实际求解 `--batch-solves` 个已抽取种子（默认一份），分别报告抽取、尝试及通过数。 `--trace` 显示拒绝原因；若要扩大搜索，增加 `--proposals`、`--finalists` 或确定性预算。切换实验时换输出目录，入选文件以该次 `batch.json` 为准。

每艘入选船附同源 XML、PNG、格位／材料账以及原生分配器机动／油耗估算。可手动复制入选 XML 到用户 `DeltaV/Prefabs/ShipSearch/`，从一次性轨道图的 **Prefab: replace focused map...** 导入。预览不随默认生成池发布。设计约束见[离线飞船生成](../../doc/Prefab状态重建.md#离线飞船生成)。

用同一 `-ShipSearchDirectory` 配合 `-Version 1.7` 和安装版 1.7 Harmony 路径复核另一版本。回归同时重新读取 XML、检查原版放置和接近区，再重跑机动及燃料报告。

### 舰船创作工具

用已导出的原始船壳请求运行 `generate_ships.py --shell-request <请求> --shell-task transport --output <预览目录>`，任务可选 `balanced`、`transport`、`production` 或 `escort`。God 模式将任务写进请求。没有任务属性的旧请求可用于复跑七舱基线，也可使用 `--legacy-shell`。请求还支持 `crewCount`、四维整数 `weights`、`distance`、`harborDistance` 与逐项 `<Materials><Limit def="Steel" count="…" /></Materials>`。预算、无用途甲板门槛和拒绝阶段记录在 `batch.json` 中。

用同一请求和游戏 Data 路径运行 `python scripts/TutorialContent/test_ship_program.py <请求> <Data>`，检查两版 Def 账目、清单差异、确定性、材料上限与标记冲突。实际通过布局与原生机动验收的预览再交给 `TutorialRegression/run.ps1 -ShipSearchDirectory <预览目录>`；空结果不会被计为通过。范围及进度见[舰船生成实施计划](../../doc/舰船生成选舱与布局实施计划.md)。

任务预览先按横截面冻结走廊，再由同一个 CP-SAT 模型选择 WARGEAR 的舱位、舱室配比和接缝。`--no-skeleton-guide` 运行原按需接路对照。两条流程使用同一船壳、资源预算和整船验收门槛。用 `compare_shell_skeleton.py <存档56请求> <新输出目录> --baseline <旧七舱预览目录>` 先比较同面积 H 型与细长船壳，再跑运输、生产和护航。参数 `--programs`、`--proposals`、`--native` 等透传给生成器。输出含骨架分区图、拒绝日志和 `comparison.json`。`layout-attempt-*.png` 展示未验收的排舱尝试。双版本夹具复核实际入选的 XML 后才算整船验收。几何专项可用 `python scripts/TutorialContent/test_hull_skeleton.py` 复跑。

固定骨架路径在排舱前枚举可容纳两层脚印的安装带。排舱模型使用空载、典型载荷和满载的推力估算来约束方向容量，以参考船的原生航线和阻力预算估算油箱下限。模块确定后，把安装带收窄到实际外墙，保留内侧检修和外侧排气。重试点位沿用同一推进模型与已生成的连通约束。最终仍由原生 `ShipPhysics` 和整船夹具验收，估算不能替代验收。`--late-propulsion` 可复跑排舱之后才估算推进的对照路径。

先用 `just build 1.6` 或 `just build` 构建当前版本，再运行下例配置本机 Python 与原生机动校验器。参数为本机的游戏安装目录、Harmony DLL 和 RimWorld 用户数据目录。配置按游戏版本分开；普通游戏流程无需安装 Python。已安装的创作工具也可以通过 `worker` 指向开发仓库的 `scripts/TutorialContent/generate_ships.py`。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/TutorialContent/configure_ship_planner.ps1 -Version 1.6 -GameRoot '<游戏目录>' -HarmonyPath '<Harmony DLL>' -SaveDataDirectory '<RimWorld 用户数据目录>'
```

在「模组设置 → 开发调试」开启默认关闭的「显示实验性舰船生成」，再开启 God 模式。在轨道图建连通的空基架，放一个「舰船布局控制标记」，再放功能区标记。人员和 SpaceObject 标记从「地板」分类放置，在正式导入后展开。选中控制标记，选择舰船任务与走廊宽度并点击「生成舰船预览」。自动宽度按区段判定，指定宽度用于全船。试装成功后可导出或回退。回退前让人员离船。回退会清除原船壳内的建筑和物品，包括试装后添加的内容。求解失败时原船壳保留，拒绝统计在 RimWorld 用户数据 `DeltaV/ShipPlanJobs/` 下。

生成期间，选中控制标记可查看阶段、阶段内尝试次数和已用时间。每次任务实时写入 `DeltaV/ShipPlanJobs/<任务 ID>/worker.log`。日志带从任务开始计起的时间，包含 worker 输出、CP-SAT 的实际用时及确定性用时、拒绝原因和游戏主线程的采集／落位阶段。打开 Mod 设置的「日志：开发调试」可在游戏日志中查看阶段摘要和该次日志路径。离线命令加 `--progress --trace` 可输出相同的求解里程碑及拒绝原因。

残骸保留用户原船的舱室与原版飞船连接关系，在原导出上添加后部爆炸断口、拆除搜刮设备并保留原床铺／家具／厨房，把队长舱与保留唯一一套真空服的服装架移入货仓。正式资源与本地已归档到 `DeltaV/PrefabArchive/Tutorial/ArrivalStationWreck.xml` 的历史副本使用同一生成结果；归档目录不参与 prefab 扫描。回归覆盖六舱存取、货仓队长绑定、唯一电网、教练机泊口与货仓温控。

`--only Shipyard PrototypeShip` 仅重建本轮两份内容。`shipyard.states` 保存其状态原型：原有设施从本地导出摘取并清除外部引用；水培、工作台、ASF 仓储和 EVA 按各自 ExposeData 补齐。每次生成重编号炮塔内部枪与 Verb 引用，最终写出前检查整份资源所有顶层／嵌套 Thing 的数字 ID 唯一及枪械／Verb 一致；不同 Def 前缀不能复用同一数字 ID。弹头／装药分别写入当前库存节点。布局图和 `.layout.json` 同样来自最终记录；月池在地表与基架两层一致，留给现有力场屋顶逻辑处理。

环形船厂与赠船的配置、归属及验收边界见[环形船厂与小型货船](../../doc/教程开局实施计划.md#环形船厂与小型货船2026-11-04)。原版回归从安装版 Def 独立核对交互格布尔值及派生供电组件，并检查实体屋顶支撑、工作台／水培／EVA 容器与虚拟工具 Scribe 和双库存状态。小货船额外以 Def 质量、生产物理常量和货柜内容估算交付／满载钢铁两种质量分布，调用生产分配器验证六方向；假定推进器健康满功率，不执行完整运行时质量扫描或燃料消耗。

布局朝北；残骸保留用户导出的原版 root，其余程序布局的编辑位置使用旋转后占地西南格，生成器反求原版 root。Def 继承后的地形承载检查及原版四向／操作格／电网检查使用 `TutorialRegression` 的 1.6 程序集夹具；不运行 Unity 或启动游戏。当前包含移民船前段和阶段 B 操作课程首版；训练船派系／公共属性在生成角色绑定时设置，赠船独立归属。货架含完整筛选存档，回收钢铁为真实 75 件。完整课程继续按[计划](../../doc/教程开局实施计划.md)施工。

教程回归同时覆盖六方向实际推力分配（假设质量 1000、惯量 30000、推进器均可用）、课程阶段及操作事实存档、真实主台公共属性存取、公共船控制／建造／占领权限、原版货架候选搜索和外交通知。地图格可用性、特殊物品筛选与外交消息边界使用夹具；不执行完整 SpawnSetup 或游戏内寻路。具体状态见教程计划。

训练船通过西侧对接口硬对接移民船前段东侧气闸，赠船同样硬对接船厂东侧接口。船体先在独立空图生成，再走正常搬图与通道收尾；教练机接口与普通气闸门（赠船与船厂的相向接口）间距四格，中间三格只铺对接通道，不铺基架。船厂西侧接口留给玩家软对接。离线回归读取同源布局，检查基架不重叠／不相邻，并用实际通道探测器核对连接面；移民船一侧没有对接口，由教练机单侧延伸通道。

移民船前段使用原版 OrbitalPlatform 作为平台地形，不写 Substructure；六座船用休眠舱、旧式主梁和已运行反应堆来自原版 Def／Scribe 契约。角色、原版通讯和远离殖民地的生成规则见[当前开局](../../doc/教程开局实施计划.md#11-受损移民船与星界边境2026-11-04)。


2026-11-04 教练机改为四台纵向 `LargeThruster` 加两台尾部侧向 `SmallThruster`，氧气泵让开主推脚印；示意图箭头表示推力方向。具体位置与理论课契约见[计划第 12 节](../../doc/教程开局实施计划.md#12-理论课与教练机布局2026-11-04)。推进器排气净空从 Def 读取并与原版 `GetExclusionZone` 对账。新增侧移回归在局部质心 x=12.5～13.5、z=7～8 的九点范围内用真实分配器验证前后主推抵消尾部 RCS 力矩；保持上述质量、惯量和健康设备假设，不替代完整运行时质量测量与飞行手感验收。

试验场使用 `range.states` 的导出原型，由 `combat_range_layout.py` 校正当前枪体、弹仓与护盾状态。单独生成用 `python scripts/TutorialContent/build.py --only WeaponRange ShieldTarget`，离线检查给上述 `TutorialRegression/run.ps1` 添加 `-RangeOnly`。配置、机制与验收边界只维护在[试验场计划](../../doc/教程开局实施计划.md#13-可选武器与护盾试验场2026-11-04)。

2026-11-06 赠船新增船尾月池温室和水培，靶舰改为六层钢墙；现行布局与验证边界见[本批记录](../../doc/教程开局实施计划.md#135-文案回填与月池温室2026-11-06)。

残骸布局、实际维生容量与验收边界见[残骸重设计](../../doc/教程开局实施计划.md#111-残骸布局与维生重设计2026-11-06)。`ArrivalChecks.cs` 直接调用原版暖气、房间散热与氧气泵，在预组装的封闭房间拓扑和持续供电假设下验证一天；不模拟开门、人物任务或完整 SpawnSetup。

2026-11-06 试验场改为 10 格大型轨道炮，三道守卫圆盾都覆盖装甲前方；布局、原型变更与验证见[长炮对照](../../doc/教程开局实施计划.md#136-长炮标定与三层守卫对照2026-11-06)。

2026-11-07 试验场统一为 10×3 轨道炮，使用同规格装弹机加九段加速器；现行配置见[教程 §13.7](../../doc/教程开局实施计划.md#137-统一三格宽轨道炮2026-11-07)。
