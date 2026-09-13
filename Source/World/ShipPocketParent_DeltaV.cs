using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 飞船（及 POI）口袋图的宿主 WorldObject。
    ///
    /// 唯一新增行为：覆写 WorldCameraPosition——星球背景相机（WorldCameraDriver.
    /// ApplyMapPositionToGameObject）以本属性为基准定位，原版默认返回 tile 中心，
    /// 而 tile 重锚是 60 tick 离散跳步；这里改为按该图对应宇宙对象的**连续** universe
    /// 位置换算球面点（UniverseLayerMapping.AbsoluteSpherePoint），驾驶时背景随
    /// 位移连续移动（阶段3）。查询失败（无宇宙状态/无宿主图）回退原版 tile 中心。
    /// </summary>
    public class ShipPocketParent_DeltaV : PocketMapParent
    {
        public override Vector3 WorldCameraPosition
        {
            get
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                SpaceObject obj = universe?.SpaceObjectOnMap(Map); // 飞船与 POI 通用
                if (obj == null)
                {
                    return base.WorldCameraPosition;
                }
                Vector3 point = UniverseLayerMapping.AbsoluteSpherePoint(universe,
                    obj.pose.position + universe.anchorDisplacement);
                return point == Vector3.zero ? base.WorldCameraPosition : point;
            }
        }
    }
}
