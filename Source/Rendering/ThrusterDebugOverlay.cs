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
    /// 舱体脚印格）与全部舰炮的当前射界/射程。只读派生
    ///（MassReport/ShipRegistry/武器 Comp/脚印），表现层纪律。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ThrusterDebugOverlay
    {
        /// <summary>会话级视图开关（调试显示，不落盘；渲染类静态白名单口径）。</summary>
        public static bool Show;

        private static Material domainMaterial;
        private static Material weaponArcMaterial;
        private static readonly Dictionary<int, Mesh> WeaponArcMeshes = new Dictionary<int, Mesh>();

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
                weaponArcMaterial = new Material(ShaderDatabase.Transparent)
                {
                    name = "DeltaV_WeaponArcOverlay",
                    mainTexture = texture,
                    color = new Color(1f, 0.1f, 0.65f, 0.12f),
                    renderQueue = 3101,
                };
            }
        }

        /// <summary>聚焦图叠加绘制（UniverseManager.GameComponentUpdate 调用）。</summary>
        public static void DrawFocused(UniverseManager universe)
        {
            if (!Show) return;
            Map map = Find.CurrentMap;
            if (map == null || universe == null || universe.FrameOf(map) == null) return;

            // 推进器矢量：MassReport.thrusters（真实 + 虚拟同一清单）。
            ShipPhysics.MassReport report = ShipPhysics.GetReport(map);
            if (report != null)
            {
                Vector2 origin = SpaceTransform.ShipLocalOrigin(map);
                float lineLayer = Altitudes.AltInc * 60f;
                foreach (ShipPhysics.Thruster thruster in report.thrusters)
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
                foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
                {
                    DrawCells(ShipRegistry.ControlDomain(ship), altitude);
                }
                foreach (SpaceObject obj in universe.objects)
                {
                    if (obj.baseTag != SpaceObjectTag.Shuttle || obj.map != map) continue;
                    Building hull = SpaceShuttleService.HullOf(obj);
                    if (hull == null) continue;
                    DrawCells(new HashSet<IntVec3>(hull.OccupiedRect().Cells), altitude);
                }
            }

            DrawWeaponArcs(map);
        }

        private static void DrawWeaponArcs(Map map)
        {
            float altitude = AltitudeLayer.MetaOverlays.AltitudeFor() + Altitudes.AltInc * 3f;
            foreach (CompSpaceWeapon weapon in SpaceWeaponService.AllWeaponsOn(map))
            {
                Vector2 muzzle = SpaceWeaponGeometry.MuzzleMapPoint(weapon);
                float range = Mathf.Max(1f, weapon.WeaponProps.range);
                float arc = Mathf.Clamp(weapon.WeaponProps.fireArcDeg, 0f, 360f) * Mathf.Deg2Rad;
                // 射界是安装座允许目标的区域，中心必须取建筑基准朝向；当前炮管角只
                // 表示正在转向哪里，否则有限射界武器转到边缘时会把填充错误平移出机械弧。
                float center = ShipOrientation.AngleFromRot4(weapon.parent.Rotation);
                int segments = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(arc, 0.03f) * 12f));
                float start = center - arc * 0.5f;
                Vector3 origin = ArcPoint(muzzle, 0f, altitude);
                Vector3 previous = ArcPoint(muzzle, range, start, altitude);
                // 有面积的射界使用半透明填充；0° 固定轴炮只画明确发射轴。
                if (arc <= 0.001f)
                {
                    GenDraw.DrawLineBetween(origin, previous, SimpleColor.Magenta, 0.12f);
                    continue;
                }
                DrawArcFill(origin, range, center, Mathf.RoundToInt(arc * Mathf.Rad2Deg));
                if (arc < Mathf.PI * 2f - 0.001f)
                {
                    GenDraw.DrawLineBetween(origin, previous, SimpleColor.Magenta, 0.1f);
                }
                for (int i = 1; i <= segments; i++)
                {
                    float angle = start + arc * i / segments;
                    Vector3 next = ArcPoint(muzzle, range, angle, altitude);
                    GenDraw.DrawLineBetween(previous, next, SimpleColor.Magenta, 0.1f);
                    previous = next;
                }
                if (arc < Mathf.PI * 2f - 0.001f)
                {
                    GenDraw.DrawLineBetween(origin, previous, SimpleColor.Magenta, 0.1f);
                }
            }
        }

        private static void DrawArcFill(Vector3 origin, float range, float centerAngle, int degrees)
        {
            if (weaponArcMaterial == null || degrees <= 0) return;
            degrees = Mathf.Clamp(degrees, 1, 360);
            if (!WeaponArcMeshes.TryGetValue(degrees, out Mesh mesh))
            {
                int segments = Mathf.Max(2, Mathf.CeilToInt(degrees / 3f));
                var vertices = new Vector3[segments + 2];
                var triangles = new int[segments * 3];
                vertices[0] = Vector3.zero;
                float half = degrees * 0.5f * Mathf.Deg2Rad;
                for (int i = 0; i <= segments; i++)
                {
                    float angle = Mathf.Lerp(-half, half, i / (float)segments);
                    vertices[i + 1] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    if (i == segments) continue;
                    int tri = i * 3;
                    triangles[tri] = 0;
                    triangles[tri + 1] = i + 2;
                    triangles[tri + 2] = i + 1;
                }
                mesh = new Mesh { name = $"DeltaV_WeaponArc_{degrees}" };
                mesh.vertices = vertices;
                mesh.triangles = triangles;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                WeaponArcMeshes.Add(degrees, mesh);
            }
            Matrix4x4 matrix = Matrix4x4.TRS(origin,
                Quaternion.Euler(0f, -centerAngle * Mathf.Rad2Deg, 0f),
                new Vector3(range, 1f, range));
            Graphics.DrawMesh(mesh, matrix, weaponArcMaterial, 0);
        }

        private static Vector3 ArcPoint(Vector2 center, float distance, float angle, float altitude)
        {
            return new Vector3(center.x + Mathf.Cos(angle) * distance, altitude,
                center.y + Mathf.Sin(angle) * distance);
        }

        private static Vector3 ArcPoint(Vector2 center, float distance, float altitude)
        {
            return new Vector3(center.x + distance, altitude, center.y);
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
        public static bool Show;

        private static readonly List<IntVec3> Cells = new List<IntVec3>();
        private static readonly Material CellMaterial = MaterialPool.MatFrom(BaseContent.WhiteTex,
            ShaderDatabase.Transparent, new Color(0.1f, 0.85f, 1f, 0.24f), 3102);

        public static void DrawFocused(UniverseManager universe)
        {
            if (!Show || universe == null) return;
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
