using System;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal static class SpaceCoordinateGrid
    {
        // 网格是轨道球面的绝对经纬线；缩放只选择嵌套线距，没有随船平移的网格原点。
        internal static double Step(double desired) => Math.Pow(2d, Math.Ceiling(Math.Log(Math.Max(1d, desired), 2d)));
        internal static double AngularStep(double desired) => 2d * Math.PI
            / Step(2d * Math.PI / Math.Max(1e-9d, Math.Min(Math.PI / 8d, desired)));

        internal static int Emit(UniverseManager universe, SpaceObject frame, Map map, ShellLayer layer, int slot)
        {
            if (!universe.ShowCoordinateGrid || !OrbitSphere.Valid) return slot;
            float opacity = Mathf.Clamp01(DeltaVMod.CurrentSettings?.coordinateGridOpacity ?? 0.22f);
            if (opacity <= 0f) return slot;
            Camera camera = Find.Camera;
            float perPixel = 2f * camera.orthographicSize / Mathf.Max(1, camera.pixelHeight);
            Vector3 cam = camera.transform.position;
            Vector3 center = SpaceTransform.MapPointToUniverse(frame, map, new Vector2(cam.x, cam.z));
            OrbitSphere.GridAngles(center, out double lat, out double lon);
            double radius = OrbitSphere.CellsPerRadian;
            double reach = camera.orthographicSize * Math.Sqrt(1d + camera.aspect * camera.aspect) * 1.2d / radius;
            // 整圈二等分使 ±π 接缝两侧同相位，跨接缝／切图不会整张网格跳动。
            double latStep = AngularStep(perPixel * 90d / radius);
            double lonStep = Math.Min(Math.PI / 8d, latStep * Step(1d / Math.Max(0.000001d, Math.Cos(lat))));
            double bottom = Math.Max(-Math.PI / 2d, lat - reach), top = Math.Min(Math.PI / 2d, lat + reach);
            bool polar = Math.Abs(lat) + reach >= Math.PI / 2d;
            double span = polar ? Math.PI : Math.Min(Math.PI, reach / Math.Max(0.000001d, Math.Cos(lat)) * 1.2d);
            if (polar) lonStep = Math.PI / 8d;
            double left = lon - span, right = lon + span;
            MotionParticleRenderer.Begin(MotionParticleRenderer.Kind.Grid);
            int count = 0;
            for (long i = (long)Math.Floor(bottom / latStep); i * latStep <= top && count < 96; i++, count++)
            {
                double y = i * latStep;
                if (y < -Math.PI / 2d || y > Math.PI / 2d) continue;
                Curve(frame, map, y, y, left, right, polar ? 6 : 4, perPixel, opacity, i % 4 == 0);
            }
            count = 0;
            for (long i = (long)Math.Floor(left / lonStep); i * lonStep <= right && count < 96; i++, count++)
                Curve(frame, map, bottom, top, i * lonStep, i * lonStep, 4, perPixel, opacity, i % 4 == 0);
            return MotionParticleRenderer.Emit(MotionParticleRenderer.Kind.Grid, layer, slot);
        }

        private static void Curve(SpaceObject frame, Map map, double latA, double latB, double lonA, double lonB,
            int maxDepth, float width, float opacity, bool major)
        {
            float altitude = AltitudeLayer.Terrain.AltitudeFor();
            Vector3 start = SpaceTransform.FocusedDrawPosition(frame, map, OrbitSphere.GridPoint(latA, lonA), altitude);
            Vector3 end = SpaceTransform.FocusedDrawPosition(frame, map, OrbitSphere.GridPoint(latB, lonB), altitude);
            Color color = new Color(0.55f, 0.75f, 0.85f, opacity * (major ? 1f : 0.55f));
            EmitCurve(frame, map, latA, latB, lonA, lonB, start, end, altitude,
                width * 0.75f, width * (major ? 1.3f : 1f), color, 0, maxDepth);
        }

        private static void EmitCurve(SpaceObject frame, Map map, double latA, double latB, double lonA, double lonB,
            Vector3 start, Vector3 end, float altitude, float tolerance, float lineWidth, Color color,
            int depth, int maxDepth)
        {
            if (depth < maxDepth)
            {
                double latMid = (latA + latB) * 0.5d;
                double lonMid = (lonA + lonB) * 0.5d;
                Vector3 midpoint = SpaceTransform.FocusedDrawPosition(frame, map,
                    OrbitSphere.GridPoint(latMid, lonMid), altitude);
                // 曲线在屏幕上足够直时只需一段；大角跨度仍拆开，避免跨极区漏掉弯折。
                if ((midpoint - (start + end) * 0.5f).sqrMagnitude > tolerance * tolerance
                    || Math.Max(Math.Abs(latB - latA), Math.Abs(lonB - lonA)) > Math.PI / 8d)
                {
                    EmitCurve(frame, map, latA, latMid, lonA, lonMid, start, midpoint, altitude,
                        tolerance, lineWidth, color, depth + 1, maxDepth);
                    EmitCurve(frame, map, latMid, latB, lonMid, lonB, midpoint, end, altitude,
                        tolerance, lineWidth, color, depth + 1, maxDepth);
                    return;
                }
            }
            MotionParticleRenderer.Line(MotionParticleRenderer.Kind.Grid, start, end, lineWidth, color);
        }
    }
}
