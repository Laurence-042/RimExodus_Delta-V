using System;
using System.IO;
using System.Linq;
using DeltaV;
using HarmonyLib;
using UnityEngine;
using Verse;

internal static partial class Program
{
    private static Vector2 GuardCenter(CompSpaceShield shield) =>
        (Vector2)AccessTools.Property(typeof(CompSpaceShield), "GuardCenter").GetValue(shield);

    private static void CheckGuardCenters()
    {
        IntVec2 previousSize = shieldDef.size;
        try
        {
            foreach (int size in new[] { 1, 3 })
            for (int facing = 0; facing < 4; facing++)
            foreach (float angle in new[] { 0f, 0.7f, 1.5707963f })
            {
                shieldDef.size = new IntVec2(size, size);
                Reset(32);
                CompSpaceShield shield = DualShield();
                // The fixture has no region graph; only the stored placement rotation is needed here.
                Set(shield.parent, "rotationInt", new Rot4(facing));
                hull.pose.angle = angle;
                Vector2 center = GuardCenter(shield);
                Vector3 native = shield.parent.Position.ToVector3Shifted();
                Check(center.x == native.x && center.y == native.z,
                    $"{size}x{size} guard center follows native root cell, rotation {facing}");
                var shot = Fire(100, lane: center.y);
                Check(shot.dead && contacts.Count == 1 && contacts[0].shieldRadius > 0
                    && (contacts[0].shieldCenter - center).sqrMagnitude < 1e-8f,
                    "production sweep and impact snapshot share the rendered guard center");
                Check(Math.Abs(contacts[0].localPoint.x - (center.x - 12)) < 0.03f
                    && Math.Abs(contacts[0].localPoint.y - center.y) < 0.03f,
                    "actual incoming hit lies on the native circle, without a footprint-size offset");

                SpacePose original = hull.pose;
                Vector3 absolute = SpaceTransform.MapPointToUniverse(hull, map, center);
                var other = new SpaceObject(nextId++, new SpacePose(Vector3.forward, angle + 0.6f));
                Vector2 local = SpaceTransform.MapToShipLocal(map, center);
                Vector2 expectedRelative = SpaceTransform.ShipLocalOrigin(map) + SpaceTransform.Rotate(local, -0.6f);
                for (int switchIndex = 0; switchIndex < 8; switchIndex++)
                {
                    Vector3 focused = SpaceTransform.FocusedDrawPosition(hull, map, absolute, 0);
                    Vector3 relative = SpaceTransform.FocusedDrawPosition(other, map, absolute, 0);
                    Check(Math.Abs(focused.x - center.x) < 0.03f && Math.Abs(focused.z - center.y) < 0.03f
                        && Math.Abs(relative.x - expectedRelative.x) < 0.03f && Math.Abs(relative.z - expectedRelative.y) < 0.03f,
                        "repeated focus switches preserve identity and relative guard angle/position");
                }
                Check(hull.pose.position == original.position && hull.pose.angle == original.angle
                    && hull.pose.velocity == original.velocity && hull.pose.angularVelocity == original.angularVelocity,
                    "drawing the guard in either frame never mutates its absolute pose");

                string path = Path.GetFullPath("Source/obj/RailgunRegression/guard-center.xml");
                IntVec3 root = shield.parent.Position;
                SpacePose savedPose = hull.pose;
                Scribe.saver.InitSaving(path, "guard");
                Scribe_Values.Look(ref root, "position");
                savedPose.ExposeData(); shield.PostExposeData();
                Scribe.saver.FinalizeSaving();
                CompSpaceShield loaded = DualShield();
                SpacePose loadedPose = default;
                IntVec3 loadedRoot = default;
                Scribe.loader.InitLoading(path);
                Scribe_Values.Look(ref loadedRoot, "position");
                loadedPose.ExposeData(); loaded.PostExposeData();
                Scribe.loader.FinalizeLoading();
                Set(loaded.parent, "positionInt", loadedRoot);
                Check(GuardCenter(loaded) == center && loaded.groundRadius == shield.groundRadius
                    && loadedPose.position == original.position && loadedPose.angle == original.angle,
                    "Scribe restores shield geometry and absolute pose; derived center needs no saved cache");
            }
        }
        finally { shieldDef.size = previousSize; }
    }
}
