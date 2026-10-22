using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 推进器/船体范围调试叠加（2026-09-26 用户需求）：右下角 PlaySettings 开关
    ///（原版「开关屋顶区显示」同款 ToggleableIcon，经 Patch_PlaySettings 行追加）；
    /// 开启后在聚焦图叠加显示——全部推进器的**位置与推力方向矢量**（真实推进器绿色、
    /// shuttle 虚拟推进器洋红）、全部船体范围遮罩（船 = 控制域格、在飞 shuttle =
    /// 舱体脚印格）。武器射界由独立 WeaponArcOverlay 开关控制。只读派生
    ///（MassReport/ShipRegistry/武器 Comp/脚印），表现层纪律。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ThrusterDebugOverlay
    {
        private static Material domainMaterial;

        [StaticConstructorOnStartup]
        private static class Assets
        {
            static Assets()
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var pixels = new Color32[4];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                domainMaterial = new Material(ShaderDatabase.Transparent)
                {
                    name = "DeltaV_DebugDomainOverlay",
                    mainTexture = texture,
                    color = new Color(1f, 0.85f, 0.3f, 0.28f),
                    renderQueue = 3100,
                };
            }
        }

        /// <summary>聚焦图叠加绘制（UniverseManager.GameComponentUpdate 调用）。</summary>
        public static void DrawFocused(UniverseManager universe)
        {
            if (universe?.ShowThrusterDebugOverlay != true) return;
            Map map = Find.CurrentMap;
            if (map == null || universe == null || universe.FrameOf(map) == null) return;

            // 推进器矢量：MassReport.thrusters（真实 + 虚拟同一清单）。
            DriveInfo report = Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map)?.MotionInfo;
            if (report != null)
            {
                Vector2 origin = SpaceTransform.ShipLocalOrigin(map);
                float lineLayer = Altitudes.AltInc * 60f;
                foreach (ThrusterInfo thruster in report.thrusters)
                {
                    // 箭头 = 推力方向：尾部在喷口外侧、头部落在推进器中心（喷口向外喷、
                    // 推力指向船内——尾部即喷射侧，2026-09-26 用户定案）。
                    Vector3 head = new Vector3(origin.x + thruster.localPos.x,
                        AltitudeLayer.Item.AltitudeFor() + lineLayer,
                        origin.y + thruster.localPos.y);
                    Vector3 dirV = new Vector3(thruster.dir.x, 0f, thruster.dir.y);
                    Vector3 tail = head - dirV * 2.2f;
                    SimpleColor color = thruster.thing == null ? SimpleColor.Magenta : SimpleColor.Green;
                    GenDraw.DrawLineBetween(tail, head, color, 0.15f);
                    // 箭头头部（两条短斜线指向 head）。
                    Vector3 back = -dirV * 0.35f;
                    Vector3 side = new Vector3(-dirV.z, 0f, dirV.x) * 0.18f;
                    GenDraw.DrawLineBetween(head + back + side, head, color, 0.15f);
                    GenDraw.DrawLineBetween(head + back - side, head, color, 0.15f);
                }
            }

            // 船体范围遮罩：船 = 控制域；在飞 shuttle = 舱体脚印。
            if (domainMaterial != null)
            {
                float altitude = AltitudeLayer.Item.AltitudeFor() + Altitudes.AltInc * 55f;
                foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(map))
                    DrawCells(ship.HullCells, altitude);
            }

        }

        private static void DrawCells(IEnumerable<IntVec3> cells, float altitude)
        {
            foreach (IntVec3 cell in cells)
            {
                GenDraw.DrawQuad(domainMaterial,
                    new Vector3(cell.x + 0.5f, altitude, cell.z + 0.5f), Quaternion.identity, 1f);
            }
        }
    }

    /// <summary>碰撞核心实际缓存的独立调试层：精确占用格 + 宽相位包围圆。</summary>
    [StaticConstructorOnStartup]
    public static class CollisionDebugOverlay
    {
        private static readonly List<IntVec3> Cells = new List<IntVec3>();
        private static readonly Material CellMaterial = MaterialPool.MatFrom(BaseContent.WhiteTex,
            ShaderDatabase.Transparent, new Color(0.1f, 0.85f, 1f, 0.24f), 3102);

        public static void DrawFocused(UniverseManager universe)
        {
            if (universe?.ShowCollisionDebugOverlay != true) return;
            Map focusedMap = Find.CurrentMap;
            if (focusedMap == null) return;
            SpaceObject focused = universe.FrameOf(focusedMap);
            if (focused == null) return;

            float altitude = AltitudeLayer.MetaOverlays.AltitudeFor() + Altitudes.AltInc * 5f;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj?.map == null || obj.map.Disposed || !obj.ParticipatesInOverlays) continue;
                if (universe.Generation?.IsGenerating(obj.map) == true) continue;
                if (!SpaceCollision.CollectDebugSurface(obj, Cells,
                    out Vector2 boundsCenter, out float boundsRadius)) continue;

                Quaternion rotation = SpaceTransform.FocusedMeshRotation(focused, obj.pose.angle);
                Vector3 drawPos = SpaceTransform.FocusedDrawPosition(focused, focusedMap,
                    obj.pose.position, altitude);
                Vector2 sourceOrigin = SpaceTransform.ShipLocalOrigin(obj.map);
                Vector3 translation = drawPos
                    - rotation * new Vector3(sourceOrigin.x, 0f, sourceOrigin.y);
                Matrix4x4 matrix = Matrix4x4.TRS(translation, rotation, Vector3.one);

                foreach (IntVec3 cell in Cells)
                {
                    Vector3 center = matrix.MultiplyPoint3x4(
                        new Vector3(cell.x + 0.5f, 0f, cell.z + 0.5f));
                    Graphics.DrawMesh(MeshPool.plane10, center, rotation, CellMaterial, 0);
                }

                Vector3 circleCenter = matrix.MultiplyPoint3x4(
                    new Vector3(boundsCenter.x, 0f, boundsCenter.y));
                DrawBoundsCircle(circleCenter, boundsRadius);
            }
        }

        private static void DrawBoundsCircle(Vector3 center, float radius)
        {
            if (radius <= 0f) return;
            int segments = Mathf.Clamp(Mathf.CeilToInt(radius * 2f), 32, 128);
            Vector3 previous = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = Mathf.PI * 2f * i / segments;
                Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f,
                    Mathf.Sin(angle) * radius);
                GenDraw.DrawLineBetween(previous, next, SimpleColor.Cyan, 0.12f);
                previous = next;
            }
        }
    }
}
