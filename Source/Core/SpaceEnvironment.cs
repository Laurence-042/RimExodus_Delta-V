using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Map 的世界占位 tile 与环境采样位置之间的唯一适配层。真实地图沿用 Map.Tile；
    /// Delta-V 船/POI 口袋图从 SpacePose 派生临时轨道格，但永不改写 Map.Tile。
    /// </summary>
    public static class SpaceEnvironment
    {
        public static bool IsDeltaVPocket(Map map)
        {
            return map?.Parent is ShipPocketParent_DeltaV;
        }

        public static PlanetTile TileFor(Map map)
        {
            if (map == null)
            {
                return PlanetTile.Invalid;
            }
            PlanetTile tile = map.Tile;
            if (tile.Valid)
            {
                return tile;
            }
            return map.Parent is ShipPocketParent_DeltaV parent
                ? parent.EnvironmentalTile
                : PlanetTile.Invalid;
        }

        public static Vector2 LongLatFor(Map map)
        {
            PlanetTile tile = TileFor(map);
            // 直接进 PlanetLayer，避免重新落入 WorldGrid.LongLatOf(Invalid) 的
            // AnyPlayerHomeMap 回退链。
            return tile.Valid ? tile.Layer.LongLatOf(tile) : Vector2.zero;
        }

        /// <summary>
        /// 当地日期/作息的地理位置。真实地图保留原版经纬度；Delta-V 口袋图
        /// 只保留动态纬度，经度固定为 0，避免航行跨时区造成时钟倒退。
        /// </summary>
        public static Vector2 DateLocationFor(Map map)
        {
            Vector2 location = LongLatFor(map);
            return IsDeltaVPocket(map) ? new Vector2(0f, location.y) : location;
        }
    }
}
