using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// POI 删除段的结构级快照（阶段3）：地形（foundation 层 + 非 Void 表层）、屋顶、
    /// 建筑与物品。保真边界：不保 pawn（删除条件已保证图上无 pawn）、不保 comp 内部
    /// 状态（电力开关等）、不保燃气/雪/区带。
    ///
    /// 序列化形态：若干平行列表（格列表 + def 列表一一对应），保存/加载共用同一组
    /// Scribe_Collections.Look 调用；所有 def 列表不含 null（null 项在拍摄时直接跳过）。
    /// 快照是存档事实（玩家改动无法从模板恢复），挂在 PoiState 上深序列化。
    /// </summary>
    public class PoiSnapshot : IExposable
    {
        // 平行列表约定：cells[i] 与同下标的 def/值列表一一对应。
        private List<IntVec3> foundationCells = new List<IntVec3>();
        private List<TerrainDef> foundationDefs = new List<TerrainDef>();
        private List<IntVec3> topCells = new List<IntVec3>();
        private List<TerrainDef> topDefs = new List<TerrainDef>();
        private List<IntVec3> roofCells = new List<IntVec3>();
        private List<RoofDef> roofDefs = new List<RoofDef>();

        private List<IntVec3> thingCells = new List<IntVec3>();
        private List<ThingDef> thingDefs = new List<ThingDef>();
        private List<string> thingStuffDefNames = new List<string>(); // 空 = 无材质
        private List<int> thingRots = new List<int>();
        private List<int> thingHitPoints = new List<int>();
        private List<int> thingStacks = new List<int>();
        private List<Faction> thingFactions = new List<Faction>();

        public int ThingCount => thingCells.Count;

        public static PoiSnapshot Capture(Map map)
        {
            var snapshot = new PoiSnapshot();
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                TerrainDef foundation = map.terrainGrid.FoundationAt(cell);
                if (foundation != null)
                {
                    snapshot.foundationCells.Add(cell);
                    snapshot.foundationDefs.Add(foundation);
                }
                TerrainDef top = map.terrainGrid.TerrainAt(cell);
                if (top != null && top != DeltaVDefOf.DeltaV_Void && top != foundation)
                {
                    snapshot.topCells.Add(cell);
                    snapshot.topDefs.Add(top);
                }
                RoofDef roof = map.roofGrid.RoofAt(cell);
                if (roof != null)
                {
                    snapshot.roofCells.Add(cell);
                    snapshot.roofDefs.Add(roof);
                }
            }
            foreach (Thing thing in map.listerThings.AllThings)
            {
                // 只保建筑与物品（pawn 由删除条件排除；植物/幽灵等其他类别不属于 POI 模板词汇）。
                if (!(thing is Building) && thing.def.category != ThingCategory.Item)
                {
                    continue;
                }
                if (!thing.Spawned)
                {
                    continue;
                }
                snapshot.thingCells.Add(thing.Position);
                snapshot.thingDefs.Add(thing.def);
                snapshot.thingStuffDefNames.Add(thing.Stuff?.defName ?? string.Empty);
                snapshot.thingRots.Add(thing.Rotation.AsInt);
                snapshot.thingHitPoints.Add(thing.HitPoints);
                snapshot.thingStacks.Add(thing.stackCount);
                snapshot.thingFactions.Add(thing.Faction);
            }
            return snapshot;
        }

        /// <summary>
        /// 回放到一张刚生成的空 POI 图（全 Void 底），建筑先于物品落位。ownerId 用于
        /// 基架归属盖章（POI 的 objectId），恢复出的基架与原对象保持同一归属。
        /// </summary>
        public void ApplyTo(Map map, int ownerId)
        {
            SubstructureOwnership.For(map).SetPendingStamp(foundationCells, ownerId);
            try
            {
                for (int i = 0; i < foundationCells.Count; i++)
                {
                    map.terrainGrid.SetFoundation(foundationCells[i], foundationDefs[i]);
                }
            }
            finally
            {
                SubstructureOwnership.For(map).ClearPendingStamp();
            }
            for (int i = 0; i < topCells.Count; i++)
            {
                map.terrainGrid.SetTerrain(topCells[i], topDefs[i]);
            }
            for (int i = 0; i < roofCells.Count; i++)
            {
                map.roofGrid.SetRoof(roofCells[i], roofDefs[i]);
            }
            for (int i = 0; i < thingCells.Count; i++)
            {
                if (!thingDefs[i].MadeFromStuff)
                {
                    SpawnThing(map, i, ThingMaker.MakeThing(thingDefs[i]));
                }
            }
            for (int i = 0; i < thingCells.Count; i++)
            {
                if (thingDefs[i].MadeFromStuff)
                {
                    ThingDef stuff = DefDatabase<ThingDef>.GetNamedSilentFail(thingStuffDefNames[i]);
                    SpawnThing(map, i, ThingMaker.MakeThing(thingDefs[i], stuff));
                }
            }
        }

        private void SpawnThing(Map map, int i, Thing thing)
        {
            if (thing == null)
            {
                return;
            }
            if (thingDefs[i].useHitPoints)
            {
                thing.HitPoints = Mathf.Clamp(thingHitPoints[i], 1, thing.MaxHitPoints);
            }
            if (thingStacks[i] > 1 && thing.def.stackLimit > 1)
            {
                thing.stackCount = Mathf.Min(thingStacks[i], thing.def.stackLimit);
            }
            if (thingFactions[i] != null)
            {
                thing.SetFaction(thingFactions[i]);
            }
            GenSpawn.Spawn(thing, thingCells[i], map, new Rot4(thingRots[i]), WipeMode.Vanish);
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref foundationCells, "foundationCells", LookMode.Value);
            Scribe_Collections.Look(ref foundationDefs, "foundationDefs", LookMode.Def);
            Scribe_Collections.Look(ref topCells, "topCells", LookMode.Value);
            Scribe_Collections.Look(ref topDefs, "topDefs", LookMode.Def);
            Scribe_Collections.Look(ref roofCells, "roofCells", LookMode.Value);
            Scribe_Collections.Look(ref roofDefs, "roofDefs", LookMode.Def);
            Scribe_Collections.Look(ref thingCells, "thingCells", LookMode.Value);
            Scribe_Collections.Look(ref thingDefs, "thingDefs", LookMode.Def);
            Scribe_Collections.Look(ref thingStuffDefNames, "thingStuffDefNames", LookMode.Value);
            Scribe_Collections.Look(ref thingRots, "thingRots", LookMode.Value);
            Scribe_Collections.Look(ref thingHitPoints, "thingHitPoints", LookMode.Value);
            Scribe_Collections.Look(ref thingStacks, "thingStacks", LookMode.Value);
            Scribe_Collections.Look(ref thingFactions, "thingFactions", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // XML 加载路径绕过字段初始化器；缺失节点的列表保持 null，统一兜底为空表
                // （正常存档每个列表都会写节点，这里是防御层）。
                foundationCells ??= new List<IntVec3>();
                foundationDefs ??= new List<TerrainDef>();
                topCells ??= new List<IntVec3>();
                topDefs ??= new List<TerrainDef>();
                roofCells ??= new List<IntVec3>();
                roofDefs ??= new List<RoofDef>();
                thingCells ??= new List<IntVec3>();
                thingDefs ??= new List<ThingDef>();
                thingStuffDefNames ??= new List<string>();
                thingRots ??= new List<int>();
                thingHitPoints ??= new List<int>();
                thingStacks ??= new List<int>();
                thingFactions ??= new List<Faction>();
            }
        }
    }
}
