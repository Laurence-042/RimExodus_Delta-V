# 更新记录 / Changelog

本文件记录已发布版本的玩家可见变化，并列出下一版本的近期计划。计划项不是已实现功能，也不承诺固定发布日期；完整路线与待验收项分别见 [开发路线](doc/SteamWorkshop/discussions/开发路线%20-%20Development%20Roadmap.txt) 和 [TODO](doc/TODO.md)。
This file records player-visible changes in released versions and the near-term plan for the next version. Planned items are not implemented features and have no guaranteed release date; see the [development roadmap](doc/SteamWorkshop/discussions/开发路线%20-%20Development%20Roadmap.txt) and [TODO](doc/TODO.md) for the complete roadmap and pending verification.

## 下一版本计划 / Next version plan

- 修复从星区内口袋图或同地址的其他地图发射时，误将星区对应 POI 当作出发图并摧毁的问题；现在只有实际出发的同一张世界地图会进入放弃流程。
  Fixed launches from pocket maps or other maps at the same sector address incorrectly treating and destroying the sector POI as the departure map; only the actual source world map can now be abandoned.
- 修复逆重飞船搬运通过原始屋顶清除入口时未刷新船体结构缓存的问题；月池上的普通船顶移除后会立即恢复力场顶。
  Fixed gravship transfer through the native roof-removal path leaving the ship structure cache stale; removing an ordinary roof over a moon pool now immediately restores its force-field roof.
- 修复点击原版星球地图的玩家飞船剪影后遗留世界拖拽状态的问题。玩家剪影现在只显示己方飞船；屏幕位置重叠时合并为带数量的代表剪影，点击后可从菜单选择具体飞船。
  Fixed clicking a player-ship silhouette on the vanilla world map leaving world dragging active. Player silhouettes now show only player-owned ships and merge overlapping markers into a counted representative with a ship-selection menu.

@@UTcurtain：加重一下开门引导。在新手教程开局试了好几次，一直在怼通讯台，打开任何和定位任务都摁过检查过，太空站进去过钢铁也捡过，最后发现是按征召右边的太空生存指导来推进任务。(:steamfacepalm:)
- 准备在下个版本把教程引导统一做到通讯台的星界边境派系交互里，然后让通讯台掉一个附件类型的便携通讯器，然后让装备通讯器的人员可以直接等价于用通讯台呼叫星界边境。同时初次对话末尾也会提及这个通讯器。

@UTcurtain：全部做到通讯台肯定没问题，这方面的话金鸢尾兰鼠族做的就不错。(也有个交流平台可以直接揣怀里打电话。)我现在在开局的太空站里，卡在了可以右键交互的EVA工程套装，进得去出不来。也能随便拿空间站仓库的各种物资。
- 已为非敌对 EVA、小艇和原版任务穿梭机补充「让己方人员离开载具」命令。图内按原版容器规则下车，单格飞行图等图外舱口会在确认后将活体乘员交给可牵引的太空漂流物链；货物和尸体无需确认。原版整舱「卸载」也复用该事务：取消或交接失败时不提前清理剩余内容与装载状态，整舱成功放出后才清理装载组。
  Added a “Let our personnel leave the vehicle” command to non-hostile EVAs, shuttles, and vanilla quest shuttles. In-bounds occupants use native container placement; out-of-bounds living occupants enter the recoverable space-drift chain after confirmation, while cargo and corpses require no warning. The native full-hold unload command now uses the same transaction and only clears loading state after the complete hold has been released successfully.

@UTcurtain：建议给采矿激光加一个货箱底座，这真空岩自填充塞满了我的飞船空间
- 已将采矿光束改为常规炮座：2×2 小型有一个专用槽，3×3 中型有两个专用槽，可安装采矿货箱或目标识别器。货箱复用原版储物、搬运、过滤与设置联动，默认低优先级、允许岩块并保存 8 堆；识别器用可合并 Gizmo 批量设置过滤。舰载与 EVA 采矿产物现共用同一输出入口，未识别材料从实际命中点以逐堆不同的速度和角度成为漂浮物。
  Mining beams now use standard turret bases: the 2×2 model has one dedicated slot, while a new 3×3 model has two, accepting mining cargo boxes or target identifiers. Cargo boxes reuse native storage, hauling, filters, and linked settings, default to low priority, accept chunks, and hold eight stacks. Identifiers expose one mergeable batch-filter gizmo. Ship-mounted and EVA mining now share one output path, with rejected materials becoming drifting objects from the actual impact point with per-stack speed and angle variation.

@UTcurtain：标准姿态-手动驾驶-挪动几下逆重飞船后发射-确认着陆区-切换手动驾驶-尝试移动和选择 。推进器正常发力，但此时我通过定位坐标发现自己根本无法移动，旋转时开始抽搐(角度倾斜和高频角度归正)，这个问题我通过新存档复现了。(之前那份评论是因为我开自动导航就开始抽搐，应该飞船的姿态判断问题)， 解决问题也不是切换驾驶台而是重新进行舱内/外作业按钮切换。
- 已修复：战略降落图现标记为舱外作业图，首次真实出力会将飞船提取到独立飞行图；空轨道降落临时创建的玩家 Home 会在最后一艘船脱离且没有遗留人员、建筑、物品或在途运输后自动删除。
  Fixed: strategic landing maps are now marked as orbital worksites, and first real thrust extracts the ship into an independent flight map. A temporary player Home created by landing on an empty orbital tile is automatically removed after the last ship departs when no pawns, buildings, items, or incoming transports remain.


- 武器和护盾适配

- 为跳帮艇被击毁时的乘员死亡补充明确通知。
  Add clear crew-death notifications when a boarding shuttle is destroyed.
- 改善无人地图在原版世界地图和星区界面中的可发现性。
  Improve discoverability of uncrewed maps on the vanilla world map and sector interface.
- 继续验证并加固 Prefab、任务系统、地图归档与恢复流程。
  Continue validating and hardening prefabs, the mission system, and map archiving and restoration.
- 持续处理问题修复、性能优化与数值调整。
  Continue bug fixing, performance optimization, and balance tuning.

## 基于收集的用户反馈的正在排期的需求（排名不分先后）

@C鼬柠檬茶（bilibili）：空间站仓库里的东西免费拿真的没问题吗，我前期就靠这个苟着
- 当前空间站仓库里的物品可以直接拿。考虑给非封存态的货仓也加基于派系的去用判断，应该不需要影响其他储物设备，否则一些mod给玩家的“来自盟友的战前补给”就会很黑色幽默

@C鼬柠檬茶（bilibili）：当前空间站的地板和地基没有拆除鉴权

@某光头的神棍先知（bilibili）：希望能兼容VGE，我要造10000格以上的超级大飞船去肘击敌人，最好把VGEC2的敌方飞船和战斗空间站也做成可以这样战斗[doge][doge][doge]
- 考虑禁用VGEC2的飞船战斗机制，但是适配其武器，将其事件触发转接到DeltaV的类似袭击事件生成上——需要明确VGE的飞船推进器可能不足以承担DeltaV的战斗要求，可能会在转向能力限制下做出愚蠢的行为

@亚空间魔丸丰川祥子（bilibili）：可以弄成对30以下的伤害免疫?（指毁伤效果）
- 我搞完手头这些会考虑给个可以调的设置阈值，默认0


## 一些俺寻思

一些不在计划里，也不一定会做的突发灵感。但如果大家特别喜欢里面某些想法的话可以说一声，我会进一步进行实现成本调研，如果不会带来很多麻烦的话我会在推进计划的空档将其做出来

- 缴获海盗通讯器，诱骗海盗货船到埋伏点

## 已完成 / Shipped

### 0.1.1 — 2026-11-23

- 右下角新增真实地图船体渲染开关。开启后，RimExodus Framework 通过源地图的原版绘制流程渲染非聚焦船体，显示完整建筑与地图内容；默认关闭，可随时切回 Delta-V 自定义船壳。
  Added a bottom-right native map shell rendering toggle. When enabled, RimExodus Framework renders non-focused ships through each source map's vanilla draw path, showing complete buildings and map contents; it is off by default and can be switched back to the Delta-V custom shell at any time.
- 真实地图渲染模式继续保留 Delta-V 的散落物、宇宙弹体、坐标网格和推进器表现层，不重复提交自定义船壳。
  Native map rendering retains Delta-V's loose-object, space-projectile, coordinate-grid, and thruster presentation layers without submitting a duplicate custom shell.
- 修复 IsoRim 3D 视图在中键拖拽后重新限制 Delta-V 太空图摄像机范围的问题；普通地图继续保留 IsoRim 的原边界限制。
  Fixed IsoRim 3D middle-button dragging reapplying the camera clamp on Delta-V space maps; ordinary maps retain IsoRim's original boundary.

### 0.1.0 — Alpha 初次发布 / Initial alpha release

- 提供连续飞船宇宙飞行、手动驾驶与自动导航，包括非基架飞船。
  Introduced continuous ship spaceflight, manual piloting, and autopilot, including ships without substructure.
- 提供飞船交战、舰炮、激光、导弹、护盾、自动战斗与跳帮。
  Introduced ship combat, cannons, lasers, missiles, shields, automated combat, and boarding.
- 提供软对接、硬对接、分离、人员与货物转运，以及 EVA、采矿和牵引回收。
  Introduced soft docking, hard docking, separation, crew and cargo transfer, EVA, mining, and tractor salvage.
- 提供按距离归档的宇宙 POI、Prefab 系统、空间站贸易与委托，以及基础太空 POI Prefab。
  Introduced distance-based space POI archiving, the prefab system, station trade and contracts, and the initial space POI prefab set.
- 提供可编辑、可导入导出的星区生成预设。
  Added editable sector-generation presets with import and export support.
- 修复非聚焦船壳拦截地图点击的问题。
  Fixed non-focused ship shells intercepting map clicks.
- 修正教程真空服配置，并补齐 Prefab 植物与相关生成内容的保留规则。
  Corrected tutorial vacsuit configuration and preserved prefab plants and related generated content.

  ## 已拒绝的用户需求（排名不分先后）

@碧海lan天：话说有没有考虑用动量轮来旋转飞船 感觉纯RCS有点奇怪
- DeltaV的飞船工况也更接近现实里的载人飞船，而现实里的载人飞船因为需要在交会对接时平移这种动量轮解决不了的动作，其仍然搭载了大量RCS，甚至平时就以RCS为主要的转向方式
- 现实里主要用动量轮或者类似机制CMG的空间站，也仍然需要RCS，而DeltaV的空间站通常不会面临现实空间站的角度微调要求，往哪一停就不再动了，动量轮的使用场景也很有限
- 动量卸载仍然需要RCS、旋转的动量轮能否拆除、同时有动量轮和RCS转向时用哪个也需要更多的控制复杂度
- 综上，收益有限、工作量不小且潜在风险也不小，至少在alpha阶段不会进行实现。
