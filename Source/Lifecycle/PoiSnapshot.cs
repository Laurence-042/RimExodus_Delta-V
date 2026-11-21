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
        private List<int> thingQualities = new List<int>(); // 品质枚举值；-1 = 无 CompQuality

        // 源图尺寸（Capture 时记录，快照几何 = 捕获事实的一部分）：快照格是**源图绝对
        // 坐标**，物化目标图必须不小于源图——TerrainGrid.SetFoundation 是三个地形写
        // 入口中唯一无 InBounds 守卫者，尺寸不足时越界写经 CellToIndex 静默回卷落到
        // 错误格子（2026-09-24 事故）。旧档缺省 0 = 未知，物化侧退回 PoiMapSize 并靠
        // ApplyTo 边界守卫丢弃越界项。
        private int sourceSizeX;
        private int sourceSizeZ;

        public int ThingCount => thingCells.Count;

        [System.NonSerialized] private int? globeArea;
        [System.NonSerialized] private bool? globeWreck;
        internal int GlobeArea
        {
            get
            {
                if (globeArea.HasValue) return globeArea.Value;
                var entries = new List<(IntVec3, TerrainDef)>();
                for (int i = 0; i < foundationCells.Count; i++) entries.Add((foundationCells[i], foundationDefs[i]));
                for (int i = 0; i < topCells.Count; i++) entries.Add((topCells[i], topDefs[i]));
                globeArea = GlobeDisplayRules.TerrainCells(entries).Count;
                return globeArea.Value;
            }
        }
        internal bool GlobeWreck => globeWreck ??= GlobeDisplayRules.IsWreck(false, GlobeDisplayRules.HasEquipment(thingDefs));

        /// <summary>快照是否有归档价值（有基架或有建筑/物品）；完全空白的图不值得建 marker。</summary>
        public bool HasContent => foundationCells.Count > 0 || thingCells.Count > 0;

        /// <summary>快照是否记录了有效源图尺寸（旧档快照为否）。</summary>
        public bool HasSourceSize => sourceSizeX > 0 && sourceSizeZ > 0;

        /// <summary>物化目标图应采用的尺寸（= 源图尺寸，保证绝对坐标全部落界）。</summary>
        public IntVec3 SourceMapSize => new IntVec3(sourceSizeX, 1, sourceSizeZ);

        public static PoiSnapshot Capture(Map map)
        {
            var snapshot = new PoiSnapshot();
            snapshot.sourceSizeX = map.Size.x;
            snapshot.sourceSizeZ = map.Size.z;
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
            foreach (Thing thing in ShipStructureIndex.Read(map).Things)
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
                snapshot.thingQualities.Add(thing.TryGetComp<CompQuality>() is { } quality
                    ? (int)quality.Quality
                    : -1);
            }
            return snapshot;
        }

        /// <summary>
        /// 回放到一张刚生成的空 POI 图（全 Void 底），建筑先于物品落位。ownerId 用于
        /// 基架归属盖章（物化对象的 objectId），恢复出的基架与该对象同一归属。
        /// 边界守卫：快照格是源图绝对坐标，越界项（目标图小于源图——只应来自旧档无
        /// 尺寸记录的回放）跳过并在末尾一次性聚合报错；绝不带病回卷写图
        /// （SetFoundation 无 InBounds 守卫，越界写会静默落到错误格子）。
        /// </summary>
        public void ApplyTo(Map map, int ownerId)
        {
            int skipped = 0;
            var stampCells = new List<IntVec3>(foundationCells.Count);
            foreach (IntVec3 cell in foundationCells)
            {
                if (cell.InBounds(map)) stampCells.Add(cell);
                else skipped++;
            }
            // 回放内核（MapContentSpawner，地图工具包公共件）：盖章包裹 + 记录落物。
            MapContentSpawner.WithOwnershipStamp(map, stampCells, ownerId, delegate
            {
                for (int i = 0; i < foundationCells.Count; i++)
                {
                    if (foundationCells[i].InBounds(map))
                    {
                        map.terrainGrid.SetFoundation(foundationCells[i], foundationDefs[i]);
                    }
                }
            });
            for (int i = 0; i < topCells.Count; i++)
            {
                if (!topCells[i].InBounds(map)) { skipped++; continue; }
                map.terrainGrid.SetTerrain(topCells[i], topDefs[i]);
            }
            for (int i = 0; i < roofCells.Count; i++)
            {
                if (!roofCells[i].InBounds(map)) { skipped++; continue; }
                map.roofGrid.SetRoof(roofCells[i], roofDefs[i]);
            }
            for (int i = 0; i < thingCells.Count; i++)
            {
                if (!thingDefs[i].MadeFromStuff && thingCells[i].InBounds(map))
                {
                    SpawnRecord(map, i, null);
                }
                else if (!thingDefs[i].MadeFromStuff)
                {
                    skipped++;
                }
            }
            for (int i = 0; i < thingCells.Count; i++)
            {
                if (thingDefs[i].MadeFromStuff && thingCells[i].InBounds(map))
                {
                    ThingDef stuff = DefDatabase<ThingDef>.GetNamedSilentFail(thingStuffDefNames[i]);
                    SpawnRecord(map, i, stuff);
                }
                else if (thingDefs[i].MadeFromStuff)
                {
                    skipped++;
                }
            }
            if (skipped > 0)
            {
                Log.Error($"[DeltaV] PoiSnapshot.ApplyTo: {skipped} out-of-bounds entries skipped "
                    + $"(snapshot from {sourceSizeX}x{sourceSizeZ}, target {map.Size.x}x{map.Size.z}); "
                    + $"POI geometry may be incomplete.");
            }
        }

        /// <summary>单条 Thing 记录回放（走 MapContentSpawner 统一入口）。</summary>
        private void SpawnRecord(Map map, int i, ThingDef stuff)
        {
            MapContentSpawner.SpawnRecord(map, thingDefs[i], stuff, thingCells[i],
                new Rot4(thingRots[i]), thingHitPoints[i], thingStacks[i], thingFactions[i],
                thingQualities[i] >= 0 ? (QualityCategory)thingQualities[i] : (QualityCategory?)null);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref sourceSizeX, "sourceSizeX", 0);
            Scribe_Values.Look(ref sourceSizeZ, "sourceSizeZ", 0);
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
            Scribe_Collections.Look(ref thingQualities, "thingQualities", LookMode.Value);
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
                thingQualities ??= new List<int>();
            }
        }
    }
}
