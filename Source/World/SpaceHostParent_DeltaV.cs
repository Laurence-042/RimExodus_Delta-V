using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 宿主/显示地图的 WorldObject（轨道层真实 tile 上的普通 MapParent）。
    ///
    /// 唯一新增行为：覆写 WorldCameraPosition——星球背景相机以本属性为基准定位。
    /// 宿主图的**位置记账**随玩家主船同步运动（动态锚，见 UniverseManager.
    /// anchorDisplacement）：地块与 WorldObject tile 永不移动，但背景视角始终取
    /// HostFrame 的连续 universe 位置换算球面点；飞行时它等于主船位置，停靠时
    /// 则由主船 Universe 位置与宿主图内真实船体原点共同确定。与聚焦飞船图时的背景**同源同步**
    /// ——两图切换聚焦背景无跳变（阶段3 用户决策）。无主船时回退原版 tile 中心。
    /// </summary>
    public class SpaceHostParent_DeltaV : MapParent
    {
        public override Vector3 WorldCameraPosition
        {
            get
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                ShipState primary = universe?.PrimaryShip;
                if (primary == null)
                {
                    return base.WorldCameraPosition;
                }
                SpaceObject hostFrame = universe.HostFrame;
                Vector3 point = UniverseLayerMapping.AbsoluteSpherePoint(universe,
                    hostFrame.pose.position + universe.anchorDisplacement);
                return point == Vector3.zero ? base.WorldCameraPosition : point;
            }
        }
    }
}
