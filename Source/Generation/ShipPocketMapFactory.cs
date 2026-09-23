using RimWorld.Planet;
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
        private const int StructureMargin = 15;
        private const int MinimumDimension = 80;

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

        public static bool TryGenerateForGravship(Gravship gravship, UniverseManager universe,
            out Map map, out IntVec3 root)
        {
            map = null;
            root = IntVec3.Invalid;
            if (gravship == null || universe == null) return false;

            bool any = false;
            int minX = int.MaxValue;
            int minZ = int.MaxValue;
            int maxX = int.MinValue;
            int maxZ = int.MinValue;
            foreach (IntVec3 cell in gravship.Foundations.Keys)
            {
                any = true;
                minX = Mathf.Min(minX, cell.x);
                minZ = Mathf.Min(minZ, cell.z);
                maxX = Mathf.Max(maxX, cell.x);
                maxZ = Mathf.Max(maxZ, cell.z);
            }
            if (!any)
            {
                Log.Error("[DeltaV] Cannot generate a ship pocket map: Gravship has no foundation cells.");
                return false;
            }

            int width = maxX - minX + 1;
            int height = maxZ - minZ + 1;
            int mapWidth = DimensionFor(width);
            int mapHeight = DimensionFor(height);
            int padX = (mapWidth - width) / 2;
            int padZ = (mapHeight - height) / 2;
            root = new IntVec3(padX - minX, 0, padZ - minZ);
            map = Generate(new IntVec3(mapWidth, 1, mapHeight), universe);
            return map != null;
        }

        private static int DimensionFor(int structureSize)
        {
            int size = Mathf.Max(MinimumDimension, structureSize + StructureMargin * 2);
            // 保持额外空间为偶数，使结构几何中心与 Map 几何中心精确重合。
            if (((size - structureSize) & 1) != 0) size++;
            return size;
        }
    }
}
