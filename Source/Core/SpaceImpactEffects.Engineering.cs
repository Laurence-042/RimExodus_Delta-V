using System.Linq;
using System.Collections.Generic;
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
            var receiverCells = receiver.HullCells;
            // 只在设备所在船的实体甲板上出料；没有落物空间时不切除目标。
            bool OutputCell(IntVec3 c) => c.InBounds(receiverMap) && c.Standable(receiverMap)
                && receiverCells.Contains(c) && SubstructureOwnership.HasFoundation(receiverMap, c);
            var outputCells = new HashSet<IntVec3>();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(launcher.Position, 8f, true))
                if (OutputCell(c) && c.GetFirstItem(receiverMap) == null) outputCells.Add(c);
            if (outputCells.Count == 0) return;
            // 原版产物量不超过建造成本；按完整堆栈保守预留空格，防止最后一刀塞不下材料。
            int Slots(IEnumerable<ThingDefCountClass> costs) => costs == null ? 0
                : costs.Sum(cost => Mathf.CeilToInt(cost.count / (float)Mathf.Max(1, cost.thingDef.stackLimit)));
            IntVec3 output = outputCells.OrderBy(c => c.DistanceToSquared(launcher.Position)).ThenBy(c => c.x)
                .ThenBy(c => c.z).First();

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
                int required = mineral != null
                    ? Mathf.CeilToInt(building.def.building.EffectiveMineableYield * part.miningYieldFactor
                        / Mathf.Max(1, mineral.stackLimit))
                    : building is Mineable ? 0 : Slots(building.CostListAdjusted());
                if (outputCells.Count < required) return;
                int mineralCount = mineral != null && Rand.Chance(building.def.building.mineableDropChance)
                    ? GenMath.RoundRandom(building.def.building.EffectiveMineableYield * part.miningYieldFactor) : 0;
                // Vanish 不生成击杀掉落；移除成功后仅兑现本次原版拆除产物。
                building.Destroy(DestroyMode.Vanish);
                if (!building.Destroyed) return;
                if (mineralCount > 0)
                {
                    Thing ore = ThingMaker.MakeThing(mineral);
                    ore.stackCount = mineralCount;
                    GenPlace.TryPlaceThing(ore, output, receiverMap, ThingPlaceMode.Near,
                        extraValidator: outputCells.Contains);
                }
                else if (!(building is Mineable))
                    GenLeaving.DoLeavingsFor(building, receiverMap, DestroyMode.Deconstruct,
                        CellRect.SingleCell(output), outputCells.Contains);
                return;
            }

            IntVec3 cell = evt.hitCell;
            if (!evt.defenderIsSubstructure || !cell.IsValid || !cell.InBounds(targetMap)
                || !CanDamageSubstructure(targetMap, cell)) return;
            TerrainDef foundation = SubstructureOwnership.FoundationAt(targetMap, cell);
            if (foundation == null) return;
            if (outputCells.Count < Slots(foundation.CostListAdjusted(null))) return;
            // 基架没有耐久；达到共同毁伤阈值才一次移除。上层地板由现有补丁清理。
            if (part.damage * evt.damageScale < part.substructureBreakThreshold) return;
            targetMap.terrainGrid.RemoveFoundation(cell, doLeavings: false);
            if (SubstructureOwnership.HasFoundation(targetMap, cell)) return;
            GenLeaving.DoLeavingsFor(foundation, output, receiverMap);
        }
    }
}
