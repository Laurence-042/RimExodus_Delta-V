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

        /// <summary>建筑（含非气密炮塔等；命中概率读取掩体效果）。</summary>
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
    /// 建筑碰撞与船壳可见结构的分类入口。碰撞纳入全部建筑，再按掩体效果掷命中；
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
        public static float BuildingHitChance(Thing building) => building?.def.IsWall == true
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
        /// 分类一个格子（碰撞占用集构建用）：建筑优先于基架；同格重叠时墙优先，
        /// 其余取掩体效果最高者。无建筑但有基架 → Substructure；否则 Empty。
        /// </summary>
        public static SpaceCellKind ClassifyCell(Map map, IntVec3 cell, out Thing solidThing)
        {
            solidThing = null;
            if (map == null || !cell.InBounds(map)) return SpaceCellKind.Empty;
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            float bestChance = -1f;
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing is not Building building || building.Destroyed || !building.Spawned
                    || !building.OccupiedRect().Contains(cell)) continue;
                // 电缆铺在结构内，不作为独立碰撞面；隐藏电缆免疫攻击，不能因此让基架无敌。
                if (building.def.building.isPowerConduit) continue;
                float chance = BuildingHitChance(thing);
                if (solidThing != null && (chance < bestChance
                    || chance == bestChance && !thing.def.IsWall)) continue;
                solidThing = thing;
                bestChance = chance;
            }
            if (solidThing != null)
                return solidThing.def.building.isNaturalRock ? SpaceCellKind.NaturalRock : SpaceCellKind.Building;
            if (SubstructureOwnership.HasFoundation(map, cell)) return SpaceCellKind.Substructure;
            return SpaceCellKind.Empty;
        }
    }
}
