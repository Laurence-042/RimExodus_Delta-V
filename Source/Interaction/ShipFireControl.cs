using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>每船战术目标的唯一所有者；输入、机动和弹体只消费其身份与意图。</summary>
    internal sealed class ShipFireControl
    {
        private readonly UniverseManager universe;
        private readonly DriveSubject ownerId;
        private CombatTarget target;
        private int preferredSystemId;
        private int targetLockedUntil;
        private bool targetObserved;
        private readonly Dictionary<int, PdLock> pointDefense = new Dictionary<int, PdLock>();
        private readonly List<int> staleWeapons = new List<int>();
        private struct PdLock { internal CombatTarget target; internal int retargetAt; }
        internal CombatTarget Target => target;
        internal ShipFireControl(UniverseManager universe, DriveSubject ownerId)
        { this.universe = universe; this.ownerId = ownerId; }

        internal void Clear() { target = default; preferredSystemId = 0; targetLockedUntil = 0; }
        internal void Dispose()
        { Clear(); pointDefense.Clear(); aimCapabilities.Clear(); leadPoints.Clear(); uniqueLeadPoints.Clear(); }
        internal void ForgetWeapon(CompSpaceWeapon weapon) => pointDefense.Remove(weapon.parent.thingIDNumber);

        internal void ForgetMap(Map map)
        {
            if (!target.TryResolve(universe, out var resolved) || resolved.Container?.map == map) Clear();
            staleWeapons.Clear();
            foreach (var pair in pointDefense)
                if (!pair.Value.target.TryResolve(universe, out var pd)
                    || pd.Container?.map == null || pd.Container.map == map) staleWeapons.Add(pair.Key);
            foreach (int id in staleWeapons) pointDefense.Remove(id);
        }

        internal static bool CandidateValid(ShipBase attacker, SpaceObject own, SpaceObject container, ShipBase member)
            => CandidateValid(attacker, own, container, member, out _);

        private static bool CandidateValid(ShipBase attacker, SpaceObject own, SpaceObject container,
            ShipBase member, out float distance)
        {
            distance = 0f;
            if (attacker?.IsValid != true || attacker.Faction == null || own == null || container == null
                || container == own || container.map == null || container.map.Disposed
                || container.baseTag == SpaceObjectTag.Projectile || !container.HasTag(SpaceObjectTag.Ship)
                || member?.IsValid != true || member.Map != container.map || member.Faction == null
                || !member.Faction.HostileTo(attacker.Faction) || !member.CanDriveIn(container)) return false;
            distance = OrbitSphere.ArcCells(ShipRegistry.ShipPose(attacker).position, ShipRegistry.ShipPose(member).position);
            if (distance > DeltaVMod.CurrentSettings.poiActiveRadius) return false;
            return container.MotionInfo?.thrusters.Any(t => t.operational && t.thrust > 0f
                && (t.thing != null ? member.Contains(t.thing) : member.Identity.shuttle)) == true;
        }

        internal static IEnumerable<ShipBase> Candidates(ShipBase ship, SpaceObject own, SpaceObject container)
        {
            if (container == null || container == own) yield break;
            foreach (ShipBase member in container.Ships)
                if (CandidateValid(ship, own, container, member)) yield return member;
        }

        internal bool TryResolveAutomatic(ShipBase ship, SpaceObject own, CombatTarget handle,
            out ResolvedCombatTarget resolved)
        {
            return handle.TryResolve(universe, out resolved)
                && CandidateValid(ship, own, resolved.Container, resolved.Ship);
        }

        internal static float HullRadius(ShipBase ship)
        {
            Vector2 center = ship.HullCenter;
            float radius = 0.5f;
            foreach (IntVec3 cell in ship.HullCells)
                radius = Mathf.Max(radius, (new Vector2(cell.x + 0.5f, cell.z + 0.5f) - center).magnitude + 0.71f);
            return radius;
        }

        internal void Decide(ShipBase ship, SpaceObject own)
        {
            float best = float.NegativeInfinity;
            float ownRadius = HullRadius(ship);
            CombatTarget selected = default;
            foreach (SpaceObject container in universe.objects)
            foreach (ShipBase member in container.Ships)
            {
                if (!CandidateValid(ship, own, container, member, out float distance)) continue;
                CombatTarget candidate = CombatTarget.ForShip(member.Identity);
                float enemyFire = universe.Weapons.EstimateFirepower(member, distance, ownRadius);
                float survivability = member.Map.listerThings.AllThings.OfType<Building>()
                    .Where(b => b.Spawned && !b.Destroyed && member.Contains(b)).Sum(b => (float)b.HitPoints);
                float ownFire = universe.Weapons.EstimateFirepower(ship, distance, HullRadius(member));
                float score = enemyFire * 2f + ownFire / (1f + survivability / 1000f)
                    - distance * 0.1f + (candidate == target ? 10f : 0f);
                if (candidate == target && Find.TickManager.TicksGame < targetLockedUntil) score += 100000f;
                if (score < best || score == best && CompareIdentity(candidate.Ship, selected.Ship) >= 0) continue;
                best = score; selected = candidate;
            }
            if (selected != target)
            { targetLockedUntil = Find.TickManager.TicksGame + 180; preferredSystemId = 0; }
            if (DeltaVLog.Enabled(DeltaVLogModule.Combat) && (!targetObserved || selected != target))
                DeltaVLog.Message(DeltaVLogModule.Combat, $"ship #{ownerId} fire-control target "
                    + (selected.HasLock ? selected.Ship.ToString() : "none"));
            targetObserved = true;
            target = selected;
        }

        private static int CompareIdentity(DriveSubject a, DriveSubject b)
        { int kind = a.shuttle.CompareTo(b.shuttle); return kind != 0 ? kind : a.key.CompareTo(b.key); }

        internal bool TryGetIntent(ShipBase ship, SpaceObject own, bool suppressSystems, out ShipVolleyIntent intent)
        {
            intent = default;
            if (universe.Lifecycle.IsFrozen(ship.Map) || !TryResolveAutomatic(ship, own, target, out var victim)) return false;
            if (suppressSystems && preferredSystemId == 0)
            {
                Thing system = victim.Ship.Weapons.Where(w => w.SpaceModeEnabled && w.aimMode == SpaceWeaponAimMode.PointDefense
                        && w.parent.Spawned && !w.parent.Destroyed).Select(w => (Thing)w.parent).FirstOrDefault()
                    ?? victim.Container.MotionInfo?.thrusters.Where(t => t.operational && t.thing != null
                        && victim.Ship.Contains(t.thing)).Select(t => t.thing).FirstOrDefault();
                preferredSystemId = system?.thingIDNumber ?? 0;
            }
            intent = new ShipVolleyIntent(CombatTarget.ForShip(target.Ship, suppressSystems ? preferredSystemId : 0), !suppressSystems);
            return true;
        }

        internal void TickVolley(ShipBase ship, SpaceObject container, ShipVolleyIntent intent)
        {
            if (!intent.target.TryResolve(universe, out var resolved)) return;
            if (intent.fireAll && !CandidateValid(ship, container, resolved.Container, resolved.Ship)) return;
            foreach (CompSpaceWeapon weapon in ship.Weapons)
            {
                if (!WeaponBelongsHere(weapon, container) || !weapon.SpaceModeEnabled
                    || weapon.WeaponProps.manualOnly && intent.fireAll
                    || weapon.aimMode != SpaceWeaponAimMode.Volley || !weapon.GroupActive) continue;
                WeaponAimInfo info = SpaceWeaponService.AimInfo(weapon, container);
                Vector3 aim = resolved.Pose.position;
                if (intent.predictIntercept && !info.Laser && TrySolveIntercept(info.Muzzle, container.pose, resolved.Pose,
                    info.Speed, out Vector3 intercept)) aim = intercept;
                SpaceWeaponService.AimWeapon(weapon, container, info, aim);
                bool fire = intent.fireAll || intent.main && weapon.salvoSet == SpaceWeaponSalvoSet.Main
                    || intent.secondary && weapon.salvoSet == SpaceWeaponSalvoSet.Secondary;
                if (!fire || Find.TickManager.TicksGame < weapon.cooldownUntilTick) continue;
                CombatTarget guidance = intent.target.HasLock ? intent.target : default;
                if (!guidance.HasLock && info.Guided)
                    guidance = ManualTargetAlongRay(universe, container, info.Muzzle,
                        OrbitSphere.Offset(info.Muzzle, aim).normalized, ship.Faction);
                universe.Weapons.TryFire(weapon, container, new WeaponFireRequest(aim, guidance));
            }
        }

        internal void PrunePointDefense(ShipBase ship)
        {
            if (pointDefense.Count == 0) return;
            // 归属、模式与移除事件可能在下一次 tick 前发生；每次消费前仍复核。
            staleWeapons.Clear();
            foreach (int id in pointDefense.Keys)
                if (!ship.IsValid || !ship.Weapons.Any(w => w.parent.thingIDNumber == id && w.parent.Spawned && !w.parent.Destroyed
                    && w.SpaceModeEnabled && w.aimMode == SpaceWeaponAimMode.PointDefense && w.OwnerShip.Identity == ownerId)) staleWeapons.Add(id);
            foreach (int id in staleWeapons) pointDefense.Remove(id);
        }

        private bool WeaponBelongsHere(CompSpaceWeapon weapon, SpaceObject container) =>
            weapon.parent.Spawned && !weapon.parent.Destroyed
            && weapon.OwnerShip.Identity == ownerId && weapon.Container == container;

        internal void TickPointDefense(ShipBase ship, SpaceObject container)
        {
            PrunePointDefense(ship);
            int now = Find.TickManager.TicksGame;
            foreach (CompSpaceWeapon weapon in ship.Weapons)
            {
                if (!weapon.SpaceModeEnabled || weapon.aimMode != SpaceWeaponAimMode.PointDefense
                    || weapon.WeaponProps.manualOnly && !weapon.WeaponProps.tractor
                    || !WeaponBelongsHere(weapon, container) || !weapon.GroupActive) continue;
                if (weapon.WeaponProps.tractor && !SpaceTractor.CanOperate(weapon)) continue;
                WeaponAimInfo info = SpaceWeaponService.AimInfo(weapon, container);
                pointDefense.TryGetValue(weapon.parent.thingIDNumber, out PdLock state);
                ResolvedCombatTarget victim = default;
                bool valid = now < state.retargetAt && PdTargetValid(weapon, state.target, info.Muzzle, out victim);
                if (!valid)
                {
                    // A failed lock does not bypass the authorized 30-tick search cadence.
                    if (weapon.WeaponProps.tractor && now < state.retargetAt) continue;
                    if (weapon.WeaponProps.tractor && !SpaceWeaponShot.HasShotEnergy(weapon))
                    {
                        state.target = default; state.retargetAt = now + 30;
                        pointDefense[weapon.parent.thingIDNumber] = state;
                        continue;
                    }
                    state.target = CombatTarget.ForContainer(FindPdTarget(weapon, info.Muzzle)); state.retargetAt = now + 30;
                    pointDefense[weapon.parent.thingIDNumber] = state;
                    valid = state.target.TryResolve(universe, out victim);
                }
                if (!valid) continue;
                Vector3 aim = victim.Pose.position;
                if (!info.Laser && !TrySolveIntercept(info.Muzzle, container.pose, victim.Pose, info.Speed, out aim)) continue;
                SpaceWeaponService.AimWeapon(weapon, container, info, aim);
                if (now >= weapon.cooldownUntilTick)
                    universe.Weapons.TryFire(weapon, container, new WeaponFireRequest(aim, state.target));
            }
        }

        private readonly List<WeaponAimInfo> aimCapabilities = new List<WeaponAimInfo>();
        private readonly List<Vector3> leadPoints = new List<Vector3>();
        private readonly HashSet<Vector3> uniqueLeadPoints = new HashSet<Vector3>();

        internal IReadOnlyList<Vector3> LeadPoints(ShipBase ship)
        {
            leadPoints.Clear(); uniqueLeadPoints.Clear(); aimCapabilities.Clear();
            SpaceObject own = ship.IsValid ? universe.FrameOf(ship.Map) : null;
            if (own == null) return leadPoints;
            foreach (CompSpaceWeapon weapon in ship.Weapons)
                if (WeaponBelongsHere(weapon, own) && weapon.SpaceModeEnabled
                    && weapon.GroupActive && weapon.aimMode == SpaceWeaponAimMode.Volley)
                    aimCapabilities.Add(SpaceWeaponService.AimInfo(weapon, own));
            foreach (SpaceObject container in universe.objects)
            foreach (ShipBase member in Candidates(ship, own, container))
            {
                if (!CombatTarget.ForShip(member.Identity).TryResolve(universe, out var resolved)) continue;
                foreach (WeaponAimInfo info in aimCapabilities)
                {
                    Vector3 point = resolved.Pose.position;
                    if (!info.Laser && !TrySolveIntercept(info.Muzzle, own.pose, resolved.Pose, info.Speed, out point)) continue;
                    if (uniqueLeadPoints.Add(point)) leadPoints.Add(point);
                }
            }
            return leadPoints;
        }

        internal static CombatTarget ManualTargetAlongRay(UniverseManager universe, SpaceObject source,
            Vector3 origin, Vector2 direction, Faction faction)
        {
            SpaceObject container = TargetAlongRay(universe, source, origin, direction, faction);
            if (container == null) return default;
            ShipBase selected = null;
            float best = float.MaxValue;
            foreach (ShipBase member in container.Ships)
            {
                if (!member.IsValid || member.Faction?.HostileTo(faction) != true) continue;
                Vector2 offset = OrbitSphere.Offset(origin, ShipRegistry.ShipPose(member).position);
                float along = Vector2.Dot(offset, direction);
                if (along <= 0f || (offset - direction * along).magnitude > HullRadius(member)) continue;
                if (along < best || along == best && CompareIdentity(member.Identity, selected.Identity) < 0)
                { best = along; selected = member; }
            }
            return selected != null ? CombatTarget.ForShip(selected.Identity) : CombatTarget.ForContainer(container);
        }

        internal bool PdTargetValid(CompSpaceWeapon weapon, CombatTarget handle, Vector3 muzzle,
            out ResolvedCombatTarget resolved)
        {
            return handle.TryResolve(universe, out resolved) && weapon.Container != null
                && PdCandidateValid(weapon, resolved.Container, muzzle, out _);
        }

        private static bool PdCandidateValid(CompSpaceWeapon weapon, SpaceObject target, Vector3 muzzle, out float distance)
        {
            distance = 0f;
            if (weapon.WeaponProps.tractor)
            {
                if (target?.IsScattered != true || target.map == null || target.map.Disposed) return false;
                distance = OrbitSphere.Offset(muzzle, target.pose.position).magnitude;
                return distance <= weapon.WeaponProps.range;
            }
            if (target == null || target == weapon.Container || target.map == null || target.map.Disposed
                || !target.InterceptionEligible || !target.HostileTo(weapon.parent.Faction)) return false;
            distance = OrbitSphere.Offset(muzzle, target.pose.position).magnitude - SpaceCollision.BoundsRadiusOf(target);
            return distance >= weapon.WeaponProps.minRange && distance <= weapon.WeaponProps.range;
        }

        /// <summary>
        /// PD 索敌（§4.2）：射程内敌对的可拦截容器；优先级 =「预计命中己方的最近
        /// 容器优先」（来袭弹逼近自身容器 → 按预计命中时间升序），兜底最近。
        /// </summary>
        internal SpaceObject FindPdTarget(CompSpaceWeapon weapon, Vector3 muzzle)
        {
            SpaceObject own = weapon.Container;
            if (own == null) return null;
            SpaceObject best = null;
            float bestScore = float.MaxValue;
            IEnumerable<SpaceObject> candidates = weapon.WeaponProps.tractor ? universe.Scatter.Candidates : universe.objects;
            foreach (SpaceObject obj in candidates)
            {
                if (!PdCandidateValid(weapon, obj, muzzle, out float distance)) continue;
                if (weapon.WeaponProps.tractor)
                {
                    if (distance < bestScore) { bestScore = distance; best = obj; }
                    continue;
                }
                Vector2 offset = OrbitSphere.Offset(own.pose.position, obj.pose.position);
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

        internal static bool TrySolveIntercept(Vector3 muzzle, SpacePose sourcePose,
            SpacePose targetPose, float projectileSpeed, out Vector3 interceptUniverse)
        {
            interceptUniverse = default;
            if (projectileSpeed <= 0f) return false;
            Vector2 p = OrbitSphere.Offset(muzzle, targetPose.position);
            Vector2 targetVel = SpaceTransform.TransportVelocity(targetPose, muzzle);
            Vector2 muzzleVel = SpaceTransform.TransportVelocity(sourcePose, muzzle);
            Vector2 relativeVelocity = targetVel - muzzleVel;
            if (!SpaceTransform.TrySolveInterceptPlanar(p, relativeVelocity, projectileSpeed, out float time))
            {
                return false;
            }
            // Launch adds the source velocity to the projectile. Aim along the relative
            // intercept vector, otherwise the source drift is counted a second time.
            interceptUniverse = OrbitSphere.Move(muzzle, p + relativeVelocity * time);
            return true;
        }

        private static SpaceObject TargetAlongRay(UniverseManager universe, SpaceObject source,
            Vector3 origin, Vector2 dirTangent, Faction faction)
        {
            SpaceObject best = null;
            float bestAlong = float.MaxValue;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == source || obj.map == null || obj.map.Disposed) continue;
                if (!obj.HostileTo(faction)) continue;
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

    }
}
