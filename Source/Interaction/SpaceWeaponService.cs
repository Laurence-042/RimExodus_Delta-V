using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeltaV
{
    /// <summary>
    /// 武器服务（太空战架构 §5/§6）：每武器每 tick 的就绪判定与发射（玩家瞄准射击 +
    /// PD 自动索敌）、「进入战斗位置」炮手征用/退出、敌对判定与沿射线目标锁定。
    /// 实例服务（universe.Weapons）：瞄准会话（<see cref="SpaceAimController"/>）与
    /// 战斗位置状态等运行时数据不序列化、读档归零；武器持久配置（组别/PD 模式/
    /// 炮手预留）驻留武器 comp、激活位驻留主驾驶台 comp。
    ///
    /// 瞄准会话与战斗位置均按归属域 ownerId 记录：对接合图/脱离换图后自动跟随
    /// （武器与图都换了，ownerId 不变）。会话独立于驾驶/自动驾驶——对接中可瞄准。
    ///
    /// 就绪口径（§6.1，单实现）：所在组激活 ∧（MannedNow ∨ 插件（段6））∧ 供电 ∧
    /// 冷却完毕 ∧ 瞄准方向在射界内 ∧ 非地表图（有宇宙容器）。地表/同图战斗完全走
    /// 原版炮塔 comp，本服务零介入（双模互不干涉）。
    /// </summary>
    public sealed class SpaceWeaponService
    {
        /// <summary>PD 索敌重扫间隔（tick）。</summary>
        private const int PdRetargetIntervalTicks = 30;

        private readonly UniverseManager universe;

        /// <summary>当前瞄准会话（无 = 巡航模式）。会话对象自持全部瞄准工作。</summary>
        private SpaceAimController aimController;

        /// <summary>战斗位置已激活的归属域（运行时）：按 ownerId 记录，换图后随船保持；
        /// 查询/切换时惰性剔除已消失的归属域。不序列化。</summary>
        private readonly HashSet<int> battleStations = new HashSet<int>();

        /// <summary>炮塔状态变更事件登记的待补分配图（事件驱动，见 NotifyWeaponAvailabilityChanged）。</summary>
        private readonly HashSet<Map> pendingRedispatchMaps = new HashSet<Map>();

        public SpaceWeaponService(UniverseManager universe)
        {
            this.universe = universe;
        }

        // ---- 瞄准会话（面板按钮与瞄准键的唯一入口）----

        /// <summary>瞄准会话是否激活（任意船）。</summary>
        public bool AimActive => aimController != null && aimController.Active;

        /// <summary>指定船是否处于瞄准会话中（面板按钮态）。</summary>
        public bool AimActiveFor(Ship ship)
        {
            return AimActive && aimController.ownerId == ship.ownerId;
        }

        /// <summary>瞄准会话是否作用于当前聚焦图（点击让位/射击/标记的共同前提）。</summary>
        public bool AimSessionOnFocusedMap =>
            aimController != null && aimController.SessionOnFocusedMap;

        /// <summary>
        /// 切换瞄准/巡航：同船再切 = 退出；异船 = 切换目标（旧会话析构）。会话独立于
        /// 驾驶与自动驾驶，跨聚焦切换、对接/脱离换图保持（§6.1 修订：PD 不受影响）。
        /// </summary>
        public void ToggleAim(Ship target)
        {
            if (!target.IsValid)
            {
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (aimController != null)
            {
                bool sameShip = aimController.ownerId == target.ownerId;
                aimController.Dispose(sameShip ? "toggled off" : "target switched");
                aimController = null;
                if (sameShip) return;
            }
            aimController = new SpaceAimController(universe, target.ownerId);
            DeltaVLog.Message(DeltaVLogModule.Input, $"aim session engaged (owner #{target.ownerId})");
        }

        /// <summary>帧路径：前台层瞄准标记（无会话 = 不发任何指示器）。</summary>
        internal int EmitAimMarkers(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
        {
            return aimController != null && aimController.Active
                ? aimController.EmitMarkers(frame, frameMap, layer, baseSlot)
                : baseSlot;
        }

        // ---- 战斗位置（§5.5；按归属域记录，跨换图保持）----

        /// <summary>图上是否有任一玩家船处于战斗位置（面板按钮态）。</summary>
        public bool BattleStationsActive(Map map)
        {
            if (map == null) return false;
            PruneStaleBattleStations();
            foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
            {
                if (battleStations.Contains(ship.ownerId)) return true;
            }
            return false;
        }

        /// <summary>剔除已消失归属域的陈旧条目（船销毁/脱离控制域后 ownerId 无船）。</summary>
        private void PruneStaleBattleStations()
        {
            battleStations.RemoveWhere(id => !ShipRegistry.ShipByOwner(id).IsValid);
        }

        // ---- tick（GameComponentTick 内、Projectiles 制导之后、容器积分之前）----

        public void TickWeapons()
        {
            ProcessPendingRedispatch();
            TickAim();
            TickPointDefense();
        }

        // ---- 战斗位置补分配（事件驱动，2026-09-27 用户决策——炮塔状态变更时触发，不做轮询）----

        /// <summary>
        /// 炮塔状态变更登记：事件源为 <see cref="CompSpaceWeapon"/> 的 PowerTurnedOn
        /// 信号（短路损毁后重连、转移后电网恢复）与 PostSpawnSetup（起飞/对接搬运
        /// StripDomain → PlaceIntoMap 后重放置；JobDriver_ManTurret 的
        /// FailOnDespawned 在 DeSpawn 时已丢 job，原版无召回）。信号可能于电网重连/
        /// 放置链的任意调用栈内到达，这里只登记，延迟到 TickWeapons 头部处理——
        /// 仅为离开当前调用栈的安全时点，不是轮询。
        /// </summary>
        public void NotifyWeaponAvailabilityChanged(Map map)
        {
            if (map != null && !map.Disposed)
            {
                pendingRedispatchMaps.Add(map);
            }
        }

        private void ProcessPendingRedispatch()
        {
            if (pendingRedispatchMaps.Count == 0) return;
            foreach (Map map in pendingRedispatchMaps)
            {
                if (map == null || map.Disposed) continue;
                if (!BattleStationsActive(map)) continue; // 战斗位置未启用：事件空转
                DispatchGunners(map);
            }
            pendingRedispatchMaps.Clear();
        }

        /// <summary>瞄准会话射击 tick；会话自检失效（船损毁）时在此收尾析构。</summary>
        private void TickAim()
        {
            if (aimController == null) return;
            aimController.Tick();
            if (!aimController.Active) aimController = null;
        }

        /// <summary>
        /// 点防御（段4 完整接入；先跑基础链验证）：PD 武器自动索敌「可拦截容器」
        ///（结构判据 InterceptionEligible）并按截击提前量射击，不受瞄准模式控制。
        /// </summary>
        private void TickPointDefense()
        {
            int now = Find.TickManager.TicksGame;
            foreach (Map map in Find.Maps)
            {
                SpaceObject container = universe.FrameOf(map);
                if (container == null) continue;
                foreach (CompSpaceWeapon weapon in AllWeaponsOn(map))
                {
                    if (weapon.aimMode != SpaceWeaponAimMode.PointDefense) continue;
                    if (!weapon.GroupActive) continue;
                    if (now < weapon.cooldownUntilTick) continue;
                    if (weapon.Container == null) continue;
                    if (now >= weapon.pdRetargetAtTick || !PdTargetValid(weapon))
                    {
                        weapon.pdRetargetAtTick = now + PdRetargetIntervalTicks;
                        weapon.pdTarget = FindPdTarget(weapon);
                    }
                    SpaceObject target = weapon.pdTarget;
                    if (target == null) continue;
                    // 截击提前量 = 与预瞄同一纯函数；解不出（追不上）本 tick 不射。
                    if (!TrySolveIntercept(weapon, weapon.Container, target,
                        ProjectileSpeedOf(weapon.WeaponProps.projectileDef), out Vector3 interceptPoint))
                    {
                        continue;
                    }
                    TryFire(weapon, weapon.Container, interceptPoint, target);
                }
            }
        }

        private static bool PdTargetValid(CompSpaceWeapon weapon)
        {
            SpaceObject target = weapon.pdTarget;
            return target != null && target.map != null && !target.map.Disposed
                && target.InterceptionEligible;
        }

        /// <summary>
        /// PD 索敌（§4.2）：射程内敌对的可拦截容器；优先级 =「预计命中己方的最近
        /// 容器优先」（来袭弹逼近自身容器 → 按预计命中时间升序），兜底最近。
        /// </summary>
        private SpaceObject FindPdTarget(CompSpaceWeapon weapon)
        {
            SpaceObject own = weapon.Container;
            if (own == null) return null;
            Faction faction = weapon.parent.Faction;
            float range = weapon.WeaponProps.range;
            SpaceObject best = null;
            float bestScore = float.MaxValue;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == own || !obj.InterceptionEligible) continue;
                if (!HostileTo(obj, faction)) continue;
                Vector2 offset = OrbitSphere.Offset(own.pose.position, obj.pose.position);
                float distance = offset.magnitude - SpaceCollision.BoundsRadiusOf(obj);
                if (distance > range) continue;
                // 来袭判定：目标相对速度朝己方逼近 → 预计命中时间（秒）作优先分；
                // 非来袭（掠过/远离）兜底排在所有来袭之后、按距离。
                Vector2 targetVel = SpaceTransform.TransportVelocity(obj.pose, own.pose.position)
                    - own.pose.velocity;
                float closing = offset.sqrMagnitude > 1e-6f
                    ? -Vector2.Dot(targetVel, offset.normalized) : 0f;
                float score = closing > 1f
                    ? Mathf.Max(0f, distance) / closing
                    : 10000f + distance;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = obj;
                }
            }
            return best;
        }

        // ---- 发射（就绪判定单实现）----

        /// <summary>
        /// 尝试发射：就绪判定（组激活已在调用方）→ 炮手/插件 → 供电 → 冷却 → 射界 →
        /// 散布（§5.4：基础散射角 × 技能偏差系数，高斯偏移）→ 弹体服务发射。
        /// aimPoint = 瞄准参考点（鼠标 universe 点或 PD 截击点）；guided 武器无显式
        /// target 时按瞄准方向最近敌方容器锁定（§6.2）。
        /// </summary>
        public bool TryFire(CompSpaceWeapon weapon, SpaceObject container, Vector3 aimPoint, SpaceObject explicitTarget)
        {
            CompProperties_SpaceWeapon props = weapon.WeaponProps;
            if (container == null || props.projectileDef == null) return false;
            if (!weapon.parent.Spawned || weapon.parent.Destroyed) return false;
            int now = Find.TickManager.TicksGame;
            if (now < weapon.cooldownUntilTick) return false;
            // 炮手或插件（§5.4 同一映射）：两者皆无不开火。
            if (!weapon.TryGetSkillLevel(out int skill)) return false;
            if (!PowerOn(weapon.parent)) return false;
            // 轨道炮分体门禁（§5.7）：完整链（直线邻接 + 装弹机构收尾）∧ 装弹机构供电
            // ∧ 有弹药；倍率 = Σ 加速段加成（唯一查询入口），发射成功后消耗 1 弹。
            float damageScale = 1f;
            CompRefuelable railgunLoader = null;
            if (weapon.parent.TryGetComp<CompSpaceRailgun>() != null)
            {
                if (!CompSpaceRailgun.TryGetChain((Building)weapon.parent, out damageScale, out _, out railgunLoader))
                {
                    return false;
                }
            }
            // 射界：瞄准方向相对挂载朝向夹角 ≤ 射界弧/2。
            Map map = weapon.parent.Map;
            Vector2 muzzleMap = new Vector2(weapon.parent.Position.x + 0.5f, weapon.parent.Position.z + 0.5f);
            Vector2 aimMap = SpaceTransform.UniverseToMapPoint(container, map, aimPoint);
            Vector2 mapDelta = aimMap - muzzleMap;
            if (mapDelta.sqrMagnitude < 1e-6f) return false;
            float aimAngle = Mathf.Atan2(mapDelta.y, mapDelta.x);
            float arcLimit = props.fireArcDeg * 0.5f * Mathf.Deg2Rad;
            if (arcLimit < Mathf.PI - 0.01f
                && Mathf.Abs(SpaceTransform.WrapAngle(aimAngle - weapon.FacingAngle)) > arcLimit)
            {
                return false;
            }

            // 散布：基础散射角 × 技能偏差系数（skill 0–20 线性 1.6→0.4），高斯偏移。
            Vector3 muzzle = weapon.MuzzleUniverse;
            Vector2 dir = OrbitSphere.Offset(muzzle, aimPoint);
            if (dir.sqrMagnitude < 1e-8f) return false;
            dir.Normalize();
            float offset = Mathf.Clamp(Rand.Gaussian(0f, 0.4f), -1.2f, 1.2f)
                * props.baseSpreadDeg * Mathf.Deg2Rad * SpreadFactorForSkill(skill);
            dir = SpaceTransform.Rotate(dir, offset);
            OrbitSphere.Basis(muzzle, out Vector3 east, out Vector3 north);
            Vector3 directionWorld = east * dir.x + north * dir.y;

            SpaceObject missileTarget = explicitTarget;
            if (missileTarget == null && IsGuided(props.projectileDef))
            {
                missileTarget = TargetAlongRay(universe, container, muzzle, dir, faction: weapon.parent.Faction);
            }
            bool launched = universe.Projectiles.Launch(props.projectileDef, container, muzzle,
                directionWorld, weapon.parent.Faction, weapon.parent, missileTarget, damageScale);
            if (launched)
            {
                weapon.cooldownUntilTick = now + props.cooldownTicks;
                railgunLoader?.ConsumeFuel(1f);
                // 开火后效接口（§5.8 预留，无实现 def）：发射时刻回调。
                foreach (ThingComp comp in weapon.parent.AllComps)
                {
                    if (comp is IFireEffect fireEffect)
                    {
                        fireEffect.NotifyFired(weapon, container, muzzle);
                    }
                }
            }
            return launched;
        }

        /// <summary>供电判定（原版 comp 状态直读：无供电 comp = 不要求供电，per def）。</summary>
        private static bool PowerOn(ThingWithComps parent)
        {
            CompPowerTrader trader = parent.TryGetComp<CompPowerTrader>();
            return trader == null || trader.PowerOn;
        }

        /// <summary>技能 → 散布偏差系数（§5.4 单一映射：skill 0–20 线性 1.6→0.4；自动瞄准插件的等价技能同函数）。</summary>
        public static float SpreadFactorForSkill(int skillLevel)
        {
            return Mathf.Lerp(1.6f, 0.4f, Mathf.Clamp01(skillLevel / 20f));
        }

        /// <summary>弹体是否制导（导弹）。</summary>
        public static bool IsGuided(ThingDef projectileDef)
        {
            var props = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            return props != null && props.guided;
        }

        public static float ProjectileSpeedOf(ThingDef projectileDef)
        {
            var props = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            return props?.speed ?? 0f;
        }

        /// <summary>
        /// 截击解算（发射点切平面）：目标匀速外推 + 弹速，最小正根；输出截击 universe 点。
        /// 预瞄指示器与本方法共用 SpaceTransform.TrySolveInterceptPlanar 同一纯函数。
        /// </summary>
        public static bool TrySolveIntercept(CompSpaceWeapon weapon, SpaceObject container,
            SpaceObject target, float projectileSpeed, out Vector3 interceptUniverse)
        {
            interceptUniverse = default;
            if (container == null || target == null || projectileSpeed <= 0f) return false;
            Vector3 muzzle = weapon.MuzzleUniverse;
            Vector2 p = OrbitSphere.Offset(muzzle, target.pose.position);
            Vector2 targetVel = SpaceTransform.TransportVelocity(target.pose, muzzle);
            Vector2 muzzleVel = SpaceTransform.TransportVelocity(container.pose, muzzle);
            if (!SpaceTransform.TrySolveInterceptPlanar(p, targetVel - muzzleVel, projectileSpeed, out float time))
            {
                return false;
            }
            interceptUniverse = OrbitSphere.Move(target.pose.position,
                target.pose.velocity * time);
            return true;
        }

        /// <summary>沿瞄准方向最近的敌方容器（射线 × 包围域；导弹锁定 §6.2，无命中按直射不自导）。</summary>
        public static SpaceObject TargetAlongRay(UniverseManager universe, SpaceObject source,
            Vector3 origin, Vector2 dirTangent, Faction faction)
        {
            SpaceObject best = null;
            float bestAlong = float.MaxValue;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == source || obj.map == null) continue;
                if (!HostileTo(obj, faction)) continue;
                Vector2 offset = OrbitSphere.Offset(origin, obj.pose.position);
                float along = Vector2.Dot(offset, dirTangent);
                if (along <= 0f) continue;
                float miss = (offset - dirTangent * along).magnitude;
                if (miss > SpaceCollision.BoundsRadiusOf(obj)) continue;
                if (along < bestAlong)
                {
                    bestAlong = along;
                    best = obj;
                }
            }
            return best;
        }

        /// <summary>
        /// 容器敌对判定（单点）：环境派系 hostile，或图上任何船的派系 hostile
        ///（多船混合按「有任何敌对船即敌对」——武器射击不逐船区分停泊目标）。
        /// </summary>
        public static bool HostileTo(SpaceObject container, Faction faction)
        {
            if (faction == null) return false;
            if (container.faction != null && container.faction.HostileTo(faction)) return true;
            Map map = container.map;
            if (map == null) return false;
            foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
            {
                Faction shipFaction = ShipRegistry.FactionOf(ship);
                if (shipFaction != null && shipFaction.HostileTo(faction)) return true;
            }
            return false;
        }

        /// <summary>鼠标位置映射的 universe 点（瞄准参考点，§6.1）。</summary>
        public static Vector3 MouseUniversePoint(SpaceObject container, Map map)
        {
            Vector3 mouse = UI.MouseMapPosition();
            return SpaceTransform.MapPointToUniverse(container, map, new Vector2(mouse.x, mouse.z));
        }

        internal static bool MouseOverUiWindow()
        {
            // 与 HandleShellClick 同一坐标口径（GUI → screen 后查窗口矩形）。
            return Find.WindowStack.GetWindowAt(UI.GUIToScreenPoint(UI.MousePositionOnUI)) != null;
        }

        /// <summary>图上全部太空武器 comp（扫描 listerThings；每 tick 武器数量级小）。</summary>
        public static List<CompSpaceWeapon> AllWeaponsOn(Map map)
        {
            var result = new List<CompSpaceWeapon>();
            if (map == null) return result;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is Building building && !building.Destroyed)
                {
                    CompSpaceWeapon weapon = building.TryGetComp<CompSpaceWeapon>();
                    if (weapon != null) result.Add(weapon);
                }
            }
            return result;
        }

        /// <summary>指定船（归属域）上的武器。</summary>
        internal static List<CompSpaceWeapon> WeaponsOn(Map map, int ownerId)
        {
            var result = new List<CompSpaceWeapon>();
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null) return result;
            foreach (CompSpaceWeapon weapon in AllWeaponsOn(map))
            {
                if (ownership.OwnerAt(weapon.parent.Position) == ownerId) result.Add(weapon);
            }
            return result;
        }

        // ---- 进入战斗位置（§5.5；作用域 = 本图全部玩家派系武器的激活组）----

        /// <summary>
        /// 切换战斗位置：召集炮手（ManTurret job 批量下发）/ 退出（结束 job）。
        /// 征用规则：gizmo 预留炮手仍可射击且未征召 → 由其操作；否则从
        /// 「胜任（Violent 未禁用）+ 可到达 + 未征召」随机；drafted 不征用。
        /// 记账按图上全部玩家船的 ownerId（换图后随船保持；job 挂在 pawn 上随
        /// 捕获搬运，本就存活）。
        /// </summary>
        public void ToggleBattleStations(Map map)
        {
            if (map == null) return;
            PruneStaleBattleStations();
            var playerShips = new List<Ship>();
            foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
            {
                if (ShipRegistry.FactionOf(ship) == Faction.OfPlayer) playerShips.Add(ship);
            }
            bool anyActive = playerShips.Any(ship => battleStations.Contains(ship.ownerId));
            if (anyActive)
            {
                foreach (Ship ship in playerShips) battleStations.Remove(ship.ownerId);
                foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
                {
                    if (pawn.CurJob?.def == JobDefOf.ManTurret
                        && pawn.CurJob.targetA.Thing is Building turret
                        && turret.TryGetComp<CompSpaceWeapon>() != null)
                    {
                        pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                    }
                }
                Messages.Message("DeltaV_BattleStationsOff".Translate(), MessageTypeDefOf.NeutralEvent, false);
                return;
            }
            int manned = DispatchGunners(map);
            foreach (Ship ship in playerShips) battleStations.Add(ship.ownerId);
            Messages.Message("DeltaV_BattleStationsOn".Translate(manned), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>
        /// 战斗位置炮手征用（唯一计算入口：ToggleBattleStations 开启与状态变更补分配
        /// 共用）。征用规则：图上玩家武器的激活组 ∧ Mannable ∧ 供电 ∧ 未在岗 →
        /// PickGunner 下发 ManTurret；断电窗口不征用（炮手站死炮无意义），恢复供电后
        /// 由 PowerTurnedOn 事件补上。返回征用后已在岗/已征用数。
        /// </summary>
        private static int DispatchGunners(Map map)
        {
            int manned = 0;
            foreach (CompSpaceWeapon weapon in AllWeaponsOn(map))
            {
                if (weapon.parent.Faction != Faction.OfPlayer) continue;
                if (!weapon.GroupActive) continue;
                if (weapon.Mannable == null) continue;
                if (weapon.Mannable.MannedNow) { manned++; continue; }
                if (!PowerOn(weapon.parent)) continue;
                Pawn pawn = PickGunner(weapon);
                if (pawn == null) continue;
                Job job = JobMaker.MakeJob(JobDefOf.ManTurret, weapon.parent);
                if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                {
                    manned++;
                }
            }
            return manned;
        }

        /// <summary>征用：预留炮手优先（可用时）；否则随机选「胜任 + 可到达 + 未征召」殖民者。</summary>
        private static Pawn PickGunner(CompSpaceWeapon weapon)
        {
            Map map = weapon.parent.Map;
            Pawn reserved = weapon.reservedGunner;
            if (GunnerUsable(reserved, weapon)) return reserved;
            var candidates = new List<Pawn>();
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (GunnerUsable(pawn, weapon)) candidates.Add(pawn);
            }
            return candidates.RandomElementWithFallback();
        }

        /// <summary>殖民者可操作该武器：在册、未征召、非倒下、Violent 未禁用、可预约可到达。</summary>
        private static bool GunnerUsable(Pawn pawn, CompSpaceWeapon weapon)
        {
            if (pawn == null || !pawn.Spawned || pawn.Downed || pawn.Drafted
                || pawn.WorkTagIsDisabled(WorkTags.Violent) || pawn.InAggroMentalState)
            {
                return false;
            }
            Map map = weapon.parent.Map;
            if (pawn.Map != map) return false;
            if (!pawn.CanReserve(weapon.parent)) return false;
            Thing parent = weapon.parent;
            return !parent.def.hasInteractionCell
                || pawn.CanReach(parent.InteractionCell, PathEndMode.OnCell, Danger.Deadly);
        }

        /// <summary>dev ResetObjects / 图销毁时的流程清零。</summary>
        public void ResetFlow()
        {
            aimController?.Dispose("reset");
            aimController = null;
            battleStations.Clear();
            pendingRedispatchMaps.Clear();
        }
    }
}
