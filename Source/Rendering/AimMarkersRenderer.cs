using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 预瞄指示器 + 瞄准位置标记（太空战架构 §6.3，纯显示派生、不参与发射判定）：
    /// - 预瞄标记：驾驶中每帧对每个可见敌方容器 × 就绪齐射武器的去重弹速档解算
    ///   截击点（目标匀速外推 + 弹速，与 PD 提前量同一纯函数 SpaceTransform.
    ///   TrySolveInterceptPlanar；激光档 = 目标当前位置）；聚焦帧投影绘制。
    /// - 瞄准位置标记：瞄准模式激活时画在鼠标 universe 点，与预瞄同管线、视觉区分。
    /// 静态渲染缓存（标记材质），换档销毁重建。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class AimMarkersRenderer
    {
        private static Material leadMaterial;
        private static Material aimMaterial;

        private static readonly HashSet<float> SpeedScratch = new HashSet<float>();

        /// <summary>
        /// 前台层发射（ShellRenderer 弹体标记之后调用）：返回消耗后的下一槽位号。
        /// frame = 聚焦容器；piloted 无效（未驾驶）时只画预瞄（预瞄属于驾驶 HUD）——
        /// 实际上两者都只在驾驶中出现，未驾驶直接返回。
        /// </summary>
        internal static int TryEmitMarkers(UniverseManager universe, SpaceObject frame, Map frameMap,
            Ship piloted, ShellLayer layer, int baseSlot)
        {
            if (universe == null || frame == null || frameMap == null || !piloted.IsValid) return baseSlot;
            EnsureMaterials();
            float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 9f;
            int slot = baseSlot;
            bool drewAny = false;

            // ---- 预瞄标记：可见敌方容器 × 去重弹速档 ----
            SpeedScratch.Clear();
            SpaceObject own = universe.FrameOf(piloted.map);
            if (own != null)
            {
                bool laserSeen = false;
                foreach (CompSpaceWeapon weapon in SpaceWeaponService.AllWeaponsOn(piloted.map))
                {
                    if (weapon.aimMode != SpaceWeaponAimMode.Volley || !weapon.GroupActive) continue;
                    ThingDef projectileDef = weapon.WeaponProps.projectileDef;
                    if (projectileDef == null) continue;
                    var props = projectileDef.GetCompProperties<CompProperties_SpaceProjectile>();
                    if (props == null) continue;
                    if (props.laserRange > 0f) laserSeen = true;
                    else SpeedScratch.Add(props.speed);
                }
                foreach (SpaceObject target in universe.objects)
                {
                    if (target == own || target.map == null) continue;
                    if (!SpaceWeaponService.HostileTo(target, Faction.OfPlayer)) continue;
                    // 激光档 = 目标当前位置。
                    if (laserSeen) drewAny |= EmitMarker(frame, frameMap, target.pose.position, leadMaterial, layer, ref slot);
                    foreach (float speed in SpeedScratch)
                    {
                        Vector2 p = OrbitSphere.Offset(own.pose.position, target.pose.position);
                        Vector2 targetVel = SpaceTransform.TransportVelocity(target.pose, own.pose.position);
                        Vector2 ownVel = own.pose.velocity;
                        if (!SpaceTransform.TrySolveInterceptPlanar(p, targetVel - ownVel, speed, out float time))
                        {
                            continue;
                        }
                        Vector3 intercept = OrbitSphere.Move(target.pose.position, target.pose.velocity * time);
                        drewAny |= EmitMarker(frame, frameMap, intercept, leadMaterial, layer, ref slot);
                    }
                }
            }

            // ---- 瞄准位置标记（鼠标 universe 点；瞄准模式激活时）----
            if (universe.Pilot.AimModeActive)
            {
                Vector3 aimPoint = SpaceWeaponService.MouseUniversePoint(frame, frameMap);
                drewAny |= EmitMarker(frame, frameMap, aimPoint, aimMaterial, layer, ref slot);
            }
            return drewAny ? slot : baseSlot;
        }

        private static bool EmitMarker(SpaceObject frame, Map frameMap, Vector3 universePos, Material material,
            ShellLayer layer, ref int slot)
        {
            Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap, universePos,
                AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 9f);
            Quaternion rot = SpaceTransform.FocusedMeshRotation(frame, 0f);
            Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, new Vector3(1.6f, 1f, 1.6f));
            MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10, matrix, material);
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
