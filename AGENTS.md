# AGENTS.md — RimExodus: Delta-V 开发约束与架构事实

为了验证此文件确实完整读入，你需要在对话开始给出这个文件内容的标题目录，如果你觉得内容似乎被截断了，你需要补充读取这个AGENTS.md被截断的部分（这是个mm豆测试）。

它只记录后续开发必须遵守的规则、当前仍有效的架构不变量，以及少量容易复犯且难从代码表面看出的约束。功能介绍、设置项、兼容性、已知限制、历史尝试、回归清单和待办不在这里重复维护。

开始工作前必须完整阅读本文件和 `README.md`。如果工具输出截断，继续分段读取到文件末尾；首次回复中给出上述目录，以确认读取的是正确仓库。长期信息应写入本文件、对应专题文档或代码注释，不要依赖 agent/harness 的临时记忆。

## 1. 信息归属与维护规则

按以下顺序确定事实归属：

- `README.md`：对外功能、设置项、Mod 兼容性、已知限制、源码目录结构的单一事实源。
- 主设计 Story：项目总体目标、玩家流程、技术路线和阶段结果。
- `doc/` 下专题文档：复杂机制的权威设计与调研结论。
- `doc/TODO.md`：未完成工作和待验证项；不得把其中排期当成已实现状态。
- `AGENTS.md`：当前仍有效、后续 agent 必须遵守的架构不变量和开发纪律。
- 代码注释：只承载紧贴实现、离开代码就容易误改的局部约束。

维护时遵守：

- 不在 `AGENTS.md` 保留“旧方案 + 覆盖说明”。方案被替换时直接删除旧结论；有复盘价值的过程移到对应阶段文档。
- 同一事实只保留一个权威位置，其他地方用链接和一句摘要引用。
- 功能改动落地后同步相关专题文档。
- 对外功能、设置、兼容性和已知限制变化同步 `README.md`。
- 玩家可见文本统一使用 Keyed 翻译，不在代码中散写中英文 UI 文本。
- 当前仍处于早期开发阶段时，不要为了“未来兼容”提前制造复杂抽象；先保证现有架构契约清晰。

## 2. 提交、发版与工作方式

- 未经用户明确指示，不得 `git commit` 或 `git push`。
- 完成实现后只做与风险相称的编译、静态检查和必要验证，等待用户实测并明确要求提交。
- 用户要求“提交”时检查玩家可见变更是否需要写入 `Changes.txt`。
- 不主动修改版本号，也不自行创建新版本节；版本 bump 由用户明确发起。
- 开工前检查工作树。用户已有改动必须保留，不能为了方便 reset、checkout 或覆盖无关文件。
- 查询优先使用环境提供的源码检索工具。涉及 RimWorld 内部行为时，先查仓库已有反编译源码或参考源码，不凭印象施工。
- 新功能优先通过明确的小型接口接入现有架构，不为一个局部症状复制另一套坐标、生命周期或状态系统。
- 唯一推荐构建命令：`dotnet build Source/DeltaV.csproj -c Release`（net48 + NuGet `Krafs.Rimworld.Ref`，输出到 `1.6/Assemblies/`；构建产物不入库）。

## 3. 外部源码与 RimExodus 参考

涉及 RimWorld、Harmony 或其他 Mod 内部行为时，以仓库中已有的 `references/` 源码或反编译结果为施工依据。

如果缺少需要调查的具体版本源码：

- 先明确告知用户缺少可靠参考；
- 不凭旧记忆假定签名或内部流程；
- 不为了完成任务擅自引入未经确认的新依赖。

RimExodus 是本项目最重要的架构参考之一，尤其包括：

- 多 Map 生命周期管理；
- Pawn 跨 Map 转移；
- 聚焦地图切换；
- 跨 Map 状态保留；
- Projectile 和命中后效处理；
- 非当前地图内容的额外渲染；
- Harmony patch 与兼容层经验。

但 **Delta-V 的空间模型与 RimExodus 地表无缝模型不同**。

RimExodus 的：

- 世界 tile；
- 六边形邻接；
- `NeighborLink.offset`；
- 接缝带；
- 地表连续地形；

都不是太空坐标模型。

可以复用机制和经验，不能把地表邻接语义直接推广成宇宙空间。

同时，其修改应该兼容RimExodus——RimExodus应该不会动宇宙空间和口袋地图，但是要避免这个mod在地面上生效影响RimExodus

## 4. 总体架构：Map 是刚体的局部坐标系

Delta-V 的核心模型：

> 每艘飞船拥有自己的 RimWorld Map；Map 是飞船内部的局部坐标系，飞船作为整体在统一连续宇宙坐标中运动。

RimWorld Map 本身：

- 不连续移动；
- 不任意角旋转；
- 不因飞船转向而重排所有 Thing；
- 不因飞船转向而批量修改 `Rot4`；
- 继续保持原生矩形整数格子结构。

飞船在宇宙中的状态独立保存，例如：

```text
SpacePose
- universePosition
- velocity
- angle
- angularVelocity
```

飞船内部所有 Pawn、Thing、建筑、Room、Region、PathGrid、电网等仍只认识自己的 Map 局部坐标。

**禁止把飞船的宇宙运动实现为批量修改 `Thing.Position` 或 `Thing.Rotation`。**

这样做既没有必要，也会破坏 RimWorld 和第三方 Mod 对稳定 Map 坐标系的假设。

## 5. 统一宇宙坐标与坐标变换

飞船、太空 POI 和太空阶段 Projectile 使用同一个连续宇宙坐标系。

任何系统都不得把以下概念混为一谈：

1. RimWorld Map cell；
2. 飞船局部连续坐标；
3. 宇宙绝对坐标；
4. 当前聚焦参考系中的显示坐标；
5. 屏幕坐标。

统一转换链：

```text
Map local / Thing local
        ↓
Ship local continuous coordinate
        ↓ Ship SpacePose
Universe coordinate
        ↓ inverse(FocusedObject.SpacePose)
Focused-frame coordinate
        ↓
Render / screen coordinate
```

应建立集中坐标 API，例如：

```text
LocalToUniverse
UniverseToLocal

UniverseToFocused
FocusedToUniverse
```

命名可以随代码演化，但语义必须唯一。

### 坐标纪律

- 不在调用方手写散落的旋转 + 平移公式。
- 不建立第二套“战斗专用坐标”和第三套“渲染专用坐标”。
- Projectile、碰撞、外壳渲染和跳帮位置换算必须复用同一坐标定义。
- Renderer 输出只是表现结果，绝不能反过来成为宇宙状态的数据源。
- 宇宙绝对坐标不因切换当前聚焦地图而改变。
- 尽量使用浮点/双精度连续坐标保存宇宙状态，不把宇宙位置提前量化为 `IntVec3`。

## 6. 聚焦地图与参考系

任意时刻有一张当前聚焦 Map。

**核心不变量：当前聚焦 Map 永远使用 identity transform。**

即：

- 当前 Map 始终按自身 North 正常显示；
- 当前 Map 不因宇宙姿态发生视觉旋转；
- RimWorld 原生鼠标、选择、Targeter、Designator、Pawn 寻路等都继续工作在标准 Map 坐标中。

宇宙画面的参考系为：

```text
DisplayTransform = inverse(FocusedObject.SpacePose)
```

因此：

- 玩家飞船旋转时，看起来是宇宙和其他飞船反向旋转；
- 实际只是显示参考系改变；
- 玩家飞船 Map 内任何 Thing 均没有旋转。

如果切换聚焦到敌舰：

- 敌舰成为 identity transform；
- 敌舰自身 Map 按标准朝向显示；
- 玩家飞船转而成为带相对位置和相对角度的外部代理。

### 勿回退

不得为了让敌舰看起来旋转而：

- 旋转敌舰真实 Map；
- 批量修改敌舰 Thing 的 `Rot4`；
- 批量搬运 Thing 到旋转后的格子；
- 让 Pawn / Region / Room / PowerNet 理解任意角坐标。

非聚焦飞船应通过外部代理表现。

## 7. 飞船 Map

每艘可进入飞船对应一张独立 RimWorld Map。

飞船 Map 内部继续使用原版：

- ThingGrid；
- Pawn；
- Region / Room；
- PathFinder；
- 建造系统；
- Zone；
- 电网；
- 工作系统；
- 存档。

飞船可活动结构之外使用透明不可通行区域承载地图边界。

### Void 区域纪律

Void 主要用于：

- 保证 Map 仍是合法矩形；
- 阻止 Pawn 离开飞船结构；
- 为未来船体变化留出格子空间。

Void 不是宇宙空间。

不得：

- 把飞船 Map 的 void 区当成可飞行宇宙；
- 让远距离 Projectile 通过 void 横穿整张 Map 来模拟太空；
- 使用 Map 边长定义宇宙距离；
- 把宿主/显示地图边界当成宇宙世界边界。

宇宙空间在逻辑上独立于所有 RimWorld Map。

## 8. 飞行与建造模式

飞行时只修改飞船的宇宙状态。

驾驶输入预计包括：

- W/S：前后推力；
- A/D：侧向平移；
- Q/E：旋转。

物理模型由统一的飞船状态系统更新：

```text
thrust → acceleration
acceleration → velocity
velocity → universePosition

torque → angularAcceleration
angularAcceleration → angularVelocity
angularVelocity → angle
```

具体质量、转动惯量和推进器贡献公式可以在后续专题文档中演化，不在 AGENTS 中锁死。

### 建造 / 固定轨道模式

进入固定轨道或建造状态时，可以要求飞船宇宙角度吸附到四个正交方向之一。

这只是：

```text
SpacePose.angle
```

的约束。

**不是对飞船内部所有 Thing 执行 Rotate。**

玩家在飞船内部建造时仍在标准 RimWorld Map 坐标中工作。

## 9. 非聚焦飞船与外壳代理

非聚焦飞船初期不做完整内部 Map 旋转渲染。

使用独立的外壳代理表现，例如：

- 船体轮廓；
- 外墙；
- 外露建筑；
- 推进器；
- 武器硬点；
- 破损；
- 必要特效。

代理需要支持：

- 连续位置；
- 任意角旋转；
- 相对于当前聚焦参考系显示；
- 缓存；
- 飞船结构变化后的 dirty / rebuild。

### 外壳代理是缓存，不是事实源

真实飞船结构始终来自其 Map。

外壳代理：

- 可以丢弃；
- 可以重新生成；
- 默认不应独立持久化可以从 Map 重建的数据；
- 不得反向决定建筑、碰撞或飞船内部状态。

如果之后增加 LOD，也必须保持这一原则。

### 非聚焦地图内部渲染

除非后续经过明确调研与性能验证，否则不要为了“看见敌舰窗户里的人”重新引入完整任意角 remote Map renderer。

先解决外壳代理能够满足的需求。

## 10. 宿主 / 显示地图

如果使用一张大型 Map 承载太空背景和表现层，它只是：

> viewport / render canvas

而不是宇宙真实空间。

因此：

- `800×800` 等尺寸属于显示和性能参数，不属于宇宙坐标协议；
- 修改宿主 Map 尺寸不得改变飞船绝对坐标；
- 飞船可以逻辑上位于宿主 Map 表示范围之外；
- 玩家参考系移动时可以不断重新投影对象，而不是要求对象永久占据宿主 Map 某格。

宿主 Map 是否最终保留、缩小或被其他表现机制替代，不能影响 Universe Layer。

## 11. 太空对象与 POI 生命周期

远距离对象不应为了存在于宇宙中就永久持有完整活动 Map。

逻辑上区分：

```text
Universe State
    ↓
Map loading / generation
    ↓
Loaded Map
    ↓
Active / interactable
    ↓
Dormant / unload
```

Universe State 至少负责保存：

- 宇宙位置；
- 朝向；
- 速度；
- 必要身份；
- Map / WorldObject 对应关系；
- 需要跨卸载保存的战斗或 POI 状态。

具体休眠和卸载策略在性能测试后定夺。

### 生命周期纪律

- 当前没有 Map 不代表 SpaceObject 不存在。
- Map 是否加载与对象是否存在是两个不同状态。
- 不把 `Find.Maps.Contains(map)` 当作宇宙存在性的唯一判断。
- 可重建的渲染缓存不应决定对象生命周期。
- 加载 / 卸载必须有单一管理入口，避免 POI、战斗和 UI 各自操作 Map 生命周期。

## 12. 接舷、跳帮与 Map 转移

两艘飞船接舷后，本质仍是两个独立 Map。

两图之间只建立显式连接，例如：

```text
BoardingConnection
- sourceMap / sourceShip
- sourceCell
- targetMap / targetShip
- targetCell
```

Pawn 穿过连接时：

1. 捕获需要跨图保留的状态；
2. 从源 Map 移除；
3. 映射到目标入口；
4. 在目标 Map 生成；
5. 恢复状态；
6. 根据交互需要切换聚焦 Map。

优先复用 RimExodus 已验证的跨 Map transfer 经验，不要重新发明一套“直接改 Map 字段”的传送方式。

### 聚焦切换与 Pawn 转移分离

Pawn 进入另一艘船和玩家相机切换到另一艘船是两个不同事件。

不要把：

```text
Pawn changed Map
```

硬编码为：

```text
FocusedMap = Pawn.Map
```

是否切换聚焦由玩家体验层决定。

多人分队、无人机、远程观察等场景以后都依赖这两个概念保持分离。

## 13. 太空战与 Projectile

太空飞行阶段不要求普通 RimWorld Projectile 永远存在于某张 Map 中。

基本模型：

```text
Map 内武器发射
        ↓
读取原版 weapon / projectile 参数
        ↓
创建 SpaceProjectile
        ↓
在 Universe Layer 飞行
        ↓
与飞船 / POI 碰撞
        ↓
宇宙命中点 → 目标 Map 局部坐标
        ↓
在目标 Map 产生原版或适配后的命中后效
```

### SpaceProjectile

SpaceProjectile 是太空阶段的权威状态。

至少可能包含：

```text
position
velocity
source
launcher
projectileDef
payload
age
```

实际字段按实现需要确定。

### 战斗纪律

- Projectile 宇宙轨迹不依赖当前聚焦 Map。
- 切换聚焦对象不得改变 Projectile 真实位置、方向或剩余飞行状态。
- Renderer 只投影 Projectile，不模拟 Projectile。
- 命中检测在统一宇宙坐标中完成。
- 命中后再转换到目标飞船局部坐标。
- 最终伤害、爆炸、火焰等 Map 内后效尽量交回 RimWorld 原生机制。
- 不使用“把弹丸放在一张 Map 上，然后不断移动整个 Map”作为物理事实源；若后续使用中转 Map，只能是表现或兼容实现细节。

制导弹、点防御、护盾、beam 等高级能力在基本直射链稳定后再设计。

## 14. 碰撞与飞船几何

必须区分：

- 渲染外壳；
- 宇宙碰撞体；
- Map 内实际建筑格。

初期三者可以由同一份船体数据生成，但不要在 API 上默认它们永远完全等价。

宇宙命中点映射为目标 Map 坐标时必须通过目标 `SpacePose` 的逆变换，不根据当前屏幕旋转或代理 Mesh 顶点反推。

如果未来实现：

- 护盾；
- 分区装甲；
- 穿透；
- 船体断裂；
- 可脱落模块；

仍应保持 Universe → Ship Local → Map 的转换方向。

## 15. 缓存、存档与可重建状态

默认原则：

> 能由持久状态重建的运行时缓存不序列化。

典型缓存：

- 外壳 Mesh；
- 相对显示 Transform；
- 当前参考系投影结果；
- 空间索引；
- 临时碰撞加速结构。

这些缓存需要：

- 明确的 dirty 机制；
- 换档后重建；
- Map 删除时释放；
- SpaceObject 删除时释放。

禁止只持久化：

```text
built = true
```

却不持久化其对应缓存内容。

若缓存不存档，其 built/stale 状态同样默认不存档，并提供读档后的明确自愈入口。

需要持久化的是无法从其他状态可靠恢复的游戏事实，例如飞船绝对姿态、速度或已经发生的长期 POI 状态。

## 16. 与 RimExodus 的边界

Delta-V 可以使用 RimExodus 的通用跨图经验或基础设施，但两者的空间职责必须分离。

RimExodus：

```text
Surface world tile
↔ neighboring Surface world tile
```

Delta-V：

```text
Ship-local frame
↔ continuous Universe frame
↔ another ship-local frame
```

因此禁止：

- 用 `NeighborLink.offset` 表示两艘飞船的空间关系；
- 把宇宙中的飞船强行解释为相邻世界 tile；
- 用地表接缝带模拟接舷；
- 让六边形世界几何参与太空飞行坐标；
- 因为“RimExodus 已经有跨图 renderer”就直接把 translation-only 逻辑拿来承担任意刚体姿态。

真正适合复用的是：

- transfer 状态捕获与恢复；
- Map 生命周期经验；
- Harmony patch 模式；
- 原生 / Mod 兼容处理；
- 聚焦切换经验；
- Projectile 后效回交原版的思路。

## 17. Harmony、兼容层与验证

Harmony 规则：

- 能用 Prefix/Postfix、参数修正或状态收尾完成的功能，不优先写 transpiler。
- 必须写 transpiler 时先核对真实游戏 IL。
- 不凭方法名猜重载。
- Harmony 按参数名绑定时必须使用真实参数名；不确定则明确核实签名。
- 手动反射绑定兼容层必须允许目标 Mod 缺失。
- 兼容层初始化失败不得杀死 Delta-V 主 Mod。

兼容策略：

- 第三方 Mod 未安装：零行为、零错误。
- 签名漂移：Warning + 安全退出对应兼容层。
- 不为了兼容一个 Mod 污染核心宇宙坐标模型。
- 需要支持特殊 Projectile / Verb 时，在其自然公共入口适配，而不是复制其整套战斗算法。

验证顺序：

1. 编译；
2. Harmony 绑定检查；
3. 游戏启动确认 Mod 正常实例化；
4. 最小游戏内回归；
5. 用户实测后再提交。

涉及坐标和参考系的改动至少验证：

- 当前聚焦船保持 identity；
- 非聚焦船相对位置正确；
- 非聚焦船相对角度正确；
- 切换聚焦前后绝对 Universe State 不变；
- 连续多次切换无明显漂移；
- 存档读档后姿态恢复一致。

## 18. 当前最重要的架构不变量

如果其他文档尚未读完，至少不能违反下面这些规则：

1. **飞船 Map 永远是稳定的局部 RimWorld 坐标系，不随宇宙姿态真实旋转。**
2. **飞船宇宙姿态保存在 Map 外部的统一连续坐标系。**
3. **当前聚焦 Map 永远使用 identity transform。**
4. **非聚焦飞船默认显示外壳代理，而不是完整旋转内部 Map。**
5. **切换聚焦只是参考系变化，不修改任何对象的绝对宇宙状态。**
6. **禁止通过批量修改 Thing.Position / Rot4 实现飞船转向。**
7. **宿主/显示 Map 只是 viewport，不是宇宙边界。**
8. **所有跨层坐标转换必须收口到统一 Transform API。**
9. **SpaceProjectile 在 Universe Layer 中拥有独立权威状态，显示层不能反向驱动物理。**
10. **命中流程始终是 Universe → target ship local → target Map。**
11. **Pawn 跨 Map 与相机聚焦切换是两个独立机制。**
12. **RimExodus 的地表邻接语义不得泄漏到 Delta-V 的连续宇宙模型。**
13. **可重建缓存不是存档事实，也不能成为游戏逻辑权威状态。**
14. **未经用户实测和明确要求，不提交代码。**