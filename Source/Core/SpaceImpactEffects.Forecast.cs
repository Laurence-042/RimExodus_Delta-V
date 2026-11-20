using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceImpactEffects
    {
        private static readonly Dictionary<Type, bool> CustomDamageHooks = new Dictionary<Type, bool>();
        private static bool HasCustomDamageHook(Type type)
        {
            if (CustomDamageHooks.TryGetValue(type, out bool custom)) return custom;
            bool comp = typeof(ThingComp).IsAssignableFrom(type);
            Type owner = type.GetMethod(comp ? nameof(ThingComp.PostPreApplyDamage) : nameof(Thing.PreApplyDamage))?.DeclaringType;
            return CustomDamageHooks[type] = comp ? owner != typeof(ThingComp)
                : owner != typeof(Thing) && owner != typeof(ThingWithComps) && owner != typeof(Building);
        }
        internal static float HitChance(ThingDef projectile, SpaceCellKind kind,
            Thing building, SpaceImpactEffectDef effect)
        {
            if (kind == SpaceCellKind.Substructure)
                return Mathf.Clamp01(effect?.substructureHitChance ?? SpaceImpactEffectDef.DefaultSubstructureHitChance);
            if (building == null || SpaceStructureRules.IsCollisionWall(building)) return 1;
            if (effect?.buildingHitChance >= 0) return Mathf.Clamp01(effect.buildingHitChance);
            return GuaranteedMissileHit(projectile, building.def) ? 1 : SpaceStructureRules.BuildingHitChance(building);
        }

        internal static float DurabilityScale(SpaceImpactEffectDef effect, Thing target) =>
            target != null && effect != null && effect.projectileDefs.NullOrEmpty()
                && effect.targetThingDefs.NullOrEmpty() && !effect.targetsSubstructure
                ? Mathf.Clamp(effect.fallbackBaselineHitPoints / Mathf.Max(1, MaxHitPointsOf(target)), .2f, 2f) : 1;

        internal static bool Ricochets(ImpactOutcomePart part, Thing target, float incidence) =>
            part != null && part.ricochetMinHitPoints > 0 && target != null && !target.Destroyed
                && MaxHitPointsOf(target) >= part.ricochetMinHitPoints
                && incidence >= part.ricochetMinIncidenceDeg * Mathf.Deg2Rad;

        // Vanilla 1.6/1.7 DamageWorker.Apply building branch, in expectation before
        // RoundRandom. Special damage workers/comps are explicitly marked uncertain.
        internal static float ExpectedBuildingDamage(Thing target, DamageDef damage,
            float amount, out bool uncertain)
        {
            uncertain = false;
            if (target == null || !target.def.useHitPoints || damage?.harmsHealth == false) return 0;
            if (target.def.building?.canBeDamagedByAttacks == false) return 0;
            if (damage == null) { uncertain = true; return Mathf.Max(0, amount); }
            Type worker = damage.workerClass;
            uncertain = worker != typeof(DamageWorker) && worker != typeof(DamageWorker_AddInjury)
                && worker != typeof(DamageWorker_SpaceHighExplosive);
            uncertain |= HasCustomDamageHook(target.GetType());
            if (target is ThingWithComps withComps && withComps.AllComps != null)
                foreach (var comp in withComps.AllComps) uncertain |= HasCustomDamageHook(comp.GetType());
            if (target.def.damageMultipliers != null)
                foreach (var multiplier in target.def.damageMultipliers)
                    if (multiplier.damageDef == damage) amount = Mathf.RoundToInt(amount * multiplier.multiplier);
            float factor = damage.buildingDamageFactor * (target.def.passability == Traversability.Impassable
                ? damage.buildingDamageFactorImpassable : damage.buildingDamageFactorPassable);
            if (damage.scaleDamageToBuildingsBasedOnFlammability)
                factor *= Mathf.Max(.05f, target.GetStatValue(RimWorld.StatDefOf.Flammability));
            return Mathf.Max(0, amount * factor);
        }
    }
}
