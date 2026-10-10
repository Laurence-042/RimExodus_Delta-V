using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeltaV
{
    /// <summary>
    /// 武器服务（太空战架构 §5/§6）：武器索引、同一发射事务、炮手分配算法、
    /// 敌对判定与沿射线目标锁定；逐 tick 调度由归属 ShipController 执行。
    /// 实例服务（universe.Weapons）的武器索引不序列化、读档重建；武器持久配置（组别/PD 模式/
    /// 炮手预留）驻留武器 comp、激活位驻留主驾驶台 comp。
    ///
    /// 瞄准会话与战斗位置由归属 controller 按 ownerId 记录：对接合图/脱离换图后
    /// 自动跟随。会话独立于驾驶/自动驾驶——对接中可瞄准。
    ///
    /// 就绪口径（§6.1，单实现）：所在组激活 ∧（MannedNow ∨ 插件（段6））∧ 供电 ∧
    /// 冷却完毕 ∧ 瞄准方向在射界内 ∧ 非地表图（有宇宙容器）。地表/同图战斗完全走
    /// 原版炮塔 comp，本服务零介入（双模互不干涉）。
    /// </summary>
    public sealed class SpaceWeaponService
    {
        private readonly UniverseManager universe;

        // PD 武器随 spawn/despawn/模式切换登记；空态让 controller 立即早退。
        // 读档与 dev 场景重置时从现有图重建一次，不让索引成为事实源。
        private readonly Dictionary<Map, HashSet<CompSpaceWeapon>> weaponsByMap =
            new Dictionary<Map, HashSet<CompSpaceWeapon>>();
        private readonly HashSet<CompMissileAutoloader> pendingMissileReloads =
            new HashSet<CompMissileAutoloader>();
        private readonly HashSet<CompMissileAutoloader> reservedMissileReloads =
            new HashSet<CompMissileAutoloader>();
        private int nextReservationRetryTick;

        public SpaceWeaponService(UniverseManager universe)
        {
            this.universe = universe;
        }

        public void RegisterWeapon(CompSpaceWeapon weapon)
        {
            Map map = weapon?.parent?.Map;
            if (map == null || map.Disposed || weapon.aimMode != SpaceWeaponAimMode.PointDefense) return;
            if (!weaponsByMap.TryGetValue(map, out HashSet<CompSpaceWeapon> weapons))
                weaponsByMap[map] = weapons = new HashSet<CompSpaceWeapon>();
            weapons.Add(weapon);
        }

        public void UnregisterWeapon(CompSpaceWeapon weapon, Map map)
        {
            if (map == null || weapon == null) return;
            if (!weaponsByMap.TryGetValue(map, out HashSet<CompSpaceWeapon> weapons)) return;
            weapons.Remove(weapon);
            if (weapons.Count == 0) weaponsByMap.Remove(map);
        }

        public void NotifyWeaponModeChanged(CompSpaceWeapon weapon)
        {
            Map map = weapon?.parent?.Map;
            if (map == null) return;
            UnregisterWeapon(weapon, map);
            RegisterWeapon(weapon);
            universe.Controls.NotifyGunnerAvailabilityChanged(map);
        }

        public void ForgetMap(Map map)
        {
            if (map == null) return;
            weaponsByMap.Remove(map);
            pendingMissileReloads.RemoveWhere(loader => loader.parent.Map == map);
            reservedMissileReloads.RemoveWhere(loader => loader.parent.Map == map);
            universe.Controls.ForgetMap(map);
        }

        internal void QueueMissileReload(CompMissileAutoloader loader)
        {
            if (loader == null) return;
            reservedMissileReloads.Remove(loader);
            pendingMissileReloads.Add(loader);
        }

        internal void CancelMissileReload(CompMissileAutoloader loader)
        {
            pendingMissileReloads.Remove(loader);
            reservedMissileReloads.Remove(loader);
        }

        internal void NotifyMissileOwnershipChanged(Map map)
        {
            if (map == null || map.Disposed) return;
            foreach (Thing thing in map.listerThings.ThingsOfDef(DeltaVDefOf.DeltaV_Weapon_MissileLauncher))
                thing.TryGetComp<CompMissileAutoloader>()?.QueueReload();
        }

        /// <summary>事件请求在下一游戏 tick 顺序执行；共享弹仓由实际取弹原子消费。</summary>
        internal void TickPendingMissileReloads()
        {
            if (pendingMissileReloads.Count == 0 && reservedMissileReloads.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            if (reservedMissileReloads.Count > 0 && now >= nextReservationRetryTick)
            {
                pendingMissileReloads.UnionWith(reservedMissileReloads);
                reservedMissileReloads.Clear();
            }
            if (pendingMissileReloads.Count == 0) return;
            List<CompMissileAutoloader> due = pendingMissileReloads
                .OrderBy(loader => loader.parent.thingIDNumber).ToList();
            pendingMissileReloads.Clear();
            foreach (CompMissileAutoloader loader in due)
                if (loader.TryReload()) reservedMissileReloads.Add(loader);
            if (reservedMissileReloads.Count > 0 && now >= nextReservationRetryTick)
                nextReservationRetryTick = now + 60;
        }

        public void RebuildWeaponIndex()
        {
            weaponsByMap.Clear();
            foreach (SpaceObject obj in universe.objects)
            {
                Map map = obj.map;
                if (map == null || map.Disposed) continue;
                RegisterMapWeapons(map);
            }
        }

        internal void RegisterMapWeapons(Map map)
        {
            if (map == null || map.Disposed) return;
            foreach (CompSpaceWeapon weapon in AllWeaponsOn(map)) RegisterWeapon(weapon);
        }

        /// <summary>已登记的点防武器，按结构归属交给对应 controller。</summary>
        internal bool TryGetPointDefenseWeapons(Map map, out HashSet<CompSpaceWeapon> weapons)
        {
            if (map != null && weaponsByMap.TryGetValue(map, out weapons)) return true;
            weapons = null;
            return false;
        }

        internal bool HasPointDefenseWeapons(Map map) => map != null && weaponsByMap.ContainsKey(map);

        /// <summary>只读火力估计：武器当前可用性、射程、命中散布、冷却与弹体后效同源。</summary>
        public float EstimateFirepower(Ship ship, float distance, float targetRadius,
            bool missileOnly = false)
        {
            if (!ship.IsValid) return 0f;
            float total = 0f;
            foreach (CompSpaceWeapon weapon in WeaponsOn(ship.map, ship.ownerId))
            {
                if (weapon.aimMode != SpaceWeaponAimMode.Volley || !weapon.GroupActive
                    || weapon.parent.TryGetComp<CompPowerTrader>() is { PowerOn: false }
                    || !weapon.TryGetSkillLevel(out int skill)
                    || !SpaceWeaponShot.HasShotEnergy(weapon)) continue;
                CompSpaceWeaponMagazine magazine = weapon.SelectMagazine();
                if (!weapon.MagazineAvailable(magazine)) continue;
                ThingDef projectile = magazine?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
                bool missile = IsGuided(projectile);
                if (missileOnly && !missile) continue;
                float range = weapon.WeaponProps.range;
                if (distance > range || range <= 0f) continue;
                SpaceImpactEffectDef impact = SpaceImpactEffects.Resolve(projectile, null);
                float damage = impact?.defenderEffect?.damage ?? 20f;
                float spread = weapon.WeaponProps.baseSpreadDeg
                    * SpreadFactorForSkill(skill) * Mathf.Deg2Rad;
                float hitChance = Mathf.Clamp01(targetRadius
                    / Mathf.Max(targetRadius, distance * Mathf.Tan(spread)));
                total += damage * weapon.WeaponProps.impactScale * hitChance
                    * (60f / Mathf.Max(1, weapon.WeaponProps.cooldownTicks));
            }
            return total;
        }

        public float EstimatePointDefense(Ship ship)
        {
            if (!ship.IsValid) return 0f;
            float total = 0f;
            foreach (CompSpaceWeapon weapon in WeaponsOn(ship.map, ship.ownerId))
            {
                if (weapon.aimMode != SpaceWeaponAimMode.PointDefense || !weapon.GroupActive
                    || weapon.parent.TryGetComp<CompPowerTrader>() is { PowerOn: false }
                    || !weapon.TryGetSkillLevel(out _)
                    || !SpaceWeaponShot.HasShotEnergy(weapon)) continue;
                CompSpaceWeaponMagazine magazine = weapon.SelectMagazine();
                if (!weapon.MagazineAvailable(magazine)) continue;
                ThingDef projectile = magazine?.SelectedProjectileDef
                    ?? weapon.WeaponProps.projectileDef;
                SpaceImpactEffectDef impact = SpaceImpactEffects.Resolve(projectile, null);
                total += (impact?.defenderEffect?.damage ?? 20f)
                    * (60f / Mathf.Max(1, weapon.WeaponProps.cooldownTicks));
            }
            return total;
        }

        internal bool PdTargetValid(CompSpaceWeapon weapon)
        {
            SpaceObject target = weapon.pdTarget;
            if (target == null || target.map == null || target.map.Disposed
                || !target.InterceptionEligible || !HostileTo(target, weapon.parent.Faction)) return false;
            return OrbitSphere.Offset(weapon.MuzzleUniverse, target.pose.position).magnitude
                - SpaceCollision.BoundsRadiusOf(target) <= weapon.WeaponProps.range;
        }

        /// <summary>
        /// PD 索敌（§4.2）：射程内敌对的可拦截容器；优先级 =「预计命中己方的最近
        /// 容器优先」（来袭弹逼近自身容器 → 按预计命中时间升序），兜底最近。
        /// </summary>
        internal SpaceObject FindPdTarget(CompSpaceWeapon weapon)
        {
            SpaceObject own = weapon.Container;
            if (own == null) return null;
            Faction faction = weapon.parent.Faction;
            float range = weapon.WeaponProps.range;
            Vector3 muzzle = weapon.MuzzleUniverse;
            SpaceObject best = null;
            float bestScore = float.MaxValue;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == own || !obj.InterceptionEligible) continue;
                if (!HostileTo(obj, faction)) continue;
                Vector2 offset = OrbitSphere.Offset(own.pose.position, obj.pose.position);
                float distance = OrbitSphere.Offset(muzzle, obj.pose.position).magnitude
                    - SpaceCollision.BoundsRadiusOf(obj);
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
            return new SpaceWeaponShot(universe, weapon, container, aimPoint, explicitTarget).TryExecute();
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

        public static float ProjectileSpeedOf(CompSpaceWeapon weapon)
        {
            return SpaceWeaponShot.ProjectedSpeed(weapon);
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
                if (SpaceWeaponGeometry.OwnerIdOf(weapon.parent) == ownerId) result.Add(weapon);
            }
            return result;
        }

        // ---- 战斗位置（§5.5；本图全部可用玩家武器，激活组仅作人手不足时的优先级）----

        public int BattleStationGunnerCount(Map map)
        {
            if (map == null) return 0;
            int count = 0;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (TryGetJobWeapon(pawn.CurJob, out _)) count++;
            }
            return count;
        }

        internal int EndBattleStationJobs(Map map, Faction faction, HashSet<int> ownerIds)
        {
            int released = 0;
            // EndCurrentJob 会同步刷新 MapPawns 派生列表，必须先快照。
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            foreach (Pawn pawn in colonists)
            {
                if (pawn.Faction != faction
                    || !TryGetJobWeapon(pawn.CurJob, out CompSpaceWeapon weapon)
                    || !ownerIds.Contains(weapon.OwnerShip.ownerId)) continue;
                released++;
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            return released;
        }

        private static bool TryGetJobWeapon(Job job, out CompSpaceWeapon weapon)
        {
            weapon = null;
            if (job == null) return false;
            ThingWithComps parent = null;
            if (job.def == DeltaVDefOf.DeltaV_OperateSpaceWeapon)
                parent = job.targetB.Thing as ThingWithComps;
            else if (job.def == JobDefOf.ManTurret)
                parent = job.targetA.Thing as ThingWithComps;
            weapon = parent?.TryGetComp<CompSpaceWeapon>();
            return weapon != null;
        }

        /// <summary>
        /// 玩家/NPC 共用炮手分配。全部可用武器都参与；激活组优先、同优先级内有可用
        /// 预留炮手者优先，再按 thingId 稳定排序。断电窗口不征用，恢复后事件补分配。
        /// </summary>
        internal int DispatchGunners(Map map, Faction faction, int ownerId)
        {
            return DispatchGunners(map, faction, new HashSet<int> { ownerId });
        }

        internal int DispatchGunners(Map map, Faction faction, HashSet<int> ownerIds)
        {
            // 任一状态边沿都重走同一判定；先释放失效岗位，再用同一候选集补位。
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (pawn.Faction != faction || !TryGetJobWeapon(pawn.CurJob, out CompSpaceWeapon assigned)
                    || !ownerIds.Contains(assigned.OwnerShip.ownerId)) continue;
                if (CanStaff(assigned, map, faction, ownerIds, alreadyAssigned: true)) continue;
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            int manned = 0;
            var weapons = AllWeaponsOn(map)
                .Where(w => CanStaff(w, map, faction, ownerIds, alreadyAssigned: false))
                .OrderByDescending(w => w.GroupActive)
                .ThenByDescending(w => GunnerUsable(w.Crew.reservedGunner, w))
                .ThenBy(w => w.parent.thingIDNumber)
                .ToList();
            foreach (CompSpaceWeapon weapon in weapons)
            {
                CompSpaceWeaponCrew crew = weapon.Crew;
                if (crew.MannedNow) { manned++; continue; }
                if (WeaponHasAssignedJob(map, weapon)) { manned++; continue; }
                Pawn pawn = PickGunner(weapon, faction);
                if (pawn == null) continue;
                Job job = JobMaker.MakeJob(DeltaVDefOf.DeltaV_OperateSpaceWeapon);
                job.targetB = weapon.parent;
                if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                {
                    manned++;
                }
            }
            return manned;
        }

        private static bool CanStaff(CompSpaceWeapon weapon, Map map, Faction faction,
            HashSet<int> ownerIds, bool alreadyAssigned)
        {
            if (weapon?.parent == null || !weapon.parent.Spawned || weapon.parent.Destroyed
                || weapon.parent.Map != map || weapon.parent.Faction != faction
                || !ownerIds.Contains(weapon.OwnerShip.ownerId) || weapon.Crew == null
                || weapon.AutoAimEffective) return false;
            return alreadyAssigned && weapon.keepGunnerWhenUnavailable
                || PowerOn(weapon.parent) && weapon.MagazineAvailable(weapon.SelectMagazine());
        }

        private static bool WeaponHasAssignedJob(Map map, CompSpaceWeapon weapon)
        {
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (TryGetJobWeapon(pawn.CurJob, out CompSpaceWeapon assigned)
                    && assigned == weapon) return true;
            }
            return false;
        }

        /// <summary>征用：预留炮手优先（可用时）；否则随机选「胜任 + 可到达 + 未征召」殖民者。</summary>
        private static Pawn PickGunner(CompSpaceWeapon weapon, Faction faction)
        {
            Map map = weapon.parent.Map;
            Pawn reserved = weapon.Crew?.reservedGunner;
            if (reserved?.Faction == faction && GunnerUsable(reserved, weapon)) return reserved;
            var candidates = new List<Pawn>();
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Faction == faction && GunnerUsable(pawn, weapon)) candidates.Add(pawn);
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
            CompSpaceWeaponCrew crew = weapon.Crew;
            return crew != null && crew.GunnerCell.Standable(map)
                && pawn.CanReach(crew.GunnerCell, PathEndMode.OnCell, Danger.Deadly);
        }

        /// <summary>dev ResetObjects / 图销毁时的流程清零。</summary>
        public void ResetFlow()
        {
            weaponsByMap.Clear();
            pendingMissileReloads.Clear();
            reservedMissileReloads.Clear();
        }
    }
}
