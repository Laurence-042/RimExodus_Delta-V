using System.Linq;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 原版流程在空白 Orbit tile 上生成的是 SpaceMapParent 地图，不能改换为 Delta-V 的
    /// WorldObject 类型。这里对"有 Delta-V 船停靠的任意真实空间图"替换背景球面位置，
    /// 让它与自建 SpaceHostParent_DeltaV 服从同一个连续语义（停靠船球面方向）。
    /// </summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.WorldCameraPosition), MethodType.Getter)]
    public static class Patch_MapParent_WorldCameraPosition_DeltaVHost
    {
        public static void Postfix(MapParent __instance, ref Vector3 __result)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null) return;
            Map map = __instance.Map;
            ShipState docked = universe.Ships.FirstOrDefault(s => s.docked && s.dockedMap == map);
            if (docked == null) return;
            __result = OrbitSphere.SpherePoint(docked.pose.position);
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
            // worldtile 帧的图（合成帧，或以 worldtile 地图为内部的 NativeParent 对象——
            // 二者位姿同为 tile 中心、角度恒 0）：背景随停靠在该图的主船绝对姿态滚转，
            // 与聚焦飞船图同源同步、降落前后无跳变。
            bool tileFrame = universe.IsSyntheticFrame(frame)
                || (frame is PoiState poi && poi.kind == PoiKind.NativeParent);
            float angle;
            if (tileFrame)
            {
                ShipState primary = universe.PrimaryShip;
                if (primary == null || !primary.docked || primary.dockedMap != map) return;
                angle = primary.pose.angle;
            }
            else
            {
                // 对象图（船/POI 口袋内部）：背景随聚焦对象自身姿态滚转。
                angle = frame.pose.angle;
            }
            Find.WorldCamera.transform.rotation *= Quaternion.AngleAxis(angle * Mathf.Rad2Deg, Vector3.forward);
        }
    }
}
