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
        private readonly Dictionary<DriveSubject, (Vector3 position, float distance, float power)> enemyFacts =
            new Dictionary<DriveSubject, (Vector3, float, float)>();
        private float ownRadius = -1f;
        internal readonly float hullFraction, thrustFraction, shieldFraction, readyFraction;
        internal readonly float incomingSeconds;

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
            incomingSeconds = IncomingAt(own.pose.position, own.pose.velocity);
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
                SpaceWeaponBallistics.HasBaseShotEnergy(weapon) ? 1f : 0f);
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

        internal float IncomingAt(Vector3 position, Vector2 velocity)
        {
            return universe.Projectiles.IncomingSeconds(ship, position, velocity);
        }

        internal CombatStrategyContext Context(ShipBase enemy = null,
            CompSpaceWeapon weapon = null, SpacePose? ownPose = null)
        {
            SpacePose source = ownPose ?? own.pose;
            Vector3 enemyPosition = default;
            float distance = 100000f, enemyFire = 0f;
            if (enemy?.IsValid == true)
            {
                if (!ownPose.HasValue && enemyFacts.TryGetValue(enemy.Identity, out var cached))
                    (enemyPosition, distance, enemyFire) = cached;
                else
                {
                    enemyPosition = ShipRegistry.ShipPose(enemy).position;
                    distance = OrbitSphere.ArcCells(source.position, enemyPosition);
                    if (ownRadius < 0f) ownRadius = ShipFireControl.HullRadius(ship);
                    enemyFire = universe.Weapons.EstimateFirepower(enemy, distance, ownRadius);
                    if (!ownPose.HasValue) enemyFacts[enemy.Identity] = (enemyPosition, distance, enemyFire);
                }
            }
            var observed = weapon != null ? Observe(weapon) : (0f, 1f, 1f);
            float arc = weapon != null && enemy?.IsValid == true
                ? ArcError(weapon, own, source, enemyPosition) : 0f;
            return new CombatStrategyContext(hullFraction, thrustFraction, shieldFraction,
                readyFraction, observed.Item1, observed.Item2, observed.Item3,
                arc, distance, enemyFire,
                ownPose.HasValue ? IncomingAt(source.position, source.velocity) : incomingSeconds);
        }

        internal static float ArcError(CompSpaceWeapon weapon, SpaceObject own,
            SpacePose sourcePose, Vector3 target, bool locking = false)
        {
            Vector2 toTarget = OrbitSphere.Offset(sourcePose.position, target);
            if (toTarget.sqrMagnitude < 0.001f) return 0f;
            float universeAngle = Mathf.Atan2(toTarget.y, toTarget.x);
            float desiredMapAngle = SpaceTransform.WrapAngle(universeAngle - sourcePose.angle);
            float baseMapAngle = ShipOrientation.AngleFromRot4(weapon.parent.Rotation);
            float arc = locking && weapon.WeaponProps.lockArcDeg >= 0f
                ? weapon.WeaponProps.lockArcDeg : weapon.WeaponProps.fireArcDeg;
            float halfArc = Mathf.Max(0f, arc) * 0.5f * Mathf.Deg2Rad;
            return Mathf.Max(0f, Mathf.Abs(SpaceTransform.WrapAngle(desiredMapAngle - baseMapAngle))
                - halfArc) * Mathf.Rad2Deg;
        }
    }
}
