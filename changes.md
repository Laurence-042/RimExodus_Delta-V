# 更新记录 / Changelog

本文件记录已发布版本的玩家可见变化，并列出下一版本的近期计划。计划项不是已实现功能，也不承诺固定发布日期；完整路线与待验收项分别见 [开发路线](doc/SteamWorkshop/discussions/开发路线%20-%20Development%20Roadmap.txt) 和 [TODO](doc/TODO.md)。
This file records player-visible changes in released versions and the near-term plan for the next version. Planned items are not implemented features and have no guaranteed release date; see the [development roadmap](doc/SteamWorkshop/discussions/开发路线%20-%20Development%20Roadmap.txt) and [TODO](doc/TODO.md) for the complete roadmap and pending verification.

## 下一版本计划 / Next version plan

- 为跳帮艇被击毁时的乘员死亡补充明确通知。
  Add clear crew-death notifications when a boarding shuttle is destroyed.

## 基于收集的用户反馈的正在排期的需求（排名不分先后）

@C鼬柠檬茶（bilibili）：空间站仓库里的东西免费拿真的没问题吗，我前期就靠这个苟着
- 当前空间站仓库里的物品可以直接拿。考虑给非封存态的货仓也加基于派系的去用判断，应该不需要影响其他储物设备，否则一些mod给玩家的“来自盟友的战前补给”就会很黑色幽默

@C鼬柠檬茶（bilibili）：当前空间站的地板和地基没有拆除鉴权

@某光头的神棍先知（bilibili）：希望能兼容VGE，我要造10000格以上的超级大飞船去肘击敌人，最好把VGEC2的敌方飞船和战斗空间站也做成可以这样战斗[doge][doge][doge]
- 考虑禁用VGEC2的飞船战斗机制，但是适配其武器，将其事件触发转接到DeltaV的类似袭击事件生成上——需要明确VGE的飞船推进器可能不足以承担DeltaV的战斗要求，可能会在转向能力限制下做出愚蠢的行为

@亚空间魔丸丰川祥子（bilibili）：可以弄成对30以下的伤害免疫?（指毁伤效果）
- 我搞完手头这些会考虑给个可以调的设置阈值，默认0
- 不对，我得考虑点防……只让墙有这种特性？


## 一些俺寻思

一些不在计划里，也不一定会做的突发灵感。但如果大家特别喜欢里面某些想法的话可以说一声，我会进一步进行实现成本调研，如果不会带来很多麻烦的话我会在推进计划的空档将其做出来

- 缴获海盗通讯器，诱骗海盗货船到埋伏点
  - 相同逻辑也可以做“盟友通讯器呼叫盟友飞船增援”的玩法
- 在某些任务里搭乘盟友的跳帮艇，和十几个其他盟友跳帮艇一起进行饱和式跳帮

## 已完成 / Shipped

### 0.1.2 — 2026-11-26

#### 用户反馈解决 / User feedback resolved

@未知者-RX（bilibili）：所以新手教程最后一步怎么搞，我已经占领了逆重引擎给船起了名字但还是不让我交任务说是让我去逆重引擎附近确认什么，我真不知道确认什么了[热]……我是去通讯台交的任务，实际上要站在引擎旁边用通讯器交任务，我是傻呗[汤圆]
- 教程最后一步现在使用原版保存的逆重引擎检查记录。选中殖民者、右键检查任意一台逆重引擎后，即可通过任意通讯台或便携通讯器确认交接；提前检查、离开引擎以及存读档后仍然有效，不再要求通讯时站在赠船引擎旁。
  The final tutorial handover now uses the base game's saved grav-engine inspection record. After a colonist inspects any grav engine through its right-click action, the handover can be confirmed from any comms console or portable communicator. Earlier inspections, leaving the engine, and save/load all remain valid; the pawn no longer needs to stand beside the prototype engine while calling.

@老耗子比你萌：还有自动添加太空模式的炮台优先级疑似太高了点，地面上机械集群的炮塔默认太空模式，导致其无法攻击，整个机械集群只有地狱火炮塔在输出
- 自动获得太空战能力的原版与第三方炮塔现在默认使用地面模式，继续使用来源炮塔自己的索敌、弹药和冷却；玩家仍可手动切换到太空模式。机械集群等地面生成的自动适配炮塔不再因默认太空模式而停止攻击地面目标。
  Automatically adapted vanilla and third-party turrets now default to ground mode, retaining their source targeting, ammunition, and cooldown until the player switches them to space mode. Ground-spawned adapted turrets such as mech-cluster defenses no longer stop attacking local targets because they initialized in space mode.

@老耗子比你萌：在地面补给后发射去太空，起飞结束时当前地图索引越界，随后 Vulkan 崩溃。
- 修复移除当前地图时原版选中 Delta-V 内部宿主图，焦点门禁中断 setter 并遗留越界地图索引的问题。宿主图请求现在统一顺延到下一张可聚焦地图；没有可聚焦地图时清空当前地图，由原版进入世界视图。
  Fixed removing the current map selecting Delta-V's internal host map, where the focus guard interrupted the setter and left an out-of-range map index. Host-map requests now advance to the next focusable map, or clear the current map when none remains so the native flow can enter the world view.

- 穿戴指定真空与推进装备的人员现在会先成为抽象漂浮 `SpaceObject`，能力允许时以同一对象 ID 升格为可控舱外飞行。升格期间始终绑定一条与人员对象重合、不另建对象 ID 的临时弹体代理，把太空命中转交真实 Pawn。人员使用 EVA 式四角斜推，可叠加跳跃背包；主火直接执行当前主武器的真实 `Verb`，保留 burst、冷却及弹药／燃料／充能消耗，并把实际生成的原版弹丸暂存为瞬态宇宙对象，命中舰体时在目标图调用同一实例的虚 `Impact`。真实 Verb 现只接收一个图内合法占位目标，宇宙射向独立传递；修复 IsoRim 处理越界目标时开火崩溃、异常后遗留本地弹丸并误伤射手的问题。单兵射弹不进入点防，首种兼容模式暂不接管即时光束和完全自管发射的特殊 Verb。抓钩作为副组 1 持续武器，其外力视为操作输入，不再被飞行辅助或已选自动任务反推抵消。主动出舱会自动聚焦、征召、驾驶及在武装时进入手动火控；飞行人员可抛出背包货物，但不能换装。月池、开放对接口、无顶甲板和真空边缘均可主动出舱；空月池也可直接选作落脚目标。“登舰”与跳帮艇共用硬对接命令生命周期，但人员保持碰撞规避。制导以相对落脚点 `1.5 cells/s` 的穿越速度进场，并在实际相对速度低于 `2 cells/s` 时完成精确格落脚，不再要求近乎静止或依靠船体碰撞。人员与 EVA 钩索都对轻小单位使用真实双向冲量，钩住结构时把自己拉向结构；舰船坐标收货和舰载结构牵引保持原策略。
  Personnel with designated vacuum and propulsion gear first become an abstract floating `SpaceObject`, then promote under the same object ID to controllable EVA flight when capable. While mapped they keep one overlapping runtime-only projectile proxy—without another object ID—to forward space hits to the real Pawn. They use EVA-style diagonal corner thrusters with stackable jump-pack thrust. Main fire now invokes the equipped weapon's real `Verb`, preserving bursts, cooldowns, and ammunition/fuel/charge consumption; the actual spawned projectile becomes a transient space object and later runs that same instance's virtual `Impact` on the struck map. The real Verb now receives only an in-map placeholder while its universe firing direction is carried separately, fixing an IsoRim crash on out-of-bounds targets and preventing failed shots from leaving local projectiles that could hit their shooter. Personnel rounds are not point-defense targets, and the first compatibility mode does not yet capture instant beams or fully self-managed launch Verbs. Grapples remain secondary-group held weapons, and their force now counts as operator input instead of being cancelled by flight assist or an active automatic task. Active egress focuses, drafts, pilots, and enters manual fire control when armed. Flying personnel may eject inventory cargo but cannot change equipment. Moon pools—including empty ones—open docking ports, uncovered decks, and vacuum edges support egress and exact-cell boarding. The Board command shares the shuttle hard-docking lifecycle while personnel keep collision avoidance enabled. Guidance targets a 1.5 cells/s pass-through speed relative to the selected landing point and completes below the 2 cells/s safety ceiling instead of requiring a near stop. Pawn and EVA grapples use real bilateral impulses for lightweight bodies and pull their user toward fixed structures; ship coordinate collectors and structural tractors retain their existing policies.
- 修正舱外人员的活动资格、武器朝向和喷射类武器兼容。至少一件覆盖躯干、被原版认定为防真空的服装现在提供一组基础四角斜推，第三方合格服装不再需要 `requiredForFlight` 专用补丁；多件基础服装不重复叠加，跳跃背包等扩展来源继续增加推力。总真空抗性只决定原版持续暴露风险，不再阻止升格或直接触发降格。点击有效出舱口后才分别检查活动装备与真空风险：缺少活动来源可确认后保持不可控漂浮，防护不足者仍可活动但会随时间受伤。诊断分别显示活动资格与真空风险。真实 `Verb` 继续使用安全的图内占位；每次手动瞄准都会更新独立宇宙射向，手动瞄准会话期间原版不再把空闲征召人员强制转回南方，基类及派生 `AimAngleOverride` 均读取该射向且每个真实 getter 只补丁一次。最终公共装备绘制入口会为当前舱外人员主武器恢复同一角度，并把重合占位导致的错误北向前伸量旋转到准星方向，兼容 NewRatkinPlus 将全局持枪流程替换后绕过 `AimAngleOverride` 的行为。调用原版 `Projectile.Launch` 的喷射／扫射 Verb 也可接入真实弹丸转交；读档恢复聚焦人员时重新应用手动驾驶与手动火控规则。
  Corrected EVA personnel mobility eligibility, weapon orientation, and projectile-spray compatibility. At least one torso-covering apparel item recognized by the base game as vacuum-protective now supplies one set of diagonal corner thrusters, so qualifying third-party apparel no longer needs a `requiredForFlight` patch. Multiple base garments do not multiply base thrust, while jump packs and other extension sources still add thrust. Total vacuum resistance now controls native exposure risk only; it no longer blocks promotion or directly forces demotion. Mobility gear and vacuum danger are checked only after the player clicks a valid exit: personnel without mobility gear may be released as uncontrollable drifting objects, while inadequately protected personnel may maneuver but will be harmed by prolonged exposure. Diagnostics report mobility eligibility and vacuum risk separately. The real `Verb` keeps its safe in-map placeholder. Every manual aim sample updates the independent universe direction; while that manual aim session is active, vanilla no longer forces an idle drafted pawn back to south. Base and overridden `AimAngleOverride` getters read the saved direction, with each real getter patched only once. The final shared equipment-draw entry restores that same angle for the active personnel weapon and rotates the erroneous northward reach from an overlapping placeholder toward the reticle, covering NewRatkinPlus replacing the global weapon renderer and bypassing `AimAngleOverride`. Spray Verbs that call the native `Projectile.Launch` use the same real-projectile handoff, while restoring a focused pawn after load reapplies manual piloting and fire control.

@寒月融雪（bilibili）：为什么逆重飞船会显示无法到达地面？因为我有一些其他MOD需要到地表上触发/接取（幻想乡物流沃芬“社长”到来事件，米莉拉坠机魔法少女，超凡斯安威斯坦空投）
- 修复从 Delta-V 自由飞行口袋图启动逆重引擎时，地面目的地显示无法到达并因无效 tile 循环报错的问题。即使所在星区没有 POI，或先前降落产生的地图已经被抛弃，也会从飞船当前宇宙位姿对应的星区正常选择目的地；同时修复降落到普通地表后的收尾空引用。
  Fixed surface destinations appearing unreachable and repeatedly logging invalid-tile errors when launching a gravship from a Delta-V free-flight pocket map. Destination selection now uses the ship's current sector even when that sector has no POI or its previous landing map was abandoned, and landing cleanup on ordinary surface maps no longer throws a null-reference error.

### 0.1.1 — 2026-11-24

#### 核心开发路线进度

- 提供了自动为原版甚至其他mod炮塔添加太空战能力的功能。同时提供了一个“自动适配”设置页，玩家可以手动启用其中被视为“可能引发问题，所以不默认添加太空战能力”的其他炮塔的适配。
  Added automatic space-combat support for vanilla and third-party turrets. A new “Automatic adaptation” settings page lets players manually enable turrets that are supported but not enabled by default because they may cause compatibility issues.
  - 其适配原则是基本上是：尽量保持其原本特性的同时，将其映射为现有速射炮、重炮、激光炮的变种。比如原版的铀弹加农炮就会因为单发射击被归类为重炮，自动加农炮被归类为速射炮。
    The adapter preserves each source weapon's character as closely as possible while mapping it to a rapid-fire gun, heavy gun, or laser variant. For example, the vanilla uranium slug turret becomes a heavy gun because it fires single shots, while the autocannon becomes a rapid-fire gun.
  - 有些mod（比如联合重工、天工铸造）基本上重新搓了武器系统的底层管理，以至于本mod无法将其大部分炮塔识别为炮塔，目前没有额外适配计划
    Some mods, such as United Heavy Industries and Tiangong Foundry, substantially replace the underlying weapon-management system, so Delta-V cannot recognize most of their turrets as ordinary turrets. There are currently no plans for mod-specific adapters.
  - 有些mod（比如墨诺灵）的一些炮塔可能会因为添加了额外的弹仓而产生预期之外的行为导致错误（比如墨诺灵的一些炮塔会在弹仓为空时尝试绘制“缺乏弹药”的变种贴图，但是其在被适配前本身就没有弹仓也没有相应贴图，然后就会报错）
    Some turrets from other mods, such as Monolyn, may behave unexpectedly when an additional magazine is attached. For example, a turret may try to draw a “no ammunition” texture when the new magazine is empty even though the original turret had neither a magazine nor that texture, resulting in an error.

#### 自发常规机制打磨


- 右下角新增真实地图船体渲染开关。开启后，RimExodus Framework 通过源地图的原版绘制流程渲染非聚焦船体，显示完整建筑与地图内容；默认关闭，可随时切回 Delta-V 自定义船壳。（是的，甚至IsoRim都可以！只是注意把IsoRim放在mod list里本mod下面，否则其似乎会覆盖本mod的一些绘制逻辑导致出现奇怪的阴影……虽然放下面也会出现奇怪的影子，但是总归比放上面影子少。目前暂时没有进一步兼容的计划，但如果有人真的很在意这个，可以在评论区呼唤这个影子的兼容）
  Added a bottom-right native map shell rendering toggle. When enabled, RimExodus Framework renders non-focused ships through each source map's vanilla draw path, showing complete buildings and map contents; it is off by default and can be switched back to the Delta-V custom shell at any time.
  - 修复 IsoRim 3D 视图在中键拖拽后重新限制 Delta-V 太空图摄像机范围的问题；普通地图继续保留 IsoRim 的原边界限制。
    Fixed IsoRim 3D middle-button dragging reapplying the camera clamp on Delta-V space maps; ordinary maps retain IsoRim's original boundary.

- 修复从星区内口袋图或同地址的其他地图发射时，误将星区对应 POI 当作出发图并摧毁的问题；现在只有实际出发的同一张世界地图会进入放弃流程。
  Fixed launches from pocket maps or other maps at the same sector address incorrectly treating and destroying the sector POI as the departure map; only the actual source world map can now be abandoned.

- 修复点击原版星球地图的玩家飞船剪影后遗留世界拖拽状态的问题。玩家剪影现在只显示己方飞船；屏幕位置重叠时合并为带数量的代表剪影，点击后可从菜单选择具体飞船。
  Fixed clicking a player-ship silhouette on the vanilla world map leaving world dragging active. Player silhouettes now show only player-owned ships and merge overlapping markers into a counted representative with a ship-selection menu.

- 修复自定义船壳在实体屋顶、月池力场和第三方建筑屋顶交界处错误绘制破口的问题；相邻建筑屋顶现在保持连续，墙、门和气密建筑仍遮住真实屋顶终止边。
  Fixed custom ship shells drawing false breaches where solid roofs, moon-pool force fields, and third-party constructed roofs meet. Adjacent constructed roofs now stay continuous, while walls, doors, and airtight buildings still cover genuine roof ends.

- 修复逆重飞船搬运通过原始屋顶清除入口时未刷新船体结构缓存的问题；月池上的普通船顶移除后会立即恢复力场顶。
  Fixed gravship transfer through the native roof-removal path leaving the ship structure cache stale; removing an ordinary roof over a moon pool now immediately restores its force-field roof.

#### 用户反馈解决

@@UTcurtain：加重一下开门引导。在新手教程开局试了好几次，一直在怼通讯台，打开任何和定位任务都摁过检查过，太空站进去过钢铁也捡过，最后发现是按征召右边的太空生存指导来推进任务。(:steamfacepalm:)
- 把教程引导统一做到了通讯台的星界边境派系交互里，会在第一次与星界边境交互时在通讯台掉落一个附件类型的便携通讯器，装备通讯器的人员可以直接等价于用通讯台呼叫星界边境，以此让喜欢之前交互模式的玩家也能借此继续用之前的交互模式。
  Consolidated the tutorial guidance into the Astral Frontier interaction at the comms console. The first contact now drops an accessory-style portable communicator; a pawn wearing it can contact Astral Frontier as if using the console, preserving the previous interaction style for players who prefer it.

@UTcurtain：全部做到通讯台肯定没问题，这方面的话金鸢尾兰鼠族做的就不错。(也有个交流平台可以直接揣怀里打电话。)我现在在开局的太空站里，卡在了可以右键交互的EVA工程套装，进得去出不来。也能随便拿空间站仓库的各种物资。
- 已为非敌对 EVA、小艇和原版任务穿梭机补充「让己方人员离开载具」命令。图内按原版容器规则下车，单格飞行图等图外舱口会在确认后将活体乘员交给可牵引的太空漂流物链；货物和尸体无需确认。原版整舱「卸载」也复用该事务：取消或交接失败时不提前清理剩余内容与装载状态，整舱成功放出后才清理装载组。
  Added a “Let our personnel leave the vehicle” command to non-hostile EVAs, shuttles, and vanilla quest shuttles. In-bounds occupants use native container placement; out-of-bounds living occupants enter the recoverable space-drift chain after confirmation, while cargo and corpses require no warning. The native full-hold unload command now uses the same transaction and only clears loading state after the complete hold has been released successfully.

@UTcurtain：建议给采矿激光加一个货箱底座，这真空岩自填充塞满了我的飞船空间
- 已将采矿光束改为常规炮座：2×2 小型有一个专用槽，新增的 3×3 中型有两个专用槽，可安装采矿货箱或目标识别器。货箱可以暂存 8 堆采矿产物，满了之后仍会将采矿产物放在采矿光束附近的地上；识别器可以给采矿光束设置采矿产物过滤，不符合过滤条件的产物会被留在太空，不被采矿光束自动吸到船上。
  Mining beams now use standard turret bases: the 2×2 small model has one dedicated slot, while the new 3×3 medium model has two. Each slot accepts either a mining cargo box or a target identifier. A cargo box stores eight stacks of mined material; excess output is placed on the ground near the beam. The identifier adds an output filter, leaving rejected materials in space instead of automatically bringing them aboard.

@UTcurtain：标准姿态-手动驾驶-挪动几下逆重飞船后发射-确认着陆区-切换手动驾驶-尝试移动和选择 。推进器正常发力，但此时我通过定位坐标发现自己根本无法移动，旋转时开始抽搐(角度倾斜和高频角度归正)，这个问题我通过新存档复现了。(之前那份评论是因为我开自动导航就开始抽搐，应该飞船的姿态判断问题)， 解决问题也不是切换驾驶台而是重新进行舱内/外作业按钮切换。
- 已修复：战略降落图现标记为舱外作业图，首次真实出力会将飞船提取到独立飞行图；空轨道降落临时创建的玩家 Home 会在最后一艘船脱离且没有遗留人员、建筑、物品或在途运输后自动删除。
  Fixed: strategic landing maps are now marked as orbital worksites, and first real thrust extracts the ship into an independent flight map. A temporary player Home created by landing on an empty orbital tile is automatically removed after the last ship departs when no pawns, buildings, items, or incoming transports remain.


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
