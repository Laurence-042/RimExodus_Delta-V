using System;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 飞船（及 POI）口袋图的 WorldObject。口袋图不占任何轨道 tile
    /// （Tile 恒 Invalid，只存在于 Find.World.pocketMaps）。
    ///
    /// 唯一新增行为：覆写 WorldCameraPosition——星球背景相机（WorldCameraDriver.
    /// ApplyMapPositionToGameObject）以本属性为基准定位，这里按该图对应宇宙对象的
    /// **连续**球面方向换算球面点，驾驶时背景随位移连续移动。
    /// 查询失败（无宇宙状态）时：旧档残留有效 tile 则回退 tile 中心，否则回退
    /// 任意球面点——绝不回落 base（Invalid tile 的 GetTileCenter(-1) 红字）。
    /// </summary>
    public class ShipPocketParent_DeltaV : PocketMapParent
    {
        [NonSerialized] private PlanetTile environmentalTile = PlanetTile.Invalid;

        /// <summary>
        /// 只供天体、天气和当地时间采样的动态轨道格。它不写入 WorldObject.Tile，
        /// 不表示世界占位，也不会被 Game.FindMap 命中。缓存随 parent 生命周期释放。
        /// </summary>
        public PlanetTile EnvironmentalTile
        {
            get
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                SpaceObject obj = universe?.SpaceObjectOnMap(Map);
                if (obj == null || !OrbitSphere.Valid)
                {
                    environmentalTile = PlanetTile.Invalid;
                    return PlanetTile.Invalid;
                }
                environmentalTile = OrbitSphere.TileFromDirection(obj.pose.position, environmentalTile);
                return environmentalTile;
            }
        }

        public override Vector3 WorldCameraPosition
        {
            get
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                SpaceObject obj = universe?.SpaceObjectOnMap(Map); // 飞船与 POI 通用
                if (obj != null)
                {
                    return OrbitSphere.SpherePoint(obj.pose.position);
                }
                return Tile.Valid ? base.WorldCameraPosition : OrbitSphere.SpherePoint(Vector3.right);
            }
        }
    }
}
