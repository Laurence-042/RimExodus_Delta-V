---
name: delta-v-prefab
description: Design, author, validate, and troubleshoot RimExodus Delta-V map prefabs and their layout diagrams. Use for floor plans, completed ship layouts, Scribe state authoring, and prefab import failures; use delta-v-art for sprite work.
---

# Delta-V 地图 prefab

从用户要求得到可维护的布局源，再从同一份布局生成示意图和可导入 prefab。支持「只用地板标区」和「放置完整建筑」两个阶段；按用户当前范围选择，不自动把地板方案升级为建筑方案。

## 开始制作

读仓库 `AGENTS.md`、`README.md`、[Prefab 状态重建](../../../doc/Prefab状态重建.md)。运行时入口和契约以这些文件及当前源码为准。

制作或调整布局时先读 [制作流程、文件结构与控制层级](references/workflow.md)。开始写建筑坐标前读 [锚点、奇偶尺寸与旋转](references/geometry.md)。手写实例状态或遇到导入报错时读 [存档契约与诊断](references/contracts-and-diagnostics.md)。贴图工作使用相邻的 `delta-v-art` skill。

调整战舰轮廓、推进器、舱内武器、装甲或护盾时，读 [战舰布局与功能净空](references/ship-layout.md)：区分真实安装约束与外观选择，避免用额外净空把船体无谓撑大。

## 按阶段工作

| 阶段 | 主要输入 | 工作 | 应得到的结果 |
|---|---|---|---|
| 1. 确定约束 | 用户需求、参考 prefab、当前 Def | 列清必要功能、数量、能源/机动要求、制作模式、坐标约定 | 明确的设计目标和实际 Def 清单 |
| 2. 设计布局 | 目标与 Def 清单 | 定船体轮廓，分区，连接通道，预留设备脚印、操作格与排气/投射区 | 可编辑的布局源，所有区域可追溯到格位 |
| 3. 解析建筑 | 布局源与 Def 继承链 | 将预定脚印转换为 root；解析宿主、槽位和状态原型；调用原版函数校验 | 唯一的已解析记录集合 |
| 4. 生成示意图 | 已解析记录 | 绘地板、实际脚印、朝向、root、预留区与图例 | 能直接与格位记录对账的图 |
| 5. 生成 prefab | 同一份已解析记录 | 输出地形、foundation、roof 及按当前模式选择的 thingStates | `Prefabs/<name>.xml` |
| 6. 验证与交付 | XML、图、原版函数检查 | 检查文件引用、四向占地、交互/电网/状态；在完整测试窗口内游戏验收 | 明确区分离线通过、游戏通过和待测项 |

先完成共同的解析与校验，再生成图和 XML。两个输出不得各自重新解释布局。修改需求时回到布局源，重新生成两个输出；不要只在图上挪建筑或只手改生成的 XML。

## 控制关系

```text
用户约束 + 当前 Def + 导出状态原型
                 ↓
          布局源（编辑入口）
                 ↓
       解析、原版几何校验、冻结记录
           ↙                 ↘
     示意图渲染器         prefab 导出器
                                ↓
                  现有 MapPrefab 重建入口
                                ↓
                     游戏内实体与验收
```

Def 决定建筑实际几何与能力，布局源决定功能区和实例位置；示意图、XML 和缓存都由它们派生。飞船归属由驾驶台与基架控制域决定，宇宙姿态由 Map 外的 `SpacePose` 决定。这些层级不能互相代替，详见流程参考中的职责表。

## 交付规则

- 地板模式可以保留设备预留记录供绘图，但不能把它们写成真实建筑。foundation 是可导入船体的承载层；屋顶是否输出由任务决定。
- 建筑模式必须把计划中的设备逐实例落实，操作格、基架覆盖、屋顶、电网、补弹和通道均参与校验。用 `ShipPhysics` 的真实分配预览检查机动，注明载荷与工作状态假设。
- `Prefabs/` 是本地生成输出，遵守现有入库规则。构建脚本与示意图的目录约定见流程参考；辅助数据不能混入会递归扫描的 prefab XML 池。
- 实际建造通过 `MapPrefab`、`MapPrefabThingState`、`MapContentSpawner`，不另写等价的运行时落物管线。
- 离线调用原版函数不等于执行完整 `SpawnSetup`。需要用户运行游戏时，先保存输出及完成必要编译，再结束工作并明确停止读写；用户报告测试结束后再恢复。
- XML 更新可重新导入，Def／DLL 更新需要重启；错误测试图需要重建。只把用户确认的项目记为通过，未测边界保留在 `doc/TODO.md`。提交遵从当次用户授权。
