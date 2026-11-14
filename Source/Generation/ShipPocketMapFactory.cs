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

        public static IntVec3 CompactSize(IntVec2 contentSize, int margin = StructureMargin)
        {
            if (margin < 0) throw new System.ArgumentOutOfRangeException(nameof(margin));
            return new IntVec3(contentSize.x + margin * 2, 1, contentSize.z + margin * 2);
        }

        public static Map Generate(IntVec3 size, UniverseManager universe)
        {
            universe.Generation.DrainSynchronouslyForExternalGeneration();
            Map host = universe.GetOrCreateHostMap();
            if (host == null) return null;
            var parent = (PocketMapParent)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.sourceMap = host;
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
            return RimExodus.Maps.PocketMaps.Create(size, parent, parent.mapGenerator, parent.sourceMap);
        }

        public static bool TryGenerateForGravship(Gravship gravship, UniverseManager universe,
            out Map map, out IntVec3 root, int margin = StructureMargin, bool reserveRotations = false)
        {
            map = null;
            root = IntVec3.Invalid;
            if (gravship == null || universe == null) return false;

            if (margin < 0) throw new System.ArgumentOutOfRangeException(nameof(margin));
            CellRect bounds = PlacedBounds(gravship, reserveRotations);
            root = new IntVec3(margin - bounds.minX, 0, margin - bounds.minZ);
            map = Generate(CompactSize(bounds.Size, margin), universe);
            return map != null;
        }

        internal static CellRect PlacedBounds(Gravship gravship, bool reserveRotations = false)
        {
            var cells = new List<IntVec3>();
            foreach (IntVec3 local in gravship.Bounds)
                cells.Add(PrefabUtility.GetAdjustedLocalPosition(local, gravship.Rotation));
            foreach (var pair in gravship.ThingPlacements)
            {
                cells.AddRange(GenAdj.OccupiedRect(pair.Value.local, pair.Value.rotation, pair.Key.def.size));
                if (reserveRotations)
                    foreach (Rot4 rotation in Rot4.AllRotations)
                        cells.AddRange(GenAdj.OccupiedRect(pair.Value.local, rotation, pair.Key.def.size));
            }
            return CellRect.FromCellList(cells);
        }

    }
}
