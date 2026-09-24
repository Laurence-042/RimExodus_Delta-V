using System.Linq;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 降落图（真实轨道 tile 上的普通 MapParent，经 GetOrGenerateMap 创建）。
    ///
    /// 唯一新增行为：覆写 WorldCameraPosition——星球背景相机以本属性为基准定位。
    /// 本图属于一个承载容器时取容器连续球面方向（与聚焦飞船图背景同源同步——
    /// 两图切换聚焦背景无跳变，阶段3 用户决策）；否则回退原版 tile 中心
    /// （真实 tile 恒有效，回退安全）。
    /// </summary>
    public class SpaceHostParent_DeltaV : MapParent
    {
        public override Vector3 WorldCameraPosition
        {
            get
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                SpaceObject frame = universe?.SpaceObjectOnMap(Map);
                return frame != null
                    ? OrbitSphere.SpherePoint(frame.pose.position)
                    : base.WorldCameraPosition;
            }
        }
    }
}
