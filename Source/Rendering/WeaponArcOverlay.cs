using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Independent weapon range/traverse view; never changes targeting.</summary>
    public static class WeaponArcOverlay
    {
        private const int ColorCount = 24;
        private const float ScanRadiansPerTick = 1.4f / 60f;
        private static Material[] colors;
        private static Dictionary<long, Mesh> rangeBoundaries;

        public static void ClearRuntimeCaches()
        {
            if (colors != null)
                foreach (Material material in colors)
                    if (material != null) Object.Destroy(material);
            if (rangeBoundaries != null)
                foreach (Mesh mesh in rangeBoundaries.Values)
                    if (mesh != null) Object.Destroy(mesh);
            colors = null;
            rangeBoundaries = null;
        }

        public static void DrawFocused(UniverseManager universe)
        {
            if (universe?.ShowWeaponArcOverlay != true) return;
            Map map = Find.CurrentMap;
            if (map == null || universe.FrameOf(map) == null) return;

            float altitude = AltitudeLayer.MetaOverlays.AltitudeFor() + Altitudes.AltInc * 3f;
            int tick = Find.TickManager.TicksGame;
            foreach (object selected in Find.Selector.SelectedObjectsListForReading)
            {
                if (!(selected is ThingWithComps thing) || !thing.Spawned || thing.Map != map) continue;
                CompSpaceWeapon weapon = thing.TryGetComp<CompSpaceWeapon>();
                if (weapon == null || !weapon.SpaceModeEnabled) continue;
                float range = weapon.EffectiveRange;
                float minRange = Mathf.Clamp(weapon.WeaponProps.minRange, 0f, range);
                if (range <= 0f) continue;
                colors ??= MakeColors();
                weapon.CoverageSector(out float center, out float arcDeg);
                float arc = arcDeg * Mathf.Deg2Rad;
                float start = center - arc * 0.5f;
                Vector2 muzzle = SpaceWeaponGeometry.MuzzleMapPoint(weapon);
                Vector3 origin = Point(muzzle, 0f, 0f, altitude);
                uint hash = unchecked((uint)(weapon.parent.thingIDNumber * 1664525 + 1013904223));
                Material color = colors[hash % ColorCount];

                if (arcDeg <= 0.001f)
                {
                    GenDraw.DrawLineBetween(Point(muzzle, minRange, center, altitude),
                        Point(muzzle, range, center, altitude),
                        color, 0.14f);
                    continue;
                }

                Mesh boundary = BoundaryMesh(range, Mathf.RoundToInt(arcDeg));
                Graphics.DrawMesh(boundary,
                    Matrix4x4.TRS(origin, Quaternion.Euler(0f, -center * Mathf.Rad2Deg, 0f),
                        Vector3.one), color, 0);

                if (minRange > 0f)
                    Graphics.DrawMesh(BoundaryMesh(minRange, Mathf.RoundToInt(arcDeg)),
                        Matrix4x4.TRS(origin,
                            Quaternion.Euler(0f, -center * Mathf.Rad2Deg, 0f), Vector3.one),
                        color, 0);

                if (arcDeg < 359.999f)
                {
                    GenDraw.DrawLineBetween(Point(muzzle, minRange, start, altitude),
                        Point(muzzle, range, start, altitude),
                        color, 0.10f);
                    GenDraw.DrawLineBetween(Point(muzzle, minRange, start + arc, altitude),
                        Point(muzzle, range, start + arc, altitude),
                        color, 0.10f);
                }

                float phase = (hash % 10000u) / 10000f * arc;
                float motion = tick * ScanRadiansPerTick;
                float scan = arcDeg >= 359.999f
                    ? start + Mathf.Repeat(motion + phase, Mathf.PI * 2f)
                    : start + Mathf.PingPong(motion + phase, arc);
                GenDraw.DrawLineBetween(Point(muzzle, minRange, scan, altitude),
                    Point(muzzle, range, scan, altitude),
                    color, 0.20f);
            }
        }

        private static Vector3 Point(Vector2 center, float distance, float angle, float altitude)
        {
            return new Vector3(center.x + Mathf.Cos(angle) * distance, altitude,
                center.y + Mathf.Sin(angle) * distance);
        }

        private static Mesh BoundaryMesh(float range, int degrees)
        {
            long key = ((long)degrees << 32) | (uint)Mathf.RoundToInt(range * 10f);
            rangeBoundaries ??= new Dictionary<long, Mesh>();
            if (rangeBoundaries.TryGetValue(key, out Mesh cached)) return cached;

            float span = degrees * Mathf.Deg2Rad;
            int segments = Mathf.Clamp(Mathf.CeilToInt(range * span), 16, 1024);
            float inner = Mathf.Max(0f, range - 0.07f);
            float outer = range + 0.07f;
            var vertices = new Vector3[(segments + 1) * 2];
            var normals = new Vector3[vertices.Length];
            var triangles = new int[segments * 6];
            for (int i = 0; i <= segments; i++)
            {
                float angle = Mathf.Lerp(-span * 0.5f, span * 0.5f, i / (float)segments);
                float cosine = Mathf.Cos(angle);
                float sine = Mathf.Sin(angle);
                int vertex = i * 2;
                vertices[vertex] = new Vector3(cosine * outer, 0f, sine * outer);
                vertices[vertex + 1] = new Vector3(cosine * inner, 0f, sine * inner);
                normals[vertex] = normals[vertex + 1] = Vector3.up;
                if (i == segments) continue;
                int tri = i * 6;
                triangles[tri] = vertex;
                triangles[tri + 1] = vertex + 1;
                triangles[tri + 2] = vertex + 2;
                triangles[tri + 3] = vertex + 2;
                triangles[tri + 4] = vertex + 1;
                triangles[tri + 5] = vertex + 3;
            }
            var mesh = new Mesh { name = $"DeltaV_WeaponRange_{degrees}_{range:F1}" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            rangeBoundaries.Add(key, mesh);
            return mesh;
        }

        private static Material[] MakeColors()
        {
            var result = new Material[ColorCount];
            for (int i = 0; i < result.Length; i++)
            {
                Color color = Color.HSVToRGB((i + 0.5f) / ColorCount, 0.72f, 1f);
                color.a = 0.85f;
                result[i] = new Material(ShaderDatabase.Transparent)
                {
                    name = $"DeltaV_WeaponRangeColor_{i}",
                    mainTexture = BaseContent.WhiteTex,
                    color = color,
                    renderQueue = 3101,
                };
            }
            return result;
        }
    }
}
