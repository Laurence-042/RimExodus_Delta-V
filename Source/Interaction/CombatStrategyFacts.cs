using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>One read-only combat observation; rule evaluation never changes equipment or flight state.</summary>
    internal sealed class CombatStrategyFacts
    {
        private readonly UniverseManager universe;
        private readonly ShipBase ship;
        private readonly SpaceObject own;
        private readonly Dictionary<CompSpaceWeapon, (float cooldown, float ammo, float energy)> weaponFacts =
            new Dictionary<CompSpaceWeapon, (float, float, float)>();
        private readonly Dictionary<DriveSubject, (Vector3 position, float distance, float power,
            float firstImpact)> enemyFacts =
            new Dictionary<DriveSubject, (Vector3, float, float, float)>();
        private Vector2? ownHalfExtents;
        internal readonly float hullFraction, thrustFraction, shieldFraction, readyFraction;

        internal CombatStrategyFacts(UniverseManager universe, ShipBase ship, SpaceObject own)
        {
            this.universe = universe; this.ship = ship; this.own = own;
            float hp = 0f, maximum = 0f;
            foreach (Thing thing in ship.Map.listerThings.AllThings)
                if (thing is Building building && ship.Contains(thing) && !thing.Destroyed)
                { hp += building.HitPoints; maximum += Mathf.Max(1, building.MaxHitPoints); }
            CompShipCombatAI ai = ship.CombatAI;
            ai?.EnsureBaselineFor(ship);
            if (ai != null && ai.initialHullHp <= 0f)
                ai.initialHullHp = ship.Map.listerThings.AllThings
                    .Where(t => ship.Contains(t) && (t == ship.Anchor
                        || t.TryGetComp<CompDeltaVPilotConsole>() != null))
                    .OfType<ThingWithComps>()
                    .Select(t => t.TryGetComp<CompShipCombatAI>())
                    .Where(c => c?.baselineShipKey == ship.Identity.key)
                    .Select(c => c.initialHullHp)
                    .FirstOrDefault(value => value > 0f);
            if (ai != null && ai.initialHullHp <= 0f && maximum > 0f)
                ai.initialHullHp = maximum;
            hullFraction = maximum > 0f
                ? Mathf.Clamp01(hp / Mathf.Max(maximum, ai?.initialHullHp ?? 0f)) : 1f;
            float thrust = ship.BuildThrusters().Where(t => t.operational).Sum(t => t.thrust);
            if (ai != null && ai.initialThrust <= 0f && thrust > 0f)
            {
                float remembered = ship.Map.listerThings.AllThings
                    .Where(t => ship.Contains(t) && (t == ship.Anchor
                        || t.TryGetComp<CompDeltaVPilotConsole>() != null))
                    .OfType<ThingWithComps>()
                    .Select(t => t.TryGetComp<CompShipCombatAI>())
                    .Where(c => c?.baselineShipKey == ship.Identity.key)
                    .Select(c => c.initialThrust).FirstOrDefault(value => value > 0f);
                ai.initialThrust = remembered > 0f ? remembered : thrust;
            }
            float baseline = ship.CombatAI?.initialThrust ?? 0f;
            thrustFraction = baseline > 0f ? Mathf.Clamp01(thrust / baseline) : thrust > 0f ? 1f : 0f;
            float shield = 0f, shieldMax = 0f;
            foreach (Thing thing in ship.Map.listerThings.AllThings)
            {
                if (!ship.Contains(thing) || thing is not Building building) continue;
                CompSpaceShield comp = building.TryGetComp<CompSpaceShield>();
                if (comp == null) continue;
                shield += comp.mode == SpaceShieldMode.Guard ? comp.GuardHitPoints
                    : comp.State == SpaceShieldState.LineActive ? 1f : 0f;
                shieldMax += comp.mode == SpaceShieldMode.Guard ? Mathf.Max(1, comp.GuardHitPointsMax) : 1f;
            }
            shieldFraction = shieldMax > 0f ? Mathf.Clamp01(shield / shieldMax) : 0f;
            int weapons = 0, ready = 0;
            foreach (CompSpaceWeapon weapon in ship.Weapons)
            {
                if (!weapon.SpaceModeEnabled || !weapon.GroupActive
                    || weapon.aimMode != SpaceWeaponAimMode.Volley) continue;
                weapons++;
                if (WeaponAvailable(weapon)) ready++;
            }
            readyFraction = weapons > 0 ? (float)ready / weapons : 0f;
        }

        internal static bool WeaponReady(CompSpaceWeapon weapon) =>
            weapon.CooldownTicksLeft == 0 && weapon.SpaceModeEnabled && weapon.GroupActive
            && weapon.MagazineAvailable(weapon.SelectMagazine())
            && weapon.parent.TryGetComp<CompPowerTrader>() is not { PowerOn: false }
            && weapon.TryGetSkillLevel(out _)
            && SpaceWeaponBallistics.HasBaseShotEnergy(weapon);

        private (float cooldown, float ammo, float energy) Observe(CompSpaceWeapon weapon)
        {
            if (weaponFacts.TryGetValue(weapon, out var observed)) return observed;
            observed = (weapon.CooldownTicksLeft / 60f,
                weapon.MagazineAvailable(weapon.SelectMagazine()) ? 1f : 0f,
                SpaceWeaponBallistics.HasBaseShotEnergy(weapon)
                    && weapon.parent.TryGetComp<CompPowerTrader>() is not { PowerOn: false } ? 1f : 0f);
            weaponFacts.Add(weapon, observed);
            return observed;
        }

        private bool WeaponAvailable(CompSpaceWeapon weapon)
        {
            if (weapon.CooldownTicksLeft > 0) return false;
            var observed = Observe(weapon);
            return observed.ammo > 0f && observed.energy > 0f
                && weapon.parent.TryGetComp<CompPowerTrader>() is not { PowerOn: false }
                && weapon.TryGetSkillLevel(out _);
        }

        internal CombatStrategyContext Context(ShipBase enemy = null,
            CompSpaceWeapon weapon = null, SpacePose? ownPose = null, bool scoped = true)
        {
            SpacePose source = ownPose ?? SpaceTransform.RigidPointPose(own.pose,
                SpaceTransform.MapToShipLocal(ship.Map, ship.HullCenter), 0f);
            Vector3 enemyPosition = default;
            float distance = float.NaN, enemyFire = float.NaN, firstImpact = float.NaN;
            if (enemy?.IsValid == true)
            {
                if (!ownPose.HasValue && enemyFacts.TryGetValue(enemy.Identity, out var cached))
                    (enemyPosition, distance, enemyFire, firstImpact) = cached;
                else
                {
                    enemyFire = 0f; firstImpact = 8f;
                    enemyPosition = ShipRegistry.ShipPose(enemy).position;
                    distance = OrbitSphere.ArcCells(source.position, enemyPosition);
                    var threats = new List<CombatWeaponThreat>();
                    CombatWeaponThreat.Capture(enemy, universe.FrameOf(enemy.Map), 3f, threats);
                    ownHalfExtents ??= CombatWeaponThreat.HullHalfExtents(ship);
                    foreach (CombatWeaponThreat threat in threats)
                    {
                        threat.Score(source.position, source.velocity, ownHalfExtents.Value,
                            source.angle,
                            Vector2.zero, out float rate, out float time);
                        enemyFire += rate;
                        if (rate > 0f) firstImpact = Mathf.Min(firstImpact, time);
                    }
                    if (!ownPose.HasValue) enemyFacts[enemy.Identity] =
                        (enemyPosition, distance, enemyFire, firstImpact);
                }
            }
            var observed = weapon != null ? Observe(weapon) : (float.NaN, float.NaN, float.NaN);
            float arc = weapon != null && enemy?.IsValid == true
                ? ArcError(weapon, own, source, enemyPosition) : float.NaN;
            return new CombatStrategyContext(hullFraction, thrustFraction, shieldFraction,
                readyFraction, observed.Item1, observed.Item2, observed.Item3,
                arc, distance, enemyFire, firstImpact,
                scoped ? (criterion, retaining) => Match(criterion, retaining, enemy, weapon, ownPose) : null);
        }

        private CombatStrategyFacts enemyObservation;
        private ShipBase observedEnemy;
        private bool Match(CombatCriterion criterion, bool retaining, ShipBase enemy,
            CompSpaceWeapon weapon, SpacePose? pose)
        {
            bool equipment = criterion.metric >= CombatMetric.ReadyWeaponFraction
                && criterion.metric <= CombatMetric.WeaponArcErrorDegrees;
            if (criterion.scope == CombatScope.EnemyShip && criterion.metric <= CombatMetric.OwnShieldFraction)
            {
                if (enemy?.IsValid != true) return false;
                if (observedEnemy != enemy)
                { observedEnemy = enemy; enemyObservation = new CombatStrategyFacts(universe, enemy, universe.FrameOf(enemy.Map)); }
                return criterion.Compare(enemyObservation.Context(scoped: false).Value(criterion.metric), retaining);
            }
            if (!equipment) return criterion.Compare(Context(enemy, weapon, pose, false).Value(criterion.metric), retaining);
            if (criterion.scope == CombatScope.CurrentWeapon || criterion.scope == CombatScope.Metric && weapon != null)
                return weapon != null && criterion.Compare(criterion.metric == CombatMetric.ReadyWeaponFraction
                    ? WeaponAvailable(weapon) ? 1f : 0f : Context(enemy, weapon, pose, false).Value(criterion.metric), retaining);
            int count = 0, ready = 0;
            bool result = criterion.allWeapons;
            foreach (var item in ship.Weapons)
            {
                if (criterion.weapons?.Includes(item) != true) continue;
                count++;
                if (criterion.metric == CombatMetric.ReadyWeaponFraction)
                { if (WeaponAvailable(item)) ready++; continue; }
                bool matches = criterion.Compare(Context(enemy, item, pose, false).Value(criterion.metric), retaining);
                result = criterion.allWeapons ? result && matches : result || matches;
            }
            return count > 0 && (criterion.metric == CombatMetric.ReadyWeaponFraction
                ? criterion.Compare((float)ready / count, retaining) : result);
        }

        internal static float ArcError(CompSpaceWeapon weapon, SpaceObject own,
            SpacePose sourcePose, Vector3 target, bool locking = false)
        {
            Vector2 offset = SpaceWeaponGeometry.MuzzleMapPoint(weapon) - weapon.OwnerShip.HullCenter;
            SpacePose muzzle = SpaceTransform.RigidPointPose(sourcePose, offset, 0f);
            Vector2 toTarget = OrbitSphere.Offset(muzzle.position, target);
            if (toTarget.sqrMagnitude < 0.001f) return 0f;
            float universeAngle = Mathf.Atan2(toTarget.y, toTarget.x);
            float desiredMapAngle = SpaceTransform.WrapAngle(universeAngle - sourcePose.angle);
            return weapon.CoverageError(desiredMapAngle);
        }
    }
}
