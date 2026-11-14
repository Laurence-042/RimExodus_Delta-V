using System.Linq;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class TargetCameraFocus
    {
        private const float FlyInDuration = 0.25f;

        internal readonly Map SourceMap;
        private readonly int targetId;
        private readonly int representationVersion;
        private Vector3 start;
        private float? startTime;

        private TargetCameraFocus(Map sourceMap, int targetId, int representationVersion)
        {
            SourceMap = sourceMap;
            this.targetId = targetId;
            this.representationVersion = representationVersion;
        }

        internal static bool CanStart(UniverseManager universe, Map sourceMap, SpaceObject target)
            => Patch_CameraCenterClamp.SupportsTargetFocus && !universe.IsResettingScene
                && sourceMap == Find.CurrentMap && WorldRendererUtility.DrawingMap
                && IsShellTarget(sourceMap, target)
                && universe.FrameOf(sourceMap) is SpaceObject frame
                && (target.Payload?.Recoverable == true || ShellRenderer.HasDrawableMap(universe, target)
                    && frame.Ships.Any(ship => ship.PlayerMayOperate));

        internal static bool QueueStart(UniverseManager universe, Map sourceMap, SpaceObject target)
        {
            if (!CanStart(universe, sourceMap, target)) return false;
            int id = target.objectId, version = target.RepresentationVersion;
            universe.QueueFrameAction(() =>
            {
                if (Current.Game?.GetComponent<UniverseManager>() != universe) return;
                SpaceObject current = universe.ObjectById(id);
                if (current?.RepresentationVersion == version && CanStart(universe, sourceMap, current))
                    universe.TargetCamera = new TargetCameraFocus(sourceMap, id, version);
            });
            return true;
        }

        private static bool IsShellTarget(Map sourceMap, SpaceObject target)
            => sourceMap != null && target?.ParticipatesInOverlays == true && target.map != sourceMap;

        internal void CaptureStart(Vector3 cameraCenter)
        {
            if (startTime.HasValue) return;
            start = cameraCenter;
            startTime = Time.unscaledTime;
        }

        internal bool TryGetCenter(UniverseManager universe, out Vector3 center)
        {
            center = default;
            if (!startTime.HasValue || SourceMap != Find.CurrentMap || !WorldRendererUtility.DrawingMap)
                return false;

            SpaceObject frame = universe.FrameOf(SourceMap);
            SpaceObject target = universe.ObjectById(targetId);
            if (frame == null || target?.RepresentationVersion != representationVersion
                || !IsShellTarget(SourceMap, target) || target.Payload?.Recoverable != true
                    && !ShellRenderer.HasDrawableMap(universe, target))
                return false;

            center = SpaceTransform.FocusedDrawPosition(frame, SourceMap, target.pose.position, 0f);
            float progress = (Time.unscaledTime - startTime.Value) / FlyInDuration;
            if (progress < 1f) center = Vector3.Lerp(start, center, CameraPanner.SmootherStep(progress));
            return true;
        }
    }
}
