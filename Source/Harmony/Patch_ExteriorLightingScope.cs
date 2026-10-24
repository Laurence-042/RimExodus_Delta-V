using HarmonyLib;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>原版外部光照遮罩只扩出地图 200 格；自由镜头需让外缘覆盖当前视野。</summary>
    [HarmonyPatch(typeof(MapDrawLayer), nameof(MapDrawLayer.DrawLayer))]
    internal static class Patch_ExteriorLightingScope
    {
        private static readonly Color32 ClearVertex = new Color32(0, 0, 0, 0);
        private static readonly MeshParts GeometryParts = MeshParts.Verts | MeshParts.Tris | MeshParts.Colors;

        private static void Prefix(MapDrawLayer __instance)
        {
            if (!(__instance is MapDrawLayer_ExteriorLightingOverlay overlay)) return;
            Map map = Find.CurrentMap;
            if (map == null || !overlay.Visible
                || map.mapDrawer.GetGlobalLayer<MapDrawLayer_ExteriorLightingOverlay>() != overlay
                || Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) == null) return;

            Camera camera = Find.Camera;
            if (camera == null) return;
            float halfHeight = camera.orthographicSize + 2f;
            float halfWidth = halfHeight * camera.aspect + 2f;
            Vector3 center = camera.transform.position;
            float left = Mathf.Min(-200f, center.x - halfWidth);
            float right = Mathf.Max(map.Size.x + 200f, center.x + halfWidth);
            float bottom = Mathf.Min(-200f, center.z - halfHeight);
            float top = Mathf.Max(map.Size.z + 200f, center.z + halfHeight);
            float y = AltitudeLayer.LightingOverlay.AltitudeFor();

            foreach (LayerSubMesh mesh in overlay.subMeshes)
            {
                if (mesh.material != MatBases.LightOverlay && mesh.material != MatBases.ShadowMask) continue;
                mesh.Clear(GeometryParts);
                AddRect(mesh, left, 0f, 0f, map.Size.z, y);
                AddRect(mesh, map.Size.x, right, 0f, map.Size.z, y);
                AddRect(mesh, left, right, map.Size.z, top, y);
                AddRect(mesh, left, right, bottom, 0f, y);
                mesh.FinalizeMesh(GeometryParts);
                mesh.mesh.RecalculateBounds();
            }
        }

        private static void AddRect(LayerSubMesh mesh, float left, float right,
            float bottom, float top, float y)
        {
            int start = mesh.verts.Count;
            mesh.verts.Add(new Vector3(left, y, bottom));
            mesh.verts.Add(new Vector3(left, y, top));
            mesh.verts.Add(new Vector3(right, y, top));
            mesh.verts.Add(new Vector3(right, y, bottom));
            for (int i = 0; i < 4; i++) mesh.colors.Add(ClearVertex);
            mesh.tris.Add(start);
            mesh.tris.Add(start + 1);
            mesh.tris.Add(start + 2);
            mesh.tris.Add(start);
            mesh.tris.Add(start + 2);
            mesh.tris.Add(start + 3);
        }
    }
}
