using System;
using System.Collections.Generic;
using System.Linq;
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
            int clearance = CombatRangeClearance;
            var settings = DeltaVMod.CurrentSettings;
            var initial = CoursePose(home.pose, home.map.Size, ShipPocketMapFactory.CompactSize(platform.size),
                clearance, Mathf.Max(settings.poiDeleteRadius, settings.poiActiveRadius + 1f), -260f, 0);
            var range = SpawnCombatRange(platform, target, initial, Run.mentors, withPlayerColonist: false);
            Run.rangeConsole = range.console;
            Run.rangeTarget = range.targetConsole;
            Run.rangePort = range.port;
        }

        private static int CombatRangeClearance => Mathf.CeilToInt(DeltaVMod.CurrentSettings.dockingCancelDistance)
            + DeltaVMod.CurrentSettings.worksiteMargin + ShipPocketMapFactory.StructureMargin;

        internal static SpacePose CombatRangeTargetPose(SpacePose platform) =>
            SpaceTransform.RigidPointPose(platform, new Vector2(72f, 0f), 0f);

        internal (SpaceObject platform, Thing console, Thing targetConsole, Building_DockingPort port) SpawnCombatRange(
            MapPrefabDocument platform, MapPrefabDocument target, SpacePose initial, Faction faction, bool withPlayerColonist)
        {
            if (platform.thingEntries.Any(e => e.def.category == ThingCategory.Pawn
                || e.def.HasComp(typeof(CompPrefabPawnSpawner))))
                throw new InvalidOperationException("The shared weapon platform prefab must be uncrewed.");

            Map rangeMap = null, targetMap = null;
            try
            {
                rangeMap = CreateCourseMap(ShipPocketMapFactory.CompactSize(platform.size), initial);
                SpaceObject frame = universe.FrameOf(rangeMap);
                frame.contentTypeId = "WeaponPlatform";
                targetMap = CreateCourseMap(ShipPocketMapFactory.CompactSize(target.size), CombatRangeTargetPose(initial));
                SpaceObject targetFrame = universe.FrameOf(targetMap);
                int clearance = CombatRangeClearance;
                var rangeBounds = CellRect.WholeMap(rangeMap).ExpandedBy(clearance);
                var targetBounds = CellRect.WholeMap(targetMap).ExpandedBy(clearance);
                for (int attempt = 0; ; attempt++)
                {
                    if (attempt >= 128) throw new InvalidOperationException("No clear firing range outside gravity.");
                    frame.pose = SpaceTransform.RigidPointPose(initial, new Vector2(attempt * 128f, 0f), 0f);
                    targetFrame.pose = CombatRangeTargetPose(frame.pose);
                    if (!universe.objects.Any(g => g != frame && g != targetFrame && g.HasGravityRegion
                        && (OrbitalWorkService.IntersectsGravity(frame, rangeBounds, g)
                            || OrbitalWorkService.IntersectsGravity(targetFrame, targetBounds, g)))) break;
                }

                var facilities = SpawnCoursePrefab(platform, rangeMap, CoursePrefabOrigin, "DeltaV_Range_Platform", faction);
                Thing rangeConsole = facilities.Single(t => t.TryGetComp<CompDeltaVPilotConsole>() != null);
                var console = rangeConsole.TryGetComp<CompDeltaVPilotConsole>();
                console.isPublicFacility = true;
                console.fireControlMode = ShipFireControlMode.Disabled;
                console.SetWeaponGroupMask(SpaceWeaponSalvoSet.Main, 1);
                console.SetWeaponGroupMask(SpaceWeaponSalvoSet.Secondary, 1);
                universe.Controls.ReconcileOwner(console.shipId);
                var briefing = facilities.Select(t => t.TryGetComp<CompTutorialRangeBriefing>()).Single(c => c != null);
                briefing.entranceDoor = facilities.OfType<Building_HackableDoor>().Single();

                var targets = SpawnCoursePrefab(target, targetMap, CoursePrefabOrigin, "DeltaV_Range_Target", faction);
                var shields = targets.Select(t => t.TryGetComp<CompSpaceShield>()).Where(c => c != null).ToList();
                Thing targetConsole = targets.Single(t => t.TryGetComp<CompDeltaVPilotConsole>() != null);
                targetConsole.TryGetComp<CompDeltaVPilotConsole>().fireControlMode = ShipFireControlMode.Disabled;
                var terminals = facilities.Select(t => t.TryGetComp<CompTutorialShieldTerminal>()).Where(c => c != null).ToList();
                BindCombatRangeTerminals(terminals, targetFrame, shields);

                if (withPlayerColonist)
                {
                    IntVec3 cell = briefing.parent.InteractionCell;
                    if (!cell.Standable(rangeMap)) throw new InvalidOperationException("The range entrance has no crew spawn cell.");
                    Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer));
                    SpacePawnEquipment.EnsureVacuumSuit(pawn);
                    if (GenSpawn.Spawn(pawn, cell, rangeMap, WipeMode.Vanish) != pawn || !pawn.Spawned)
                        throw new InvalidOperationException("Could not place the weapon test colonist.");
                }
                return (frame, rangeConsole, targetConsole, facilities.OfType<Building_DockingPort>().Single());
            }
            catch
            {
                ShipTestScene.QueueFailedSceneCleanup(universe,
                    new[] { rangeMap, targetMap }.Where(map => map != null).Select(universe.FrameOf).Where(frame => frame != null).ToArray());
                throw;
            }
        }

        internal static void BindCombatRangeTerminals(IReadOnlyList<CompTutorialShieldTerminal> terminals,
            SpaceObject targetFrame, IReadOnlyList<CompSpaceShield> shields)
        {
            if (terminals.Count != 11 || shields.Count != 3)
                throw new InvalidOperationException("The range requires eleven shield terminals and distinct angled, straight and auxiliary shield generators.");
            // The northern station covers the rail lane. The angled station is authored with nonzero tilt.
            var main = shields.OrderByDescending(shield => shield.parent.Position.z).First();
            var angled = shields.Single(shield => shield.angleDeg != 0f);
            if (main == angled)
                throw new InvalidOperationException("The range requires separate straight and angled shield generators.");
            foreach (var terminal in terminals)
            {
                terminal.targetObjectId = targetFrame.objectId;
                terminal.shieldThingId = (terminal.UsesAngledShield ? angled : main).parent.thingIDNumber;
            }
            terminals.Single(t => t.Preset == TutorialShieldPreset.Unshielded).ApplyTo(shields);
        }
    }
}
