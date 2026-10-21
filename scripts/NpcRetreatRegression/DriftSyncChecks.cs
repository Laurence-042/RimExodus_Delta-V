using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckDriftSync()
    {
        foreach (Vector3 direction in new[] { Vector3.forward, new Vector3(1, 2, 3).normalized, new Vector3(-2, 1, -3).normalized })
            foreach (float distance in new[] { .01f, .1f, .35f, .5f, 3f, 30f })
            {
                Vector3 moved = OrbitSphere.Move(direction, new Vector2(distance, 0));
                Check(Math.Abs(OrbitSphere.ArcCells(direction, moved) - distance) < .003f
                    && Math.Abs(OrbitSphere.Offset(direction, moved).magnitude - OrbitSphere.ArcCells(direction, moved)) < .003f,
                    "sub-cell distance and autopilot displacement agree without acos rounding to zero");
                Check(OrbitSphere.ArcCells(direction, direction) == 0f
                    && Math.Abs(OrbitSphere.ArcCells(direction, -direction) - Mathf.PI * OrbitSphere.CellsPerRadian) < .01f,
                    "stable distance preserves identical and antipodal positions");
            }
        var host = new SpacePose(Vector3.forward, .23f)
            { velocity = new Vector2(3f, -2f), angularVelocity = .04f };
        foreach (Vector2 offset in new[] { new Vector2(.37f, .49f), new Vector2(35f, -27f), new Vector2(-66f, 14f) })
        {
            Vector3 position = SpaceTransform.ShipLocalToUniverse(host, offset);
            foreach (float angle in new[] { 0f, Mathf.PI / 2, Mathf.PI, -Mathf.PI / 2 })
            {
                SpacePose own = SpaceTransform.MatchingDriftPose(position, host, angle);
                Check(own.position == position && LandingApproachController.MatchesMotion(own, host),
                    "drift readiness accepts arbitrary positions and fractional cell phases in every grid direction");
                Check((own.velocity - SpaceTransform.TransportVelocity(host, position)).sqrMagnitude < 1e-8f,
                    "drift reference matches linear velocity without a rotating-host orbital velocity term");
                own.velocity += new Vector2(.2f, 0f);
                Check(!LandingApproachController.MatchesMotion(own, host), "drift readiness rejects unmatched linear velocity");
                own = SpaceTransform.MatchingDriftPose(position, host, angle);
                own.angle += .1f;
                Check(!LandingApproachController.MatchesMotion(own, host), "drift readiness rejects unmatched grid direction");
            }
        }
        SpacePose placement = SpaceTransform.RigidPointPose(host, new Vector2(40, 30), 0f);
        SpacePose displaced = placement;
        displaced.position = OrbitSphere.Move(placement.position, new Vector2(3, 0));
        displaced.velocity = SpaceTransform.TransportVelocity(placement, displaced.position);
        Check(LandingApproachController.InWindow(displaced, placement),
            "EVA motion readiness never requires a registered position");

        bool drag = DeltaVMod.CurrentSettings.passiveDrag;
        DeltaVMod.CurrentSettings.passiveDrag = false;
        try { CheckMotionOnlyGuidance(); }
        finally { DeltaVMod.CurrentSettings.passiveDrag = drag; }
        CheckAnchoredRaster();
    }

    static void CheckMotionOnlyGuidance()
    {
        var report = new ShipPhysics.MassReport { mass = 3f, inertia = 12f };
        foreach (Vector2 dir in new[] { Vector2.right, Vector2.left, Vector2.up, Vector2.down })
            report.thrusters.Add(new ShipPhysics.Thruster { dir = dir, thrust = 3f, operational = true });
        var info = new DeltaV.DriveInfo(report);
        var model = info.PreviewModel();
        var acceleration = new double[9];
        for (int axis = 0; axis < 3; axis++)
        {
            acceleration[axis] = model.forceColumns[axis].x / report.mass;
            acceleration[axis + 3] = model.forceColumns[axis].y / report.mass;
            acceleration[axis + 6] = model.torqueColumns[axis] / report.inertia;
        }
        var request = new GuidanceRequest { motionOnly = true, relativeVelocity = new Vector2(2f, -.5f),
            dt = .1f, controlModel = model, controlAcceleration = acceleration, brakingAcceleration = 1f };
        GuidanceCommand baseline = default, baselineFallback = default;
        foreach (Vector2 offset in new[] { Vector2.zero, new Vector2(100f, -50f), new Vector2(-33.4f, 27.2f) })
        {
            request.positionError = offset;
            var guidance = new OnlineMpcGuidance();
            guidance.Reset(AutopilotMpcProfile.Balanced);
            Check(guidance.Compute(request, out GuidanceCommand command), "motion-only MPC solves the production QP");
            BrakingGuidanceFallback.Compute(request, out GuidanceCommand fallback);
            if (offset == Vector2.zero) { baseline = command; baselineFallback = fallback; }
            Check((command.desiredLocalForce - baseline.desiredLocalForce).magnitude < 1e-5f
                && (fallback.desiredLocalForce - baselineFallback.desiredLocalForce).magnitude < 1e-5f,
                "neither MPC costs, constraints, horizon nor fallback restore a drift position");
        }
        Check(baseline.desiredLocalForce.x < 0f && baseline.desiredLocalForce.y > 0f,
            "motion-only guidance brakes both components of relative velocity");
        var loop = new OnlineMpcGuidance();
        loop.Reset(AutopilotMpcProfile.Balanced);
        float position = 0f, velocity = 2f, minimumVelocity = velocity;
        GuidanceCommand previous = default;
        for (int step = 0; step < 200; step++)
        {
            request.positionError = new Vector2(-position, 0f);
            request.relativeVelocity = new Vector2(velocity, 0f);
            request.previousX = previous.normalizedX;
            loop.Compute(request, out previous);
            info.Preview(previous.desiredLocalForce, previous.desiredTorque, out Vector2 force, out _);
            for (int tick = 0; tick < 6; tick++)
            {
                velocity += force.x / report.mass / 60f;
                position += velocity / 60f;
                minimumVelocity = Math.Min(minimumVelocity, velocity);
            }
        }
        Check(Math.Abs(velocity) < LandingApproachController.VelocityWindow && position > 1f && minimumVelocity > -.05f,
            "closed-loop drift matching settles velocity where braking ends without flying back to the command location");

        request.motionOnly = false;
        position = .8f; velocity = .1f; previous = default;
        loop.Reset(AutopilotMpcProfile.Balanced);
        bool usedThrusters = false;
        for (int step = 0; step < 200; step++)
        {
            request.positionError = new Vector2(-position, 0f);
            request.relativeVelocity = new Vector2(velocity, 0f);
            request.previousX = previous.normalizedX;
            loop.Compute(request, out previous);
            info.Preview(previous.desiredLocalForce, previous.desiredTorque, out Vector2 force, out _);
            usedThrusters |= force.sqrMagnitude > .0001f;
            for (int tick = 0; tick < 6; tick++)
            {
                velocity += force.x / report.mass / 60f;
                position += velocity / 60f;
            }
        }
        Check(usedThrusters && Math.Abs(position) < .1f && Math.Abs(velocity) < .03f,
            "soft docking positional MPC restores guide position with achievable thruster force and no void compensation");

        UniverseManager universe = Current.Game.GetComponent<UniverseManager>();
        var driver = new ArchivedSpaceObject { objectId = 49900, pose = new SpacePose(Vector3.forward, 0f),
            dynamics = DriveSnapshot.Capture(info, 49901) };
        universe.objects.Add(driver);
        Func<SpacePose, SpacePose> first = own => own, replacement = own => hostReference(own);
        SpacePose hostReference(SpacePose own) { own.angle += .1f; return own; }
        universe.Autopilot.FollowReference(driver.PrimaryDriver, first, false, () => true, null);
        Check(universe.Autopilot.IsFollowing(driver.PrimaryDriver, first), "soft reference enters the shared autopilot task table");
        universe.Autopilot.FollowReference(driver.PrimaryDriver, replacement, true, () => true, null);
        Check(!universe.Autopilot.DetachReference(driver.PrimaryDriver, first)
            && universe.Autopilot.IsFollowing(driver.PrimaryDriver, replacement),
            "ending a soft link cannot cancel a replacement drift or manual navigation task");
        Check(universe.Autopilot.DetachReference(driver.PrimaryDriver, replacement)
            && !universe.Autopilot.IsActive(driver.PrimaryDriver), "owned reference shutdown releases the control task immediately");
        universe.objects.Remove(driver);
    }

    static void CheckAnchoredRaster()
    {
        foreach (Vector2 size in new[] { new Vector2(32, 28), new Vector2(31, 29), new Vector2(32, 29), new Vector2(31, 28) })
        {
            Vector2 anchor = size / 2f;
            var cells = new HashSet<IntVec3> { new IntVec3(9, 0, 8), new IntVec3(10, 0, 8), new IntVec3(9, 0, 9) };
            Color32[] pixels = ShipSilhouette.AnchoredPixels(cells, anchor, out int side);
            int density = ShipSilhouette.AnchoredPixelsPerCell;
            Check(pixels.Count(p => p.a != 0) == cells.Count * density * density,
                "odd/even map dimensions preserve every silhouette cell without pixel collapse");
            foreach (Rot4 rotation in Rot4.AllRotations)
                for (int index = 0; index < pixels.Length; index++)
                {
                    if (pixels[index].a == 0) continue;
                    Vector2 local = new Vector2((index % side + .5f - side / 2f) / density,
                        (index / side + .5f - side / 2f) / density);
                    Vector2 sourcePoint = anchor + local;
                    var cell = new IntVec3(Mathf.FloorToInt(sourcePoint.x), 0, Mathf.FloorToInt(sourcePoint.y));
                    IntVec3 root = new IntVec3(9, 0, 8), destination = new IntVec3(30, 0, 40);
                    IntVec3 placedCell = destination + PrefabUtility.GetAdjustedLocalPosition(cell - root, rotation);
                    Vector2 origin = new Vector2(destination.x + .5f, destination.z + .5f)
                        + SpaceTransform.RotatePlacementOffset(anchor - new Vector2(root.x + .5f, root.z + .5f), rotation);
                    Vector2 drawn = origin + SpaceTransform.RotatePlacementOffset(local, rotation);
                    Check(cells.Contains(cell) && drawn.x > placedCell.x && drawn.x < placedCell.x + 1
                        && drawn.y > placedCell.z && drawn.y < placedCell.z + 1,
                        "silhouette pixels stay inside the exact vanilla placed cell for all four rotations");
                }
        }
    }
}
