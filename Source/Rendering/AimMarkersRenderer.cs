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
    /// （标记材质），换档销毁重建。图标用原版 overlay 贴图（瞄准位置 = 原版
    /// targeter 同款 TargetHighlight_Square；预瞄 = Circle75Solid 圆点）。
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
            // 与聚焦船同宇宙角：从聚焦船看恒水平（太空战 2026-09-27 用户决策——
            // 标记随聚焦船旋转，不锁宇宙 0 角）。仍走 §5 唯一变换入口。
            Quaternion rot = SpaceTransform.FocusedMeshRotation(frame, frame.pose.angle);
            Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, Vector3.one);
            MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10, matrix,
                kind == AimMarkerKind.Lead ? leadMaterial : aimMaterial);
            ShellLayer.Slot s = layer.At(slot++);
            s.layer.Emit(s);
            return true;
        }

        private static void EnsureMaterials()
        {
            if (leadMaterial != null) return;
            // MaterialPool 按路径+着色器+颜色共享缓存；克隆一份自持以设 renderQueue
            // （前台层排序口径），ClearRuntimeCaches 只销毁克隆，不动共享贴图。
            leadMaterial = new Material(MaterialPool.MatFrom("UI/Overlays/Circle75Solid",
                ShaderDatabase.Transparent, new Color(0.4f, 1f, 0.6f, 0.75f)))
            {
                name = "DeltaV_LeadMarker",
                renderQueue = 3200,
            };
            aimMaterial = new Material(MaterialPool.MatFrom("UI/Overlays/TargetHighlight_Square",
                ShaderDatabase.Transparent, new Color(1f, 0.45f, 0.3f, 0.85f)))
            {
                name = "DeltaV_AimMarker",
                renderQueue = 3200,
            };
        }

        /// <summary>换档清理（UniverseManager.ResetRuntimeServices）：只销毁自持克隆材质。</summary>
        public static void ClearRuntimeCaches()
        {
            if (leadMaterial != null)
            {
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
