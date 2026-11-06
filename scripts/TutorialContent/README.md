# 教程布局制作源

`build.py` 是生成入口；移民船残骸以用户原始导出的逐字节副本 `arrival_intact.prefab` 为基准，删改逻辑编辑 `arrival_layout.py`，环形船厂与小货船编辑 `shipyard_layout.py`，武器平台与靶舰编辑 `combat_range_layout.py`。生成器读取安装版与项目 Def 的尺寸和继承链，解析占地／操作格后由同一份记录输出：

- `1.6/Prefabs/{ArrivalStation,TrainingShip,Salvage,Shipyard,PrototypeShip,WeaponRange,ShieldTarget}.xml`：七份正式可打包 prefab，教程、调试导入与星区池共用同一目录；运行时不调用布局脚本。
- 同名 `.png`：与 XML 对应的布局图。
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

残骸保留用户原船的舱室与原版飞船连接关系，在原导出上添加后部爆炸断口、拆除搜刮设备并保留原床铺／家具／厨房，把队长舱与保留唯一一套真空服的服装架移入货仓。正式资源与本地 `ArrivalStationWreck.xml` 使用同一生成结果；不存在另一份八舱布局。回归覆盖六舱存取、货仓队长绑定、唯一电网、教练机泊口与货仓温控。

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
