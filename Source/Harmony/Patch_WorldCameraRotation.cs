using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>仅给背景相机补聚焦参考系的局部 roll；Map 相机和 Map 内容保持 identity。</summary>
    [HarmonyPatch(typeof(WorldCameraDriver), nameof(WorldCameraDriver.ApplyMapPositionToGameObject))]
    public static class Patch_WorldCameraRotation
    {
        public static void Postfix(WorldCameraDriver __instance)
        {
            Map map = Find.CurrentMap;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject frame = universe?.SpaceObjectOnMap(map);
            float angle;
            if (frame != null) angle = frame.pose.angle;
            else if (universe?.hostMap == map && universe.PrimaryShip != null) angle = universe.PrimaryShip.pose.angle;
            else return;
            Find.WorldCamera.transform.rotation *= Quaternion.AngleAxis(angle * Mathf.Rad2Deg, Vector3.forward);
        }
    }
}
