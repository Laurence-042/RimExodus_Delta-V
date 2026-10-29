using UnityEngine;

namespace DeltaV
{
    // Logical-tick state owned by each controller. No projectile or fire-event input.
    internal sealed class CombatReposition
    {
        internal bool initialized;
        internal int nextTick;
        internal float phase;
        internal Vector3 basis;
        private uint random;
        internal void Reset(int seed)
        { initialized = false; nextTick = 0; random = unchecked((uint)seed * 747796405u + 2891336453u); }
        private float Unit()
        { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return (random & 0xffffff) / 16777216f; }
        internal float PhaseAt(Vector3 center)
        {
            if (!initialized) return phase;
            var old = new SpacePose(basis, 0f) { velocity = new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)) };
            Vector2 transported = SpaceTransform.TransportVelocity(old, center);
            return Mathf.Atan2(transported.y, transported.x);
        }
        internal void Update(SpacePose own, SpacePose target, CombatAttackPose pose,
            CombatTacticSettings settings, DriveInfo drive, int tick, float? alliedOpposite = null)
        {
            Vector2 offset = OrbitSphere.Offset(target.position, own.position);
            float current = Mathf.Atan2(offset.y, offset.x);
            bool fresh = !initialized;
            phase = fresh ? current : PhaseAt(target.position);
            basis = target.position; initialized = true;
            if (settings.bearing != CombatBearing.Free)
            {
                phase = SpaceTransform.WrapAngle((settings.bearing == CombatBearing.Enemy ? target.angle : 0f)
                    + settings.bearingDegrees * Mathf.Deg2Rad);
                return;
            }
            if (pose.behavior != CombatTactic.Automatic) return;
            Vector2 tangent = new Vector2(-Mathf.Sin(current), Mathf.Cos(current));
            float speed = Vector2.Dot(SpaceTransform.TransportVelocity(own, target.position) - target.velocity, tangent);
            float remaining = SpaceTransform.WrapAngle(phase - current) * Mathf.Max(1f, offset.magnitude);
            // Keep an unfinished leg. A timed decision never forces a premature reversal.
            if (!fresh && (tick < nextTick || Mathf.Abs(remaining) > 3f || Mathf.Abs(speed) > 1.5f)) return;
            float interval = Mathf.Lerp(settings.intervalMin, settings.intervalMax, Unit());
            float a = Mathf.Min(CombatAttackPose.Acceleration(drive, Vector2.up, pose.heading),
                CombatAttackPose.Acceleration(drive, Vector2.down, pose.heading));
            float angular = drive == null ? 0f : Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
            float feasible = Mathf.Min(a * interval * interval * 0.25f * settings.movement / Mathf.Max(1f, pose.distance),
                Mathf.Sqrt(Mathf.Max(0f, angular)) * interval * 0.25f);
            float jitter = (Unit() * 2f - 1f) * settings.repositionDegrees * Mathf.Deg2Rad;
            float desired = alliedOpposite.HasValue ? SpaceTransform.WrapAngle(alliedOpposite.Value + jitter - current) : jitter;
            phase = SpaceTransform.WrapAngle(current + Mathf.Clamp(desired, -feasible, feasible));
            nextTick = tick + Mathf.RoundToInt(interval * 60f);
        }
    }
}
