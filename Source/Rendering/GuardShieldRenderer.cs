using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal static class GuardShieldRenderer
    {
        private sealed class Cache
        {
            public Map map;
            public GuardShieldOutline.Circle self;
            public readonly List<GuardShieldOutline.Circle> neighbors = new List<GuardShieldOutline.Circle>();
            public Mesh outline;
            public readonly MaterialPropertyBlock outlineTint = new MaterialPropertyBlock();
            public readonly MaterialPropertyBlock fullTint = new MaterialPropertyBlock();
            public readonly MaterialPropertyBlock hitTint = new MaterialPropertyBlock();
            public int version = -1;
            public void Dispose()
            {
                Destroy(ref outline);
            }
        }

        private static readonly Dictionary<CompSpaceShield, Cache> Caches = new Dictionary<CompSpaceShield, Cache>();
        private static readonly List<GuardShieldOutline.Circle> Scratch = new List<GuardShieldOutline.Circle>();
        private static readonly AccessTools.FieldRef<CompProjectileInterceptor, int> LastHit =
            AccessTools.FieldRefAccess<CompProjectileInterceptor, int>("lastInterceptTicks");
        private static readonly AccessTools.FieldRef<CompProjectileInterceptor, float> LastAngle =
            AccessTools.FieldRefAccess<CompProjectileInterceptor, float>("lastInterceptAngle");
        private static readonly AccessTools.FieldRef<CompProjectileInterceptor, bool> HasCone =
            AccessTools.FieldRefAccess<CompProjectileInterceptor, bool>("drawInterceptCone");
        private static Material material, hitMaterial;

        internal static void Draw(CompSpaceShield shield, CompProjectileInterceptor interceptor)
        {
            if (!shield.parent.Spawned || shield.mode != SpaceShieldMode.Guard) return;
            bool active = interceptor.Active;
            bool selected = !ShellDrawCapture.Active && Find.Selector.IsSelected(shield.parent);
            if (!Caches.TryGetValue(shield, out Cache cache))
                Caches.Add(shield, cache = new Cache { map = shield.parent.Map });
            var self = CircleOf(shield, interceptor);
            Scratch.Clear();
            if (active && shield.UsesRegular)
            {
                var members = shield.OwnedShields;
                if (members != null)
                    foreach (CompSpaceShield other in members)
                    {
                        if (other == shield || !other.UsesRegular || !other.parent.Spawned) continue;
                        CompProjectileInterceptor field = other.parent.TryGetComp<CompProjectileInterceptor>();
                        if (field?.Active == true) Scratch.Add(CircleOf(other, field));
                    }
            }
            bool changed = !self.Equals(cache.self) || cache.version != SubstructureOwnership.GlobalStructureVersion
                || Scratch.Count != cache.neighbors.Count;
            if (!changed)
                for (int i = 0; i < Scratch.Count; i++)
                    if (!Scratch[i].Equals(cache.neighbors[i])) { changed = true; break; }
            if (changed)
            {
                cache.self = self;
                cache.version = SubstructureOwnership.GlobalStructureVersion;
                cache.neighbors.Clear(); cache.neighbors.AddRange(Scratch);
                Destroy(ref cache.outline);
                cache.outline = Build(self.radius, GuardShieldOutline.Clip(self, cache.neighbors));
            }
            Vector3 center = new Vector3((float)self.x, AltitudeLayer.MoteOverhead.AltitudeFor(), (float)self.z);
            // One tick-driven phase for the cooperating field prevents brightness seams and freezes on pause.
            float pulse = 0.30f + 0.04f * Mathf.Sin(Find.TickManager.TicksGame / 60f * 2f);
            Color color = shield.UsesRegular ? new Color(0.8f, 0.9f, 1f, pulse)
                : new Color(1f, 0.15f, 0.15f, pulse + 0.08f);
            material ??= MaterialPool.MatFrom("Other/ForceField", ShaderDatabase.MoteGlow);
            if (active) DrawMesh(cache.outline, Matrix4x4.Translate(center), material, color, cache.outlineTint);
            float diameter = (float)(self.radius * GuardShieldOutline.TextureScale * 2);
            Matrix4x4 fullMatrix = Matrix4x4.TRS(center, Quaternion.identity, new Vector3(diameter, 1, diameter));
            if (selected)
            {
                Color selection = active ? color : new Color(0.5f, 0.5f, 0.5f, 0.35f);
                if (active) selection.a = 0.16f;
                DrawMesh(MeshPool.plane10, fullMatrix, material, selection, cache.fullTint);
            }
            // Preserve native directional hit art; universe impacts already use their actual contact point.
            int hitTick = LastHit(interceptor), age = Find.TickManager.TicksGame - hitTick;
            if (HasCone(interceptor) && age >= 0 && age < 24)
            {
                hitMaterial ??= MaterialPool.MatFrom("Other/ForceFieldCone", ShaderDatabase.MoteGlow);
                color.a = 0.8f * (1f - age / 24f);
                Matrix4x4 hitMatrix = Matrix4x4.TRS(center, Quaternion.Euler(0, LastAngle(interceptor) - 90f, 0),
                    new Vector3(diameter, 1, diameter));
                DrawMesh(MeshPool.plane10, hitMatrix, hitMaterial, color, cache.hitTint);
            }
        }

        private static GuardShieldOutline.Circle CircleOf(CompSpaceShield shield, CompProjectileInterceptor interceptor)
        {
            Vector2 center = shield.GuardCenter;
            return new GuardShieldOutline.Circle { x = center.x, z = center.y,
                radius = interceptor.Props.radius, id = shield.parent.thingIDNumber };
        }

        private static Mesh Build(double radius, IReadOnlyList<GuardShieldOutline.Point> polygon)
        {
            if (polygon.Count < 3) return null;
            var vertices = new Vector3[polygon.Count];
            var uv = new Vector2[polygon.Count];
            var colors = new Color[polygon.Count];
            var triangles = new int[(polygon.Count - 2) * 3];
            double diameter = radius * GuardShieldOutline.TextureScale * 2;
            for (int i = 0; i < polygon.Count; i++)
            {
                var point = polygon[i];
                vertices[i] = new Vector3((float)point.x, 0, (float)point.z);
                uv[i] = new Vector2((float)(point.x / diameter + 0.5), (float)(point.z / diameter + 0.5));
                colors[i] = Color.white;
                if (i >= 2)
                {
                    int index = (i - 2) * 3;
                    triangles[index] = 0; triangles[index + 1] = i - 1; triangles[index + 2] = i;
                }
            }
            var mesh = new Mesh { name = "DeltaV_GuardTextureClip", vertices = vertices,
                uv = uv, colors = colors, triangles = triangles };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material mat, Color color, MaterialPropertyBlock tint)
        {
            if (mesh == null) return;
            tint.SetColor(ShaderPropertyIDs.Color, color);
            Graphics.DrawMesh(mesh, matrix, mat, 0, null, 0, tint);
        }
        private static void Destroy(ref Mesh mesh)
        {
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            mesh = null;
        }
        internal static void Release(CompSpaceShield shield)
        {
            if (!Caches.TryGetValue(shield, out Cache cache)) return;
            cache.Dispose(); Caches.Remove(shield);
        }
        internal static void Release(Map map)
        {
            var remove = new List<CompSpaceShield>();
            foreach (var entry in Caches) if (entry.Value.map == map) remove.Add(entry.Key);
            foreach (var shield in remove) Release(shield);
        }
        internal static void Clear()
        {
            foreach (Cache cache in Caches.Values) cache.Dispose();
            Caches.Clear(); Scratch.Clear();
        }
    }
}
