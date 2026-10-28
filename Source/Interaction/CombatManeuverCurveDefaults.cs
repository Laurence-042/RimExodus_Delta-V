using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DeltaV
{
    internal static class CombatManeuverCurveDefaults
    {
        internal static CombatManeuverCurves For(ShipBase ship, float clearance,
            float fallbackDistance)
        {
            var ranges = new List<(float range, float weight)>();
            if (ship?.IsValid == true)
                foreach (CompSpaceWeapon weapon in ship.Weapons)
                {
                    if (weapon.aimMode != SpaceWeaponAimMode.Volley
                        || weapon.WeaponProps.manualOnly || !weapon.SpaceModeEnabled
                        || !weapon.GroupActive || weapon.WeaponProps.range <= 0f) continue;
                    float damage = SpaceImpactEffects.Resolve(
                        weapon.SelectMagazine()?.SelectedProjectileDef
                            ?? weapon.WeaponProps.projectileDef,
                        null)?.defenderEffect?.damage ?? 20f;
                    float weight = damage * weapon.WeaponProps.impactScale * 60f
                        / Mathf.Max(1, weapon.WeaponProps.cooldownTicks);
                    ranges.Add((weapon.WeaponProps.range, Mathf.Max(0.01f, weight)));
                }
            float preferred = fallbackDistance;
            if (ranges.Count > 0)
            {
                ranges.Sort((a, b) => a.range.CompareTo(b.range));
                float half = ranges.Sum(r => r.weight) * 0.5f;
                float accumulated = 0f;
                foreach (var entry in ranges)
                {
                    accumulated += entry.weight;
                    if (accumulated < half) continue;
                    preferred = entry.range * 0.85f;
                    break;
                }
            }
            preferred = Mathf.Max(clearance + 20f, preferred);
            float middle = clearance + (preferred - clearance) * 0.5f;
            float far = preferred + Mathf.Max(12f, preferred * 0.2f);
            return new CombatManeuverCurves
            {
                distance = new CombatScoreCurve(true,
                    new CombatScorePoint(0f, -300f),
                    new CombatScorePoint(clearance, -150f),
                    new CombatScorePoint(middle, 80f),
                    new CombatScorePoint(preferred, 100f),
                    new CombatScorePoint(far, -200f),
                    new CombatScorePoint(Mathf.Max(far + 1f, 2f * preferred), -400f)),
                outgoing = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, 0f), new CombatScorePoint(10f, 20f),
                    new CombatScorePoint(50f, 50f), new CombatScorePoint(500f, 100f)),
                incoming = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, 0f), new CombatScorePoint(10f, -20f),
                    new CombatScorePoint(50f, -50f), new CombatScorePoint(500f, -100f)),
                projectileSeconds = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, -250f), new CombatScorePoint(1f, -160f),
                    new CombatScorePoint(3f, -40f), new CombatScorePoint(5f, 0f),
                    new CombatScorePoint(8f, 0f)),
                hullOverlap = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, 0f), new CombatScorePoint(0.5f, -150f),
                    new CombatScorePoint(1f, -300f)),
                alignmentError = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, 20f), new CombatScorePoint(10f, 15f),
                    new CombatScorePoint(90f, 0f), new CombatScorePoint(180f, -10f))
            };
        }
    }
}
