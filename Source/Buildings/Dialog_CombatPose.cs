using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_CombatPose : Window
    {
        private readonly CombatTacticSettings settings;
        private readonly System.Action changed;
        private readonly float enemyHeading, previewDistance;
        private readonly float previewHeading, previewPhase, scaleDistance;
        internal Dialog_CombatPose(CombatTacticSettings settings, ShipBase ship, System.Action changed)
        {
            this.settings = settings; this.changed = changed;
            forcePause = true; doCloseX = true; closeOnClickedOutside = true;
            var universe = Current.Game?.GetComponent<UniverseManager>();
            var controller = ship?.IsValid == true ? universe?.Controls.ExistingFor(ship) : null;
            previewDistance = 100f;
            if (controller?.FireControl.Target.TryResolve(universe, out var target) == true)
            {
                enemyHeading = target.Pose.angle;
                var own = ShipRegistry.ShipPose(ship);
                var offset = OrbitSphere.Offset(target.Pose.position, own.position);
                previewDistance = offset.magnitude;
                previewPhase = Mathf.Atan2(offset.y, offset.x);
                previewHeading = SpaceTransform.WrapAngle(own.angle - previewPhase - Mathf.PI);
                if (controller.AutoManeuver.TryPosePreview(ship, target.Pose, out var plannedDistance, out var plannedHeading, out var plannedPhase))
                { previewDistance = plannedDistance; previewHeading = plannedHeading; previewPhase = plannedPhase; }
            }
            scaleDistance = Mathf.Max(20f, settings.distance > 0f ? settings.distance : previewDistance);
        }
        public override Vector2 InitialSize => new Vector2(620f, 570f);
        public override void DoWindowContents(Rect rect)
        {
            Widgets.Label(new Rect(0f, 0f, rect.width, 58f), "DeltaV_CombatPoseHelp".Translate());
            Rect area = new Rect(10f, 66f, rect.width - 20f, rect.height - 160f);
            Vector2 center = area.center;
            float distance = settings.distance > 0f ? settings.distance : previewDistance;
            float scale = Mathf.Min(area.width, area.height) * 0.35f / scaleDistance;
            float phase = settings.bearing == CombatBearing.Free ? previewPhase
                : (settings.bearing == CombatBearing.Enemy ? enemyHeading : 0f) + settings.bearingDegrees * Mathf.Deg2Rad;
            Vector2 shipPoint = center + new Vector2(Mathf.Cos(phase), -Mathf.Sin(phase)) * distance * scale;
            float facing = phase + Mathf.PI + (settings.facing == CombatFacing.Automatic ? previewHeading
                : settings.facing == CombatFacing.Target ? 0f : settings.facingDegrees * Mathf.Deg2Rad);
            Widgets.DrawLine(center, shipPoint, Color.gray, 1f);
            Arrow(center, enemyHeading, Color.red); Arrow(shipPoint, facing, Color.cyan);
            if (Mouse.IsOver(area) && (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag))
            {
                Vector2 mouse = Event.current.mousePosition;
                if (Event.current.button == 0)
                {
                    Vector2 delta = mouse - center;
                    settings.distance = Mathf.Max(1f, delta.magnitude / scale);
                    if (settings.bearing == CombatBearing.Free) settings.bearing = CombatBearing.Enemy;
                    settings.behavior = CombatTactic.HoldRange;
                    settings.bearingDegrees = SpaceTransform.WrapAngle(Mathf.Atan2(-delta.y, delta.x)
                        - (settings.bearing == CombatBearing.Enemy ? enemyHeading : 0f)) * Mathf.Rad2Deg;
                }
                else if (Event.current.button == 1)
                {
                    Vector2 delta = mouse - shipPoint;
                    settings.facing = CombatFacing.Relative;
                    settings.facingDegrees = SpaceTransform.WrapAngle(Mathf.Atan2(-delta.y, delta.x) - phase - Mathf.PI) * Mathf.Rad2Deg;
                }
                changed(); Event.current.Use();
            }
            Widgets.Label(new Rect(10f, rect.height - 82f, rect.width - 20f, 75f),
                "DeltaV_CombatPoseValues".Translate(settings.distance.ToString("0.0"),
                    ("DeltaV_CombatBearing_" + settings.bearing).Translate(), settings.bearingDegrees.ToString("0"),
                    ("DeltaV_CombatFacing_" + settings.facing).Translate(), settings.facingDegrees.ToString("0")));
        }
        private static void Arrow(Vector2 point, float angle, Color color)
        {
            var forward = new Vector2(Mathf.Cos(angle), -Mathf.Sin(angle));
            var side = new Vector2(-forward.y, forward.x);
            Widgets.DrawLine(point - forward * 12f, point + forward * 15f, color, 3f);
            Widgets.DrawLine(point + forward * 15f, point + side * 8f, color, 2f);
            Widgets.DrawLine(point + forward * 15f, point - side * 8f, color, 2f);
        }
    }
}
