using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DeltaV
{
    internal sealed partial class DeltaVTutorialService
    {
        private const int FlightTicks = 30;
        private const int SalvageSteel = 75;
        private ShipBase TrainingShip => ShipRegistry.ShipByIdentity(new DriveSubject(0, Run.trainingShipId));

        private IEnumerable<Thing> FlightRoles()
        {
            if (Run == null) yield break;
            yield return Run.trainingConsole;
            yield return Run.cargoShelf;
            yield return Run.trainingPort;
            yield return Run.salvageShelf;
            yield return Run.yardPort;
            yield return Run.giftConsole;
            yield return Run.giftEngine;
            yield return Run.rangeConsole;
            yield return Run.rangeTarget;
        }

        private bool CheckFlightAssets()
        {
            if (Run.stage < TutorialStage.Design || Run.stage > TutorialStage.Visit) return true;
            if (Run.trainingConsole?.Spawned != true || Run.cargoShelf?.Spawned != true
                || Run.trainingPort?.Spawned != true || Run.yardPort?.Spawned != true
                || Run.giftConsole?.Spawned != true || Run.giftEngine?.Spawned != true
                || (!Run.recoveryDocked && Run.salvageShelf?.Spawned != true))
            { Fail("DeltaV_Tutorial_FlightAssetLost"); return false; }
            if (!TrainingShip.PlayerMayOperate)
            { Fail("DeltaV_Tutorial_TrainingAccessLost"); return false; }
            return true;
        }

        private void AdvanceFlight(TutorialStage stage)
        {
            Run.stage = stage;
            universe.QueueFrameAction(ShowBriefing);
        }

        private bool ReceiveFlight(string tag)
        {
            if (Run.stage == TutorialStage.Briefing && tag == Signal(Run.quest, "StartFlight"))
            {
                Run.stage = TutorialStage.PreparingFlight;
                Resume();
                return true;
            }
            if (Run.stage < TutorialStage.Design || Run.stage > TutorialStage.Visit) return false;
            if (!CheckFlightAssets()) return true;
            if (Run.stage == TutorialStage.Design && tag == Signal(Run.quest, "StartPractice"))
            {
                AdvanceFlight(TutorialStage.Flight);
                return true;
            }
            if (Run.stage == TutorialStage.Flight && tag == Signal(Run.quest, "CheckFlight"))
            {
                ShipBase ship = TrainingShip;
                SpaceObject frame = ship.IsValid ? universe.FrameOf(ship.Map) : null;
                if (Run.forwardTicks < FlightTicks || Run.lateralTicks < FlightTicks || Run.turnTicks < FlightTicks
                    || frame == null || frame.pose.velocity.magnitude > .15f || Mathf.Abs(frame.pose.angularVelocity) > .01f
                    || !ColonistAboard(ship))
                    Messages.Message("DeltaV_Tutorial_FlightProgress".Translate(Run.forwardTicks, Run.lateralTicks, Run.turnTicks),
                        Run.trainingConsole, MessageTypeDefOf.RejectInput, false);
                else AdvanceFlight(TutorialStage.Range);
                return true;
            }
            if (Run.stage == TutorialStage.Range && tag == Signal(Run.quest, "ContinueAfterRange"))
            {
                AdvanceFlight(TutorialStage.Salvage);
                return true;
            }
            if (Run.stage == TutorialStage.Salvage && tag == Signal(Run.quest, "CheckSalvage"))
            {
                ShipBase ship = TrainingShip;
                bool shelfAboard = ship.IsValid && ShipRegistry.ShipOf(Run.cargoShelf).Identity == ship.Identity;
                int steel = shelfAboard ? Run.cargoShelf.AllSlotCells()
                    .SelectMany(c => c.GetThingList(Run.cargoShelf.Map)).Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount) : 0;
                if (!Run.recoveryDocked || steel < SalvageSteel || !ColonistAboard(ship))
                    Messages.Message("DeltaV_Tutorial_SalvageProgress".Translate(steel), Run.cargoShelf, MessageTypeDefOf.RejectInput, false);
                else AdvanceFlight(TutorialStage.SoftDock);
                return true;
            }
            if (Run.stage == TutorialStage.SoftDock && tag == Signal(Run.quest, "CheckSoftDock"))
            {
                CompSoftDocking port = Run.trainingPort.GetComp<CompSoftDocking>();
                if (port?.Connected != true || port.Peer != Run.yardPort
                    || ShipRegistry.ShipOf(Run.trainingPort).Identity != new DriveSubject(0, Run.trainingShipId))
                    Messages.Message("DeltaV_Tutorial_NeedSoftDock".Translate(), Run.yardPort, MessageTypeDefOf.RejectInput, false);
                else AdvanceFlight(TutorialStage.Visit);
                return true;
            }
            if (Run.stage == TutorialStage.Visit && tag == Signal(Run.quest, "AcceptShip"))
            {
                ShipBase gift = ShipRegistry.ShipOf(Run.giftConsole);
                bool visited = Run.colonists.Any(p => p?.Spawned == true && !p.Dead && !p.Downed
                    && p.Map == Run.giftEngine.Map && p.Position.InHorDistOf(Run.giftEngine.Position, 5f));
                if (!visited || !gift.IsValid || Run.giftConsole.Faction != Faction.OfPlayer
                    || ShipRegistry.ShipOf(Run.giftEngine).Identity != gift.Identity)
                    Messages.Message("DeltaV_Tutorial_NeedVisit".Translate(), Run.giftEngine, MessageTypeDefOf.RejectInput, false);
                else Find.SignalManager.SendSignal(new Signal(Signal(Run.quest, "Completed")));
                return true;
            }
            return false;
        }

        private bool ColonistAboard(ShipBase ship) => ship?.IsValid == true && Run.colonists.Any(p =>
            p?.Spawned == true && !p.Dead && !p.Downed && p.Map == ship.Map && ship.HullCells.Contains(p.Position));

        // Called only after real player-controlled propulsion and integration, once per game tick.
        internal void ObserveFlight(DriveSubject subject, ShipPhysics.Command command, Vector2 force, float torque,
            float frameAngle, bool assistForward, bool assistLateral, bool assistTurn)
        {
            if (!Active || Run.stage != TutorialStage.Flight || subject != new DriveSubject(0, Run.trainingShipId)) return;
            ShipBase ship = TrainingShip;
            if (!ColonistAboard(ship) || ship.State != ShipDerivedState.FreeFlight) return;
            Vector2 input = SpaceTransform.RotateInverse(new Vector2(command.fx, command.fz), frameAngle);
            RecordFlightInput(Run, new ShipPhysics.Command { fx = assistForward ? 0f : input.x,
                fz = assistLateral ? 0f : input.y, torque = assistTurn ? 0f : command.torque },
                SpaceTransform.RotateInverse(force, frameAngle), torque);
        }

        internal static void RecordFlightInput(TutorialRunState run, ShipPhysics.Command command, Vector2 force, float torque)
        {
            if (Mathf.Abs(command.fx) > .1f && command.fx * force.x > .01f) run.forwardTicks = Math.Min(FlightTicks, run.forwardTicks + 1);
            if (Mathf.Abs(command.fz) > .1f && command.fz * force.y > .01f) run.lateralTicks = Math.Min(FlightTicks, run.lateralTicks + 1);
            if (Mathf.Abs(command.torque) > .1f && command.torque * torque > .01f) run.turnTicks = Math.Min(FlightTicks, run.turnTicks + 1);
        }

        // Capture the exact target before either transfer path can retire its map.
        internal Thing RecoveryWitness(DriveSubject subject, Map target) => Active && Run.stage == TutorialStage.Salvage
            && subject == new DriveSubject(0, Run.trainingShipId) && Run.salvageShelf?.Spawned == true
            && Run.salvageShelf.Map == target ? Run.salvageShelf : null;

        internal void ConfirmRecoveryDock(DriveSubject subject, Thing witness)
        {
            if (!Active || Run.stage != TutorialStage.Salvage || witness == null || witness != Run.salvageShelf
                || subject != new DriveSubject(0, Run.trainingShipId)) return;
            ShipBase ship = TrainingShip;
            if (ship.IsValid && witness.Spawned && witness.Map == ship.Map) Run.recoveryDocked = true;
        }

        private void CreateTrainingBerth()
        {
            try
            {
                var training = MapPrefab.LoadRelative("TrainingShip.xml");
                if (Run.berthDoor?.Spawned != true || Run.berthDoor.Map != Run.station)
                    throw new InvalidOperationException("Tutorial boarding berth is missing.");
                SpaceObject station = universe.FrameOf(Run.station);
                Map trainingMap = CreateCourseMap(ShipPocketMapFactory.CompactSize(training.size), station.pose);
                var trainThings = SpawnCoursePrefab(training, trainingMap, new IntVec3(12, 0, 12), "DeltaV_Tutorial_TrainingShip", Run.mentors);
                Run.trainingConsole = trainThings.Single(t => t.TryGetComp<CompDeltaVPilotConsole>() != null);
                Run.trainingConsole.TryGetComp<CompDeltaVPilotConsole>().isPublicFacility = true;
                Run.trainingShipId = Run.trainingConsole.TryGetComp<CompDeltaVPilotConsole>().shipId;
                universe.Controls.ReconcileOwner(Run.trainingShipId);
                Run.trainingPort = trainThings.OfType<Building_DockingPort>().Single();
                Run.cargoShelf = trainThings.OfType<Building_Storage>().Single();
                Run.cargoShelf.label = "DeltaV_Tutorial_CargoShelf".Translate();
                ConfigureCargo(Run.cargoShelf, StoragePriority.Critical);
                HardDockForCourse(Run.trainingPort, Run.berthDoor);
                Run.comms = trainThings.OfType<Building_CommsConsole>().Single();
                // Ordinary docking completion runs first; wake only after the berth is usable.
                Resume();
            }
            catch (Exception ex)
            {
                Log.Error("[DeltaV] Tutorial training berth failed: " + ex);
                Fail("DeltaV_Tutorial_FlightSetupFailed");
            }
        }

        private void CreateFlightCourse()
        {
            if (!Active || Run.stage != TutorialStage.PreparingFlight) return;
            try
            {
                // Resolve every required document before mutating the player's station.
                var salvage = MapPrefab.LoadRelative("Salvage.xml");
                var yard = MapPrefab.LoadRelative("Shipyard.xml");
                var gift = MapPrefab.LoadRelative("PrototypeShip.xml");
                SpaceObject station = universe.FrameOf(Run.station);
                Map salvageMap = CreateRemoteCourseMap(ShipPocketMapFactory.CompactSize(salvage.size), station, 0f);
                var salvageThings = SpawnCoursePrefab(salvage, salvageMap, new IntVec3(12, 0, 12), "DeltaV_Tutorial_SalvageTarget");
                Run.salvageShelf = salvageThings.OfType<Building_Storage>().Single();
                ConfigureCargo(Run.salvageShelf, StoragePriority.Low);
                Map yardMap = CreateRemoteCourseMap(ShipPocketMapFactory.CompactSize(new IntVec2(yard.size.x + BerthGap + gift.size.x, Math.Max(yard.size.z, gift.size.z))), station, 180f);
                SpacePose yardPose = universe.FrameOf(yardMap).pose;
                var yardThings = SpawnCoursePrefab(yard, yardMap, new IntVec3(12, 0, 12), "DeltaV_Tutorial_Shipyard", Run.mentors);
                var crew = yardMap.mapPawns.SpawnedPawnsInFaction(Run.mentors).ToList();
                int expectedCrew = yard.thingEntries.Count(e => e.def.HasComp(typeof(CompPrefabPawnSpawner)));
                if (crew.Count != expectedCrew || expectedCrew == 0)
                    throw new InvalidOperationException($"Tutorial shipyard crew incomplete: {crew.Count}/{expectedCrew}.");
                // Persistent native duty allows food and rest without a visitor departure timer.
                LordMaker.MakeNewLord(Run.mentors, new LordJob_DefendPoint(new IntVec3(12 + yard.size.x / 2, 0, 12 + yard.size.z / 2),
                    wanderRadius: 50f, defendRadius: 60f, addFleeToil: false), yardMap, crew);
                Run.yardPort = yardThings.OfType<Building_DockingPort>().Single(p => p.Rotation == Rot4.West);
                Building_DockingPort giftBerth = yardThings.OfType<Building_DockingPort>().Single(p => p.Rotation == Rot4.East);
                Map giftMap = CreateCourseMap(ShipPocketMapFactory.CompactSize(gift.size), yardPose);
                var giftThings = SpawnCoursePrefab(gift, giftMap, new IntVec3(12, 0, 12), "DeltaV_Tutorial_PrototypeShip");
                Run.giftConsole = giftThings.Single(t => t.TryGetComp<CompDeltaVPilotConsole>() != null);
                Run.giftEngine = giftThings.OfType<Building_GravEngine>().Single();
                CreateCombatRange(station);
                Building_DockingPort giftPort = giftThings.OfType<Building_DockingPort>().Single(p => p.Rotation == Rot4.West);
                HardDockForCourse(giftPort, giftBerth);
                // Vanilla GravEngine has no power switch: no launch is ordered; normal player control remains available.
                ShipBase giftShip = ShipRegistry.ShipOf(Run.giftConsole), yardShip = ShipRegistry.ShipOf(Run.yardPort);
                if (Run.trainingShipId <= 0 || !giftShip.IsValid || !yardShip.IsValid || giftShip.Identity == yardShip.Identity)
                    throw new InvalidOperationException("Tutorial ships did not retain independent structure identities.");
                // Ordinary gift-berth completion runs before this check and creates the passage.
                var expected = Run;
                universe.QueueFrameAction(() =>
                {
                    if (!Active || Run != expected || Run.stage != TutorialStage.PreparingFlight) return;
                    if (!BerthConnected(giftPort, giftBerth))
                    { Fail("DeltaV_Tutorial_BerthUnavailable"); return; }
                    AdvanceFlight(TutorialStage.Design);
                });
            }
            catch (Exception ex)
            {
                Log.Error("[DeltaV] Tutorial flight setup failed: " + ex);
                Fail("DeltaV_Tutorial_FlightSetupFailed");
            }
        }

        internal const int BerthGap = 3;

        internal static IntVec3 BerthLandingRoot(IntVec3 sourceRoot, IntVec3 shipPort,
            IntVec3 hostPort, Rot4 hostFacing) => hostPort + hostFacing.FacingCell * (BerthGap + 1) - (shipPort - sourceRoot);

        private void HardDockForCourse(Building_DockingPort shipPort, Building_Door hostDoor)
        {
            ShipBase ship = ShipRegistry.ShipOf(shipPort);
            SpaceObject host = universe.FrameOf(hostDoor.Map);
            if (!ship.IsValid || host == null || ship.Map == host.map
                || (hostDoor is Building_DockingPort hostPort && shipPort.Rotation != hostPort.Rotation.Opposite))
                throw new InvalidOperationException("Tutorial berth needs independent ships and opposing ports.");
            PilotTarget target = PilotTarget.Of(ship);
            Gravship preview = GravshipInterop.BuildPreview(target)
                ?? throw new InvalidOperationException("Tutorial berth has no valid ship preview.");
            // Vanilla doors rotate to their neighbouring walls when drawn; only the ship port defines the passage direction.
            IntVec3 root = BerthLandingRoot(preview.originalPosition, shipPort.Position, hostDoor.Position, shipPort.Rotation.Opposite);
            var placement = new LandingApproachController.DockingPlan(target, host, preview.originalPosition, root, Rot4.North);
            if (!LandingApproachController.LandingSiteStillValid(placement) || !universe.Worksites.Dock(target, placement))
                throw new InvalidOperationException("Tutorial berth could not complete normal hard docking.");
        }

        internal static bool BerthConnected(Building_DockingPort shipPort, Building_Door hostDoor) =>
            shipPort?.Spawned == true && hostDoor?.Spawned == true && shipPort.Map == hostDoor.Map
            && (shipPort.ChannelExtended || (hostDoor is Building_DockingPort hostPort && hostPort.ChannelExtended))
            && hostDoor.Position == shipPort.Position + shipPort.Rotation.FacingCell * (BerthGap + 1);

        private Map CreateRemoteCourseMap(IntVec3 size, SpaceObject home, float lateralOffset)
        {
            Map map = CreateCourseMap(size, home.pose);
            SpaceObject frame = universe.FrameOf(map);
            int clearance = Mathf.CeilToInt(DeltaVMod.CurrentSettings.dockingCancelDistance)
                + DeltaVMod.CurrentSettings.worksiteMargin + ShipPocketMapFactory.StructureMargin;
            var footprint = CellRect.WholeMap(map).ExpandedBy(clearance);
            // Home's real map bounds, plus room for approach and a shared worksite. No home flag is changed.
            for (int attempt = 0; attempt < 128; attempt++)
            {
                frame.pose = CoursePose(home.pose, home.map.Size, size, clearance, lateralOffset, attempt);
                if (!universe.objects.Any(g => g != frame && g.HasGravityRegion
                    && OrbitalWorkService.IntersectsGravity(frame, footprint, g))) return map;
            }
            throw new InvalidOperationException("No tutorial course location outside existing gravity regions.");
        }

        internal static SpacePose CoursePose(SpacePose home, IntVec3 homeSize, IntVec3 size,
            int clearance, float lateralOffset, int attempt)
        {
            var pose = SpaceTransform.RigidPointPose(home,
                new Vector2(homeSize.x * .5f + size.x * .5f + clearance + 64f + attempt * 128f, lateralOffset), 0f);
            pose.velocity = Vector2.zero;
            pose.angularVelocity = 0f;
            return pose;
        }

        private Map CreateCourseMap(IntVec3 size, SpacePose pose)
        {
            Map map = ShipPocketMapFactory.Generate(size, universe)
                ?? throw new InvalidOperationException("Could not generate tutorial map.");
            universe.RegisterObject(new MapSpaceObject(universe.AllocateOwnerId(), map, pose));
            return map;
        }

        private List<Thing> SpawnCoursePrefab(MapPrefabDocument doc, Map map, IntVec3 root, string label, Faction faction = null)
        {
            map.regionAndRoomUpdater.Enabled = true;
            int ownerId = universe.AllocateOwnerId();
            var things = MapPrefab.Spawn(doc, map, root - PrefabUtility.GetRoot(IntVec3.Zero, doc.size, Rot4.North),
                faction ?? Faction.OfPlayer, ownerId, Rot4.North, universe);
            // Successful markers replace themselves with pawns outside the returned Thing list.
            int persistentCount = doc.thingEntries.Count(e => !e.def.comps.Any(c => typeof(CompPrefabMarker).IsAssignableFrom(c.compClass)));
            if (things.Count != persistentCount || things.Any(t => t.TryGetComp<CompPrefabMarker>() != null))
                throw new InvalidOperationException("Incomplete tutorial prefab: " + doc.name);
            foreach (Thing thing in things)
                if (thing.TryGetComp<CompDeltaVPilotConsole>() is CompDeltaVPilotConsole console
                    && (console.shipId != ownerId || !console.isMain))
                    throw new InvalidOperationException($"Tutorial prefab {doc.name}: expected primary owner {ownerId}, got {console.shipId}, main={console.isMain}.");
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            foreach (Room room in things.Where(t => t.def.defName == "OxygenPump").Select(t => t.GetRoom()).Distinct())
            {
                if (room == null || room.ExposedToSpace || room.OpenRoofCount != 0)
                    throw new InvalidOperationException("Unsealed tutorial cabin: " + doc.name);
                room.Temperature = 21f;
                room.Vacuum = 0f;
            }
            foreach (var foundation in doc.foundations)
            {
                IntVec3 cell = root + foundation.pos;
                if ((faction ?? Faction.OfPlayer) == Faction.OfPlayer) map.areaManager.Home[cell] = true;
                map.fogGrid.Unfog(cell);
            }
            foreach (Thing console in things.Where(t => t.TryGetComp<CompDeltaVPilotConsole>() != null))
                universe.NoteStructureName(console.TryGetComp<CompDeltaVPilotConsole>().shipId, label.Translate());
            if (!things.Any(t => t.TryGetComp<CompDeltaVPilotConsole>() != null))
                universe.FrameOf(map).ambientLabel = label.Translate();
            return things;
        }

        private static void ConfigureCargo(Building_Storage shelf, StoragePriority priority)
        {
            StorageSettings settings = shelf.GetStoreSettings();
            settings.filter.SetDisallowAll();
            settings.filter.SetAllow(ThingDefOf.Steel, true);
            settings.Priority = priority;
        }
    }
}
