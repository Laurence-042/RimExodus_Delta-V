# 开发脚本索引

`NpcRetreatRegression -HullContactOnly` 新增 `CollisionTargetChecks.cs`：真实 Map／无图导弹的坐标、法线、射线及实际受伤契约；舰体撞静止导弹、相向／旋转接触、工作表顺序、战斗部单次后效、销毁门禁、来源损毁后的结构失效与后续弹体续算。`-ProjectileFullOnly` 的 `MaplessMissileChecks.cs` 同时核对实体命中点与视觉快照、同 tick 击毁跳过及下一 tick 爆炸伤害。`CollisionTargetChecks.cs` 另检查生产 DLL 接线和旧入口删除，`FireControlContractChecks.cs` 对照五束散射在地图／实体上的预测和真实命中；`MaplessMissileChecks.cs` 验证延迟伤害只执行一次、换表示后作废及击毁／移除后的剩余记录释放。爆炸仅观察 Unity 场景端点的参数与归因，直接伤害执行原版 `TakeDamage`；不替代游戏内新档验收。契约见[受击结构适配](../doc/太空战架构.md#受击结构适配2026-11-19)。

`NpcRetreatRegression/run.ps1 -HullContactOnly -GameRoot <1.6 游戏目录> -HarmonyPath <Lib.Harmony net8.0 DLL>`：实际舰体对检测、一次性弹体后效请求及统一目标链，覆盖开关组合／反向来撞、双方同量损伤、旋转随动、增量破损／修复、战斗 MPC 避让和其他驾驶模式的门禁。场景仅隔离 Unity 视听／地图通知，不替代游戏内战损拆图、存读档或侵入落地；见[舰体接触](../doc/太空战架构.md#舰体主动撞击与碰撞规避2026-11-18)。

`NpcRetreatRegression/run.ps1 -HullCollisionPerformance -GameRoot <1.6 游戏目录> -HarmonyPath <Lib.Harmony net8.0 DLL>` 测量现行单层碰撞的 2／8／24 艘正撞、密集破口、凹形三体和混合实弹。`DELTAV_HULL_FILTER` 可按场景名过滤，`DELTAV_HULL_SAMPLES` 控制每场景的 tick 数。单独设 `DELTAV_HULL_COUNT=1` 可查看逐 tick 毁伤、位移和候选工作量。


`NpcRetreatRegression/run.ps1 -GameRoot <1.6 游戏目录> -HarmonyPath <Lib.Harmony net8.0 DLL>` 默认全套包括 `NonCombatCruiseChecks.cs`：无敌舰快照早退与首次／零推力基线、敌舰进出、展示读取不检查硬件、实执行重新检查动力，以及批内距离复用／当前位姿／失效和恢复门槛。2026-11-18 全套通过 2117 项，2026-11-19 当前生产 DLL 提交前复核通过 2456 项；这是 1.6 DLL 的离线场景检查，两版编译及游戏内验收分别记录。

隔离微基准在已编译的同一驱动上设置环境变量 `DELTAV_CRUISE_BENCHMARK=1`，然后运行 `dotnet Source/obj/NpcRetreatRegression/NpcRetreatRegression.dll <1.6 Managed目录>`；正式全套不设该变量。旧 DLL 与新 DLL 分目录运行，各自预热三轮、串行计时三轮，比较时统一关闭分层编译（`DOTNET_TieredCompilation=0`）。原始输出在忽略目录 `Source/obj/NonCombatCruiseBaseline/`，场景、结果与限制集中于[轮询审计](../doc/轮询审计.md#非战斗巡航前三项优化2026-11-18)。

`NpcRetreatRegression/run.ps1 -CombatManeuverOnly -GameRoot <1.6 游戏目录> -HarmonyPath <Lib.Harmony net8.0 DLL>`：生产持续意图／飞行参考／MPC／真实分配与积分的三分钟闭环，覆盖周期刹车、无收益反向、强轴／弱转向、显式朝向、途中动力下降、极区参考导数、旋转预测模型，以及 AI 日志逐 tick 对拍和设置 Scribe。`MOTION_PERF` 输出规划、QP 和驾驶总耗时；旧 DLL 基线与最新结果集中在[持续机动重构记录](../doc/持续战斗机动重构计划.md)。无武器闭环和独立火力约束检查不能替代实弹交火验收。

有利射位重构使用独立专项，统一传 `-GameRoot <1.6 游戏目录> -HarmonyPath <Lib.Harmony net8.0 DLL>`：

- `NpcRetreatRegression/run.ps1 -AutoVolleyOnly`：高速同向漂移下的动能炮与激光炮，核对逐 tick 瞄准次数、转速、冷却后的发射、对准拒绝与火控耗时。
- `NpcRetreatRegression/run.ps1 -EngagementRegression`：冻结几何的三分钟占位、共用策略配置、风险曲线、单发相对碰撞速度、暂时冷却和供电／弹药门禁，以及真实混装武器／防护改变评分与规划方位。该运动专项不发射实弹。
- `NpcRetreatRegression/run.ps1 -EngagementBattle`：实际火控、发射、激光碰撞和原版建筑／基架毁伤下，固定方位与自动策略的成对比较；另含 2048 次原生基架拦截／破坏对照。固定目标与给定驱动，不含拓扑拆图、船员或完整电网 tick。
- `RailgunRegression/run.ps1 -EngagementForecast`：只读毁伤对照生产碰撞与原版扣血，覆盖不同防护、单发脉冲供能、多格脚印及 2048 发分层掩体统计。
- 编译 NPC 驱动后，直接运行 `dotnet Source/obj/NpcRetreatRegression/NpcRetreatRegression.dll <1.6 Managed目录> --engagement-fleet-regression`，串行测 1／4／12／24 舰的完整驾驶决策；可追加 `--engagement-profile-window` 获取四舰阶段剖析。有限值检查不代表性能通过，门槛比较仍需三轮串行中位数。

新结果写到忽略目录 `Source/obj/EngagementForecast`。原 `-EngagementBaseline` 与 `--engagement-fleet-baseline` 只用于保存的旧 DLL，不能拿新实现覆盖基线；冻结参数、已通过与未过关项目统一记录在[实施计划](../doc/有利射位与交战收益规划.md#工作树实施检查点2026-11-08尚未交付)。

`GuardShieldRegression/GuardShieldRegression.csproj` 直接编译生产护盾贴图裁剪算法，对拍随机覆盖、UV 范围与退化几何，并测量裁剪重建耗时；运行和验证边界见[守卫盾裁剪检查](GuardShieldRegression/README.md)。

`TutorialRegression/run.ps1` 默认检查教程使用的 prefab、教学流程及委托接线。`-SectorOnly` 独立检查星区资源、12 个叶子类型的默认池、实装舰船和站点几何、电网、维生及泊位。用 `-Version 1.6` 或 `-Version 1.7` 选择环境，并通过 `-HarmonyPath` 传入对应的 Lib.Harmony .NET 运行时 DLL。

`TutorialRegression/run.ps1 -RangeOnly` 只读取试验平台与靶舰 prefab，检查本地占地、操作格、电网、轨道链、自瞄槽位、嵌套枪械、原版数字 ID、武器／终端 Scribe、护盾预设、射界和可选课程状态。脚本读取 `Source/obj/TutorialContent/` 下的同源布局记录。正式布局由 `TutorialContent/build.py --only WeaponRange ShieldTarget` 生成。

`PrefabRegression/run.ps1` 默认执行原有 Scribe 回归。用 `-PrefabPath` 传一个或多个文件或目录即可检查指定 prefab，目录内递归读取 XML。默认验证 REF 环境数据及原版数字 ID。可独立组合以下要求。

| 参数 | 检查 |
| --- | --- |
| `-RequireEnvironment` | 至少存在一组环境采样 |
| `-RequireRoofCoverage` | 采样格与屋顶及月池格集合一致 |
| `-ExpectedTemperature 21` | 已有的每组采样温度等于 21°C |
| `-ExpectedVacuum 0` | 已有的每组采样真空度等于 0 |

```powershell
$game = 'D:/SteamLibrary/steamapps/common/RimWorld_1.6'
$harmony = '<Lib.Harmony/lib/net8.0/0Harmony.dll>'
& scripts/PrefabRegression/run.ps1 -GameRoot $game -HarmonyPath $harmony -PrefabPath Common/Prefabs/Special
& scripts/PrefabRegression/run.ps1 -GameRoot $game -HarmonyPath $harmony -PrefabPath Common/Prefabs/Tutorial/PrototypeShip.xml -RequireEnvironment -RequireRoofCoverage -ExpectedTemperature 21 -ExpectedVacuum 0
```

教程内容由 [`TutorialContent/build.py`](TutorialContent/README.md) 生成正式 prefab 与同源布局图；`TutorialRegression/run.ps1` 使用 1.6 实际程序集验证占地／四向、地形承载、单侧气闸通道、操作格、电网、课程存档与信号边界、八座船用休眠舱／货架实例化、原版通讯接线、出生图重力范围与位姿存取、六推进器侧移力矩补偿／原版排气区、理论课阶段与推力证据、公共设施权限／原版搬运选址／关系扣减及资源路径，不替代新档游戏内验收。

这些脚本不随游戏执行，也不影响游戏性能。标准构建入口是根目录的 `just build`（1.7）及 `just build 1.6`；共同地图能力的 1.7 回归走 `just test-ref`。保留 1.6 支持期间，不能仅因脚本引用 Unity 或 `Assembly-CSharp.dll` 就认定无用。

| 入口 | 用途与边界 |
| --- | --- |
| `TopologyBenchmark/run.ps1` | 250×250 基架连通性离线对拍与性能比较：原样 TopologyLab、HLT 分层 Euler-tour tree、全量 BFS；[方法与复现](TopologyBenchmark/README.md)、[实测结论](TopologyBenchmark/RESULTS.md)，不代表游戏 TPS |
| `FoundationTopologyRegression/run.ps1` | 直接编译生产拓扑／归属计数／地图服务，与独立 BFS 对拍，含 250×250 场景和缓存生命周期；[接入回归说明](FoundationTopologyRegression/README.md) |
| `WeaponTuningRegression/run.ps1` | 当前 1.7 武器数值／伤害回归；`-OutputAudit` 扫描全部调校组合及现行盾；`-ShieldDurationAudit` 追加候选盾容／恢复、多炮齐射与间歇交火时长对照，均调用生产组件、不改游戏数值，详见其 README |
| `RailgunRegression/run.ps1 -RailgunOnly` | 1.6 轨道炮开火、冷却存读档、共享电网扣费、欠费跳段、回收与电容混装、长炮标定；装弹机与加速段的四向占格和侧槽由两版 `TutorialRegression -RangeOnly` 验证 |
| `RailgunRegression/run.ps1 -EquipmentOnly` | 1.6 电池隔离与短路、反应堆、货柜温控和生命周期、ASF 图形与库存、终端交易。交易夹具提供船域与信标覆盖，使用生产选船、原版信标枚举和成交复核，覆盖货源断开、主台或派系变化及库存离开信标覆盖 |
| `RailgunRegression/run.ps1 -EnergyOnly` | 兼容入口，仍按顺序运行轨道炮组和设备组，并提示改用各自入口 |
| `CombatStrategyRegression/` | 使用最小占位类型的策略、曲线与文件校验；不替代游戏运行时验证 |
| `NpcRetreatRegression/run.ps1` | 1.6 的战斗、驾驶、结构和生命周期专项；已接 REF，名称不代表只测撤退；`-PlayerFlightOnly` 验证驾驶权限、显式驾驶和原版按键抑制边界、导航参考与任务替换（2026-11-02 共 72 项通过；模拟焦点／按键绑定环境，不启动游戏）；`-SeparationOnly` 验证实际 Mod DLL 的归属、增量断裂检查与出力分离门禁；`-TractorOnly` 验证自动跨船独占与过滤、手动叠力、残骸分离请求、点防伤害分配及坐标／存读档 |
| `MpcRegression`、`EngineeringRegression`、`RailgunRegression` | 1.6 控制器、工程与碰撞链回归；轨道炮数值现状以武器基线及 1.7 回归为准 |
| `RailgunRegression/run.ps1` 默认入口 | 守卫归属使用原版建筑登记和实际基架拓扑。以扣容前后的差值检查同图隔离、断裂与接回、失去与替换主台、归属转移及发生器移除 |
| `PrefabRegression`、`MoonPoolRoofRegression`、`SubstructureDamageRegression` | 1.6 的 prefab、屋顶及基架破坏检查；公共 prefab 实现已迁入 REF |
| `PrefabMarkerRegression/run.ps1` | 直接引用生产 DLL，验证精确／批量 SpaceObject 标记的密度、配置上限、`clusterChild` 产生与后代继承、确定性、带权分布、面积均匀环带、最小间距、重掷种子及单一朝向来源；不启动游戏，也不替代实际 prefab 导出、半透明预览、懒物化和归档恢复验收 |
| `verify_docking_contract.ps1` | 1.6 对接原版签名、XML 字段与翻译核对，不适用于 1.7 API |
| `CombatManeuverSimulation/` | 仅保留历史研究报告与结果；实验驱动已退役，不能把旧数值当成现行武器标定 |
| `align_turret_layer.py` | 按指定转轴整数平移到画布中心，校验源像素完整 |
| `build_weapon_icons.py` | 从正式分层贴图合成舰炮、护盾、轨道炮、导弹蓝图、工程炮塔与牵引器的生产图标；`--rail-loader` 只生成装弹机图标 |
| `build_weapon_heat_masks.py`、`build_shield_prism_mask.py`、`build_cargo_masks.py` | 生成正式蒙版并检查像素与通道 |
| `build_docking_icon.py`、`generate_vacuum_barrier_atlas.py` | 生成正式对接口图标和真空屏障连接图集 |

2026-10-31 按用户选定范围删除 `CombatManeuverSimulation` 的 10 个历史实验文件（5 个 C# 探针、4 个 Python 实验／输入脚本及 `run_mpc.ps1`），保留该目录的 README 和全部结果。它们仅在旧实验驱动内部相互依赖，构建及上述有效回归不引用这些文件。删除前已将当前文件（含 `TacticProbe.cs` 未提交的轨迹记录改动）备份至本地忽略目录 `Source/obj/ScriptCleanup-20261031-042309/historical-experiments.zip`，旁附 SHA256 清单并逐项核验；已提交版本亦可从 Git 历史找回。Python 缓存已清理并忽略。1.6 驱动未在本轮逐个重跑，保留不代表已通过 1.7 运行时验证。

`build_shield_prism_mask.py`：按固定原图生成护盾北侧与中央棱镜灰度图、红通道 `_m` 遮罩；只修改棱镜像素，验证 alpha 与遮罩外像素不变。正式蒙版和材质规则见[武器与护盾绘制](../doc/太空战架构.md#combat-art)。

`doc/art` 保存原稿、各独立版本的代表美术和生成 prompt。合成图承担迭代记录时也按美术归档，文件名中的 `preview` 不决定去留。按版本查阅[美术归档索引](../doc/美术资源施工表.md#art-iterations)。正式生成器只写生产资源。

小型护盾图标的整批重建仍有已知差异，重生成结果与现行 `ShieldGeneratorSmall_MenuIcon.png` 不同，需独立核对。

### EVA 回归

`NpcRetreatRegression/run.ps1 -EvaOnly -GameRoot <1.6 游戏目录> -HarmonyPath <Lib.Harmony net8.0 DLL>` 使用构建后的生产 DLL，覆盖虚拟挂载身份／Scribe／分组隔离、两态火控限制、旋转相机投影、零角速度与接触点速度、0/1/12 外延和多格旋转边界、原版拆除材料链、焊接后效、货舱部分接收／拒收回滚／帧边界取消。仅替代 Unity 场景端点，接收、容量、请求生命周期及后效使用生产实现。`-TractorOnly` 保留月池与点防专项；`EngineeringRegression` 增加 EVA 实际 XML 字段、枚举和继承验证。完整游戏起降、归档恢复及手感由新档验收覆盖。

2026-11-04 历史问题专项：`NpcRetreatRegression -EvaOnly` 增加起飞能力／原版门禁转接、同格钢铁捕获差异、激光脉冲复用／相对端点／反射段／重复聚焦／停火和销图清理；`SubstructureDamageRegression` 增加切割十字清顶及建筑保护；`EngineeringRegression` 校验切割器基架必中。`RailgunRegression` 的持续束耗能夹具改用当前 XML 逐 tick 费用，避免沿用旧数值。用户本轮确认历史问题修复效果正常并授权提交。

本轮检查结果：1.6／1.7 构建均零警告、零错误；EVA 77、玩家驾驶 72、牵引 165、对接 142、基架／屋顶 149、弹道／光束 200、工程 8560 项离线检查通过。本轮修改文件的 whitespace 检查和两种语言新增键检查通过；未启动或操作游戏。


星区地图专项：`NpcRetreatRegression/run.ps1 -GlobeOnly -GameRoot <1.6 游戏目录> -HarmonyPath <Lib.Harmony net8.0 DLL>` 验证构建后的生产投影、固定距离缩放、同屏与标签边界、球背／极点、真实剪影尺寸、聚合、独立于缩放的列表、跨容器目标与归档摘要、课程导航角色、书签 Scribe、聚焦坐标不变量及原版窗口输入阻挡。`GlobeFilterChecks.cs` 增加多维组合、任务豁免、草稿隔离／应用、偏好 Scribe、弹体仅投影／散落点、池租约资源复用与换弹种、激光／侵入艇去重和条目回收；2026-11-07 共 332 项通过。Unity 原生旋转在离线剪影夹具中由固定 45° 渲染姿态替代，投影和范围求解仍走生产代码；真实 GL 偏移、字体、窗口裁剪及交互手感由游戏内验收覆盖。

2026-11-08 结构失效优化专项：`NpcRetreatRegression/StructureVersionChecks.cs` 随全套回归验证 Map 隔离、事件合并、转图／池复用；选位专项额外检查连续结构／动力通知不提前搜索、不延后期限和不消耗随机流。多舰夹具增加完成工作量、版本发布、分配量与实际 GC 暂停数据，原全局失效时点及性能门槛保留。阶段范围和未完成项以[优化实施检查点](../doc/有利射位与交战收益规划.md#优化实施检查点map-级失效与事件抢占2026-11-08)为准。

武器两档功耗回归位于 `NpcRetreatRegression/WeaponExecutionChecks.cs`，检查可配置待机、原战时功耗、组事件、原版功耗重设与稳定 10000 tick 的同步次数；`RailgunRegression/BatteryIsolationChecks.cs` 检查真实充电额度和余电分配、未武装颜色及电容策略。沿用现有运行入口。
