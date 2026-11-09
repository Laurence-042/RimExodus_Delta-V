using System.Linq;
using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed partial class DeltaVTutorialService
    {
        private void CreateCombatRange(SpaceObject home)
        {
            var platform = MapPrefab.LoadRelative("Tutorial/WeaponRange.xml");
            var target = MapPrefab.LoadRelative("Tutorial/ShieldTarget.xml");
            Map rangeMap = CreateRemoteCourseMap(ShipPocketMapFactory.CompactSize(platform.size), home, -260f);
            var facilities = SpawnCoursePrefab(platform, rangeMap, new IntVec3(12, 0, 12), "DeltaV_Range_Platform", Run.mentors);
            Run.rangeConsole = facilities.Single(t => t.TryGetComp<CompDeltaVPilotConsole>() != null);
            Run.rangePort = facilities.OfType<Building_DockingPort>().Single();
            var console = Run.rangeConsole.TryGetComp<CompDeltaVPilotConsole>();
            console.isPublicFacility = true;
            console.fireControlMode = ShipFireControlMode.Disabled;
            console.SetWeaponGroupMask(SpaceWeaponSalvoSet.Main, 1);
            console.SetWeaponGroupMask(SpaceWeaponSalvoSet.Secondary, 1);
            universe.Controls.ReconcileOwner(console.shipId);
            facilities.Single(t => t.TryGetComp<CompTutorialRangeBriefing>() != null)
                .TryGetComp<CompTutorialRangeBriefing>().entranceDoor = facilities.OfType<Building_HackableDoor>().Single();
            // The firing line points away from the colony. Target and platform remain separate rigid bodies.
            SpaceObject frame = universe.FrameOf(rangeMap);
            frame.contentTypeId = "WeaponPlatform";
            SpacePose targetPose = SpaceTransform.RigidPointPose(frame.pose, new Vector2(120f, 0f), 0f);
            Map targetMap = CreateCourseMap(ShipPocketMapFactory.CompactSize(target.size), targetPose);
            SpaceObject targetFrame = universe.FrameOf(targetMap);
            int clearance = Mathf.CeilToInt(DeltaVMod.CurrentSettings.dockingCancelDistance)
                + DeltaVMod.CurrentSettings.worksiteMargin + ShipPocketMapFactory.StructureMargin;
            SpacePose initialPose = frame.pose;
            for (int attempt = 0; ; attempt++)
            {
                if (attempt >= 128) throw new InvalidOperationException("No clear tutorial firing range outside colony gravity.");
                frame.pose = SpaceTransform.RigidPointPose(initialPose, new Vector2(attempt * 128f, 0f), 0f);
                targetFrame.pose = SpaceTransform.RigidPointPose(frame.pose, new Vector2(120f, 0f), 0f);
                if (new[] { frame, targetFrame }.All(f => !universe.objects.Any(g => g != f && g.HasGravityRegion
                    && OrbitalWorkService.IntersectsGravity(f, CellRect.WholeMap(f.map).ExpandedBy(clearance), g)))) break;
            }
            var targets = SpawnCoursePrefab(target, targetMap, new IntVec3(12, 0, 12), "DeltaV_Range_Target", Run.mentors);
            var shields = targets.Select(t => t.TryGetComp<CompSpaceShield>()).Where(s => s != null)
                .OrderByDescending(s => s.groundRadius).ToList();
            Run.rangeTarget = targets.Single(t => t.TryGetComp<CompDeltaVPilotConsole>() != null);
            Run.rangeTarget.TryGetComp<CompDeltaVPilotConsole>().fireControlMode = ShipFireControlMode.Disabled;
            foreach (var terminal in facilities.Select(t => t.TryGetComp<CompTutorialShieldTerminal>()).Where(t => t != null))
            {
                terminal.targetObjectId = universe.FrameOf(targetMap).objectId;
                terminal.primaryShieldId = shields[0].parent.thingIDNumber;
            }
            CompTutorialShieldTerminal.ApplyPreset(shields, TutorialShieldPreset.Unshielded);
        }
    }
}
