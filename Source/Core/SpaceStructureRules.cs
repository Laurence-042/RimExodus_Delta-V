using System.Collections.Generic;
using Verse;

namespace DeltaV
{
    /// <summary>格内容类别（碰撞占用集与外壳渲染共用的分类结果）。</summary>
    public enum SpaceCellKind : byte
    {
        /// <summary>无碰撞面（空真空/地板格……）。</summary>
        Empty = 0,

        /// <summary>实心建筑（墙/门/气密结构；含护盾发生器、推进器等气密设备）。</summary>
        Building = 1,

        /// <summary>基架格（无实心建筑覆盖的甲板基架；命中判定可概率放行）。</summary>
        Substructure = 2,

        /// <summary>自然岩石（ asteroid 岩壁，实心）。</summary>
        NaturalRock = 3
    }

    /// <summary>
    /// 「哪些东西算船体结构」的唯一分类器（太空战架构 §3.3：占用格集来源与外壳渲染
    /// 同数据源）。外壳渲染（MapOverlayRenderer.ShouldDrawThing）的建筑实心部分与
    /// 碰撞核心（SpaceCollision）的占用集都经本类判定——两份规则不得各自演化
    ///（规则越一致、维护越简单；漂移即「渲染显示但打不中/打中了却看不见」）。
    /// 渲染侧的 ShellRenderRuleDef（exclude/替代贴图/zIndex）只影响画不画，
    /// 不改变本分类。
    /// </summary>
    public static class SpaceStructureRules
    {
        /// <summary>
        /// 建筑是否属于实心结构（墙/门/气密建筑/自然岩石；外壳渲染与碰撞占用同判）。
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
        /// 分类一个格子（碰撞占用集构建用）：实心建筑优先于基架（门/墙下的甲板格
        /// 命中建筑本体）；无实心建筑但有基架 → Substructure；否则 Empty。
        /// </summary>
        public static SpaceCellKind ClassifyCell(Map map, IntVec3 cell, out Thing solidThing)
        {
            solidThing = null;
            if (map == null || !cell.InBounds(map)) return SpaceCellKind.Empty;
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (!IsSolidStructure(thing) || !thing.OccupiedRect().Contains(cell)) continue;
                solidThing = thing;
                return thing.def.building.isNaturalRock ? SpaceCellKind.NaturalRock : SpaceCellKind.Building;
            }
            if (map.terrainGrid.FoundationAt(cell)?.IsSubstructure == true) return SpaceCellKind.Substructure;
            return SpaceCellKind.Empty;
        }
    }
}
