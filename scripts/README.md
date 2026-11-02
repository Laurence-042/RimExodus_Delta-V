# 开发脚本索引

这些脚本不随游戏执行，也不影响游戏性能。标准构建入口是根目录的 `just build`（1.7）及 `just build 1.6`；共同地图能力的 1.7 回归走 `just test-ref`。保留 1.6 支持期间，不能仅因脚本引用 Unity 或 `Assembly-CSharp.dll` 就认定无用。

| 入口 | 用途与边界 |
| --- | --- |
| `TopologyBenchmark/run.ps1` | 250×250 基架连通性离线对拍与性能比较：原样 TopologyLab、HLT 分层 Euler-tour tree、全量 BFS；[方法与复现](TopologyBenchmark/README.md)、[实测结论](TopologyBenchmark/RESULTS.md)，不代表游戏 TPS |
| `FoundationTopologyRegression/run.ps1` | 直接编译生产拓扑／归属计数／地图服务，与独立 BFS 对拍，含 250×250 场景和缓存生命周期；[接入回归说明](FoundationTopologyRegression/README.md) |
| `WeaponTuningRegression/run.ps1` | 当前 1.7 武器数值／伤害回归，详见其 README |
| `RailgunRegression/run.ps1 -EnergyOnly` | 1.6 真实能量事务与货柜温控专项，加载安装版 ASF；包括双回收／混装电容、折后费用及室外温控，不启动游戏 |
| `preview_equipment_cost_art.py` | 只读设备 Def 与三张新贴图，输出四向、实际格尺寸及宿主槽位叠画预览，不改正式贴图 |
| `CombatStrategyRegression/` | 使用最小占位类型的策略、曲线与文件校验；不替代游戏运行时验证 |
| `NpcRetreatRegression/run.ps1` | 1.6 的战斗、驾驶、结构和生命周期专项；已接 REF，名称不代表只测撤退；`-SeparationOnly` 验证实际 Mod DLL 的归属、增量断裂检查与出力分离门禁；`-TractorOnly` 验证自动跨船独占与过滤、手动叠力、残骸分离请求、点防伤害分配及坐标／存读档 |
| `MpcRegression`、`EngineeringRegression`、`RailgunRegression` | 1.6 控制器、工程与碰撞链回归；轨道炮数值现状以武器基线及 1.7 回归为准 |
| `PrefabRegression`、`MoonPoolRoofRegression`、`SubstructureDamageRegression` | 1.6 的 prefab、屋顶及基架破坏检查；公共 prefab 实现已迁入 REF |
| `verify_docking_contract.ps1` | 1.6 对接原版签名、XML 字段与翻译核对，不适用于 1.7 API |
| `CombatManeuverSimulation/` | 仅保留历史研究报告与结果；实验驱动已退役，不能把旧数值当成现行武器标定 |
| `align_turret_layer.py`、`build_*.py`、`preview_*.py` | 贴图对齐、图标生成与布局复现；部分预览脚本会调用图标生成器并写入正式图标，使用前先阅读脚本 |

2026-10-31 按用户选定范围删除 `CombatManeuverSimulation` 的 10 个历史实验文件（5 个 C# 探针、4 个 Python 实验／输入脚本及 `run_mpc.ps1`），保留该目录的 README 和全部结果。它们仅在旧实验驱动内部相互依赖，构建及上述有效回归不引用这些文件。删除前已将当前文件（含 `TacticProbe.cs` 未提交的轨迹记录改动）备份至本地忽略目录 `Source/obj/ScriptCleanup-20261031-042309/historical-experiments.zip`，旁附 SHA256 清单并逐项核验；已提交版本亦可从 Git 历史找回。Python 缓存已清理并忽略。1.6 驱动未在本轮逐个重跑，保留不代表已通过 1.7 运行时验证。

`build_shield_prism_mask.py`：按固定原图生成护盾北侧与中央棱镜灰度图、红通道 `_m` 遮罩与两种尺寸的四色预览；只修改棱镜像素，验证 alpha 与遮罩外像素不变。素材记录见[美术资源施工表](../doc/美术资源施工表.md#护盾棱镜状态色2026-11-01)。
