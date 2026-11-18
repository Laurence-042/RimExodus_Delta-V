---
name: delta-v-art
description: Create and align RimExodus Delta-V top-down building and projectile sprites against RimWorld 1.6/Odyssey art and the current Def geometry. Use for Delta-V texture generation, replacement, or visual QA; not for unrelated UI or world-map art.
---

# Delta-V 俯视贴图

## 开工依据

- 先读仓库 `AGENTS.md`、`README.md`、相关正式专题与[待验收事项](../../../doc/TODO.md#art-acceptance)；检查工作树，保留已有改动。
- 从目标 Def 读取实际 `size`、`graphicData.drawSize`、`turretTopDrawSize`、`turretGunDef`、`attachmentSlots`、`gunnerOffset`、`muzzleOffset`。代码中的落点和渲染若已变更，以当前源码为准，不沿用本 skill 的旧数字。
- 美术对照放在 `references/RimWorldArt/`；贴图朝向、旋转、占地和绘制契约以 `references/RimWorldDecompiled/` 与 `references/RimWorldData/` 为准。不要从示意图猜格位。

## 视觉规范

- 专有名词：**固定底座**是随建筑 Rot4 转动的 `graphicData` 图；**旋转炮身**是 `TurretTop` 连续转动的顶部图；**覆盖插件**是独立的 1×1 建筑；**炮手操作终端**只属于固定底座。炮塔按脚印称点防御激光炮（1×1）、小型（2×2）、中型（3×3）、大型（4×4）；大型暂不实装。1×1 点防御底座无插件槽和炮手终端。
- 纯正交俯视、透明背景、清晰暗色外轮廓；中性灰装甲分成少量宽大的明暗面。高斯炮提供机械语言，2×2 的细节密度接近 Odyssey 装甲炮塔，不能把 9×9 高斯炮的面板数量缩小复制。
- 炮塔分别生成固定底座与旋转炮身，不从完整概念图粗切两层。可共用同尺寸固定底座；中型旋转炮身应复用对应小型贴图并由 Def 放大。轨道炮按自身脚印另制。激光炮身参考 Odyssey `BeamRepeater.png` 的红色发射器结构，并保持前述灰色装甲语言。
- 重炮、速射炮、激光炮的旋转顶部必须靠**占面积最大的主体或外周轮廓**区分；只换炮口颜色或小零件不够。激光采用成对的大面积红色发射器／能量槽和短发射口，不沿用重炮的八边形装甲主体；速射炮也须另有主轮廓。
- 武器家族配色：重炮中性灰，速射炮低饱和青色，激光炮红色，护盾蓝色，工程类炮塔（如残骸拆解激光与动量捕捉炮塔）黄黑。颜色覆盖炮塔顶部的主要机匣／装甲或功能主体；共用底座继续保持中性灰，黑色外轮廓与明暗层级不变。避免只靠颜色辨识，轮廓仍须区分。
- 导弹外壳形态可共用；不同战斗部用可读的色带或弹头区域标识：EMP 蓝、高爆黄、纯动能黑。纯动能黑色需要灰白边缘以免在深色背景消失。工程炮塔的黄黑危险条纹与高爆导弹的黄色弹头通过形态和位置区分。
- 2×2 朝北炮塔的现有单个覆盖式插件槽在根格 `(0,0,0)`（西南格），炮手在根格南侧 `(0,0,-1)`。底座给插件留可见安装面，底座南缘对准炮手格留操作终端；它们不随炮身转。3×3 炮塔有两个槽；具体偏移每次读 Def。
- 插件的建造格与视觉位置分开校对：武器 Def 的 `attachmentSlots` 每项用 `cell` 指定建造格，用朝北格心相对的 `drawOffset` 与 `drawScale` 指定完成品贴图。插件自身贴图按完整 1×1 画布制作，不把宿主槽位偏移烘焙进像素；四向显示由 `Graphic_SpaceWeaponAttachment` 随宿主旋转这些参数。绘制比例和逻辑占地不得混用。
- 设计先看原尺寸，再缩到实际地图显示尺寸检视。细线、微螺栓、密集排气孔若缩小后只成噪声，应删去；炮口、炮身和底座边界仍须可辨。

货仓与安全电池的状态配色、语义及精确乘色参数统一遵循 [货仓与安全电池共用状态色](../../../doc/空间站交易与货柜.md#storage-state-colors)。扩展同类资源时复用该表，不另立红绿接网色或封存色；常态与锁定色分工、蒙版通道和取色方法以该专题为准。

## 对齐与原版绘制契约

- `Building_TurretGun.DrawAt` 分别绘制顶部和建筑底座。顶部材质来自 `building.turretGunDef.graphicData.texPath`（`BuildingProperties` 初始化），更换底座的 `graphicData.texPath` 不会更换炮身。
- Delta-V 的 `Building_SpaceTurretGun` 仅在 Def 配有 `DefModExtension_SpaceTurretArt.topTexPath` 时覆盖顶部材质；未配置者仍取原版 `turretGunDef` 材质。它不改原版旋转、位置与图层机制。
- `TurretTop.DrawTurret` 用正方形 `turretTopDrawSize` 的 plane，绕其中心旋转；`ArtworkRotation = -90`，原版顶部图案朝右。炮身的机械旋转轴必须落在透明画布正中心，透明边距也计入定位。不要用固定的 `turretTopOffset` 补偿图内偏心，转动后会绕圈。
- 底座用 `Graphic_Single` 随建筑 `Rot4` 绘制；顶层自由旋转。2×2 北向脚印、根格和炮手位可用 `GenAdj.OccupiedRect`、`SpaceWeaponGeometry.RotatedCell` 核对。检查北/东/南/西建造和炮身全周旋转，插件、终端与炮身不得产生误导性错位。
- 覆盖插件保留 `BuildingOnTop` 建造层及非 Edifice 属性以维持同格叠建；其 `graphicData.drawOffset.y=-0.712` 仅调整贴图深度。当前原版层距约 0.36585，旋转炮身比固定底座高约 0.03659；静态贴图的顶点高度差约 0.01。改贴图时须复验底座、插件和旋转炮身的实际遮挡，不要为调画面而直接修改插件的建造层。
- 生成图首先是概念稿。接入前检查 alpha、画布尺寸、视觉中心、炮身旋转轴、贴图缩小效果、插件实际叠画和缩放/旋转下的裁切；`doc/art/` 只归档两类内容，即原始及迭代美术、生成美术的 prompt。每个独立版本保留代表作，必要的完整合成图也属于迭代美术，文件名含 `preview` 不作为删除依据。正式贴图放 `1.6/Textures/`。用图像生成工具制作或修改位图，参考图只提供风格，不直接复制原版美术。
- 转轴对齐是确定性几何操作：用户指定使用 Python。先用 alpha 轮廓和机械主体确定源图轴心，再运行 `scripts/align_turret_layer.py SOURCE OUTPUT --pivot-x X --pivot-y Y --canvas N`。脚本仅整数平移并增补透明画布，逐像素验证 RGBA 不变、拒绝裁切；**不要用生成式修图来移动轴心**。同一炮塔家族的底座和顶部用相同方形画布，接入时再核对 `drawSize` 的世界尺寸。
- 非聚焦外壳的渲染与地图真建筑要一起验证，结构表面与炮塔顶部可能走不同的显示入口。长期规则写入对应正式专题，玩家可见变化同步 README，开放验收集中到 TODO。收尾先将长期结论写入正式文档并更新引用，再清理一次性预览、重复缩图和定位检查图及对应脚本。各版代表美术与 prompt 继续归档，正式资源生成入口及所需源图随契约保留。

## 弹体特别规则

- 查 `SpaceProjectileService.TryEmitProjectiles` 再选弹体图：实体弹体从 Def 的 `graphicData` 绘制，图案前向为 UV 上方（局部 +Z）；与炮塔顶部图案朝右的约定不同。
- 当前瞬时激光在同服务中绘制程序线段，激光弹体 Def 的贴图不作为可见飞行物。参考 Beam Repeater 的色彩与发光层次来调整光束，不把整把手持武器图当作弹体。
- 原版高爆迫击炮弹路径为 `Things/Projectile/ShellHighExplosive`；原版自动加农炮弹 `Bullet_AutocannonTurret` 使用 `Things/Projectile/Bullet_Big`。复用时核对当前版本 Def；若路径本就正确，无需复制一份相同素材。
