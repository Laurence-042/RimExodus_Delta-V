using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 降落表现层：原版仍负责捕获、遮罩、真实放置与收尾；这里只替换
    /// GravshipRenderer.UpdateLanding 的“从视野外飞入”为起始宇宙姿态 → 选定落点姿态插值。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class DeltaVLandingAnimator
    {
        private static readonly FieldInfo RendererGravship = AccessTools.Field(typeof(GravshipRenderer), "gravship");
        private static readonly FieldInfo RendererTargetCenter = AccessTools.Field(typeof(GravshipRenderer), "takeoffOrLandingPosition");
        private static readonly FieldInfo RendererTargetEngine = AccessTools.Field(typeof(GravshipRenderer), "takeoffOrLandingEnginePos");
        private static readonly FieldInfo RendererLandingRotation = AccessTools.Field(typeof(GravshipRenderer), "landingRotation");
        private static readonly MethodInfo RendererDrawGravship = AccessTools.Method(typeof(GravshipRenderer),
            "DrawGravship");

        public static bool Active
        {
            get
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                return universe != null && Current.Game.Gravship != null
                    && universe.Ships.Any(s => s.docked && s.dockedMap == Find.CurrentMap);
            }
        }

        public static bool Draw(GravshipRenderer renderer, float progress, bool isPollutedLanding)
        {
            if (!Active || RendererGravship == null || RendererTargetCenter == null
                || RendererTargetEngine == null || RendererLandingRotation == null
                || RendererDrawGravship == null) return false;
            var gravship = RendererGravship.GetValue(renderer) as Gravship;
            if (gravship?.capture?.capture == null) return false;

            UniverseManager universe = Current.Game.GetComponent<UniverseManager>();
            ShipState ship = universe.Ships.FirstOrDefault(s => s.docked && s.dockedMap == Find.CurrentMap);
            if (ship?.map == null || !ReferenceEquals(Current.Game.Gravship, gravship)) return false;
            SpaceObject targetFrame = universe.FrameOf(ship.dockedMap);
            if (targetFrame == null) return false;

            Vector2 sourceOrigin = SpaceTransform.ShipLocalOrigin(ship.map);
            // landing capture 是飞船已经按目标 Rot4 放到目标图后的纹理，不能拿其 captureCenter
            // 当作起点。起点中心从口袋图原引擎锚 + Gravship 原生 engineToCenter 恢复。
            Vector3 sourceCaptureCenter = ship.pocketRoot.ToVector3() + gravship.engineToCenter;
            Vector2 captureLocal = new Vector2(sourceCaptureCenter.x - sourceOrigin.x,
                sourceCaptureCenter.z - sourceOrigin.y);
            Vector2 captureUniverse = ship.preDockPose.position
                + SpaceTransform.Rotate(captureLocal, ship.preDockPose.angle);
            Vector2 startLocal = SpaceTransform.UniverseToFocused(targetFrame, captureUniverse);
            Vector2 startMap = SpaceTransform.ShipLocalToMap(ship.dockedMap, startLocal);
            Vector3 startCenter = new Vector3(startMap.x, 0f, startMap.y);
            Vector3 targetCenter = (Vector3)RendererTargetCenter.GetValue(renderer);
            Rot4 targetRot = (Rot4)RendererLandingRotation.GetValue(renderer);
            float relativeStartAngle = SpaceTransform.UniverseAngleToFocused(targetFrame, ship.preDockPose.angle);
            // 捕获纹理本身已经包含 targetRot；这里只施加“起始 Map 姿态 - 目标 Map 姿态”的差。
            float targetMapAngle = -targetRot.AsAngle * Mathf.Deg2Rad;
            float startDelta = SpaceTransform.WrapAngle(relativeStartAngle - targetMapAngle);
            Quaternion startRotation = Quaternion.Euler(0f, -startDelta * Mathf.Rad2Deg, 0f);

            float t = Mathf.SmoothStep(0f, 1f, progress.RemapClamped(0f, 0.95f, 0f, 1f));
            Vector3 center = Vector3.Lerp(startCenter, targetCenter, t);
            Quaternion rotation = Quaternion.Slerp(startRotation, Quaternion.identity, t);

            // DrawGravship 同时负责捕获纹理、室内遮罩、推进器火焰/耀斑/尾烟和
            // 逆重引擎辉光。enginePos 只随当前中心平移；该私有方法会围绕 center
            // 应用 rotation，从而保证所有效果与船体保持同一个刚体变换。
            Vector3 targetEngine = (Vector3)RendererTargetEngine.GetValue(renderer);
            Vector3 enginePos = targetEngine + (center - targetCenter);
            RendererDrawGravship.Invoke(renderer, new object[]
            {
                gravship.capture,
                Find.Camera.transform.position,
                center,
                enginePos,
                rotation,
                t,
                0f,
                false,
                targetRot,
                isPollutedLanding
            });
            return true;
        }

        public static void EnsureLaunchInfoBeforeLandingEnds(WorldComponent_GravshipController controller)
        {
            if (!Active || RendererGravship == null) return;
            Gravship gravship = Current.Game.Gravship;
            if (gravship?.Engine != null && gravship.Engine.launchInfo == null)
            {
                gravship.Engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            }
        }
    }
}
