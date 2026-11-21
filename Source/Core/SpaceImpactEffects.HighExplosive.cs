using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class HighExplosiveProfile
    {
        public SimpleCurve damageByMass;
        public SimpleCurve radiusByMass;

        internal bool IsValid => ValidCurve(damageByMass) && ValidCurve(radiusByMass);
        private static bool ValidCurve(SimpleCurve curve) => curve?.PointsCount > 0
            && curve.Points.All(point => point.x > 0f && !float.IsInfinity(point.x)
                && point.y > 0f && !float.IsInfinity(point.y));

        public int CenterDamage(float mass) => Mathf.Max(1, Mathf.RoundToInt(damageByMass.Evaluate(mass)));
        public float Radius(float mass) => radiusByMass.Evaluate(mass);
    }

    public static partial class SpaceImpactEffects
    {
        internal static float ExplosionRadius(ImpactOutcomePart part, float mass) =>
            part?.highExplosive?.Radius(mass) ?? part?.explosionRadius ?? 0f;

        internal static List<IntVec3> HighExplosiveCells(DamageDef damage, Map map, IntVec3 center, float radius) =>
            damage.Worker.ExplosionCellsToHit(center, map, radius).ToList();

        // Native Explosion.GetDamageAmountAt applies this falloff with random rounding.
        // Foundation rolls and forecasts use the amount before rounding.
        internal static float HighExplosiveDamageAt(int centerDamage, float radius, IntVec3 center, IntVec3 cell) =>
            Mathf.Max(1f, Mathf.Lerp(centerDamage, centerDamage * .2f, (cell - center).LengthHorizontal / radius));

        // Matches Explosion.StartExplosion at the standard propagation speed. The wave consumes the list backwards.
        internal static void OrderHighExplosiveCells(List<IntVec3> cells, IntVec3 center)
        {
            cells.Sort((a, b) => ((int)((b - center).LengthHorizontal * 1.5f))
                .CompareTo((int)((a - center).LengthHorizontal * 1.5f)));
        }

        private static void DetonateHighExplosive(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part, Map map)
        {
            IntVec3 center = evt.hitCell;
            if (!center.InBounds(map)) return;
            float radius = part.highExplosive.Radius(evt.mass);
            DamageDef damage = part.damageDef;
            GenExplosion.DoExplosion(center, map, radius, damage, evt.launcher,
                part.highExplosive.CenterDamage(evt.mass), part.armorPenetration, projectile: evt.projectileDef,
                damageFalloff: true, overrideCells: HighExplosiveCells(damage, map, center, radius));
        }
    }

    public sealed class DamageWorker_SpaceHighExplosive : DamageWorker_AddInjury
    {
        public override void ExplosionAffectCell(Explosion explosion, IntVec3 c, List<Thing> damagedThings,
            List<Thing> ignoredThings, bool canThrowMotes)
        {
            base.ExplosionAffectCell(explosion, c, damagedThings, ignoredThings, canThrowMotes);
            var part = SpaceImpactEffects.Resolve(explosion.projectile, null)?.defenderEffect;
            if (part?.highExplosive == null || !SubstructureDamageUtility.CanDamage(explosion.Map, c)) return;

            using var leavings = CombatLeavingsScope.Enter(explosion.Map);
            if (leavings == null) return;
            float damage = SpaceImpactEffects.HighExplosiveDamageAt(explosion.damAmount, explosion.radius,
                explosion.Position, c);
            if (Rand.Chance(SpaceImpactEffects.FoundationBreakChance(damage, part)))
                SubstructureDamageUtility.TryDestroyHitFoundation(explosion.Map, c, instigator: explosion.instigator);
        }
    }
}
