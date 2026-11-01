# 基架增量拓扑接入回归

本脚本直接编译生产源码 `GridTopology`、`FoundationConnectivity` 和 `FoundationTopologyService`，只替代 Map、归属读取与主台位置等游戏边界。算法来自 **《新边疆》开发组的浪淘沙**，出处与发布署名要求见 [CONTRIBUTOR.md](../../CONTRIBUTOR.md)。

## 运行

仓库根目录执行（需要 .NET 10 SDK，优先使用仓库 `.local-tools/dotnet`）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/FoundationTopologyRegression/run.ps1
```

另用实际 1.6 Mod DLL 验证原有控制域、归属写入口、Harmony postfix 和出力分离流程（先 `just build 1.6`；需要 .NET 8 SDK 和对应运行时的 Harmony）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/NpcRetreatRegression/run.ps1 `
  -GameRoot 'D:/SteamLibrary/steamapps/common/RimWorld_1.6' `
  -HarmonyPath 'C:/Users/Laure/.nuget/packages/lib.harmony/2.4.2/lib/net8.0/0Harmony.dll' `
  -SeparationOnly
```

路径按本机安装调整。两套脚本均不启动游戏。

## 覆盖与结果（2026-11-01）

- 生产源码对拍 **36,415 个状态通过**：全部 512 种 3×3 图形及逐格删补／改归属，1×1 和窄长图，四种子矩形随机操作，节点重新加入、手动整理及 13 次自动节点池整理。
- 250×250 实心板、单格桥、大环、随机碎片各 400 次更新。独立四邻接 BFS 核对完整前景分区；互补八邻接 BFS 核对孔洞和欧拉计数；按主台种子与归属另算控制域，比较“域外有无基架”的精确判据。
- 服务事件合并、混合归属、未认领基架、主台种子变更、释放后重建、新游戏服务及已删除地图通知。稳定的 10,000 次查询不读取基架；同格 1,000 次通知合并为一次事实读取。计时只供观察，不作为性能基准。
- 实际生产 DLL 专项 **34 项检查通过**：真实 `Assign/ClearOwner/NotifyFoundationRemoved`；地形变更与 unsafe 剥离 postfix 的签名绑定及回调；宿主分离方向、暂停出力、重复排队抑制、部分搬运失败重试、主船 Map／绝对运动保留、散落货物失败和新残片重新拦截。
- `just build`（1.7）和 `just build 1.6` 通过，均 0 警告、0 错误。

生产 DLL 专项仅替代 Unity 地图生成／实际搬运、原版素材判断和指定的散落失败结果；归属更新、控制域、残片分组、公共分离门禁及重试逻辑使用真实 Mod 实现。回调测试在夹具写入地形最终状态后调用真实 postfix，不等于启动 Unity 执行完整地形变更链。**新档游戏内战损、修补、主备用台继任、确认窗、存读档及实际跨图搬运仍待用户验收。**

本脚本验证接入正确性；TopologyLab 与 HLT 的性能比较及原始样本仍以 [TopologyBenchmark](../TopologyBenchmark/README.md) 为准，不把离线耗时换算成游戏 TPS。
