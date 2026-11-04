# 教程布局制作源

`build.py` 是唯一布局编辑入口，读取安装版与项目 Def 的尺寸和继承链，解析占地／操作格后由同一份记录输出：

- `1.6/TutorialPrefabs/{ArrivalStation,TrainingShip,Salvage,Shipyard,PrototypeShip}.xml`：五份正式可打包 prefab。
- 同名 `.png`：与 XML 对应的布局图。
- `Source/obj/TutorialContent/*.records.json`：原版离线检查输入，不入库。

`prototypes.states` 是从本地 Demo／Demo_0 导出中摘取并清除源图、派系、控制台身份、电网引用后的实例状态。空休眠舱按原版 `Building_Casket`／`ThingOwner` 存档契约补齐，并经真实 Scribe 加载验证。不要删掉内部容器的具体 Class 或空列表，也不要把完整 Scribe 替换成普通 PrefabThingData。

```powershell
python scripts/TutorialContent/build.py
just build 1.6
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/TutorialRegression/run.ps1 -HarmonyPath C:/Users/Laure/.nuget/packages/lib.harmony/2.4.1/lib/net8.0/0Harmony.dll
just build
```

可用 `--data` 指定其他安装目录的 Data。构建依赖 Python Pillow；它只用于制作期示意图，不是 Mod 运行前置。

布局朝北，编辑位置使用旋转后占地西南格，生成器反求原版 root。Def 继承后的地形承载检查及原版四向／操作格／电网检查使用 `TutorialRegression` 的 1.6 程序集夹具；不运行 Unity 或启动游戏。当前包含移民船前段和阶段 B 操作课程首版；训练船派系／公共属性在生成角色绑定时设置，赠船独立归属。货架含完整筛选存档，回收钢铁为真实 75 件。完整课程继续按[计划](../../doc/教程开局实施计划.md)施工。

教程回归同时覆盖六方向实际推力分配（假设质量 1000、惯量 30000、推进器均可用）、课程阶段及操作事实存档、真实主台公共属性存取、公共船控制／建造／占领权限、原版货架候选搜索和外交通知。地图格可用性、特殊物品筛选与外交消息边界使用夹具；不执行完整 SpawnSetup 或游戏内寻路。具体状态见教程计划。

训练船通过西侧对接口硬对接移民船前段东侧气闸，赠船同样硬对接船厂东侧接口。船体先在独立空图生成，再走正常搬图与通道收尾；教练机接口与普通气闸门（赠船与船厂的相向接口）间距四格，中间三格只铺对接通道，不铺基架。船厂西侧接口留给玩家软对接。离线回归读取同源布局，检查基架不重叠／不相邻，并用实际通道探测器核对连接面；移民船一侧没有对接口，由教练机单侧延伸通道。

移民船前段使用原版 OrbitalPlatform 作为平台地形，不写 Substructure；八座船用休眠舱、旧式主梁和已运行反应堆来自原版 Def／Scribe 契约。角色、原版通讯和远离殖民地的生成规则见[当前开局](../../doc/教程开局实施计划.md#11-受损移民船与星界边境2026-11-04)。
