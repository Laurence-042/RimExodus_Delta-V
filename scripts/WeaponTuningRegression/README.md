# 舰炮调校离线回归

`./scripts/WeaponTuningRegression/run.ps1` 使用仓库 SDK 10、安装的 RimWorld 1.7 程序集、刚构建的 `1.7/Assemblies/DeltaV.dll` 与真实武器 XML；不会启动游戏。可用 `-GameRoot` 指定游戏目录、`-DeltaVAssembly` 指定独立构建输出。产物与 Scribe 测试文件仅在 `Source/obj/WeaponTuningRegression`。

已通过 52 项：装药／质量／初速关系、质量与速度共同毁伤、完整付费／只付基础费／恰好停弹／被动盾、破片总反向能量、速射过热完整锁、重炮低档机械周期与高档额外排热、调校不修改既有冷却、激光无附加热锁、空库存／整数门禁、双仓原版 Refuelable、目标装填量与零上限、自动补给／强制补给、原版滑条整数目标与独立拖动、原版 Scribe 往返库存／目标量／开关与热状态、旋转接触点和共同漂移、钢铁批产配方与轻重炮同效果配置。

不覆盖游戏派工实际可达性、原生绘图、实际 Harmony 生命周期、完整地图归档或战斗平衡；这些由新档游戏内验收补齐。历史 RailgunRegression 已切到显式预算／质量输入，需其独立 1.6 运行环境。
