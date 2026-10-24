using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // 程序覆盖率纹理只创建一次；逐粒子颜色/渐隐写入顶点，同类粒子合为一次 DrawMesh。
    internal static class MotionParticleRenderer
    {
        internal enum Kind { Smoke, Flame, Dust, Metal, Grid }
        private sealed class Batch
        {
            internal Mesh mesh;
            internal Material material;
            internal Texture2D texture;
            internal readonly List<Vector3> vertices = new List<Vector3>();
            internal readonly List<Vector2> uvs = new List<Vector2>();
            internal readonly List<Color> colors = new List<Color>();
            internal readonly List<int> triangles = new List<int>();
        }
        private static readonly Batch[] batches = new Batch[5];

        internal static float DustCoverage(float radius)
        {
            // 从中心就开始衰减，整片柔和过渡，避免实心圆盘外的一圈窄羽化。
            float t = Mathf.Clamp01(1f - radius * 2f);
            return t * t * (3f - 2f * t);
        }

        internal static void Begin(Kind kind)
        {
            Batch batch = Get(kind);
            batch.vertices.Clear(); batch.uvs.Clear(); batch.colors.Clear(); batch.triangles.Clear();
        }

        internal static void Quad(Kind kind, Vector3 center, Quaternion rotation, float width, float height, Color color)
        {
            if (color.a <= 0f || width <= 0f || height <= 0f) return;
            Batch b = Get(kind);
            int n = b.vertices.Count;
            if (n >= 60000) return;
            Vector3 x = rotation * new Vector3(width * 0.5f, 0f, 0f);
            Vector3 z = rotation * new Vector3(0f, 0f, height * 0.5f);
            b.vertices.Add(center - x - z); b.vertices.Add(center - x + z);
            b.vertices.Add(center + x + z); b.vertices.Add(center + x - z);
            b.uvs.Add(new Vector2(0, 0)); b.uvs.Add(new Vector2(0, 1));
            b.uvs.Add(new Vector2(1, 1)); b.uvs.Add(new Vector2(1, 0));
            for (int i = 0; i < 4; i++) b.colors.Add(color);
            b.triangles.Add(n); b.triangles.Add(n + 1); b.triangles.Add(n + 2);
            b.triangles.Add(n); b.triangles.Add(n + 2); b.triangles.Add(n + 3);
        }

        internal static int Emit(Kind kind, ShellLayer layer, int slot)
        {
            Batch b = Get(kind);
            if (b.vertices.Count == 0) return slot;
            b.mesh.Clear(); b.mesh.SetVertices(b.vertices); b.mesh.SetUVs(0, b.uvs);
            b.mesh.SetColors(b.colors); b.mesh.SetTriangles(b.triangles, 0);
            MapOverlayRenderer.BeginQuadBatch();
            MapOverlayRenderer.AddQuadToBatch(b.mesh, Matrix4x4.identity, b.material);
            layer.Emit(layer.At(slot++));
            return slot;
        }

        private static Batch Get(Kind kind)
        {
            int index = (int)kind;
            if (batches[index] != null) return batches[index];
            var b = new Batch { mesh = new Mesh { name = "DeltaV " + kind } };
            b.mesh.MarkDynamic();
            b.texture = BuildTexture(kind);
            // 原版 Transparent 顶点程序消费 COLOR；Mote 不消费顶点色，不能用于合批渐隐。
            b.material = new Material(ShaderDatabase.Transparent)
            { mainTexture = b.texture, color = Color.white, renderQueue = 3202, name = "DeltaV " + kind };
            return batches[index] = b;
        }

        private static Texture2D BuildTexture(Kind kind)
        {
            int size = kind == Kind.Smoke ? 128 : 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            { name = "DeltaV procedural " + kind, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size - 0.5f, v = (y + 0.5f) / size - 0.5f;
                float r = Mathf.Sqrt(u * u + v * v), alpha = 1f, light = 1f;
                if (kind == Kind.Smoke)
                {
                    float broad = Mathf.PerlinNoise(u * 6f + 13.7f, v * 6f + 21.3f);
                    float fine = Mathf.PerlinNoise(u * 19f + 7.2f, v * 19f + 5.8f);
                    float edge = 0.30f + broad * 0.17f;
                    alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(edge, edge - 0.13f, r));
                    light = Mathf.Clamp01(0.35f + broad * 0.65f + fine * 0.20f);
                }
                else if (kind == Kind.Flame)
                    alpha = Mathf.Pow(Mathf.Clamp01(1f - r * 2f), 1.25f);
                else if (kind == Kind.Dust)
                    alpha = DustCoverage(r);
                else if (kind == Kind.Metal)
                {
                    alpha = Mathf.Abs(u) + Mathf.Abs(v * 0.6f) < 0.48f ? 1f : 0f;
                    light = u + v > 0f ? 1f : 0.35f;
                }
                pixels[y * size + x] = new Color(light, light, light, alpha);
            }
            tex.SetPixels(pixels); tex.Apply(true, true);
            return tex;
        }

        internal static void ClearRuntimeCaches()
        {
            for (int i = 0; i < batches.Length; i++)
            {
                Batch b = batches[i];
                if (b == null) continue;
                Object.Destroy(b.mesh); Object.Destroy(b.material); Object.Destroy(b.texture);
                batches[i] = null;
            }
        }
    }
}
