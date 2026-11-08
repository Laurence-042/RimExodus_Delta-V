using System;
using System.Collections.Generic;
using UnityEngine;
using RimWorld;
using Verse;

namespace DeltaV
{
    // A candidate's finite firing window. All weapons share one resource ledger and
    // advance together; evaluating guns independently would spend the same battery twice.
    internal sealed class CombatWeaponForecast
    {
        private sealed class Gun
        {
            internal CompSpaceWeapon weapon;
            internal CompWeaponTuning thermal;
            internal SpaceWeaponEnergy energy;
            internal SpaceWeaponParameters? invariantShot;
            internal SpaceWeaponParameters trackingShot;
            internal bool traverses;
            internal int cooldown, previousMagazine, shots, retry;
        }
        private readonly List<Gun> guns = new List<Gun>();
        private readonly CombatResourceLedger resources;
        internal CombatWeaponForecast(IEnumerable<CompSpaceWeapon> weapons, CombatResourceLedger resources)
        {
            this.resources = resources;
            foreach (var weapon in weapons)
            {
                resources.Observe(weapon.parent.TryGetComp<CompPowerTrader>()?.PowerNet);
                var gun = new Gun { weapon = weapon, thermal = weapon.Tuning?.ForecastCopy(),
                    energy = new SpaceWeaponEnergy(resources), cooldown = weapon.CooldownTicksLeft,
                    previousMagazine = weapon.LastMagazineThingId, traverses = weapon.Component<CompSpaceWeaponTraverse>() != null };
                // With no selectable ammunition or paid accelerator, resource changes
                // affect readiness but cannot change the shot's ballistic parameters.
                if (weapon.Layout.magazines.Count == 0 && weapon.Layout.acceleration.segments.Count == 0)
                    gun.invariantShot = SpaceWeaponBallistics.Read(weapon, weapon.Container, gun.energy, false, resources);
                guns.Add(gun);
            }
            guns.Sort((a, b) => a.weapon.parent.thingIDNumber.CompareTo(b.weapon.parent.thingIDNumber));
        }
        internal void Run(float seconds, Func<CompSpaceWeapon, SpaceWeaponParameters, float, bool> canHit,
            Action<CompSpaceWeapon, SpaceWeaponParameters, float> fire, Action<float> advance = null,
            int maxShotsPerWeapon = int.MaxValue, Action<float> afterTick = null,
            Action<CompSpaceWeapon, SpaceWeaponParameters, float> track = null)
        {
            int ticks = Mathf.Clamp(Mathf.CeilToInt(seconds * 60), 0, 7200);
            for (int tick = 0; tick < ticks; tick++)
            {
                if (tick > 0) resources.AdvanceTick();
                advance?.Invoke(tick / 60f);
                foreach (var gun in guns)
                {
                    if (gun.shots >= maxShotsPerWeapon) continue;
                    if (track != null && gun.traverses && !resources.destroyed.Contains(gun.weapon.parent))
                    {
                        if (tick % ShipFireControl.AutomaticAimRefreshTicks == 0)
                            gun.trackingShot = gun.invariantShot ?? SpaceWeaponBallistics.Read(gun.weapon,
                                gun.weapon.Container, gun.energy, false, resources, gun.previousMagazine);
                        track(gun.weapon, gun.trackingShot, tick / 60f);
                    }
                    if (tick > 0)
                    {
                        gun.thermal?.AdvanceThermalTick();
                        gun.cooldown = Mathf.Max(0, gun.cooldown - 1);
                    }
                    if (resources.destroyed.Contains(gun.weapon.parent)
                        || (gun.thermal?.RemainingTicks ?? gun.cooldown) > 0 || tick < gun.retry) continue;
                    var energy = gun.energy;
                    if (!resources.Powered(gun.weapon.parent.TryGetComp<CompPowerTrader>()))
                    { gun.retry = tick + 6; continue; }
                    SpaceWeaponParameters shot;
                    if (gun.invariantShot.HasValue)
                    {
                        shot = gun.invariantShot.Value;
                        if (!canHit(gun.weapon, shot, tick / 60f)) { gun.retry = tick + 6; continue; }
                        energy.Evaluate(gun.weapon);
                    }
                    else shot = SpaceWeaponBallistics.Read(gun.weapon, gun.weapon.Container, energy, true,
                        resources, gun.previousMagazine);
                    if (!energy.ready || !gun.invariantShot.HasValue && !canHit(gun.weapon, shot, tick / 60f)
                        || !resources.TakeAmmo(gun.weapon, shot.magazine))
                    { gun.retry = tick + 6; continue; }
                    energy.Commit();
                    gun.previousMagazine = shot.magazine?.parent.thingIDNumber ?? gun.previousMagazine;
                    gun.thermal?.CommitShot(shot.shotHeat, shot.cycleTicks);
                    gun.cooldown = Mathf.Max(1, shot.cycleTicks);
                    gun.shots++;
                    fire(gun.weapon, shot, tick / 60f);
                }
                afterTick?.Invoke(tick / 60f);
            }
        }
    }
}
