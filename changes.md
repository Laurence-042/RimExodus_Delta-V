# 更新记录 / Changelog

本文件记录已发布版本的玩家可见变化，并列出下一版本的近期计划。计划项不是已实现功能，也不承诺固定发布日期；完整路线与待验收项分别见 [开发路线](doc/SteamWorkshop/discussions/开发路线%20-%20Development%20Roadmap.txt) 和 [TODO](doc/TODO.md)。<br>
This file records player-visible changes in released versions and the near-term plan for the next version. Planned items are not implemented features and have no guaranteed release date; see the [development roadmap](doc/SteamWorkshop/discussions/开发路线%20-%20Development%20Roadmap.txt) and [TODO](doc/TODO.md) for the complete roadmap and pending verification.

## 0.1.1 — 2026-11-23

### 已完成 / Shipped

- 右下角新增真实地图船体渲染开关。开启后，RimExodus Framework 通过源地图的原版绘制流程渲染非聚焦船体，显示完整建筑与地图内容；默认关闭，可随时切回 Delta-V 自定义船壳。<br>
  Added a bottom-right native map shell rendering toggle. When enabled, RimExodus Framework renders non-focused ships through each source map's vanilla draw path, showing complete buildings and map contents; it is off by default and can be switched back to the Delta-V custom shell at any time.
- 真实地图渲染模式继续保留 Delta-V 的散落物、宇宙弹体、坐标网格和推进器表现层，不重复提交自定义船壳。<br>
  Native map rendering retains Delta-V's loose-object, space-projectile, coordinate-grid, and thruster presentation layers without submitting a duplicate custom shell.
- 修复 IsoRim 3D 视图在中键拖拽后重新限制 Delta-V 太空图摄像机范围的问题；普通地图继续保留 IsoRim 的原边界限制。<br>
  Fixed IsoRim 3D middle-button dragging reapplying the camera clamp on Delta-V space maps; ordinary maps retain IsoRim's original boundary.

## 0.1.0 — Alpha 初次发布 / Initial alpha release

- 提供连续飞船宇宙飞行、手动驾驶与自动导航，包括非基架飞船。<br>
  Introduced continuous ship spaceflight, manual piloting, and autopilot, including ships without substructure.
- 提供飞船交战、舰炮、激光、导弹、护盾、自动战斗与跳帮。<br>
  Introduced ship combat, cannons, lasers, missiles, shields, automated combat, and boarding.
- 提供软对接、硬对接、分离、人员与货物转运，以及 EVA、采矿和牵引回收。<br>
  Introduced soft docking, hard docking, separation, crew and cargo transfer, EVA, mining, and tractor salvage.
- 提供按距离归档的宇宙 POI、Prefab 系统、空间站贸易与委托，以及基础太空 POI Prefab。<br>
  Introduced distance-based space POI archiving, the prefab system, station trade and contracts, and the initial space POI prefab set.
- 提供可编辑、可导入导出的星区生成预设。<br>
  Added editable sector-generation presets with import and export support.
- 修复非聚焦船壳拦截地图点击的问题。<br>
  Fixed non-focused ship shells intercepting map clicks.
- 修正教程真空服配置，并补齐 Prefab 植物与相关生成内容的保留规则。<br>
  Corrected tutorial vacsuit configuration and preserved prefab plants and related generated content.

## 下一版本计划 / Next version plan

- 为跳帮艇被击毁时的乘员死亡补充明确通知。<br>
  Add clear crew-death notifications when a boarding shuttle is destroyed.
- 改善无人地图在原版世界地图和星区界面中的可发现性。<br>
  Improve discoverability of uncrewed maps on the vanilla world map and sector interface.
- 继续验证并加固 Prefab、任务系统、地图归档与恢复流程。<br>
  Continue validating and hardening prefabs, the mission system, and map archiving and restoration.
- 持续处理问题修复、性能优化与数值调整。<br>
  Continue bug fixing, performance optimization, and balance tuning.
