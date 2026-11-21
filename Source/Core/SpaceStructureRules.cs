using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>格内容类别（碰撞占用集；外壳另按实体结构规则筛选）。</summary>
    public enum SpaceCellKind : byte
    {
        /// <summary>无碰撞面（空真空/地板格……）。</summary>
        Empty = 0,

        /// <summary>建筑（不可通行时必中，其余默认按掩体效果）。</summary>
        Building = 1,

        /// <summary>基架格（无存活建筑覆盖的甲板；可概率命中）。</summary>
        Substructure = 2,

        /// <summary>自然岩石（ asteroid 岩壁，实心）。</summary>
        NaturalRock = 3
    }

    public readonly struct SpaceCollisionCell
    {
        public readonly IntVec3 cell;
        public readonly SpaceCellKind kind;
        public readonly Thing building;
        public SpaceCollisionCell(IntVec3 cell, SpaceCellKind kind, Thing building)
        { this.cell = cell; this.kind = kind; this.building = building; }
    }

    /// <summary>
    /// 建筑碰撞与船壳可见结构的分类入口。不可通行的碰撞建筑必中；
    /// 外壳只绘实体结构或显式规则，内部炮塔可被击中但不因此画在外壳上。
    /// </summary>
    public static class SpaceStructureRules
    {
        public static IEnumerable<SpaceCollisionCell> CollisionCells(Map map, IEnumerable<IntVec3> cells)
        {
            foreach (IntVec3 cell in cells)
            {
                SpaceCellKind kind = ClassifyCell(map, cell, out Thing building);
                yield return new SpaceCollisionCell(cell, kind, building);
            }
        }
        internal static bool IsImpassableBuilding(Thing building) =>
            building is Building && building.def.passability == Traversability.Impassable;

        public static float BuildingHitChance(Thing building) => IsImpassableBuilding(building)
            ? 1f : Mathf.Clamp01(building?.def.fillPercent ?? 0f);

        /// <summary>
        /// 建筑是否属于可绘入外壳的实体结构（墙/门/气密建筑/自然岩石）。
        /// 另含两类**实体船体**（太空战架构 §3.2/§7.3：弹体与 shuttle 的碰撞面 =
        /// 其 building 脚印，与气密性无关——否则非气密舱体会被占用集漏掉、无法被
        /// PD 命中）：挂 CompSpaceProjectile / CompSpaceShuttle 的建筑恒为实心。
        /// </summary>
        public static bool IsSolidStructure(Thing thing)
        {
            if (thing is not Building building || building.Destroyed || !building.Spawned)
            {
                return false;
            }
            if (building.def.IsWall || building.def.IsDoor
                || building.IsAirtight || building.def.building.isNaturalRock)
            {
                return true;
            }
            return building.TryGetComp<CompSpaceProjectile>() != null
                || building.TryGetComp<CompSpaceShuttle>() != null;
        }

        /// <summary>
        /// 分类一个格子（碰撞占用集构建用）：建筑优先于基架；同格重叠时不可通行建筑优先，
        /// 其余取掩体效果最高者。无建筑但有基架 → Substructure；否则 Empty。
        /// </summary>
        public static SpaceCellKind ClassifyCell(Map map, IntVec3 cell, out Thing solidThing) =>
            ClassifyCell(map, cell, out solidThing, null);

        internal static SpaceCellKind ClassifyCell(Map map, IntVec3 cell, out Thing solidThing,
            HashSet<Thing> destroyed, ShipStructureIndex.MapView view = null)
        {
            solidThing = null;
            if (map == null || !cell.InBounds(map)) return SpaceCellKind.Empty;
            if (view == null)
            {
                var index = Current.Game?.GetComponent<UniverseManager>()?.ShipStructure;
                if (index?.Tracks(map) == true) view = index.For(map);
            }

            Building best = null;
            float bestChance = -1f;
            void Check(Building building)
            {
                if (building.Destroyed || !building.Spawned || destroyed?.Contains(building) == true) return;
                // A cable inside the structure cannot hide the foundation from damage.
                if (building.def.building.isPowerConduit || building is Building_VacuumBarrier) return;
                float chance = BuildingHitChance(building);
                if (best != null && (chance < bestChance
                    || (chance == bestChance && (!IsImpassableBuilding(building) || IsImpassableBuilding(best))))) return;
                best = building;
                bestChance = chance;
            }

            if (view != null)
            {
                foreach (var record in view.RecordsAt(cell)) Check(record.Building);
            }
            else
            {
                foreach (Thing thing in map.thingGrid.ThingsListAtFast(cell))
                    if (thing is Building building && building.OccupiedRect().Contains(cell)) Check(building);
            }
            solidThing = best;
            if (best != null) return best.def.building.isNaturalRock ? SpaceCellKind.NaturalRock : SpaceCellKind.Building;
            return SubstructureOwnership.HasFoundation(map, cell) ? SpaceCellKind.Substructure : SpaceCellKind.Empty;
        }
    }
}
