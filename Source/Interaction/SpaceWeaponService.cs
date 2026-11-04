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
    /// 只读瞄准能力与实际转向；战术目标和开火请求由每船 ShipFireControl 提供。
    /// 实例服务（universe.Weapons）的武器索引不序列化、读档重建；武器持久配置（组别/PD 模式/
    /// 炮手预留）驻留武器 comp、激活位驻留主驾驶台 comp。
    ///
    /// 瞄准会话与战斗位置由归属 controller 按带种类的 DriveSubject 记录：对接合图/脱离换图后
    /// 自动跟随。会话独立于驾驶/自动驾驶——对接中可瞄准。
    ///
    /// 就绪口径（§6.1，单实现）：所在组激活 ∧（MannedNow ∨ 插件（段6））∧ 供电 ∧
    /// 冷却完毕 ∧ 瞄准方向在射界内 ∧ 处于太空模式 ∧ 非地表图（有宇宙容器）。
    /// 地面模式只由原版炮塔 Verb 负责同图战斗。
    /// </summary>
    public sealed partial class SpaceWeaponService
    {
        private readonly UniverseManager universe;

        // PD 武器随 spawn/despawn/模式切换登记；空态让 controller 立即早退。
        // 读档与 dev 场景重置时从现有图重建一次，不让索引成为事实源。
        private readonly Dictionary<Map, HashSet<CompSpaceWeapon>> weaponsByMap =
            new Dictionary<Map, HashSet<CompSpaceWeapon>>();
        private readonly HashSet<CompMissileAutoloader> pendingMissileReloads =
            new HashSet<CompMissileAutoloader>();

        public SpaceWeaponService(UniverseManager universe)
        {
            this.universe = universe;
        }

        internal void NotifySalvageOutputLost(Thing launcher)
        {
            if (launcher?.Faction?.IsPlayer != true) return;
            Messages.Message("DeltaV_SalvageOutputLost".Translate(launcher.LabelShort),
                launcher, MessageTypeDefOf.CautionInput, historical: false);
        }

        public void RegisterWeapon(CompSpaceWeapon weapon)
        {
            Map map = weapon?.parent?.Map;
            if (map == null || map.Disposed || weapon.aimMode != SpaceWeaponAimMode.PointDefense
                || !weapon.SpaceModeEnabled) return;
            if (!weaponsByMap.TryGetValue(map, out HashSet<CompSpaceWeapon> weapons))
                weaponsByMap[map] = weapons = new HashSet<CompSpaceWeapon>();
            weapons.Add(weapon);
        }

        public void UnregisterWeapon(CompSpaceWeapon weapon, Map map)
        {
            if (weapon == null) return;
            if (weapon.WeaponProps.tractor) universe.Scatter.ReleaseAutomatic(weapon);
            universe.Controls.ForgetWeaponTarget(weapon);
            if (map == null) return;
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
            foreach (Thing thing in map.spawnedThings)
                foreach (var weapon in CompSpaceWeapon.On(thing as ThingWithComps)) weapon.ClearRuntimeModel();
            weaponsByMap.Remove(map);
            // 归档时 Thing 的 Map 索引已清除；源图的 spawnedThings 仍持有废弃实例。
            pendingMissileReloads.RemoveWhere(loader => map.spawnedThings.Contains(loader.parent));
            universe.Controls.ForgetMap(map);
        }

        internal void QueueMissileReload(CompMissileAutoloader loader)
        {
            if (loader == null) return;
            pendingMissileReloads.Add(loader);
        }

        internal void CancelMissileReload(CompMissileAutoloader loader)
        {
            pendingMissileReloads.Remove(loader);
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
            if (pendingMissileReloads.Count == 0) return;
            List<CompMissileAutoloader> due = pendingMissileReloads
                .OrderBy(loader => loader.parent.thingIDNumber).ToList();
            pendingMissileReloads.Clear();
            foreach (CompMissileAutoloader loader in due)
                loader.TryReload();
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

        internal bool HasPointDefenseWeapons(Map map, bool hasInterceptionTargets, bool hasScatterTargets)
        {
            if (map == null || !weaponsByMap.TryGetValue(map, out HashSet<CompSpaceWeapon> weapons)) return false;
            foreach (CompSpaceWeapon weapon in weapons)
                if (weapon.WeaponProps.tractor ? hasScatterTargets : hasInterceptionTargets) return true;
            return false;
        }

        /// <summary>只读火力估计：武器当前可用性、射程、命中散布、冷却与弹体后效同源。</summary>
        public float EstimateFirepower(ShipBase ship, float distance, float targetRadius,
            bool missileOnly = false)
        {
            if (!ship.IsValid) return 0f;
            float total = 0f;
            foreach (CompSpaceWeapon weapon in WeaponsOn(ship))
            {
                if (!weapon.SpaceModeEnabled || weapon.WeaponProps.manualOnly
                    || weapon.aimMode != SpaceWeaponAimMode.Volley || !weapon.GroupActive
                    || weapon.Component<CompPowerTrader>() is { PowerOn: false }
                    || !weapon.TryGetSkillLevel(out int skill)
                    || !SpaceWeaponBallistics.HasBaseShotEnergy(weapon)) continue;
                CompSpaceWeaponMagazine magazine = weapon.SelectMagazine();
                if (!weapon.MagazineAvailable(magazine)) continue;
                ThingDef projectile = magazine?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
                bool missile = IsGuided(projectile);
                if (missileOnly && !missile) continue;
                float range = weapon.EffectiveRange;
                if (distance < weapon.WeaponProps.minRange || distance > range || range <= 0f) continue;
                SpaceImpactEffectDef impact = SpaceImpactEffects.Resolve(projectile, null);
                float damage = SpaceWeaponBallistics.ExpectedDamage(weapon, projectile);
                float spread = weapon.WeaponProps.baseSpreadDeg
                    * SpreadFactorForSkill(skill) * Mathf.Deg2Rad;
                float hitChance = Mathf.Clamp01(targetRadius
                    / Mathf.Max(targetRadius, distance * Mathf.Tan(spread)));
                total += damage * hitChance
                    * (60f / Mathf.Max(1, weapon.ShotInterval));
            }
            return total;
        }

        public float EstimatePointDefense(ShipBase ship)
        {
            if (!ship.IsValid) return 0f;
            float total = 0f;
            foreach (CompSpaceWeapon weapon in WeaponsOn(ship))
            {
                if (!weapon.SpaceModeEnabled || weapon.WeaponProps.manualOnly
                    || weapon.aimMode != SpaceWeaponAimMode.PointDefense || !weapon.GroupActive
                    || weapon.Component<CompPowerTrader>() is { PowerOn: false }
                    || !weapon.TryGetSkillLevel(out _)
                    || !SpaceWeaponBallistics.HasBaseShotEnergy(weapon)) continue;
                CompSpaceWeaponMagazine magazine = weapon.SelectMagazine();
                if (!weapon.MagazineAvailable(magazine)) continue;
                ThingDef projectile = magazine?.SelectedProjectileDef
                    ?? weapon.WeaponProps.projectileDef;
                SpaceImpactEffectDef impact = SpaceImpactEffects.Resolve(projectile, null);
                total += SpaceWeaponBallistics.ExpectedDamage(weapon, projectile)
                    * (60f / Mathf.Max(1, weapon.ShotInterval));
            }
            return total;
        }

        // ---- 发射（就绪判定单实现）----

        public bool TryFire(CompSpaceWeapon weapon, SpaceObject container, WeaponFireRequest request)
        {
            var resolved = new ResolvedCombatTarget(null, null, new SpacePose(request.AimPoint, 0f));
            return ApplyControl(weapon, container, new WeaponControlCommand(request.GuidanceTarget, resolved,
                false, request.ManualFire ? WeaponControlSource.Manual : WeaponControlSource.Automatic));
        }

        public static WeaponAimInfo AimInfo(CompSpaceWeapon weapon, SpaceObject container)
            => SpaceWeaponBallistics.Read(weapon, container, weapon.QueryEnergy, false).aim;

        /// <summary>供电判定（原版 comp 状态直读：无供电 comp = 不要求供电，per def）。</summary>
        private static bool PowerOn(ThingWithComps parent)
        {
            CompPowerTrader trader = parent.TryGetComp<CompPowerTrader>();
            return trader == null || trader.PowerOn;
        }

        /// <summary>技能 → 散布偏差系数；炮手、内置火控与自瞄插件共用，数值见太空战架构 §5.4。</summary>
        public static float SpreadFactorForSkill(int skillLevel)
        {
            float skill = Mathf.Clamp(skillLevel, 0, 20);
            // 低技能的额外散布在 16 级平滑归零，保留高级自瞄与高手原有精度。
            float inexperience = Mathf.Max(0f, 1f - skill / 16f);
            return Mathf.Lerp(1.6f, 0.4f, skill / 20f) + 6.4f * inexperience * inexperience;
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

        /// <summary>图上全部太空武器 comp（扫描 listerThings；每 tick 武器数量级小）。</summary>
        internal static List<CompSpaceWeapon> InstalledWeaponsOn(Map map)
        {
            var result = new List<CompSpaceWeapon>();
            if (map == null) return result;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is Building building && !building.Destroyed)
                {
                    result.AddRange(CompSpaceWeapon.On(building));
                }
            }
            return result;
        }

        /// <summary>地图中的船体武器集合；武器来源由各船实现提供。</summary>
        public static List<CompSpaceWeapon> AllWeaponsOn(Map map)
        {
            SpaceObject container = Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map);
            return container != null ? container.Weapons.ToList()
                : ShipRegistry.AllShipsOnMap(map).SelectMany(ship => ship.Weapons).Distinct().ToList();
        }

        internal static List<CompSpaceWeapon> WeaponsOn(ShipBase ship) =>
            ship?.IsValid == true ? ship.Weapons.ToList() : new List<CompSpaceWeapon>();

        // ---- 战斗位置（§5.5；本图全部可用玩家武器，激活组仅作人手不足时的优先级）----

        public int BattleStationGunnerCount(Map map)
        {
            if (map == null) return 0;
            int count = 0;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (SpaceWeaponManning.WeaponFor(pawn.CurJob) != null) count++;
            }
            return count;
        }

        internal int EndBattleStationJobs(Map map, Faction faction, HashSet<DriveSubject> ownerIds)
        {
            int released = 0;
            // EndCurrentJob 会同步刷新 MapPawns 派生列表，必须先快照。
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            foreach (Pawn pawn in colonists)
            {
                CompSpaceWeapon weapon = SpaceWeaponManning.WeaponFor(pawn.CurJob);
                if (pawn.Faction != faction || weapon == null
                    || !ownerIds.Contains(weapon.OwnerShip.Identity)) continue;
                released++;
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            return released;
        }

        /// <summary>
        /// 玩家/NPC 共用炮手分配。全部可用武器都参与；激活组优先、同优先级内有可用
        /// 预留炮手者优先，再按 thingId 稳定排序。断电窗口不征用，恢复后事件补分配。
        /// </summary>
        internal int DispatchGunners(Map map, Faction faction, DriveSubject ownerId)
        {
            return DispatchGunners(map, faction, new HashSet<DriveSubject> { ownerId });
        }

        internal int DispatchGunners(Map map, Faction faction, HashSet<DriveSubject> ownerIds)
        {
            // 任一状态边沿都重走同一判定；先释放失效岗位，再用同一候选集补位。
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
            {
                CompSpaceWeapon assigned = SpaceWeaponManning.WeaponFor(pawn.CurJob);
                if (pawn.Faction != faction || assigned == null
                    || !ownerIds.Contains(assigned.OwnerShip.Identity)) continue;
                if (CanStaff(assigned, map, faction, ownerIds, alreadyAssigned: true)) continue;
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            int manned = 0;
            var weapons = AllWeaponsOn(map)
                .Where(w => CanStaff(w, map, faction, ownerIds, alreadyAssigned: false))
                .OrderByDescending(w => w.GroupActive)
                .ThenByDescending(w => GunnerUsable(w.reservedGunner, w))
                .ThenBy(w => w.parent.thingIDNumber)
                .ToList();
            foreach (CompSpaceWeapon weapon in weapons)
            {
                if (WeaponHasAssignedJob(map, weapon)) { manned++; continue; }
                Pawn pawn = PickGunner(weapon, faction);
                if (pawn == null) continue;
                Job job = JobMaker.MakeJob(JobDefOf.ManTurret, weapon.parent);
                if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                {
                    manned++;
                }
            }
            return manned;
        }

        private static bool CanStaff(CompSpaceWeapon weapon, Map map, Faction faction,
            HashSet<DriveSubject> ownerIds, bool alreadyAssigned)
        {
            // 自瞄只阻止征用新炮手，不取消已经下达的岗位（包括玩家右键操作）。
            if (weapon?.parent == null || !weapon.parent.Spawned || weapon.parent.Destroyed
                || weapon.parent.Map != map || weapon.parent.Faction != faction
                || !ownerIds.Contains(weapon.OwnerShip.Identity) || weapon.Mannable == null
                || !alreadyAssigned && weapon.AutoAimEffective) return false;
            return alreadyAssigned && weapon.keepGunnerWhenUnavailable
                || PowerOn(weapon.parent) && weapon.MagazineAvailable(weapon.SelectMagazine());
        }

        private static bool WeaponHasAssignedJob(Map map, CompSpaceWeapon weapon)
        {
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (SpaceWeaponManning.WeaponFor(pawn.CurJob) == weapon) return true;
                foreach (QueuedJob queued in pawn.jobs.jobQueue)
                    if (SpaceWeaponManning.WeaponFor(queued.job) == weapon) return true;
            }
            return false;
        }

        /// <summary>征用：预留炮手优先（可用时）；否则随机选「胜任 + 可到达 + 未征召」殖民者。</summary>
        private static Pawn PickGunner(CompSpaceWeapon weapon, Faction faction)
        {
            Map map = weapon.parent.Map;
            Pawn reserved = weapon.reservedGunner;
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
                || pawn.InAggroMentalState)
            {
                return false;
            }
            Map map = weapon.parent.Map;
            if (pawn.Map != map) return false;
            // 自动补位不抢走其他岗位或玩家刚下达的命令。
            if (SpaceWeaponManning.WeaponFor(pawn.CurJob) != null
                || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.AnyPlayerForced) return false;
            WorkTags required = weapon.Mannable?.Props.manWorkType ?? WorkTags.None;
            if (required != WorkTags.None && pawn.WorkTagIsDisabled(required)) return false;
            return weapon.Mannable != null
                && pawn.CanReserveAndReach(weapon.parent, PathEndMode.InteractionCell, Danger.Deadly);
        }

        /// <summary>场景重建 / 图销毁时的流程清零。</summary>
        public void ResetFlow()
        {
            foreach (SpaceObject obj in universe.objects)
                if (obj.map != null)
                    foreach (Thing thing in obj.map.spawnedThings)
                        foreach (var weapon in CompSpaceWeapon.On(thing as ThingWithComps)) weapon.ClearRuntimeModel();
            weaponsByMap.Clear();
            pendingMissileReloads.Clear();
        }
    }
}
