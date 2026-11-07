using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // Structure-derived connections only. Power, ammo and stored energy are never cached here.
    internal sealed class SpaceWeaponLayout
    {
        internal readonly Map map;
        private readonly bool cacheable;
        internal readonly int version, ownerId;
        internal readonly IntVec3 position;
        internal readonly Rot4 rotation;
        internal readonly List<ThingWithComps> attachments;
        internal readonly List<CompSpaceWeaponMagazine> magazines = new List<CompSpaceWeaponMagazine>();
        internal readonly SpaceWeaponAccelerationSolution acceleration;
        internal readonly Dictionary<CompSpaceRailgun, List<ThingWithComps>> segmentAttachments =
            new Dictionary<CompSpaceRailgun, List<ThingWithComps>>();

        internal SpaceWeaponLayout(CompSpaceWeapon weapon)
        {
            // Unregistered generation/ground maps do not receive the universe structure events yet.
            cacheable = weapon.Container != null;
            ownerId = SpaceWeaponGeometry.OwnerIdOf(weapon.parent);
            map = weapon.parent.Map; version = SubstructureOwnership.GlobalStructureVersion;
            position = weapon.parent.Position; rotation = weapon.parent.Rotation;
            attachments = new List<ThingWithComps>(weapon.DiscoverAttachedThings());
            var own = weapon.Component<CompSpaceWeaponMagazine>();
            if (own != null) magazines.Add(own);
            foreach (var thing in attachments)
            {
                var magazine = thing.TryGetComp<CompSpaceWeaponMagazine>();
                if (magazine != null) magazines.Add(magazine);
            }
            acceleration = CompSpaceRailgun.TraceConnections(weapon);
            foreach (var segment in acceleration.segments)
                segmentAttachments.Add(segment, new List<ThingWithComps>(segment.AttachedThings()));
        }

        internal bool Matches(CompSpaceWeapon weapon) => cacheable && map == weapon.parent.Map
            && version == SubstructureOwnership.GlobalStructureVersion
            && position == weapon.parent.Position && rotation == weapon.parent.Rotation;
    }

    // A query has its own ledger; no deduction occurs until Commit. A shot owns a separate
    // ledger so a synchronous status refresh cannot overwrite the in-flight transaction.
    internal sealed class SpaceWeaponEnergy
    {
        private readonly Dictionary<PowerNet, float> available = new Dictionary<PowerNet, float>();
        private readonly Dictionary<PowerNet, float> reserved = new Dictionary<PowerNet, float>();
        private readonly Dictionary<PowerNet, float> localReserved = new Dictionary<PowerNet, float>();
        private readonly Dictionary<CompSpaceWeaponAttachment, float> capacitors =
            new Dictionary<CompSpaceWeaponAttachment, float>();
        internal readonly List<CompSpaceRailgun> active = new List<CompSpaceRailgun>();
        internal float kineticEnergyBonus;
        internal bool ready;

        internal void Clear()
        { available.Clear(); reserved.Clear(); localReserved.Clear(); capacitors.Clear(); active.Clear(); kineticEnergyBonus = 0f; ready = false; }

        internal bool Evaluate(CompSpaceWeapon weapon, bool includeSegments = true)
        {
            var perf = weapon.Universe?.Performance;
            long start = perf?.Start() ?? 0L;
            try
            {
                Clear();
                SpaceWeaponLayout layout = weapon.Layout;
                if (!ReserveAttachments(layout.attachments, weapon.ShotEnergy)
                    && !ReserveGrid(weapon.parent, weapon.ShotEnergy)) return false;
                ready = true;
                if (includeSegments)
                    foreach (var segment in layout.acceleration.segments)
                    {
                        if (segment.parent.TryGetComp<CompPowerTrader>() is { PowerOn: false }) continue;
                        var attachments = layout.segmentAttachments[segment];
                        float cost = segment.ShotEnergy(attachments);
                        if (!ReserveAttachments(attachments, cost)
                            && !ReserveGrid(segment.parent, cost)) continue;
                        active.Add(segment); kineticEnergyBonus += segment.RailProps.kineticEnergyBonus;
                    }
                return true;
            }
            finally { perf?.Record(PerformanceStage.WeaponEnergy, start); }
        }

        private bool ReserveAttachments(List<ThingWithComps> attachments, float cost)
        {
            if (cost <= 0f) return false;
            CompSpaceWeaponAttachment best = null;
            float bestAvailable = 0f;
            foreach (var thing in attachments)
            {
                var capacitor = thing.TryGetComp<CompSpaceWeaponAttachment>();
                if (capacitor == null || !capacitor.CanSupplyShotEnergy(cost)) continue;
                capacitors.TryGetValue(capacitor, out float held);
                float remaining = capacitor.AvailableShotEnergy - held;
                PowerNet net = capacitor.ShotEnergyBattery?.PowerNet;
                if (net != null) remaining = Mathf.Min(remaining, RemainingGridEnergy(net));
                if (remaining + .0001f < cost) continue;
                if (best == null || remaining > bestAvailable
                    || remaining == bestAvailable && thing.thingIDNumber < best.parent.thingIDNumber)
                { best = capacitor; bestAvailable = remaining; }
            }
            if (best == null) return false;
            capacitors.TryGetValue(best, out float prior);
            capacitors[best] = prior + cost;
            PowerNet sharedNet = best.ShotEnergyBattery?.PowerNet;
            if (sharedNet != null)
            {
                localReserved.TryGetValue(sharedNet, out float local);
                localReserved[sharedNet] = local + cost;
            }
            return true;
        }

        private float RemainingGridEnergy(PowerNet net)
        {
            if (!available.TryGetValue(net, out float stored))
            { stored = net.CurrentStoredEnergy(); available.Add(net, stored); }
            reserved.TryGetValue(net, out float grid);
            localReserved.TryGetValue(net, out float local);
            return stored - grid - local;
        }

        private bool ReserveGrid(ThingWithComps source, float cost)
        {
            if (cost <= 0f) return true;
            PowerNet net = source.TryGetComp<CompPowerTrader>()?.PowerNet;
            if (net == null) return false;
            reserved.TryGetValue(net, out float held);
            if (RemainingGridEnergy(net) + .0001f < cost) return false;
            reserved[net] = held + cost;
            return true;
        }

        internal void Commit()
        {
            // Pay earmarked local pulses first, so the grid cannot drain their batteries twice.
            foreach (var pair in capacitors) pair.Key.ConsumeShotEnergy(pair.Value);
            foreach (var pair in reserved)
            {
                float remaining = pair.Value;
                foreach (CompPowerBattery battery in pair.Key.batteryComps)
                {
                    if (battery.StunnedByEMP || remaining <= 0f) continue;
                    float drawn = Mathf.Min(remaining, battery.StoredEnergy);
                    if (drawn <= 0f) continue;
                    battery.DrawPower(drawn); remaining -= drawn;
                }
            }
        }
    }

    internal readonly struct SpaceWeaponParameters
    {
        internal readonly CompSpaceWeaponMagazine magazine;
        internal readonly WeaponAimInfo aim;
        internal readonly SpaceWeaponEnergy energy;
        internal readonly float mass;
        internal readonly int heads, charge, power, cycleTicks;
        internal readonly float shotHeat;
        internal SpaceWeaponParameters(CompSpaceWeaponMagazine magazine, WeaponAimInfo aim, SpaceWeaponEnergy energy, float mass = 1f, int heads = 0, int charge = 0, int power = 1, float shotHeat = 0f, int cycleTicks = 0)
        { this.magazine = magazine; this.aim = aim; this.energy = energy; this.mass = mass; this.heads = heads; this.charge = charge; this.power = power; this.shotHeat = shotHeat; this.cycleTicks = cycleTicks; }
    }

    internal static class SpaceWeaponBallistics
    {
        internal static float ExpectedDamage(CompSpaceWeapon weapon, ThingDef projectile, ThingDef target = null)
        {
            var part = SpaceImpactEffects.Resolve(projectile, target ?? ThingDefOf.Wall)?.defenderEffect;
            if (part == null) return 0f;
            float speed = ProjectedSpeed(weapon, SpaceWeaponService.ProjectileSpeedOf(projectile));
            return SpaceImpactEffects.ComputeDamage(part, speed, 0f, 1f,
                ProjectileMass(weapon, weapon.SelectMagazine())) * weapon.WeaponProps.impactScale * (weapon.Tuning?.power ?? 1);
        }

        internal static bool HasBaseShotEnergy(CompSpaceWeapon weapon)
            => weapon?.parent?.Spawned == true && weapon.QueryEnergy.Evaluate(weapon, false);

        internal static bool TryPlanEnergy(CompSpaceWeapon weapon,
            out List<CompSpaceRailgun> active)
        {
            active = null;
            if (weapon?.parent?.Spawned != true) return false;
            bool ready = weapon.QueryEnergy.Evaluate(weapon);
            active = weapon.QueryEnergy.active;
            return ready;
        }

        internal static float ProjectileMass(CompSpaceWeapon weapon, CompSpaceWeaponMagazine magazine)
        {
            var source = magazine ?? weapon.Component<CompSpaceWeaponMagazine>();
            return source?.Supply(GunSupplyKind.Heads) != null ? source.RequiredHeads(weapon) * source.MagazineProps.unitMass
                : weapon.WeaponProps.projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>()?.mass ?? 1f;
        }
        internal static float BaseSpeed(CompSpaceWeapon weapon, CompSpaceWeaponMagazine magazine, float fallback) =>
            weapon.Tuning?.Settings.chemical == true ? SpaceKinetics.LaunchSpeed(weapon.Tuning.charge, ProjectileMass(weapon, magazine))
                : weapon.WeaponProps.launchKineticEnergy > 0f ? SpaceKinetics.Speed(ProjectileMass(weapon, magazine), weapon.WeaponProps.launchKineticEnergy) : fallback;
        internal static float AcceleratedSpeed(float baseSpeed, float mass, float energy)
            => energy > 0f ? SpaceKinetics.Speed(mass, SpaceKinetics.Energy(mass, baseSpeed) + energy) : baseSpeed;
        internal static float ProjectedSpeed(CompSpaceWeapon weapon, float baseSpeed)
        {
            baseSpeed = BaseSpeed(weapon, weapon.SelectMagazine(), baseSpeed);
            return weapon.QueryEnergy.Evaluate(weapon) ? AcceleratedSpeed(baseSpeed,
                ProjectileMass(weapon, weapon.SelectMagazine()), weapon.QueryEnergy.kineticEnergyBonus) : baseSpeed;
        }


        internal static SpaceWeaponParameters Read(CompSpaceWeapon weapon, SpaceObject container,
            SpaceWeaponEnergy energy, bool forShot)
        {
            var perf = weapon.Universe?.Performance;
            long start = perf?.Start() ?? 0L;
            try
            {
                CompSpaceWeaponMagazine magazine = weapon.SelectMagazine();
                ThingDef projectile = magazine?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
                var layout = weapon.Layout;
                bool accelerated = layout.acceleration.segments.Count > 0;
                if (forShot || accelerated) energy.Evaluate(weapon);
                else energy.Clear();
                Vector2 muzzle = layout.acceleration.muzzleMap ?? SpaceWeaponGeometry.MuzzleMapPoint(weapon);
                float speed = AcceleratedSpeed(BaseSpeed(weapon, magazine, SpaceWeaponService.ProjectileSpeedOf(projectile)),
                    ProjectileMass(weapon, magazine), energy.kineticEnergyBonus);
                return new SpaceWeaponParameters(magazine,
                    new WeaponAimInfo(SpaceTransform.MapPointToUniverse(container, weapon.parent.Map, muzzle),
                        muzzle, projectile, speed, weapon.WeaponProps.minRange, weapon.EffectiveRange), energy, ProjectileMass(weapon, magazine),
                    magazine?.RequiredHeads(weapon) ?? 0, weapon.Tuning?.Settings.chemical == true ? weapon.Tuning.charge : 0, weapon.Tuning?.power ?? 1, weapon.Tuning?.ShotHeat ?? 0f,
                    weapon.Tuning?.CycleTicks ?? weapon.ShotInterval);
            }
            finally { perf?.Record(PerformanceStage.WeaponParameters, start); }
        }

        internal static SpaceWeaponParameters AtCurrentMuzzle(CompSpaceWeapon weapon, SpaceObject container,
            SpaceWeaponParameters parameters)
        {
            if (!weapon.WeaponProps.topTurret || weapon.WeaponProps.turretMuzzleDistance <= 0f
                || weapon.Layout.acceleration.muzzleMap.HasValue) return parameters;
            Vector2 muzzle = SpaceWeaponGeometry.MuzzleMapPoint(weapon);
            WeaponAimInfo info = parameters.aim;
            return new SpaceWeaponParameters(parameters.magazine,
                new WeaponAimInfo(SpaceTransform.MapPointToUniverse(container, weapon.parent.Map, muzzle),
                    muzzle, info.Projectile, info.Speed, info.MinRange, info.Range), parameters.energy, parameters.mass, parameters.heads, parameters.charge, parameters.power, parameters.shotHeat, parameters.cycleTicks);
        }

        internal static bool TryAim(WeaponAimInfo info, SpacePose source, SpacePose target, out Vector3 point)
        {
            point = target.position;
            if (info.Laser || info.Guided) return true;
            if (!TrySolveIntercept(info.Muzzle, source, target, info.Speed, out Vector3 lead))
                return false;
            float distance = OrbitSphere.ArcCells(info.Muzzle, lead);
            if (distance < info.MinRange || distance > info.Range) return false;
            point = lead;
            return true;
        }

        internal static bool TrySolveIntercept(Vector3 muzzle, SpacePose sourcePose,
            SpacePose targetPose, float projectileSpeed, out Vector3 interceptUniverse)
        {
            interceptUniverse = default;
            if (projectileSpeed <= 0f) return false;
            Vector2 p = OrbitSphere.Offset(muzzle, targetPose.position);
            Vector2 relativeVelocity = SpaceTransform.TransportVelocity(targetPose, muzzle)
                - SpaceTransform.VelocityAtUniversePoint(sourcePose, muzzle);
            if (!SpaceTransform.TrySolveInterceptPlanar(p, relativeVelocity, projectileSpeed, out float time)) return false;
            interceptUniverse = OrbitSphere.Move(muzzle, p + relativeVelocity * time);
            return true;
        }
    }
}
