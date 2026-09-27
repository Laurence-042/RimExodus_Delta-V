using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>标记种类：预瞄（截击点，绿）/ 瞄准位置（鼠标点，橙红）。</summary>
    public enum AimMarkerKind
    {
        Lead,
        Aim,
    }

    /// <summary>
    /// 瞄准标记绘制助手（太空战架构 §6.3）：只负责材质与四边形发射（聚焦帧投影），
    /// 不解算、不持会话——截击计算与「画不画」的判定都在
    /// <see cref="SpaceAimController"/>（Renderer 只显示，不模拟）。静态渲染缓存
    /// （标记材质），换档销毁重建。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class AimMarkersRenderer
    {
        private static Material leadMaterial;
        private static Material aimMaterial;

        /// <summary>发射一枚标记四边形到前台层槽位（返回是否消耗；slot 递增由调用方持有）。</summary>
        internal static bool EmitMarker(SpaceObject frame, Map frameMap, Vector3 universePos,
            AimMarkerKind kind, ShellLayer layer, ref int slot)
        {
            EnsureMaterials();
            Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap, universePos,
                AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 9f);
            Quaternion rot = SpaceTransform.FocusedMeshRotation(frame, 0f);
            Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, new Vector3(1.6f, 1f, 1.6f));
            MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10, matrix,
                kind == AimMarkerKind.Lead ? leadMaterial : aimMaterial);
            ShellLayer.Slot s = layer.At(slot++);
            s.layer.Emit(s);
            return true;
        }

        private static void EnsureMaterials()
        {
            if (leadMaterial != null) return;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var pixels = new Color32[4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            // 预瞄 = 绿色空心感（小方块）；瞄准位置 = 橙红（视觉区分，形态占位可后调）。
            leadMaterial = new Material(ShaderDatabase.Transparent)
            {
                name = "DeltaV_LeadMarker",
                mainTexture = texture,
                color = new Color(0.4f, 1f, 0.6f, 0.75f),
                renderQueue = 3200,
            };
            aimMaterial = new Material(ShaderDatabase.Transparent)
            {
                name = "DeltaV_AimMarker",
                mainTexture = texture,
                color = new Color(1f, 0.45f, 0.3f, 0.85f),
                renderQueue = 3200,
            };
        }

        /// <summary>换档清理（UniverseManager.ResetRuntimeServices）。</summary>
        public static void ClearRuntimeCaches()
        {
            if (leadMaterial != null)
            {
                if (leadMaterial.mainTexture != null) Object.Destroy(leadMaterial.mainTexture);
                Object.Destroy(leadMaterial);
                leadMaterial = null;
            }
            if (aimMaterial != null)
            {
                Object.Destroy(aimMaterial);
                aimMaterial = null;
            }
        }
    }
}
