using System;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    static void CheckRotatingPrediction(ManeuverMotionObject fixture, bool axial = false, bool offsetCenter = false)
    {
        var report = new ShipPhysics.MassReport { mass = fixture.layout.mass, inertia = fixture.layout.inertia };
        foreach (var source in fixture.layout.thrusters)
            report.thrusters.Add(new ShipPhysics.Thruster { dir = source.dir, localPos = source.localPos,
                operational = true, thrust = axial && Mathf.Abs(source.dir.y) > .5f ? 100f : source.thrust });
        var drive = new DriveInfo(report);
        if (offsetCenter)
        {
            report.comLocal = new Vector2(7f, -4f);
            foreach (var thruster in report.thrusters) thruster.localPos += report.comLocal;
        }
        if (axial)
        {
            var turret = new CombatAttackWeapon(Vector2.zero, 0f, 360f, 150f, 0f, 45f, 100f, true, true);
            var fixedGun = new CombatAttackWeapon(Vector2.zero, 0f, 2f, 150f, 0f, 45f, 100f, true, true);
            CombatAttackPose Choose(CombatAttackWeapon[] guns, CombatFacing facing = CombatFacing.Automatic) =>
                CombatAttackPose.Choose(guns, new CombatTacticSettings { distance = 100f, facing = facing }, drive,
                    Vector2.zero, 10f, 100f, 0f, 1, true, null, null);
            var free = Choose(new[] { turret }); var fixedPose = Choose(new[] { fixedGun });
            var mixed = Choose(new[] { turret, fixedGun }); var explicitFacing = Choose(new[] { turret }, CombatFacing.Target);
            Check(free.freeThrustFacing && Mathf.Abs(Mathf.Sin(free.heading)) > .9f && free.firepower > 99f,
                "a turret ship selects a sideways strong-axis attitude while retaining weapon coverage");
            Check(fixedPose.firepower > 99f && Mathf.Abs(fixedPose.heading) < .02f
                && mixed.firepower > 199f,
                "fixed and mixed weapons retain their firing constraint during joint attitude selection");
            var largeTurret = new CombatAttackWeapon(Vector2.zero, 0f, 360f, 150f, 0f, 45f, 990f, true, true);
            var smallGun = new CombatAttackWeapon(Vector2.zero, 0f, 2f, 150f, 0f, 45f, 10f, true, true);
            var mostlyTurret = Choose(new[] { largeTurret, smallGun });
            Check(mostlyTurret.freeThrustFacing && Mathf.Abs(Mathf.Sin(mostlyTurret.heading)) > .9f,
                "a small fixed gun does not impose a hull-wide attitude class on a turret-dominated ship");
            Check(!explicitFacing.freeThrustFacing && explicitFacing.heading == 0f,
                "explicit player facing wins over a stronger lateral thrust opportunity");
            report.inertia *= 1000f;
            var slow = Choose(new[] { turret });
            Check(Mathf.Abs(slow.heading) < .02f, "weak turning authority pays the actual turn cost instead of assuming immediate strong-axis access");
            report.inertia /= 1000f;
        }
        var model = drive.PreviewModel(null);
        var guidance = new OnlineMpcGuidance(); guidance.Reset(AutopilotMpcProfile.Fast);
        var start = new SpacePose(Vector3.forward, .7f) { velocity = new Vector2(1f, -2f), angularVelocity = .25f };
        var body = new SpaceObject(-1, start);
        SpacePose startCenter = SpaceTransform.RigidPointPose(start, report.comLocal, 0f);
        var b = new double[9]; ShipAutopilot.BuildControlAcceleration(start, model, 0f, b);
        var request = new GuidanceRequest { dt = .1f, controlModel = model, controlAcceleration = b,
            referencePosition = startCenter.position, referenceAngle = start.angle, relativeAngularVelocity = start.angularVelocity,
            combatTrajectory = new CombatTrajectoryIntent { seconds = 4f } };
        var type = typeof(OnlineMpcGuidance);
        var intervals = (double[])type.GetField("intervals", Private).GetValue(guidance);
        var solution = (double[])type.GetField("solution", Private).GetValue(guidance);
        var state = (double[])type.GetField("state", Private).GetValue(guidance);
        state[2] = startCenter.velocity.x; state[3] = startCenter.velocity.y; state[5] = start.angularVelocity;
        var u = new Vector3(.15f, -.02f, .1f);
        for (int step = 0; step < intervals.Length; step++)
        { intervals[step] = .1; solution[step * 3] = u.x; solution[step * 3 + 1] = u.y; solution[step * 3 + 2] = u.z; }
        type.GetMethod("BuildNominalAttitudes", Private).Invoke(guidance, new object[] { request });
        model.DesiredWrench(u, out var wanted, out float wantedTorque);
        drive.Preview(wanted, wantedTorque, out var force, out float torque);
        var advance = type.GetMethod("AdvanceStateAndSensitivity", Private);
        for (int step = 0; step < intervals.Length; step++)
        {
            advance.Invoke(guidance, new object[] { step, .1, request, step * .1 });
            for (int tick = 0; tick < 6; tick++) ShipPhysics.Integrate(body, report, force, torque, 1f / 60f, false, false, false);
        }
        double Pred(int index) => (double)type.GetMethod("PredictedState", Private).Invoke(guidance, new object[] { index });
        SpacePose actualCenter = SpaceTransform.RigidPointPose(body.pose, report.comLocal, 0f);
        Vector2 actualPosition = OrbitSphere.Offset(startCenter.position, actualCenter.position);
        Vector2 actualVelocity = SpaceTransform.TransportVelocity(actualCenter, startCenter.position);
        float positionError = (actualPosition - new Vector2((float)Pred(0), (float)Pred(1))).magnitude;
        float velocityError = (actualVelocity - new Vector2((float)Pred(2), (float)Pred(3))).magnitude;
        Console.WriteLine($"ROTATING_MODEL axial={axial} offsetCenter={offsetCenter} positionError={positionError:F6} velocityError={velocityError:F6}");
        Check(positionError < .04f && velocityError < .04f
            && Mathf.Abs(SpaceTransform.WrapAngle(body.pose.angle - start.angle - (float)Pred(4))) < .005f,
            "rotating MPC nominal dynamics match the real allocator and 60 Hz sphere integration");
    }

    static void CheckContinuousReferences(DriveInfo drive)
    {
        bool startsAtActual = true, derivatives = true, terminalMoving = true;
        float maxVelocityError = 0f, maxOmegaError = 0f;
        foreach (Vector3 center in new[] { Vector3.forward, new Vector3(.03f, .999f, .01f).normalized })
        foreach (float heading in new[] { -3.13f, .7f, 3.13f })
        foreach (Vector2 anchor in new[] { Vector2.zero, new Vector2(7f, -4f) })
        {
            var target = new SpacePose(center, .3f) { velocity = new Vector2(2f, -1f) };
            var own = new SpacePose(OrbitSphere.Move(center, new Vector2(100f, 30f)), heading)
                { velocity = new Vector2(-2f, 4f), angularVelocity = .2f };
            var action = new CombatManeuverIntent { behavior = CombatTactic.Automatic,
                distance = 100f, heading = 1f, tangentSpeed = 8f };
            var trajectory = CombatFlightPlanner.Plan(own, target, action, drive, 0);
            trajectory.localAnchor = anchor;
            var expectedOrigin = SpaceTransform.AlignLocalPoint(own, Vector2.zero, anchor, 0f);
            trajectory.Sample(0f, out var start, out var startV, out float startA, out float startW);
            startsAtActual &= OrbitSphere.ArcCells(start, expectedOrigin.position) < .01f
                && (startV - expectedOrigin.velocity).magnitude < .01f
                && Mathf.Abs(SpaceTransform.WrapAngle(startA - own.angle)) < .001f && Mathf.Abs(startW - own.angularVelocity) < .001f;
            foreach (float time in new[] { .5f, 2f, 3.9f, 4f, 4.1f, 6f })
            {
                const float dt = .05f;
                trajectory.Sample(time - dt, out var p0, out _, out float a0, out _);
                trajectory.Sample(time + dt, out var p1, out _, out float a1, out _);
                trajectory.Sample(time, out var p, out var v, out _, out float w);
                Vector2 numericalV = SpaceTransform.TransportVelocity(new SpacePose(p0, 0f)
                    { velocity = OrbitSphere.Offset(p0, p1) / (2f * dt) }, p);
                maxVelocityError = Mathf.Max(maxVelocityError, (numericalV - v).magnitude);
                maxOmegaError = Mathf.Max(maxOmegaError, Mathf.Abs(SpaceTransform.WrapAngle(a1 - a0) / (2f * dt) - w));
                derivatives &= (numericalV - v).magnitude < .04f
                    && Mathf.Abs(SpaceTransform.WrapAngle(a1 - a0) / (2f * dt) - w) < .003f;
            }
            trajectory.Sample(4f, out _, out var endVelocity, out _, out _);
            terminalMoving &= endVelocity.magnitude > 1f;
        }
        Console.WriteLine($"REFERENCE derivativeVelocityMax={maxVelocityError:F6} derivativeOmegaMax={maxOmegaError:F6}");
        Check(startsAtActual, "rolling combat references start at the actual full state, including polar positions and wrapped angles");
        Check(derivatives, "reference velocity and angular velocity match numerical derivatives before, at and beyond the horizon");
        Check(terminalMoving, "a continuing orbit keeps a nonzero terminal velocity instead of stopping at the horizon");
        var moving = new SpacePose(Vector3.forward, .3f) { velocity = new Vector2(4f, 2f), angularVelocity = .4f };
        var brake = CombatFlightPlanner.Brake(moving, drive, 0);
        brake.Sample(0f, out _, out var v0, out _, out float w0);
        brake.Sample(4f, out _, out var v1, out _, out float w1);
        Check((v0 - moving.velocity).magnitude < .001f && w0 == moving.angularVelocity
            && v1.magnitude < v0.magnitude && Mathf.Abs(w1) < Mathf.Abs(w0),
            "blocked-route braking starts with actual momentum and requests reachable deceleration");
    }
}
