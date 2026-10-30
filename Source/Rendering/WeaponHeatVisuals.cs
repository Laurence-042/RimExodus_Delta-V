using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal static class WeaponHeatVisuals
    {
        private static readonly Dictionary<(float, bool), Mesh> meshes = new Dictionary<(float, bool), Mesh>();
        private static readonly Dictionary<(Material, int), Material> materials = new Dictionary<(Material, int), Material>();

        internal static bool TryPart(ThingWithComps thing, Material source, int part, out Mesh mesh, out Material material)
        {
            mesh = null; material = null;
            var heat = thing?.TryGetComp<CompWeaponTuning>();
            if (heat?.Settings.chemical != true || source == null || heat.heat <= 0f) return false;
            bool barrel = part == 0;
            int level = Mathf.RoundToInt(32f * heat.HeatFraction * (barrel ? 1f : heat.overheated ? .75f : 0f));
            if (level == 0) return false;
            var meshKey = (heat.Settings.barrelStart, barrel);
            if (!meshes.TryGetValue(meshKey, out mesh))
            {
                mesh = new Mesh { name = "DeltaV gun heat region" };
                var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var triangles = new List<int>();
                void Region(float left, float right, float bottom, float top)
                {
                    int start = vertices.Count;
                    foreach (var point in new[] { new Vector2(left, bottom), new Vector2(left, top),
                        new Vector2(right, top), new Vector2(right, bottom) })
                    { vertices.Add(new Vector3(point.x - .5f, .002f, point.y - .5f)); uv.Add(point); }
                    triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
                }
                float split = heat.Settings.barrelStart;
                // The autocannon's body fins extend alongside its barrel; an X-only split heats those fins too early.
                if (barrel) Region(split, 1f, .44f, .56f);
                else { Region(0f, split, 0f, 1f); Region(split, 1f, 0f, .44f); Region(split, 1f, .56f, 1f); }
                mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
                meshes.Add(meshKey, mesh);
            }
            var key = (source, level);
            if (!materials.TryGetValue(key, out material))
            {
                material = new Material(source) { color = Color.Lerp(Color.white, new Color(2f, .16f, .025f), level / 32f) };
                materials.Add(key, material);
            }
            return true;
        }

        internal static void Clear()
        {
            foreach (var mesh in meshes.Values) Object.Destroy(mesh);
            foreach (var material in materials.Values) Object.Destroy(material);
            meshes.Clear(); materials.Clear();
        }
    }
}
