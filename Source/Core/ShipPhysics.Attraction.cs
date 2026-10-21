using UnityEngine;

namespace DeltaV
{
    public static partial class ShipPhysics
    {
        // External engineering/soft-dock force changes velocity only. Integrate remains authoritative.
        internal static void AttractPoint(SpaceObject body, Vector2 localPoint, SpacePose destination,
            float acceleration, float angleError = 0f, float angularAcceleration = 0f,
            float velocityDamping = 1.5f)
        {
            SpacePose current = SpaceTransform.RigidPointPose(body.pose, localPoint, 0f);
            Vector2 error = OrbitSphere.Offset(current.direction, destination.direction);
            Vector2 delta = SpaceTransform.TransportVelocity(destination, current.position) - current.velocity;
            Vector2 desired = error * .6f + delta * velocityDamping;
            body.pose.velocity += SpaceTransform.TransportTangent(Vector2.ClampMagnitude(desired, acceleration) / 60f,
                current.position, body.pose.position);
            body.pose.angularVelocity += Mathf.Clamp(angleError * .6f
                + (destination.angularVelocity - body.pose.angularVelocity) * velocityDamping,
                -angularAcceleration, angularAcceleration) / 60f;
        }
    }
}
