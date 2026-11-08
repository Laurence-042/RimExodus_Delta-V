using System;
using DeltaV;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static SpacePose AdvanceAnchor(SpacePose pose, float seconds)
    {
        pose.direction = OrbitSphere.Move(pose.direction, pose.velocity * seconds);
        pose.angle = SpaceTransform.WrapAngle(pose.angle + pose.angularVelocity * seconds);
        return pose;
    }

    static void CheckRigidMotion()
    {
        float maxPointError = 0f, maxInverseError = 0f, maxCenterDrift = 0f;
        bool oldDrag = DeltaVMod.CurrentSettings.passiveDrag;
        DeltaVMod.CurrentSettings.passiveDrag = false;
        try
        {
            foreach (Vector3 direction in new[] { Vector3.forward, new Vector3(.03f, .999f, .01f).normalized,
                new Vector3(.03f, -.999f, -.01f).normalized, Vector3.up })
            foreach (float angle in new[] { -3.13f, .7f, 3.13f })
            {
                var host = new SpacePose(direction, angle)
                    { velocity = new Vector2(3f, -2f), angularVelocity = .23f };
                var anchor = new Vector2(7f, -4f);
                var localVelocity = new Vector2(.7f, -.4f);
                const float dt = .005f;
                SpacePose before = SpaceTransform.RigidPointPose(AdvanceAnchor(host, -dt), anchor - localVelocity * dt, .4f);
                SpacePose after = SpaceTransform.RigidPointPose(AdvanceAnchor(host, dt), anchor + localVelocity * dt, .4f);
                SpacePose at = SpaceTransform.RigidPointPose(host, anchor, .4f);
                Vector2 numeric = SpaceTransform.TransportTangent(OrbitSphere.Offset(before.direction, after.direction) / (2 * dt),
                    before.position, at.position);
                maxPointError = Mathf.Max(maxPointError, (numeric - SpaceTransform.VelocityAtMovingLocalPoint(
                    host, anchor, localVelocity, at.position)).magnitude);
                // At the exact pole keep both anchors in the existing fallback basis cap.
                // A derivative across the discontinuous geographic chart seam is undefined.
                Vector2 hostPoint = direction == Vector3.up ? Vector2.zero : new Vector2(-3f, 5f);
                SpacePose origin = SpaceTransform.AlignLocalPoint(host, hostPoint, anchor, .4f);
                before = SpaceTransform.AlignLocalPoint(AdvanceAnchor(host, -dt), hostPoint, anchor, .4f);
                after = SpaceTransform.AlignLocalPoint(AdvanceAnchor(host, dt), hostPoint, anchor, .4f);
                numeric = SpaceTransform.TransportTangent(OrbitSphere.Offset(before.direction, after.direction) / (2 * dt),
                    before.position, origin.position);
                maxInverseError = Mathf.Max(maxInverseError, (numeric - origin.velocity).magnitude);

                // A spinning body whose COM is at rest must not translate it on each tick.
                var center = new SpacePose(direction, angle) { angularVelocity = .23f };
                var body = new SpaceObject(-1, SpaceTransform.AlignLocalPoint(center, Vector2.zero, anchor, 0f));
                var report = new ShipPhysics.MassReport { mass = 100f, inertia = 1000f, comLocal = anchor };
                for (int tick = 0; tick < 600; tick++)
                    ShipPhysics.Integrate(body, report, Vector2.zero, 5f, 1f / 60f, false, false, false);
                SpacePose actualCenter = SpaceTransform.RigidPointPose(body.pose, anchor, 0f);
                maxCenterDrift = Mathf.Max(maxCenterDrift, OrbitSphere.ArcCells(center.direction, actualCenter.direction));
                Check(actualCenter.velocity.magnitude < .003f, "offset COM pure torque does not create linear momentum");
                // Ordinary drift and a snapshot-backed archived body share exactly this contract.
                var archived = new ArchivedSpaceObject { objectId = -2, pose = body.pose,
                    dynamics = DriveSnapshot.Capture(new DriveInfo(report), 1) };
                bool same = true, derivative = true;
                for (int tick = 0; tick < 120; tick++)
                {
                    SpacePose old = body.pose;
                    ShipPhysics.Integrate(body, report, Vector2.zero, 0f, 1f / 60f, false, false, false);
                    Set(Find.TickManager, "ticksGameInt", 20000 + tick);
                    archived.AdvanceDrift(1f / 60f);
                    same &= OrbitSphere.ArcCells(body.pose.direction, archived.pose.direction) < .00001f
                        && (body.pose.velocity - archived.pose.velocity).magnitude < .00001f;
                    Vector2 measured = OrbitSphere.Offset(old.direction, body.pose.direction) * 60f;
                    Vector2 average = (old.velocity + SpaceTransform.TransportVelocity(body.pose, old.position)) * .5f;
                    derivative &= (measured - average).magnitude < .01f;
                }
                Check(same && derivative, "offset origin velocity matches integration and archived drift follows the same path");
            }
            CheckOffsetImpulseAndSave();
        }
        finally { DeltaVMod.CurrentSettings.passiveDrag = oldDrag; }
        Console.WriteLine($"RIGID_MOTION pointDerivative={maxPointError:F6} inverseDerivative={maxInverseError:F6} spinCenterDrift={maxCenterDrift:F6}");
        Check(maxPointError < .01f && maxInverseError < .01f,
            "shared forward and inverse rigid transforms differentiate their positions at poles and wrapped angles");
        Check(maxCenterDrift < .01f, "ten seconds of offset-COM rotation preserves the physical center");
        var slow = new SpacePose(Vector3.forward, .7f) { velocity = new Vector2(.000001f, 0f) };
        Check(SpaceTransform.RigidPointPose(slow, new Vector2(7f, -4f), 0f).velocity.magnitude > .0000009f,
            "exact rest fast path never snaps a small nonzero rigid-point velocity to zero");
    }

    static void CheckOffsetImpulseAndSave()
    {
        var anchor = new Vector2(7f, -4f);
        var center = new SpacePose(new Vector3(.03f, .999f, .01f).normalized, .7f)
            { velocity = new Vector2(2f, -1f), angularVelocity = .2f };
        var report = new ShipPhysics.MassReport { mass = 100f, inertia = 1000f, comLocal = anchor };
        var body = new SpaceObject(-3, SpaceTransform.AlignLocalPoint(center, Vector2.zero, anchor, 0f));
        Vector2 localHit = anchor + new Vector2(3f, 2f);
        SpacePose hit = SpaceTransform.RigidPointPose(body.pose, localHit, 0f);
        Vector2 impulse = new Vector2(15f, -8f);
        Vector2 expected = center.velocity + SpaceTransform.TransportTangent(impulse, hit.position, center.position) / report.mass;
        typeof(ShipPhysics).GetMethod("ApplyAt", Private).Invoke(null,
            new object[] { body, report, localHit, hit.position, impulse });
        SpacePose actual = SpaceTransform.RigidPointPose(body.pose, anchor, 0f);
        Check((actual.velocity - expected).magnitude < .001f && body.pose.angularVelocity != center.angularVelocity,
            "off-center impulse changes COM momentum and spin without confusing origin velocity");
        var partnerCenter = actual;
        partnerCenter.angularVelocity = -.3f;
        var partner = new SpaceObject(-5, SpaceTransform.AlignLocalPoint(partnerCenter, Vector2.zero, anchor, 0f));
        float spinDifference = body.pose.angularVelocity - partner.pose.angularVelocity;
        ShipPhysics.CoupleMotion(body, report, partner, report, actual.position, .5f, 1000f, 1000f);
        Check((SpaceTransform.RigidPointPose(body.pose, anchor, 0f).velocity - actual.velocity).magnitude < .001f
            && (SpaceTransform.RigidPointPose(partner.pose, anchor, 0f).velocity - actual.velocity).magnitude < .001f
            && Mathf.Abs(body.pose.angularVelocity - partner.pose.angularVelocity - spinDifference * .5f) < .001f,
            "coupling equal COM drift exchanges spin without creating translation on offset bodies");
        SpacePose saved = body.pose, restored = default;
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "offset-pose.xml");
        try
        {
            Scribe.saver.InitSaving(path, "offset-pose"); saved.ExposeData(); Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path); restored.ExposeData(); Scribe.loader.FinalizeLoading();
        }
        finally { Scribe.ForceStop(); }
        var replay = new SpaceObject(-4, restored);
        for (int tick = 0; tick < 120; tick++)
        {
            ShipPhysics.Integrate(body, report, Vector2.zero, 0f, 1f / 60f, false, false, false);
            ShipPhysics.Integrate(replay, report, Vector2.zero, 0f, 1f / 60f, false, false, false);
        }
        Check(OrbitSphere.ArcCells(body.pose.direction, replay.pose.direction) < .00001f
            && body.pose.velocity == replay.pose.velocity && body.pose.angularVelocity == replay.pose.angularVelocity,
            "offset-COM motion resumes identically after real Scribe roundtrip without a second motion state");
    }
}
