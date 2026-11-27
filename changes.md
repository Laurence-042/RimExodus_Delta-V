# 更新记录 / Changelog

本文件记录已发布版本的玩家可见变化，并列出下一版本的近期计划。计划项不是已实现功能，也不承诺固定发布日期；完整路线与待验收项分别见 [开发路线](doc/SteamWorkshop/discussions/开发路线%20-%20Development%20Roadmap.txt) 和 [TODO](doc/TODO.md)。
This file records player-visible changes in released versions and the near-term plan for the next version. Planned items are not implemented features and have no guaranteed release date; see the [development roadmap](doc/SteamWorkshop/discussions/开发路线%20-%20Development%20Roadmap.txt) and [TODO](doc/TODO.md) for the complete roadmap and pending verification.

## 下一版本计划 / Next version plan



- 为跳帮艇被击毁时的乘员死亡补充明确通知？单纯爆开把乘员无伤扔出来？

## 基于收集的用户反馈的正在排期的需求（排名不分先后）

@某光头的神棍先知（bilibili）：希望能兼容VGE，我要造10000格以上的超级大飞船去肘击敌人，最好把VGEC2的敌方飞船和战斗空间站也做成可以这样战斗[doge][doge][doge]
- 考虑禁用VGEC2的飞船战斗机制，但是适配其武器，将其事件触发转接到DeltaV的类似袭击事件生成上——需要明确VGE的飞船推进器可能不足以承担DeltaV的战斗要求，可能会在转向能力限制下做出愚蠢的行为

@亚空间魔丸丰川祥子（bilibili）：可以弄成对30以下的伤害免疫?（指毁伤效果）
- 我搞完手头这些会考虑给个可以调的设置阈值，默认0
- 不对，我得考虑点防……只让墙有这种特性？

@Alexquiet：could you please increase the quantity of goods available at stations?
- 这个功能现在其实有点占位的感觉……目前其核心机制其实是直接调用原版商人库存生成器，所以原版商人卖多少它就在补货时使用多少货物替换供货的锁定货柜的内容（会强制预留1/3的空间用来放玩家卖出的货物，所以总是不会满）。实际数量我还没有做好平衡。贸易系统仍是一个占位符，仅在此处用于演示。除了这个空间站，其他所有贸易站都有自动生成的布局和货柜接线，不保证能正常工作。我们还计划在“空间站自给自足”开发目标完成后，对贸易系统进行大幅重构，替换掉目前这种实际上只是原版商人的机制

@瓦斯气体（bilibili）：问下左边的操作菜单能不能移动，会跟角色日志和装备按钮形成干涉 
- 暂时不行，可以在0.1.4加

@不看路的路人（bilibili）：子弹打护盾的声音有些太刺耳了。考虑到模组中似乎也在真空中做了声音适配，把子弹打到护盾上的声音改成固体传导或稀薄空气下传导的沉闷声音会不会更有代入感。[鼓掌]
- 啊，确实，做舱壁受击声音后处理的时候忘记一起做护盾受击和护盾失效了，如果没有意外我应该能在0.1.4把它整好

@jeff_steveng之前报告的归档空间站无法还原的问题已在未发布版本修复。但还有个遗留计划：空间站自维持尚未实现，在那之前玩家离开后 NPC 船员仍可能饿死。

@C鼬柠檬茶（bilibili）：空间站仓库里的东西免费拿真的没问题吗，我前期就靠这个苟着
- 当前空间站仓库里的物品可以直接拿。考虑给非封存态的货仓也加基于派系的去用判断，应该不需要影响其他储物设备，否则一些mod给玩家的“来自盟友的战前补给”就会很黑色幽默
- 最好把空间站自维持搞定后再做

@C鼬柠檬茶（bilibili）：当前空间站的地板和地基没有拆除鉴权
- 最好把空间站自维持搞定后再做

## 一些俺寻思

一些不在计划里，也不一定会做的突发灵感。但如果大家特别喜欢里面某些想法的话可以说一声，我会进一步进行实现成本调研，如果不会带来很多麻烦的话我会在推进计划的空档将其做出来

- 缴获海盗通讯器，诱骗海盗货船到埋伏点
  - 相同逻辑也可以做“盟友通讯器呼叫盟友飞船增援”的玩法
- 在某些任务里搭乘盟友的跳帮艇，和十几个其他盟友跳帮艇一起进行饱和式跳帮

## 已完成 / Shipped

### 未发布 / Unreleased

#### 修复 / Fixes

- 修复联合重工（SRALib 系）等自驱 TurretTop 的炮塔在太空模式头部固定不转、三联涡轮激光炮炮管与头部朝向脱节、以及炮塔朝向偏离射击方向的问题：头部渲染替换现仅用于角度由 Delta-V 接管的原版结构炮塔，其余来源放行其自身绘制（后坐偏移仍注入），非聚焦船壳经来源自身维护的顶部角度显示；协议格方位保真改为优先于来源射程带——同方位档内先取射程带内格，贴边或最小射程过大时直接用同方位任意图内格，`OrderAttack` 层的最小射程拒绝经最小射程公式替答为 0 一并消除。修复为纯通用判定，不含按 Mod 特判。
  Fixed mod turrets that drive their own TurretTop (UF Heavy Industrie / SRALib and similar) showing a frozen turret head in space mode, the triplet turbolaser' barrels disagreeing with its head, and heads aiming away from the actual shot direction: the render replacement now applies only to vanilla-structured turrets whose orientation Delta-V owns, every other source keeps its own draw (with recoil offset still injected), non-focused hulls read the source's live top angle, and protocol-cell bearing fidelity now outranks the source range band — an in-band cell is preferred within one bearing step, map edges or a long minimum range fall back to any cell on the exact bearing, and the OrderAttack-level minimum-range rejection is answered as zero through the shared minimum-range formula. The gates are purely generic with no per-mod checks.

- 修复 Milira 一类自建炮塔逻辑的 Mod 炮塔在太空模式既不跟随瞄准也无法开火，以及 Dead man switch 类武器开火瞬间炮口甩向无关方向的问题。自动适配的接管面从原版 `Building_TurretGun` 子类扩大到全部 `Building_Turret` 实现：开火授权改在来源武器发射门现场续期与重建，第三方炮塔的慢速摆头和长预热不再令授权过期死锁；本地协议格改为发射方位的代理，来源对它做的本地射程／视线判定由火控替答，炮塔贴边或最小射程过大不再直接拒射；太空模式下不再采信来源武器的本地瞄准角覆盖绘制炮身。非原版结构的炮塔保留其原生预热与选中指示（方向已对齐真实目标）。
  Fixed mod turrets that rebuild turret logic on the abstract base (e.g. Milira) neither tracking nor firing in space mode, and Dead-man-switch-style weapons snapping their muzzle elsewhere the moment they fire. Adaptation now spans every `Building_Turret` implementation instead of only `Building_TurretGun` subclasses: the cast gate renews or rebuilds the fire authorization on site so slow third-party head sweeps and long warmups no longer deadlock; the local protocol cell is now a bearing proxy whose local range and sight checks are answered by fire control, so map edges or a long minimum range never refuse the shot; and in space mode a source verb's local aim-angle override is no longer trusted when drawing the turret head. Non-vanilla turrets keep their native warmup and selection visuals, now pointing at the real target.

- 修复 EVA 登舰时在所选格附近来回移动、无法进入：人员与 EVA 的登舰完成不再要求钩索曾经挂上目标，钩索只作为可选加速；EVA 未启用武装或钩索不可用时仅用推进器进场。
  Fixed EVA boarding oscillating around the selected cell without entering. Personnel and EVA boarding no longer requires the tether to have hooked the target: the tether is an optional accelerator, and an EVA without an armed or usable tether approaches on thrusters alone.

- 统一经纬网格与比例尺的步长与密度机制：宇宙图网格、星区图面板网格与左下角比例尺原本各自取整——宇宙图经度补偿按 2 的幂向上取整，除赤道外格子横向最多偏宽 2 倍；面板网格按度数档、比例尺按 1-2-5×10ⁿ 格数档，同一缩放下可差近一倍；面板还残留一条只把经线间距翻倍的旧密度保险，使展开的导航窗口在约半数缩放档位下格子 2 倍偏宽而纬向仍等于比例尺。现在三处共用 OrbitSphere 的同一选择入口（1-2-5 格数阶梯 + 纬度补偿 + 整圆整数等分保接缝同相位），线数上限也统一为共享的截断式规则（超限只截断边缘线、永不放大步长）：宇宙图格子在任意纬度目视方正、边长为整档格数；紧凑与展开面板的所有档位下格子两边均与比例尺线段等长，跳档同步。
  Unified the coordinate-grid step and density mechanism: the space-map grid, the sector globe grid and the bottom-left scale bar each snapped to their own ladders — the space map's longitude compensation rounded up to powers of two, making cells up to 2× too wide everywhere off the equator; the globe used degree steps and the bar the 1-2-5×10ⁿ cell ladder, disagreeing by nearly 2× at one zoom; and the globe kept a legacy density guard that doubled only the longitude spacing, leaving expanded-navigation cells 2× too wide at about half the zoom rungs while the vertical edge still matched the bar. All three now share one OrbitSphere entry point (the 1-2-5 cell ladder, latitude compensation, and whole-circle integer division keeping the ±π seam in phase), with one shared truncation cap for line count (excess lines are dropped, steps are never enlarged): space-map cells look square at any latitude with edges on round cell counts, and both compact and expanded globe cells match the scale bar segment on both sides at every rung, re-stepping together.

#### 自发常规机制打磨

- 聚焦接管补全：没有自动任务的非基架可控飞船（人员、EVA、shuttle）被聚焦时进入手动驾驶，有武器的船同时启用武装并选择手动集火。此前只有人员／EVA 会切换火控、不会启用武装，shuttle 不受影响，并且只有 Delta-V 自己的切图入口会触发；现在任何切入地图的方式都应用该规则。
  Focusing an idle non-foundation craft (personnel, EVA, shuttle) now enters manual piloting, arms craft that carry weapons and selects manual fire control. Previously only personnel and EVA switched fire control, nothing was armed, shuttles were skipped, and only Delta-V's own map switches triggered the rule; any switch onto the craft's map now applies it.

- 自动适配炮塔在太空模式冷却期间从炮身顶部冒烟，与重炮同款：同一条宇宙粒子流、同一套蓬松消散参数，随船体运动、随炮身转向，冷却结束即停。冷却状态直接读炮塔自带的冷却倒计时：原版结构与镜像该字段的第三方自建炮塔通用，未镜像者无烟降级。地面模式与原生舰炮表现不变。
  Auto-adapted turrets now vent smoke from the turret top while cooling down in space mode, matching the heavy cannon: the same ship-bound particle stream and puffy fade-out profile, following both the hull and the turret's rotation, and stopping when the cooldown ends. The cooldown fact is the turret's own countdown: vanilla-structured turrets and third-party rebuilds that mirror the vanilla field both work, while sources without the mirrored field simply stay smokeless. Ground mode and native ship guns are unchanged.

### 0.1.3 — 2026-11-27

#### 用户反馈解决 / User feedback resolved

@Soulless Jelu：Enemy EVA Pwans cannot be damaged, They just flying around until the die from hunger =(
- 修复敌方 EVA 人员无法受伤、一直漂浮直到饿死的问题。舱外人员现在按体型判定命中，原生弹丸与高爆舰炮命中后可以正常造成伤害
  Fixed enemy EVA pawns being unable to take damage and drifting until they starved. Spacewalking pawns now use body-size hit checks, and native projectiles and high-explosive ship shells can damage them after a hit. Automatic ship targeting

@jeff_steveng：几个小问题：
1.飞船行驶时会受太阳耀斑影响，考虑到维生系统基本上得靠电力驱动，而且奥德赛本来轨道上太阳耀斑是不造成影响的，可能改一改比较好
2.飞船在飞行而未开启EVA时，小人不会感觉自己在太空，导致星舟永嗣模因的小人会扣心情——虽然本质上是pocketmap的锅，但是还是很难绷
- 第 1、2 条：飞船与空间站口袋图现在按轨道层处理环境与星球层判定，与真实轨道图一致：不再受太阳耀斑影响（日食与极光照常），「太空居所」信条（含复用原版该判定的模因）与「低重力适应」基因在飞船上按太空计算，观星报告、驾驶台航程显示和条件提示使用轨道口径。原版对轨道层的限制同样生效：迫击炮无法操作，帝国的劳工队、劳工帮、三档军事援助、两种无人机打击和运输穿梭机许可，以及天灯节在飞船上不可用。口袋图仍不会被 storyteller 选为事件目标。
  Items 1 and 2: Ship and station pocket maps now follow orbit-layer environment and planet-layer rules, matching real orbital maps: solar flares no longer affect them (eclipses and auroras still do), the "space habitat" precept (including memes that reuse the vanilla check) and the "low gravity adapted" gene treat ships as space, and stargazing reports, pilot-console range readouts and condition tooltips use orbit wording. Vanilla orbit-layer restrictions apply as well: mortars cannot be manned, and the Empire's laborer team, laborer gang, three tiers of military aid, two aerodrone strike permits and transport shuttle permit, along with the skylantern festival, are unavailable aboard ships. Pocket maps are still never chosen as storyteller incident targets.

@未知者-RX（bilibili）：想给炮台加插件小人一定会把炮台先拆掉，再修插件，我只能重新拍炮塔
- 小人给炮塔、轨道炮部件、采矿设备或货柜安装覆盖插件时，不再先拆除宿主。附加弹仓、自瞄、激光与轨道炮电容、轨道能量回收与快速复位、采矿货箱与目标识别器、货柜温控模块现在和电缆一样保留施工格上的既有建筑。
  Installing an overlay attachment no longer makes pawns deconstruct the host first. Auxiliary magazines, auto-aim, laser and railgun capacitors, railgun energy recovery and rapid reset, mining cargo modules and target identifiers, and the cargo temperature module now keep the existing building on their cell, like power conduits.

@未知者-RX（bilibili）：原版的加农炮也不索敌了
- 0.1.2 的自动适配功能引入的bug，错误地将被适配的武器默认模式设成太空战模式了。已修复
  A bug introduced by the automatic adaptation feature in version 0.1.2 has been fixed; it was incorrectly setting the default mode for adapted weapons to "space combat mode."

@Alexquiet：When playing with Ideologies, it appears that colonist roles get reset whenever the ship switches to a different mode.
- 修复启用 Ideology 时，随船切换飞行、舱外作业或对接状态会清空道德导师等单人职位人选的问题；殖民者全部在船上时每次都会触发。搬运瞬间船上人员不属于任何地图，原版在第一名殖民者落图时重数信徒并把职位判为失活。现在搬运期间按原版逆重飞行的方式登记在途飞船，职位人选保持不变。
  Fixed Ideology roles with a single holder, such as the moral guide, being cleared whenever the ship they were on switched between flight, worksite and docking states. This happened every time while the whole colony was aboard: pawns briefly belonged to no map during the transfer, so the base game's believer recount on the first colonist spawn deactivated the role. The ship in transit is now registered like a native gravship flight, and role holders are preserved.

@未知者-RX（bilibili）：建议是和本体开局给的教程一样给个教程白信封或者给个交易教程，比如开局摸到的75铁换几个白银做个演示
- 最后一段领船对话说明本站交易：对接后在西泊口交易终端买卖，站内无人机按飞船的轨道交易信标取货，购入货物放在终端旁的取货柜自取。对话附开发人员注释：贸易系统目前为占位实现，其他贸易站的布局与货仓接线为自动生成、不保证可用，计划在空间站自我维持后重构。
  The final ship handover dialogue now explains trading at this station: dock, then buy and sell at the west berth terminal. Station drones collect cargo through the ship's orbital trade beacon, and purchases are left in the pickup container near the terminal. It also carries a developer note: the trade system is a placeholder for now, other trade stations are auto-generated and not guaranteed to work, and a rework is planned after station self-sufficiency.

@未知者-RX（bilibili）：最重要的，开局我没武器我拿头打第一波袭击😡至少起个斯捷奇金（自动手枪）让我e一局啊，富有冒险家开都有一把电步，空投袭击又必然是高科技派系，发一个战锤一把自动手枪一把重冲完全没问题吧
- 教程开局在队长休眠舱旁新增一把电荷步枪。
  The tutorial start now includes a charge rifle beside the captain's cryptosleep casket.

@jeff_steveng：目前我的存档在靠近最开始的船厂后log会疯狂刷无法restore地图，删掉这个船厂后会报告95号prefabr无法estore。
- 第 4 条：修复带尸体的地图在存成快照后、存读档一次就无法还原的问题。空间站或舰船离远后被存成快照时，只要图上留着尸体（比如空间站船员饿死），读档后它就再也还原不出来：靠近时游戏严重卡顿、日志刷屏。现在快照存续期间会一并保住尸体对应的人物，还原或退役后照常处理。已经出问题的存档按新规则也应当能还原：人物已丢失的尸体会被移除，污渍缺失不再阻止还原，其余内容照常还原。确实无法还原的对象不再反复重试拖慢游戏，改为逐步拉长重试间隔（约 42 秒起、最长一天），并只提示一次。游戏内效果待验证。
  Item 4: Fixed maps that contain corpses becoming impossible to restore after being archived and reloading the save. When a station or ship far away was archived with corpses left on its map (for example, station crew that starved), it could never be restored again after a reload: approaching it caused heavy lag and log spam. The pawns behind those corpses are now kept while the archive exists and handled as usual once it is restored or retired. Saves already affected should restore under the new rules: corpses whose pawn is gone are removed, missing filth no longer blocks the restore, and everything else comes back as usual. An object that really cannot be restored no longer retries constantly and slows the game; retries start about 42 seconds apart and stretch up to a day, with a single notice. In-game results are still to be confirmed.

#### 自发常规机制打磨

- 聚焦非substructure架构（比如穿梭机、eva人员等等）、没有正在执行的自动任务的可控船会自动进入手动驾驶并使用手动火控。
  Controllable ships that do not utilize a "substructure" architecture (such as shuttles, EVA craft, etc.) and have no automated tasks currently in progress will automatically switch to manual control, with manual fire control enabled.

- 射失并远离所有潜在目标的太空弹体会按保守净空降低精确碰撞检测频率，以此优化大量长射程慢弹速弹丸射失后的性能。
  For space projectiles that miss and move away from all potential targets, the frequency of precise collision detection is reduced—while maintaining a conservative safety margin—to optimize performance following the miss of a large volume of long-range, low-velocity projectiles.

- 自动适配炮塔不再要求 Def 声明弹体：原版迫击炮和按已装填弹药决定弹种的 Mod 炮塔现在可以在太空模式开火，爆炸、EMP 与燃烧弹也按各自的原版后效命中。瞄准提前量改用来源实际射出弹丸的速度；太空模式下曲射弹不再受「屋顶下不得开火」限制。来源开火却不产生可接管射弹的炮塔会提示一次并停止太空模式开火，地面模式不受影响。适配分析报告升为 schema 4。2026-11-27 用户实测无误并授权提交。
  Auto-adapted turrets no longer need a projectile declared in their def: the vanilla mortar and mod turrets whose round depends on the loaded shell can now fire in space mode, and explosive, EMP and incendiary rounds hit with their own vanilla effects. Lead is now computed from the speed of the round actually launched, and in space mode overhead rounds are no longer blocked by the "no firing under a roof" rule. A turret whose shot launches nothing capturable is reported once and stops firing in space mode; ground mode is unaffected. The adaptation report is now schema 4. Confirmed in game by the user on 2026-11-27.

- 使用 REF 真实地图绘制时，可见邻船上的原版爆炸现在会正常显示爆闪、烟雾等地图内视效；Delta-V 的宇宙爆炸仍独立绘制。该修复由 REF 1.0.7 提供、。
  With REF native map rendering, vanilla explosions on visible neighboring ships now show their map-local flashes, smoke, and related effects. Delta-V's universe-space explosions remain separately rendered. This is provided by REF 1.0.7.

#### 修复 / Fixes

- 修复舰载点防把舱外人员当作拦截目标、敌舰主炮把附近舱外人员选作攻击目标的问题，手动瞄准直射或者人员飞到火线上仍可能命中。
  Fixed an issue where ship-mounted point-defense systems would target personnel outside the ship as if they were incoming projectiles, and another issue where enemy capital ship main guns would select nearby personnel outside the ship as targets. Note that personnel may still be hit if they drift into the line of fire or if a weapon is aimed manually.

- 修复制导导弹发射后不追踪目标、只沿发射方向直飞的问题。
  Fixed guided missiles flying straight along their launch direction instead of tracking their target.

### 0.1.2 — 2026-11-26

#### 核心开发路线进度 / Core development progress

- 殖民者现在可以穿戴覆盖躯干、具有真空防护的服装进行舱外飞行。基础推进方式与 EVA 工程套装相同，跳跃背包等推进装备可以继续叠加；真空防护不足者仍可活动，但长时间暴露会受伤。缺少舱外活动装备的人员被抛入太空后只能漂浮，需要由其他人员、抓钩或牵引设备回收。
  Colonists can now perform EVA flight while wearing torso-covering vacuum-protective apparel. Their basic propulsion matches the EVA engineering suit, and jump packs or other propulsion gear add to it. Pawns with inadequate vacuum protection can still maneuver but suffer from prolonged exposure. Pawns without EVA-capable gear drift uncontrollably and must be recovered by another pawn, a grapple, or a tractor device.
- 人员可以从月池、开放对接口、露天甲板和真空边缘主动出舱，随后自动进入征召与驾驶状态；也可以飞向月池、开放对接口或其他与真空相邻的可站立位置登舰，不需要撞破船体，也不必先完全停稳。飞行中可以抛出背包货物，但不能更换装备。
  Personnel can exit through moon pools, open docking ports, exposed decks, and vacuum edges, then automatically enter drafted flight control. They can board through moon pools, open ports, or other standable cells adjoining vacuum without breaching the hull or first coming to a complete stop. Inventory cargo may be released in flight, but equipment cannot be changed.
- 舱外人员可以直接使用手持远程武器进行太空射击，保留原武器的连射、冷却、弹药、燃料与充能规则。抓钩可以把使用者拉向结构、让双方可动物体互相靠近，并在接触后收取货物或抱起其他人员。当前支持标准射弹以及多数喷射／扫射武器，并修正 NewRatkinPlus 等第三方持枪显示替换造成的武器朝向问题；即时光束和完全不生成原版射弹实体的武器暂不支持。
  EVA personnel can fire equipped ranged weapons in space while retaining their burst, cooldown, ammunition, fuel, and charge rules. Grapples pull the user toward fixed structures, draw two movable bodies together, and collect cargo or carry another pawn on contact. Standard projectiles and most spray or sweeping weapons are supported, and weapon orientation now works with third-party render replacements such as NewRatkinPlus; instant beams and weapons that never create a vanilla projectile entity are not yet supported.
- 原版、Mod 炮塔与人员武器现共用同一条原生射弹接管链。太空开火保留来源 Verb、弹药、连射与冷却；实际生成的射弹在首次本地 tick 前进入宇宙弹道，命中后由同一实例执行来源 `Impact`，因此爆炸与贯穿后效不再被换算模板替代。静息时不扫描炮塔或射弹；即时光束仍走转换模式，自管制导／特殊飞行默认不自动启用。
  Vanilla turrets, mod turrets, and pawn weapons now share one native-projectile handoff pipeline. Space fire retains the source Verb, ammunition, burst, and cooldown; each real projectile enters universe flight before its first local tick and later executes its source `Impact` as the same instance, preserving explosive and penetrating effects. No turret or projectile scan runs while idle. Instant beams remain converted, while self-managed guidance or special flight is not enabled by default.
- 修复舱外人员射弹接管后，同一次原版 `DoTick` 继续以空 Map 执行 `TickInterval` 的报错。人员与炮塔现在都以宇宙准星为中心线，把各枚 Launch 相对占位中心的位移按来源 Verb 射程换算成角偏移；承载图大小和占位距离不再放大随机抖动，一格图中坍缩的通用 spray 扇面也会按来源参数恢复。
  Fixed an error where a pawn projectile continued into native `TickInterval` with a null map after handoff in the same `DoTick`. Personnel and turrets now share a universe center ray and convert each Launch displacement around its placeholder center into an angular offset using the source Verb range. Carrier-map size and placeholder distance no longer amplify jitter, while generic spray patterns collapsed by a one-cell map are restored from their source parameters.
- 修复原版与 Mod 炮塔在太空模式反复瞄准却不开火：来源 `OrderAttack` 的临时目标现在会在授权预热和 burst 期间保留，占位格也严格遵守来源 Verb 的最小与最大射程。
  Fixed vanilla and mod turrets repeatedly aiming without firing in space mode. The temporary target issued through the source `OrderAttack` now survives the authorized warmup and burst, and its placeholder cell stays within the source Verb's minimum and maximum range.
- 已验证 **The Dead Man's Switch - MobileDragoon** 的太空用龙骑装甲可以提供舱外活动能力，能够携带武器进行一定程度的太空交战，并完成跳帮登舰作战。该结果仅代表已测试装备与流程，不表示该 Mod 的全部内容均已适配。
  The space-rated Dragoon armor from **The Dead Man's Switch - MobileDragoon** has been verified to provide EVA mobility, support limited space combat with equipped weapons, and complete boarding actions. This result covers the tested armor and workflow, not every feature in that mod.

#### 用户反馈解决 / User feedback resolved

@未知者-RX（bilibili）：所以新手教程最后一步怎么搞，我已经占领了逆重引擎给船起了名字但还是不让我交任务说是让我去逆重引擎附近确认什么，我真不知道确认什么了……我是去通讯台交的任务，实际上要站在引擎旁边用通讯器交任务……
- 教程最后一步现在使用原版保存的逆重引擎检查记录。选中殖民者、右键检查任意一台逆重引擎后，即可通过任意通讯台或便携通讯器确认交接；提前检查、离开引擎以及存读档后仍然有效，不再要求通讯时站在赠船引擎旁。
  The final tutorial handover now uses the base game's saved grav-engine inspection record. After a colonist inspects any grav engine through its right-click action, the handover can be confirmed from any comms console or portable communicator. Earlier inspections, leaving the engine, and save/load all remain valid; the pawn no longer needs to stand beside the prototype engine while calling.

@老耗子比你萌：还有自动添加太空模式的炮台优先级疑似太高了点，地面上机械集群的炮塔默认太空模式，导致其无法攻击，整个机械集群只有地狱火炮塔在输出
- 自动获得太空战能力的原版与第三方炮塔现在默认使用地面模式，继续使用来源炮塔自己的索敌、弹药和冷却；玩家仍可手动切换到太空模式。机械集群等地面生成的自动适配炮塔不再因默认太空模式而停止攻击地面目标。
  Automatically adapted vanilla and third-party turrets now default to ground mode, retaining their source targeting, ammunition, and cooldown until the player switches them to space mode. Ground-spawned adapted turrets such as mech-cluster defenses no longer stop attacking local targets because they initialized in space mode.

@老耗子比你萌：在地面补给后发射去太空，起飞结束时当前地图索引越界，随后 Vulkan 崩溃。
- 修复飞船从地面起飞进入太空后可能留下无效地图索引并导致崩溃的问题。起飞结束时会安全切换到仍可查看的地图；没有可用地图时返回世界视图。
  Fixed a crash caused by an invalid map selection after a gravship launched from the surface into space. Launch completion now switches safely to another viewable map, or returns to the world view when none remains.

@寒月融雪（bilibili）：为什么逆重飞船会显示无法到达地面？因为我有一些其他MOD需要到地表上触发/接取（幻想乡物流沃芬“社长”到来事件，米莉拉坠机魔法少女，超凡斯安威斯坦空投）
- 逆重飞船现在可以从 Delta-V 自由飞行区域正常选择并前往地表目的地，即使当前星区没有空间站等地点，或此前的降落地图已经被抛弃。降落到普通地表后的收尾也不再报错。
  Gravships in Delta-V free flight can now select and reach surface destinations even when their current sector has no station or other location, or when an earlier landing map was abandoned. Cleanup after landing on an ordinary surface map no longer errors.

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

@jeff_steveng：当距离较远时，自动导航不会沿测底线方向驾驶船只，此外有时会用垂直于船头方向的rcs推进器而不是沿船体方向的主推进器推进
- 应该不是问题，飞船侧推比较强时，同时斜向侧置和后置推进器合力比单独直向后置推进器更大，自动驾驶就会用合力更大的方案
