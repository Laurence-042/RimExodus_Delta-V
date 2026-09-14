using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 飞船口袋图的唯一生成入口。口袋图只是飞行态的局部坐标容器；每次从固定轨道
    /// 返回飞行时按当前 Gravship 的实际旋转后边界重建，不继承旧图尺寸或空网格。
    /// </summary>
    public static class ShipPocketMapFactory
    {
        private const int StructureMargin = 15;
        private const int MinimumDimension = 80;

        public static Map Generate(IntVec3 size, Map sourceMap, Vector2 universePos)
        {
            if (sourceMap == null || !sourceMap.Tile.Valid) return null;
            PlanetLayer layer = sourceMap.Tile.Layer;
            PlanetTile projected = UniverseLayerMapping.ProjectToTile(layer, sourceMap.Tile, universePos, sourceMap.Tile);
            PlanetTile shipTile = UniverseLayerMapping.FindFreeTileNear(layer, projected);
            if (!shipTile.Valid)
            {
                Log.Error("[DeltaV] No free orbit tile near the projected ship position; pocket map generation aborted.");
                return null;
            }
            var parent = (PocketMapParent)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.Tile = shipTile;
            parent.sourceMap = sourceMap;
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
            Map map = MapGenerator.GenerateMap(size, parent, DeltaVDefOf.DeltaV_ShipPocket,
                null, null, isPocketMap: true);
            Find.World.pocketMaps.Add(parent);
            return map;
        }

        public static bool TryGenerateForGravship(Gravship gravship, Map sourceMap,
            Vector2 universePos, out Map map, out IntVec3 root)
        {
            map = null;
            root = IntVec3.Invalid;
            if (gravship == null || sourceMap == null) return false;

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
            map = Generate(new IntVec3(mapWidth, 1, mapHeight), sourceMap, universePos);
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
