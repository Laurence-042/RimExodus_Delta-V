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
        private const float MinimumAlignmentDuration = 0.35f;
        private const float MaximumAlignmentDuration = 15f;
        private const float FallbackLinearAcceleration = 2f;
        private const float FallbackAngularAcceleration = 0.5f;

        private static readonly FieldInfo RendererGravship = AccessTools.Field(typeof(GravshipRenderer), "gravship");
        private static readonly FieldInfo RendererTargetCenter = AccessTools.Field(typeof(GravshipRenderer), "takeoffOrLandingPosition");
        private static readonly FieldInfo RendererTargetEngine = AccessTools.Field(typeof(GravshipRenderer), "takeoffOrLandingEnginePos");
        private static readonly FieldInfo RendererLandingRotation = AccessTools.Field(typeof(GravshipRenderer), "landingRotation");
        private static readonly MethodInfo RendererDrawGravship = AccessTools.Method(typeof(GravshipRenderer),
            "DrawGravship");
        private static readonly FieldInfo ControllerTimeLeft =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "timeLeft");

        private static ShipState preparedShip;
        private static ShipPhysics.MassReport preparedMassReport;
        private static float alignmentDuration = -1f;
        private static float alignmentElapsed;

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
            if (!TryGetAlignment(renderer, out Gravship gravship, out _, out Vector3 startCenter,
                out Vector3 targetCenter, out float startDelta, out Rot4 targetRot)) return false;

            Quaternion startRotation = Quaternion.Euler(0f, -startDelta * Mathf.Rad2Deg, 0f);
            float t = RestToRestFraction(progress);
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

        /// <summary>GenerateGravship 清空源图前缓存真实质量/推进器几何，供选点后计算对齐能力。</summary>
        public static void Prepare(ShipState ship)
        {
            preparedShip = ship;
            // 对齐只计算一次，直接重测可避免玩家刚扩建/拆除后命中两秒飞行缓存窗口。
            preparedMassReport = ship?.map == null ? null : ShipPhysics.Measure(ship.map);
            alignmentDuration = -1f;
            alignmentElapsed = 0f;
        }

        /// <summary>
        /// 原版 BeginCutscene 已写入目标中心/角度后计算时长。对称静止到静止运动满足
        /// T=2*sqrt(distance/acceleration)；平移与旋转共用较长者，另一轴自动降低出力同步到达。
        /// </summary>
        public static void BeginDynamicTiming(GravshipRenderer renderer)
        {
            if (!TryGetAlignment(renderer, out _, out ShipState ship, out Vector3 startCenter,
                out Vector3 targetCenter, out float startDelta, out Rot4 targetRot)) return;

            Vector2 targetFrameDelta = new Vector2(targetCenter.x - startCenter.x,
                targetCenter.z - startCenter.z);
            float linearAcceleration = FallbackLinearAcceleration;
            float angularAcceleration = FallbackAngularAcceleration;
            ShipPhysics.MassReport report = ReferenceEquals(preparedShip, ship) ? preparedMassReport : null;
            if (report != null)
            {
                if (targetFrameDelta.sqrMagnitude > 1e-6f)
                {
                    float targetMapAngle = -targetRot.AsAngle * Mathf.Deg2Rad;
                    float relativeStartAngle = SpaceTransform.WrapAngle(startDelta + targetMapAngle);
                    Vector2 localDirection = SpaceTransform.RotateInverse(
                        targetFrameDelta.normalized, relativeStartAngle);
                    ShipPhysics.Allocate(report, new ShipPhysics.Command
                    {
                        fx = localDirection.x,
                        fz = localDirection.y,
                        torque = 0f
                    }, out Vector2 force, out _);
                    linearAcceleration = Mathf.Max(Vector2.Dot(force, localDirection) / report.mass, 0.05f);
                }
                if (Mathf.Abs(startDelta) > 1e-5f)
                {
                    ShipPhysics.Allocate(report, new ShipPhysics.Command
                    {
                        torque = -Mathf.Sign(startDelta)
                    }, out _, out float torque);
                    angularAcceleration = Mathf.Max(Mathf.Abs(torque) / report.inertia, 0.02f);
                }
            }

            float linearTime = RestToRestTime(targetFrameDelta.magnitude, linearAcceleration);
            float angularTime = RestToRestTime(Mathf.Abs(startDelta), angularAcceleration);
            alignmentDuration = Mathf.Clamp(Mathf.Max(linearTime, angularTime),
                MinimumAlignmentDuration, MaximumAlignmentDuration);
            alignmentElapsed = 0f;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: landing alignment duration={alignmentDuration:F2}s, distance={targetFrameDelta.magnitude:F1}, angle={Mathf.Abs(startDelta) * Mathf.Rad2Deg:F0}deg, a={linearAcceleration:F2}, alpha={angularAcceleration:F2}");
        }

        public static void BeforeControllerUpdate(WorldComponent_GravshipController controller)
        {
            if (alignmentDuration <= 0f || ControllerTimeLeft == null) return;
            if (!Active)
            {
                ResetTiming();
                return;
            }
            float remainingFraction = 1f - Mathf.Clamp01(alignmentElapsed / alignmentDuration);
            // 原版用 progress=1-timeLeft/10；写回归一化的 10 秒刻度即可保留其全部收尾流程。
            ControllerTimeLeft.SetValue(controller, remainingFraction * 10f);
        }

        public static void AfterControllerUpdate()
        {
            if (alignmentDuration <= 0f) return;
            if (Current.Game?.Gravship == null)
            {
                ResetTiming();
                return;
            }
            alignmentElapsed += Time.deltaTime;
        }

        public static void ResetTiming()
        {
            preparedShip = null;
            preparedMassReport = null;
            alignmentDuration = -1f;
            alignmentElapsed = 0f;
        }

        private static bool TryGetAlignment(GravshipRenderer renderer, out Gravship gravship,
            out ShipState ship, out Vector3 startCenter, out Vector3 targetCenter,
            out float startDelta, out Rot4 targetRot)
        {
            gravship = null;
            ship = null;
            startCenter = Vector3.zero;
            targetCenter = Vector3.zero;
            startDelta = 0f;
            targetRot = Rot4.North;
            if (!Active || RendererGravship == null || RendererTargetCenter == null
                || RendererTargetEngine == null || RendererLandingRotation == null
                || RendererDrawGravship == null) return false;
            gravship = RendererGravship.GetValue(renderer) as Gravship;
            if (gravship?.capture?.capture == null) return false;

            UniverseManager universe = Current.Game.GetComponent<UniverseManager>();
            ship = universe.Ships.FirstOrDefault(s => s.docked && s.dockedMap == Find.CurrentMap);
            if (ship?.map == null || !ReferenceEquals(Current.Game.Gravship, gravship)) return false;
            SpaceObject targetFrame = universe.FrameOf(ship.dockedMap);
            if (targetFrame == null) return false;

            Vector2 sourceOrigin = SpaceTransform.ShipLocalOrigin(ship.map);
            Vector3 sourceCaptureCenter = ship.pocketRoot.ToVector3() + gravship.engineToCenter;
            Vector2 captureLocal = new Vector2(sourceCaptureCenter.x - sourceOrigin.x,
                sourceCaptureCenter.z - sourceOrigin.y);
            Vector3 captureDir = SpaceTransform.ShipLocalToUniverse(ship.preDockPose, captureLocal);
            Vector2 startLocal = SpaceTransform.UniverseToFocused(targetFrame, captureDir);
            Vector2 startMap = SpaceTransform.ShipLocalToMap(ship.dockedMap, startLocal);
            startCenter = new Vector3(startMap.x, 0f, startMap.y);
            targetCenter = (Vector3)RendererTargetCenter.GetValue(renderer);
            targetRot = (Rot4)RendererLandingRotation.GetValue(renderer);
            float relativeStartAngle = SpaceTransform.UniverseAngleToFocused(targetFrame, ship.preDockPose.angle);
            float targetMapAngle = -targetRot.AsAngle * Mathf.Deg2Rad;
            startDelta = SpaceTransform.WrapAngle(relativeStartAngle - targetMapAngle);
            return true;
        }

        private static float RestToRestTime(float distance, float acceleration)
        {
            return distance <= 1e-6f ? 0f : 2f * Mathf.Sqrt(distance / Mathf.Max(acceleration, 1e-4f));
        }

        /// <summary>对称恒加速/恒减速的位置曲线；速度连续，起点和终点速度均为零。</summary>
        private static float RestToRestFraction(float progress)
        {
            float u = Mathf.Clamp01(progress);
            return u < 0.5f ? 2f * u * u : 1f - 2f * (1f - u) * (1f - u);
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
