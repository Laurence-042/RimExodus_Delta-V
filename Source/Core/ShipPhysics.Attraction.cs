using UnityEngine;

namespace DeltaV
{
    public static partial class ShipPhysics
    {
        // Tractor force changes velocity only. Soft docking uses positional interpolation instead.
        internal static void AttractPoint(SpaceObject body, Vector2 localPoint, SpacePose destination,
            float acceleration)
        {
            SpacePose current = SpaceTransform.RigidPointPose(body.pose, localPoint, 0f);
            Vector2 error = OrbitSphere.Offset(current.direction, destination.direction);
            Vector2 delta = SpaceTransform.TransportVelocity(destination, current.position) - current.velocity;
            Vector2 desired = error * .6f + delta * 1.5f;
            body.pose.velocity += SpaceTransform.TransportTangent(Vector2.ClampMagnitude(desired, acceleration) / 60f,
                current.position, body.pose.position);
        }
    }
}
