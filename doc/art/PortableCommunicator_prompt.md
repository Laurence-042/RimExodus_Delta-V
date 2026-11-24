# Portable Communicator

## 用途

`DeltaV_PortableCommunicator` 的地图物品与 Gizmo 图标。通讯器作为无身体覆盖的外挂附件穿戴，不提供人物穿戴贴图。

## 初次生成提示词

> Create a single game item sprite for a RimWorld-style science-fiction mod: a compact portable radio communicator belonging to an orbital faction. Orthographic top-down three-quarter view, centered, isolated. Palm-sized rugged rectangular device with dark gunmetal casing, one short folding antenna, a small luminous cyan-blue status screen, two simple orange physical buttons, a subtle pale edge highlight, believable industrial wear. Chunky readable silhouette and hand-painted texture suitable for a tiny 1x1 ground item and UI icon. Match RimWorld item art proportions and restrained painterly shading. No character, no hand, no cable, no logo, no text, no letters, no symbols, no scenery, no floor, no cast shadow, no border. Keep generous transparent padding around the complete object and crisp alpha edges.

初次结果造型可用，但透明通道使物体整体半透明并带有宽暗色光晕，因此未直接入库。

## 透明通道修订提示词

> Edit the most recent portable communicator sprite. Preserve the exact device design, top-down three-quarter orientation, gunmetal casing, cyan screen, orange buttons, and folding antenna. Correct only the cutout and presentation: remove every black or gray background pixel, remove the broad dark halo and all cast shadow, make the entire physical device fully opaque, and use transparency only for a very narrow clean anti-aliased edge. Center the complete device with even transparent padding on a square canvas. No new text, symbols, scenery, border, hand, character, cable, or shadow. Output a crisp transparent-background game sprite that remains high contrast at 64 pixels.

## 入库结果

- 资源：`1.6/Textures/Things/Item/DeltaV_PortableCommunicator.png`
- 初版尺寸：1254×1254，透明背景；主体绝大多数采样像素 alpha 为 250–255，边缘保留窄抗锯齿。
- 1.7 资源由标准构建从 1.6 同步，不单独维护源图。

## 原版风格纠偏

初版虽然解决了透明通道，但仍是写实 3D 道具渲染：三分之四透视、写实金属磨损、密集倒角和长天线都明显偏离 RimWorld 原版小物品。纠偏时以原版／Odyssey 的 `CerebrexNodePack.png`、`SmokepopPack.png` 和 `SentienceCatalyst.png` 为细节密度、色块组织、轮廓粗细和缩图可读性参考，只借鉴视觉契约，不复制其造型。

### 纠偏生成提示词

> Create a new portable communicator sprite from scratch for a RimWorld 1.6/Odyssey-compatible ground item and Gizmo. It is a compact wearable sci-fi radio terminal belonging to an orbital faction. Use the supplied original-game images only as references for simplification, color-block density, outline weight, orthographic presentation, and readability at 64 pixels; do not copy their shapes or identifiable designs. Draw one small flat rectangular communicator, closer to a pager or compact control module than a modern walkie-talkie: broad dark charcoal casing, one inset muted cyan status light or tiny screen, one small muted orange physical key, and at most one short stub antenna integrated into the body. Use a hand-painted RimWorld item-sprite language with an intentionally simple chunky silhouette, flat broad color regions, a dark soft outline, only two or three large value planes, restrained highlights, and slight painterly irregularity. True orthographic top-down view, centered upright with generous even transparent padding. Neutral soft upper-left highlight, minimal shading. The physical device must be opaque except for a narrow anti-aliased edge and remain legible at 64×64 and 32×32. Avoid photorealism, 3D rendering, three-quarter or isometric perspective, complex bevels, detailed scratches, realistic metal texture, long antennae, exposed screws, tiny vents, dense panel lines, bloom, cast shadow, halo, background, floor, text, symbols, logos, borders, hands, characters and cables.

### 第二版结果（已替换）

- 正式贴图重新采样为 64×64 RGBA，与上述三张原版参考图的画布规格一致。
- 主体采用正交俯视的深灰扁平终端、短桩天线、单块青色状态灯和单个橙色按键；不提供人物穿戴图。
- 生成器两次输出都把实体内部写成接近 255 但仍半透明的 alpha；最终处理在高分辨率源图上将 `alpha >= 128` 的实体像素设为完全不透明、其余归零，再以高质量双三次缩到 64×64，因此只有最终外轮廓保留窄抗锯齿。
- 64×64 和 32×32 缩图均能区分主体轮廓、通讯状态灯与操作键；透明背景无投影和宽暗色光晕。

## 机械集群靶向器改造版

用户指定原版 `OrbitalTargeterMechCluster` 为直接造型来源。原 Def 位于 Royalty 的 `ThingDefs_Misc/Weapons/OrbitalWeapons.xml`，原物与通讯器的 `drawSize` 都是 `(0.7,0.7)`，因此无需修改 Def，只在正方形贴图画布中收窄主体。编辑输入为用户提供的 156×156 原版靶向器图；正式美术仍只复用其视觉特征，不复用 `Verb_MechCluster`、一次性充能或任何危险逻辑。

### 首次靶向器改造提示词

> Turn the supplied original orbital mech cluster targeter sprite into a visibly customized portable communicator while preserving its identity. Treat the supplied image as the authoritative design reference. Preserve the upright orthographic presentation, trapezoidal light-gray metal casing, darker left face and lighter right/top facets, thin vertical antenna rising from the upper-left, two short horizontal muted-cyan bars on the front, and simple official RimWorld hand-painted color blocks. Make only the main trapezoidal body approximately 15–20 percent narrower without making it taller. Expose two or three chunky readable insulated wires from a small opened seam on the lower-right side: one muted red/copper wire and one dark teal or charcoal wire, with a short visible loop. Keep them thick enough to remain visible at 64×64 but subordinate to the original silhouette. Center the complete device on a transparent square canvas at the original visual scale. Remove the black background completely; keep the device and wires opaque except for one narrow anti-aliased outer edge. Do not redesign the item, move the antenna, add screens, buttons, text, symbols, logos, extra cyan markings, long dangling cables, photorealism, perspective, shadows, glow, halo, scenery, hands, pawns or worn-apparel views.

首版仍接近原宽，线缆只剩右下少量色点，64×64 下不足以表达改装。最终版提高收窄幅度，并把两条飞线的完整路径列为硬约束。

### 最终修订提示词

> Edit the supplied original RimWorld Orbital mech cluster targeter sprite into a visibly improvised portable long-range communicator. Treat the supplied image as the authoritative design reference and preserve its recognizable identity: upright orthographic presentation, simple light-gray trapezoidal metal casing with darker left face and lighter right/top facets, thin vertical antenna rising from the upper-left, and exactly two short horizontal muted-cyan bars on the front. Make the main trapezoidal body substantially narrower, about 30–35 percent narrower than the reference, while keeping approximately the same height and the antenna in the same relative location. Add very obvious chunky exposed insulated wiring that reads clearly at 64×64: one thick muted copper-red wire must exit from an opened seam on one side of the casing, extend clearly outside the silhouette, make a broad full U-shaped bend, and visibly reconnect into the casing; a second thick dark teal wire must visibly cross almost the entire front face from one side to the other and reconnect, like an improvised external jumper wire. The wires should sit above the casing, have dark pixel-art-like outlines, and remain unmistakable when downscaled, not tiny pins or subtle marks. Keep the original restrained hand-painted RimWorld item-sprite style, broad flat color blocks, chunky silhouette, minimal detail, no photorealism. Center the complete device and all wire loops on a transparent square canvas with even padding. No background, floor, cast shadow, glow, halo, text, symbols, logos, extra screen, buttons, hands, pawns, perspective view, or apparel rendering. The physical device and wires should be opaque except for a narrow clean anti-aliased outer edge.

### 最终契约

用户最终指定一张 1254×1254 RGBA 成稿替换生成结果；造型不再二次重绘。成稿保留原靶向器主体、天线与青色双条，并加入收窄机壳、顶部后装模块、右侧红蓝双线、固定夹及底角板。

- 正式贴图为 64×64 RGBA。原图的黑色背景完全不透明，因此按亮度键控：最大 RGB 通道不高于 8 的像素清空，高于 24 的实体像素保持不透明，中间区间只保留窄抗锯齿。
- 裁出实体后按原纵横比缩至 29×46，居中放入 64×64 透明画布；最终 alpha 低于 32 的缩放噪点清零、高于 223 的实体像素固化。
- 64×64 与 32×32 下均检查原版主体、青色双条及右侧红蓝线缆；不修改用户成稿的造型、颜色或配件位置。
