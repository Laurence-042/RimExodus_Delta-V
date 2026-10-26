using RimWorld.Planet;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 飞船口袋图的唯一生成入口。口袋图只是飞行态的局部坐标容器；每次从固定轨道
    /// 开始自由飞行时按当前 Gravship 的实际旋转后边界重建，不继承旧图尺寸或空网格。
    ///
    /// 口袋图不占任何轨道 tile（Tile 恒 Invalid，只进 Find.World.pocketMaps）；
    /// sourceMap 统一取 UniverseManager.GetOrCreateHostMap() 的专用宿主口袋图。
    /// 宇宙位置由调用方随后写进 ShipState.pose（球面绝对坐标），与图的生成解耦。
    /// </summary>
    public static class ShipPocketMapFactory
    {
        public const int StructureMargin = 12;

        public static IntVec3 CompactSize(IntVec2 contentSize) =>
            new IntVec3(contentSize.x + StructureMargin * 2, 1, contentSize.z + StructureMargin * 2);

        public static Map Generate(IntVec3 size, UniverseManager universe)
        {
            var parent = (PocketMapParent)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.sourceMap = universe.GetOrCreateHostMap();
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
            Map map = MapGenerator.GenerateMap(size, parent, DeltaVDefOf.DeltaV_ShipPocket,
                null, null, isPocketMap: true);
            Find.World.pocketMaps.Add(parent);
            return map;
        }

        /// <summary>
        /// 同步生成极小口袋图（射弹临时图等）：走 IncrementalMapGenerationService 的
        /// 同一 Prepare/Finish 链，绕开 MapGenerator.GenerateMap 方法体，不触发其
        /// Harmony 补丁链（MapPreview 等）。大图仍走 <see cref="Generate"/>。
        /// </summary>
        public static Map GenerateImmediate(IntVec3 size, UniverseManager universe)
        {
            using var timing = universe.Performance.Measure(PerformanceStage.SyncMapGeneration);
            var parent = (PocketMapParent)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.sourceMap = universe.GetOrCreateHostMap();
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
            Map map = universe.Generation.GenerateSynchronously(new IncrementalMapGenerationRequest
            {
                key = $"pooledMap_{size.x}x{size.z}",
                parent = parent,
                size = size,
                generator = DeltaVDefOf.DeltaV_ShipPocket,
                isPocketMap = true
            });
            if (map == null)
            {
                if (!parent.Destroyed) parent.Destroy();
                return null;
            }
            Find.World.pocketMaps.Add(parent);
            return map;
        }

        public static bool TryGenerateForGravship(Gravship gravship, UniverseManager universe,
            out Map map, out IntVec3 root)
        {
            map = null;
            root = IntVec3.Invalid;
            if (gravship == null || universe == null) return false;

            CellRect bounds = PlacedBounds(gravship);
            root = new IntVec3(StructureMargin - bounds.minX, 0, StructureMargin - bounds.minZ);
            map = Generate(CompactSize(bounds.Size), universe);
            return map != null;
        }

        internal static CellRect PlacedBounds(Gravship gravship)
        {
            var cells = new List<IntVec3>();
            foreach (IntVec3 local in gravship.Bounds)
                cells.Add(PrefabUtility.GetAdjustedLocalPosition(local, gravship.Rotation));
            foreach (var pair in gravship.ThingPlacements)
                cells.AddRange(GenAdj.OccupiedRect(pair.Value.local, pair.Value.rotation, pair.Key.def.size));
            return CellRect.FromCellList(cells);
        }

    }
}
