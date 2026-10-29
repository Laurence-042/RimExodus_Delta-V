using System;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    private static void CheckBallisticMath()
    {
        int checks = 0;
        foreach (float tangential in new[] { 0f, 12f, 16f })
        {
            bool solved = SpaceTransform.TrySolveInterceptPlanar(Vector2.right * 115f,
                Vector2.up * tangential, 45f, out float time);
            double expected = 115 / Math.Sqrt(45 * 45 - tangential * tangential);
            if (!solved || Math.Abs(time - expected) > 1e-5)
                throw new Exception("Intercept flight time differs from analytical model.");
            checks++;
        }
        bool originalDrag = DeltaVMod.CurrentSettings.passiveDrag;
        try
        {
            foreach (bool drag in new[] { false, true })
            {
                DeltaVMod.CurrentSettings.passiveDrag = drag;
                var body = new SpaceObject { pose = new SpacePose(Origin, 0f) };
                var report = new ShipPhysics.MassReport { mass = 1f, inertia = 1f };
                const float acceleration = 5f;
                for (int tick = 1; tick <= 240; tick++)
                {
                    ShipPhysics.Integrate(body, report, Vector2.up * acceleration,
                        0f, Tick, false, false, false);
                    if (tick % 30 != 0) continue;
                    float decay = ShipPhysics.PassiveDragFactors(Tick).linear;
                    ShipPhysics.HeldInputFactors(tick / 60.0, decay,
                        out _, out double velocityGain, out double positionGain);
                    Vector2 offset = OrbitSphere.Offset(Origin, body.pose.position);
                    if (Math.Abs(offset.y - acceleration * positionGain) > .01
                        || Math.Abs(body.pose.velocity.y - acceleration * velocityGain) > .01)
                        throw new Exception("60 Hz sphere integration differs from held-force model.");
                    checks++;
                }
                Console.WriteLine($"BALLISTIC math drag={drag}: y@4s="
                    + $"{OrbitSphere.Offset(Origin, body.pose.position).y:F3} cells, v={body.pose.velocity.y:F3}");
            }
        }
        finally { DeltaVMod.CurrentSettings.passiveDrag = originalDrag; }
        Console.WriteLine($"PASS {checks} production intercept/60 Hz integration checks for ballistic math.");
    }
}
