# 开发脚本索引

`TutorialRegression/run.ps1 -RangeOnly` 检查武器试验平台／靶舰的原版占地、操作格、电网和屋顶支撑、三段轨道链与自瞄槽位、真实武器／终端 Scribe、全部内置 prefab 的原版数字 ID 注册、船厂／试验平台炮塔与嵌套枪械／Verb 的完整重复加载、六种护盾预设不补库存、射界／设置门禁与可直接跳过的课程阶段。仅隔离 Unity 图形静态初始化及夹具的归属登记／设施存在边界；不代替游戏交互和实弹验收。布局编辑 `TutorialContent/combat_range_layout.py`，通过共用 `build.py --only WeaponRange ShieldTarget` 生成。

教程内容由 [`TutorialContent/build.py`](TutorialContent/README.md) 生成正式 prefab 与同源布局图；`TutorialRegression/run.ps1` 使用 1.6 实际程序集验证占地／四向、地形承载、单侧气闸通道、操作格、电网、课程存档与信号边界、八座船用休眠舱／货架实例化、原版通讯接线、出生图重力范围与位姿存取、六推进器侧移力矩补偿／原版排气区、理论课阶段与推力证据、公共设施权限／原版搬运选址／关系扣减及资源路径，不替代新档游戏内验收。

这些脚本不随游戏执行，也不影响游戏性能。标准构建入口是根目录的 `just build`（1.7）及 `just build 1.6`；共同地图能力的 1.7 回归走 `just test-ref`。保留 1.6 支持期间，不能仅因脚本引用 Unity 或 `Assembly-CSharp.dll` 就认定无用。

| 入口 | 用途与边界 |
| --- | --- |
| `TopologyBenchmark/run.ps1` | 250×250 基架连通性离线对拍与性能比较：原样 TopologyLab、HLT 分层 Euler-tour tree、全量 BFS；[方法与复现](TopologyBenchmark/README.md)、[实测结论](TopologyBenchmark/RESULTS.md)，不代表游戏 TPS |
| `FoundationTopologyRegression/run.ps1` | 直接编译生产拓扑／归属计数／地图服务，与独立 BFS 对拍，含 250×250 场景和缓存生命周期；[接入回归说明](FoundationTopologyRegression/README.md) |
| `WeaponTuningRegression/run.ps1` | 当前 1.7 武器数值／伤害回归；`-OutputAudit` 扫描全部调校组合及现行盾；`-ShieldDurationAudit` 追加候选盾容／恢复、多炮齐射与间歇交火时长对照，均调用生产组件、不改游戏数值，详见其 README |
| `RailgunRegression/run.ps1 -EnergyOnly` | 1.6 真实能量事务与货柜／终端专项，加载安装版 ASF；包括双回收／混装电容、折后费用、室外温控、原版覆盖规则和终端 God Mode 建造与配置／补货派系及调试开关／地图货源／原版交易信标与成交复核，以及原版战利品箱封存／骇入开箱、两种形态的库存与配置往返及失败回滚，不启动游戏 |
| `preview_equipment_cost_art.py` | 只读设备 Def 与三张新贴图，输出四向、实际格尺寸及宿主槽位叠画预览，不改正式贴图 |
| `CombatStrategyRegression/` | 使用最小占位类型的策略、曲线与文件校验；不替代游戏运行时验证 |
| `NpcRetreatRegression/run.ps1` | 1.6 的战斗、驾驶、结构和生命周期专项；已接 REF，名称不代表只测撤退；`-PlayerFlightOnly` 验证驾驶权限、显式驾驶和原版按键抑制边界、导航参考与任务替换（2026-11-02 共 72 项通过；模拟焦点／按键绑定环境，不启动游戏）；`-SeparationOnly` 验证实际 Mod DLL 的归属、增量断裂检查与出力分离门禁；`-TractorOnly` 验证自动跨船独占与过滤、手动叠力、残骸分离请求、点防伤害分配及坐标／存读档 |
| `MpcRegression`、`EngineeringRegression`、`RailgunRegression` | 1.6 控制器、工程与碰撞链回归；轨道炮数值现状以武器基线及 1.7 回归为准 |
| `PrefabRegression`、`MoonPoolRoofRegression`、`SubstructureDamageRegression` | 1.6 的 prefab、屋顶及基架破坏检查；公共 prefab 实现已迁入 REF |
| `verify_docking_contract.ps1` | 1.6 对接原版签名、XML 字段与翻译核对，不适用于 1.7 API |
| `CombatManeuverSimulation/` | 仅保留历史研究报告与结果；实验驱动已退役，不能把旧数值当成现行武器标定 |
| `align_turret_layer.py`、`build_*.py`、`preview_*.py` | 贴图对齐、图标生成与布局复现；部分预览脚本会调用图标生成器并写入正式图标，使用前先阅读脚本 |

2026-10-31 按用户选定范围删除 `CombatManeuverSimulation` 的 10 个历史实验文件（5 个 C# 探针、4 个 Python 实验／输入脚本及 `run_mpc.ps1`），保留该目录的 README 和全部结果。它们仅在旧实验驱动内部相互依赖，构建及上述有效回归不引用这些文件。删除前已将当前文件（含 `TacticProbe.cs` 未提交的轨迹记录改动）备份至本地忽略目录 `Source/obj/ScriptCleanup-20261031-042309/historical-experiments.zip`，旁附 SHA256 清单并逐项核验；已提交版本亦可从 Git 历史找回。Python 缓存已清理并忽略。1.6 驱动未在本轮逐个重跑，保留不代表已通过 1.7 运行时验证。

`build_shield_prism_mask.py`：按固定原图生成护盾北侧与中央棱镜灰度图、红通道 `_m` 遮罩与两种尺寸的四色预览；只修改棱镜像素，验证 alpha 与遮罩外像素不变。素材记录见[美术资源施工表](../doc/美术资源施工表.md#护盾棱镜状态色2026-11-01)。

### EVA 回归

`NpcRetreatRegression/run.ps1 -EvaOnly -GameRoot <1.6 游戏目录> -HarmonyPath <Lib.Harmony net8.0 DLL>` 使用构建后的生产 DLL，覆盖虚拟挂载身份／Scribe／分组隔离、两态火控限制、旋转相机投影、零角速度与接触点速度、0/1/12 外延和多格旋转边界、原版拆除材料链、焊接后效、货舱部分接收／拒收回滚／帧边界取消。仅替代 Unity 场景端点，接收、容量、请求生命周期及后效使用生产实现。`-TractorOnly` 保留月池与点防专项；`EngineeringRegression` 增加 EVA 实际 XML 字段、枚举和继承验证。完整游戏起降、归档恢复及手感由新档验收覆盖。

2026-11-04 历史问题专项：`NpcRetreatRegression -EvaOnly` 增加起飞能力／原版门禁转接、同格钢铁捕获差异、激光脉冲复用／相对端点／反射段／重复聚焦／停火和销图清理；`SubstructureDamageRegression` 增加切割十字清顶及建筑保护；`EngineeringRegression` 校验切割器基架必中。`RailgunRegression` 的持续束耗能夹具改用当前 XML 逐 tick 费用，避免沿用旧数值。用户本轮确认历史问题修复效果正常并授权提交。

本轮检查结果：1.6／1.7 构建均零警告、零错误；EVA 77、玩家驾驶 72、牵引 165、对接 142、基架／屋顶 149、弹道／光束 200、工程 8560 项离线检查通过。本轮修改文件的 whitespace 检查和两种语言新增键检查通过；未启动或操作游戏。


星区地图专项：`NpcRetreatRegression/run.ps1 -GlobeOnly -GameRoot <1.6 游戏目录> -HarmonyPath <Lib.Harmony net8.0 DLL>` 验证构建后的生产投影、固定距离缩放、同屏与标签边界、球背／极点、真实剪影尺寸、聚合、独立于缩放的列表、跨容器目标与归档摘要、课程导航角色、书签 Scribe、聚焦坐标不变量及原版窗口输入阻挡。Unity 原生旋转在离线剪影夹具中由固定 45° 渲染姿态替代，投影和范围求解仍走生产代码；真实 GL 偏移、字体、窗口裁剪及交互手感由游戏内验收覆盖。
