using System.Linq;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 原版流程在空白 Orbit tile 上生成的是 SpaceMapParent 地图，不能改换为 Delta-V 的
    /// WorldObject 类型。这里对"承载 Delta-V 船的任意真实空间图"替换背景球面位置，
    /// 让它与自建 SpaceHostParent_DeltaV 服从同一个连续语义（容器球面方向）。
    /// </summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.WorldCameraPosition), MethodType.Getter)]
    public static class Patch_MapParent_WorldCameraPosition_DeltaVHost
    {
        public static void Postfix(MapParent __instance, ref Vector3 __result)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null) return;
            Map map = __instance.Map;
            SpaceObject frame = map == null ? null : universe.SpaceObjectOnMap(map);
            if (frame == null) return;
            __result = OrbitSphere.SpherePoint(frame.pose.position);
        }
    }

    /// <summary>仅给背景相机补聚焦参考系的局部 roll；Map 相机和 Map 内容保持 identity。</summary>
    [HarmonyPatch(typeof(WorldCameraDriver), nameof(WorldCameraDriver.ApplyMapPositionToGameObject))]
    public static class Patch_WorldCameraRotation
    {
        public static void Postfix(WorldCameraDriver __instance)
        {
            Map map = Find.CurrentMap;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject frame = universe?.FrameOf(map);
            if (frame == null) return;
            // 背景随聚焦容器自身姿态滚转（worldtile 图与口袋图同一规则——容器位姿是
            /// 唯一承载，多船图的成员不再各自持有姿态）。
            Find.WorldCamera.transform.rotation *=
                Quaternion.AngleAxis(frame.pose.angle * Mathf.Rad2Deg, Vector3.forward);
        }
    }
}
