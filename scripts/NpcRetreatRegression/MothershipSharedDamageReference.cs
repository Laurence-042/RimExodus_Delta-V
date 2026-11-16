#if MOTHERSHIP_OFFLINE
using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using UnityEngine;
using ShotInput = DeltaV.CombatEngagementEvaluation.OfflineShot;

internal static partial class Program
{
    internal sealed class MothershipSharedDamageReference
    {
        internal readonly record struct Arrival(int Tick, int Slot, int Pellet, ShotInput Shot)
        {
            internal int Sample => (Shot.Weapon.parent.thingIDNumber + Pellet) & 7;
            internal Vector2 Direction => SpaceTransform.Rotate(Shot.Direction,
                SpaceWeaponShot.FanOffset(Shot.Weapon.WeaponProps, Pellet));
        }

        internal readonly record struct Benefit(float Damage, float Guard, float Critical, float Armor,
            float Uncertainty, float Returned)
        {
            internal Benefit Add(CombatDamageEstimate damage, float critical) => new(Damage + damage.structure,
                Guard + damage.guard, Critical + critical, Armor + damage.armorProgress,
                Uncertainty + (damage.uncertain || damage.exhausted
                    ? Math.Max(damage.structure, damage.uncertainDamageUpper) : 0),
                Returned + damage.returnedPotential);
        }

        internal static Arrival[] Schedule(IReadOnlyList<ShotInput> shots, int window)
        {
            var arrivals = new List<Arrival>();
            for (int slot = 0; slot < shots.Count; slot++)
            {
                var shot = shots[slot];
                int flight = Mathf.CeilToInt(shot.Flight * 60);
                int interval = Math.Max(1, shot.Shot.cycleTicks > 0 ? shot.Shot.cycleTicks : shot.Weapon.ShotInterval);
                for (int tick = shot.Weapon.CooldownTicksLeft + flight; tick < window; tick += interval)
                    for (int pellet = 0; pellet < shot.Weapon.WeaponProps.projectilesPerShot; pellet++)
                        arrivals.Add(new Arrival(tick, slot, pellet, shot));
            }
            return arrivals.OrderBy(a => a.Tick).ThenBy(a => a.Shot.Weapon.parent.thingIDNumber)
                .ThenBy(a => a.Pellet).ToArray();
        }

        internal static Benefit[] Run(IReadOnlyList<ShotInput> shots, int window, Action<bool> recordArrival)
        {
            var benefits = new Benefit[shots.Count];
            foreach (var forecast in shots.Select(s => s.Forecast).Distinct()) forecast.ResetShot();
            foreach (var arrival in Schedule(shots, window))
            {
                var shot = arrival.Shot;
                var props = shot.Weapon.WeaponProps;
                float previousCritical = shot.Forecast.CriticalDamage;
                var damage = shot.Forecast.Fire(shot.From, arrival.Direction, shot.Length,
                    shot.Shot.aim.Projectile, shot.Shot.mass / props.projectilesPerShot, shot.Speed,
                    props.impactScale * shot.Shot.power, shot.Shot.power, (arrival.Sample + .5f) / 8f);
                damage.uncertain |= !shot.Shot.aim.Laser;
                benefits[arrival.Slot] = benefits[arrival.Slot].Add(damage, shot.Forecast.CriticalDamage - previousCritical);
                recordArrival?.Invoke(shot.Outgoing);
            }
            return benefits;
        }
    }
}
#endif
