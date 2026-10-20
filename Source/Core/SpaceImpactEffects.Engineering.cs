using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceImpactEffects
    {
        private static void Salvage(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part)
        {
            Thing launcher = evt.launcher;
            if (launcher?.Spawned != true || launcher.Destroyed || evt.defender.IsActiveCollider
                || evt.damageScale <= 0f) return;
            Map targetMap = evt.defender.map;
            Map receiverMap = launcher.Map;
            if (receiverMap == targetMap || receiverMap.Disposed) return;
            ShipBase receiver = ShipRegistry.ShipOf(launcher);
            if (receiver?.IsValid != true) return;
            // 与深钻井同走原版 Near 放置：合堆、找位与失败由原版处理，不用空格数阻止切割。
            IntVec3 output = launcher.Position;

            if (evt.defenderBuilding is Building building && building.Spawned && !building.Destroyed)
            {
                if (!building.def.destroyable || !building.def.building.canBeDamagedByAttacks
                    || building is Frame) return;
                int cut = Mathf.Max(1, Mathf.RoundToInt(part.damage * evt.damageScale));
                if (building.def.useHitPoints && building.HitPoints > cut)
                {
                    // 切割进度复用真实耐久，不新增可丢失或重复兑现的材料账本。
                    building.HitPoints -= cut;
                    return;
                }
                ThingDef mineral = building is Mineable ? building.def.building.mineableThing : null;
                int mineralCount = mineral != null && Rand.Chance(building.def.building.mineableDropChance)
                    ? GenMath.RoundRandom(building.def.building.EffectiveMineableYield * part.miningYieldFactor) : 0;
                // Vanish 不生成击杀掉落；移除成功后仅兑现本次原版拆除产物。
                building.Destroy(DestroyMode.Vanish);
                if (!building.Destroyed) return;
                if (mineralCount > 0)
                {
                    Thing ore = ThingMaker.MakeThing(mineral);
                    ore.stackCount = mineralCount;
                    using (new SalvageOutputScope(launcher))
                        GenPlace.TryPlaceThing(ore, output, receiverMap, ThingPlaceMode.Near);
                }
                else if (!(building is Mineable))
                {
                    using (new SalvageOutputScope(launcher))
                        GenLeaving.DoLeavingsFor(building, receiverMap, DestroyMode.Deconstruct,
                            CellRect.SingleCell(output));
                }
                return;
            }

            IntVec3 cell = evt.hitCell;
            if (!evt.defenderIsSubstructure || !cell.IsValid || !cell.InBounds(targetMap)
                || !SubstructureDamageUtility.CanDamage(targetMap, cell)) return;
            TerrainDef foundation = SubstructureOwnership.FoundationAt(targetMap, cell);
            if (foundation == null) return;
            // 基架没有耐久；达到共同毁伤阈值后调用统一毁伤机制，再结算材料。
            if (part.damage * evt.damageScale < part.substructureBreakThreshold) return;
            if (!SubstructureDamageUtility.TryDestroy(targetMap, cell)) return;
            using (new SalvageOutputScope(launcher))
                GenLeaving.DoLeavingsFor(foundation, output, receiverMap);
        }
    }
}
