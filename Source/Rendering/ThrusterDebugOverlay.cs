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
    /// shuttle 虚拟推进器洋红）与全部船体范围遮罩（船 = 控制域格、在飞 shuttle =
    /// 舱体脚印格）。只读派生（MassReport/ShipRegistry/脚印），表现层纪律。
    /// </summary>
    public static class ThrusterDebugOverlay
    {
        /// <summary>会话级视图开关（调试显示，不落盘；渲染类静态白名单口径）。</summary>
        public static bool Show;

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
}
