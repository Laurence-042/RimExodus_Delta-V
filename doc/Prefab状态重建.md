# Prefab 状态重建

通用地图 prefab 的建筑和物品按实例保存原版 `Thing.ExposeData` 数据。地形继续使用原版 `PrefabUtility` 的布局和旋转语义；基架、屋顶仍在侧车中。导出范围为掩码内锚点属于 Building 或 Item 的实体，容器内容物由宿主的原版存档数据保存。

重建顺序：基架及归属 → 原版地形 → 整批 Scribe 读取与内部引用解析 → 为所有新实体重新分配 Thing ID → 按 prefab 坐标旋转并落位 → 屋顶 → 原版电网注册结算与设施重链 → 标记展开。落位派系与 ownerId 由调用方决定；驾驶台不继承源船 ID，对接口已导出的通道格按新锚与朝向换算。炮塔的地图帧瞄准角随 prefab 旋转。

基架写入后复核 `FoundationAt`；建筑和物品无法落位时抛出含条目、Def、位置与旋转的异常，原始异常保留为内部堆栈，不把缺失建筑算作成功重建。调用方负责处置已修改的目标图。双船测试场景在重建前解析两船 prefab；仅自动选择加载失败时允许回退程序布局，明确配置的文件加载失败则保留旧场景并中止（设置用法见 README「测试」）。落位异常则销毁本次测试图并中止，不能在已有 prefab 地板和建筑上再次生成程序船。

失败测试图的销毁与容器注销经 `UniverseManager.QueueFrameAction` 延后到帧边界，等待原版 `Map.FinalizeInit` 已排队的电网与地图绘制初始化结束；不能在生成长事件内同步销图，否则原版 `MapDrawer.Dispose` 会遇到尚未初始化的 sections，后续绘制回调还会访问已释放的网格。

聚焦图原地替换由 `SpaceLifecycleService.ReplaceFocusedMapFromPrefab` 总装：调试菜单选定文件与派系后显式传入，派系只确定一次，新容器与 `MapPrefab.Spawn` 共用该值（null 表示无派系）。先解析所选文件，再经 `ShipPocketMapFactory` 建空图、注册新容器和独立结构归属，调用同一 `MapPrefab.Spawn`。原图保留到放置成功；长事件结束后的帧边界经 `SetFocusedWithCamera` 交接，再走生命周期删图与旧容器注销。新容器继承原 `SpacePose`，旧图局部尺寸与新图局部尺寸的差异由 `SpaceTransform` 换算相机位置。失败的新图也在帧边界清理；不回退程序布局。用户入口与适用范围见 README「通用地图 prefab」。

Scribe 对同一份 prefab 内的引用在整批读取时解析。对外部 Pawn、任务或旧地图 Thing 的引用无法在新副本成立，解析后为空。第三方 Comp 只要按 RimWorld `ExposeData` 持久化且不依赖旧地图或全局注册表，即可一同恢复；无法保存或读取的实例会给出警告并按 Def 默认状态建立。跨地图原始坐标、原始 Thing ID 数值等自定义字段不具有通用语义，具体模组须适配。

身份重分配以本批 Scribe 的 `crossReferencingExposables` 为完整实例清单，在 `FinalizeLoading` 清空清单前取快照，待原 ID 的内部引用与初始化完成后统一执行。所有深度读取的 Thing（包括不属于 `IThingHolder` 的炮塔内部 gun）都重新编号、清理旧任务标签并应用目标派系；随后沿本批 `VerbTracker` 使用原版 `Verb.CalculateUniqueLoadID` 更新动作身份，保留原 Verb 实例、状态与已解析引用。不能只枚举地图顶层或 ThingOwner 内容物，也不能只改枪械 ID 而留下旧 Verb ID。默认状态回退的 `ThingMaker` 已负责新建身份，不再二次编号。

离线回归入口为 `scripts/PrefabRegression/run.ps1 -GameRoot <游戏目录> -HarmonyPath <Lib.Harmony 的 net8.0/0Harmony.dll>`，直接调用构建 DLL 与安装版原版 Scribe，验证同一炮塔模板重复实例化、内部 Thing/Verb 引用、动作状态与普通保存/重载。仅隔离 Unity 日志显示和 Def 构造的素材加载；不代替游戏内放置验收。

建筑存储格中的物品仍是地图上的独立 Item。不可通行的 `Building_Storage`（例如导弹仓）会让原版 `CanSpawnAt` 的行走检查失败；重建只在原版 SlotGroup 已登记且不会擦除储物建筑的格子放宽此检查。储存组本身是旧地图对象，不复制其引用；导出时把该建筑实际生效的筛选和优先级写为自身设置。

隐藏电线和武器槽位挂件也可与不可通行建筑同格。重建只对这两类已保存建筑放宽 `CanSpawnAt` 的行走检查，同时保留地形、槽位及原版擦除规则检查；整批落位后调用原版电网更新，使电线连通和设备供电在地图投入使用前完成。

文件格式为 `MapPrefab` formatVersion 2；旧导出文件须重新导出。这个版本号仅属于 prefab 文件，不修改 Mod 版本。正常游戏存读档与远距离 POI 快照恢复不调用 prefab 标记展开。
