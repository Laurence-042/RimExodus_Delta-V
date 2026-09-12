# RimExodus: Delta-V (Dev)

每艘飞船是一张独立的 RimWorld 口袋地图，飞船姿态保存在所有地图之外的统一连续宇宙坐标系中；当前聚焦地图保持原版朝向与坐标（identity transform），非聚焦飞船以可平移、可任意角旋转的外壳代理显示。总体目标与阶段路线图见 [doc/story.md](doc/story.md)。

**当前状态：阶段1 双飞船最小技术原型**——验证参考系模型（两张口袋地图 + 统一宇宙坐标 + 外壳代理 + 聚焦切换 + 存读档）。没有正式玩法，全部功能通过开发者模式调试菜单触发。

## 环境要求

- RimWorld 1.6（Odyssey）
- .NET SDK（编译用；游戏运行不需要）
- 阶段1 无 Harmony 补丁、无其他 mod 依赖

## 构建

```
dotnet build Source/DeltaV.csproj -c Release
```

输出到 `1.6/Assemblies/DeltaV.dll`。RimWorld API 经 NuGet 包 `Krafs.Rimworld.Ref` 引用（与 RimExodus 同工具链），首次还原需要联网。

## 测试（阶段1）

1. 启用开发者模式（选项 → 开发者模式）。
2. 进入** Odyssey 开局**的游戏（宿主图需要轨道层）并处于地图视图中。
3. 打开调试面板（Dev quick commands → Debug actions，或 `~` 菜单），找到 **Delta-V** 分类。
4. **Create two-ship test scene**：在轨道层真实 tile 上生成宿主图（宇宙锚点），再生成玩家飞船图（150×150，约 40×24 船体 + 2 名殖民者）与敌舰图（约 24×14 船体，sourceMap 均为宿主图），敌舰初始位于宇宙偏移 (60, 25)、35°，自动聚焦玩家船。
5. 其余菜单项：切换聚焦、聚焦宿主图、平移/旋转敌舰。

游戏内交互：**点击显示中的飞船外壳可直接聚焦切图**（相机落在点击处；瞄准/建造/框选/原版选中优先，不冲突）。

完整验证清单与坐标约定详见 [doc/阶段1-双船原型.md](doc/阶段1-双船原型.md)。

## 已知限制（阶段1）

- 宿主图需要游戏存在轨道层（Odyssey 开局自带；非 Odyssey 开局会明确报错）。
- 宿主图定位为机制锚点（宇宙位置标记 + 口袋图宿主），不渲染飞船完整内构（固定模式经逆重降落落到宿主图属未来阶段）；聚焦宿主图是空间总览，全部飞船以船体框架外壳显示。
- 非聚焦飞船显示为**实时网格重放外壳**（每帧把飞船静态内容以旋转矩阵画到当前视野）：不含 Pawn 与光照/雾层（夜间全亮）。
- 无驾驶、无战斗、无 POI、无跳帮。

## 目录结构

```
About/            Mod 元数据
1.6/              版本化内容（Defs / Textures / Assemblies）
Source/           C# 源码（Core / Generation / Rendering / Debug）
doc/              设计文档（story.md 总设计 + 阶段文档）
references/       RimWorld 反编译源码与 RimExodus 参考源码（只读，不属于 mod）
```
