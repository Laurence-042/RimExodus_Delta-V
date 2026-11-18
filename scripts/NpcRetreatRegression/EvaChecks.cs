using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

internal static partial class Program
{
    sealed class MountSave : IExposable
    {
        internal CompVirtualWeapons comp;
        public void ExposeData() => comp.PostExposeData();
    }

    static void CheckEva(UniverseManager universe, Faction faction)
    {
        var xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_Eva.xml");
        XElement suit = xml.Root.Elements("ThingDef").Single(x => (string)x.Element("defName") == "DeltaV_EvaSuit");
        Check((float)suit.Element("statBases").Element("Mass") == 20
            && (int)suit.Element("statBases").Element("MaxHitPoints") == 150
            && (string)suit.Element("size") == "(1,1)", "EVA Def supplies 1x1, 20 kg and 150 HP");
        Check(suit.Descendants("virtualThrusters").Elements().Count() == 4
            && suit.Descendants("mounts").Elements().Count() == 3, "EVA Def has four RCS and three separate mounts");
        ThingDefOf.PilotConsole = TestBuildingDef("evaConsole");
        faction.def = new FactionDef { isPlayer = true };
        var factions = (FactionManager)Raw(typeof(FactionManager));
        Set(factions, "ofPlayer", faction); Set(Find.World, "factionManager", factions);
        Current.Game.currentMapIndex = -1;
        var map = TestMap(58001);
        Vector2 ReadPair(XElement node)
        {
            string[] parts = node.Value.Trim('(', ')').Split(',');
            return new Vector2(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        var eva = new CompEvaSuit { props = new CompProperties_EvaSuit {
            virtualThrusters = suit.Descendants("virtualThrusters").Elements("li")
                .Select(x => new ShuttleThrusterConfig {
                    position = ReadPair(x.Element("position")), force = ReadPair(x.Element("force")) }).ToList()
        } };
        AddTestHull(map, faction, 58002, eva);
        var body = new MapSpaceObject(58001, map, new SpacePose(Vector3.forward, .31f));
        universe.RegisterObject(body);
        eva.combat.weaponsArmed = true;
        eva.combat.fireControlMode = ShipFireControlMode.Manual;
        eva.combat.weaponGroupsSecondary = 3;
        ShipBase ship = eva.Vessel;
        CheckEvaThrusters(ship);
        Check(ship.CanDriveIn(body) && !ship.AllowsAngularMotion && !ship.CanBoard
            && !ship.RequiresHeadingAlignment && !ship.RequiresAngularVelocityMatch,
            "EVA capabilities relax docking orientation and forbid angular motion / intrusion");
        Check(ship.DefaultFireControlMode == ShipFireControlMode.Manual
            && !ship.SupportsFireControlMode(ShipFireControlMode.Automatic)
            && ship.SupportsFireControlMode(ShipFireControlMode.Disabled), "EVA exposes only manual / disabled fire control");
        eva.combat.fireControlMode = ShipFireControlMode.Automatic;
        Check(ship.EffectiveFireControlMode == ShipFireControlMode.Manual, "unsupported stored mode resolves through ship capability");
        eva.combat.fireControlMode = ShipFireControlMode.Manual;

        var properties = new CompProperties_VirtualWeapons();
        foreach (XElement mount in xml.Root.Elements("DeltaV.VirtualWeaponDef"))
        {
            XElement node = mount.Element("comps").Elements().First();
            var weapon = new CompProperties_SpaceWeapon { manualOnly = true, baseSpreadDeg = 0,
                range = (float)node.Element("range"), cooldownTicks = (int)node.Element("cooldownTicks"),
                defaultGroupNumber = (int)node.Element("defaultGroupNumber"),
                defaultSalvoSet = Enum.Parse<SpaceWeaponSalvoSet>((string)node.Element("defaultSalvoSet")),
                tractor = (bool?)node.Element("tractor") ?? false };
            var def = new VirtualWeaponDef { defName = (string)mount.Element("defName"), comps = new List<CompProperties> { weapon } };
            if (weapon.tractor) def.comps.Add(new CompProperties_CargoTractor());
            properties.mounts.Add(new VirtualWeaponSlot { weapon = def });
        }
        var embedded = new CompVirtualWeapons { parent = eva.parent, props = properties };
        eva.parent.AllComps.Add(embedded);
        embedded.PostSpawnSetup(true);
        CompSpaceWeapon[] weapons = embedded.Weapons.ToArray();
        var physical = new CompSpaceWeapon { parent = eva.parent, props = new CompProperties_SpaceWeapon() };
        eva.parent.AllComps.Add(physical);
        Check(CompSpaceWeapon.On(eva.parent).Count() == 4 && weapons.Select(w => w.Identity).Append(physical.Identity).Distinct().Count() == 4,
            "physical core and three stacked mounts expose distinct stable weapon identities");
        Check(weapons[0].salvoSet == SpaceWeaponSalvoSet.Main && weapons[0].groupNumber == 1
            && weapons[1].salvoSet == SpaceWeaponSalvoSet.Secondary && weapons[1].groupNumber == 1
            && weapons[2].groupNumber == 2 && weapons.All(w => !w.AllowsPointDefense), "tool defaults and manual-only acquisition are independent");
        Check(weapons[0].Component<CompSpaceTractor>() == null && weapons[2].Component<CompCargoTractor>() != null,
            "mount capabilities never fall through to a sibling tool");
        var first = new Command_BatchSetting<CompSpaceWeapon>("setting", weapons[0], _ => { });
        var second = new Command_BatchSetting<CompSpaceWeapon>("setting", weapons[1], _ => { });
        Check(!first.GroupsWith(second), "settings on different slots do not merge into one UI action");
        weapons[0].cooldownUntilTick = 130; weapons[1].cooldownUntilTick = 110; weapons[2].cooldownUntilTick = 101;
        weapons[1].groupNumber = 6;
        var loaded = new MountSave { comp = new CompVirtualWeapons { parent = eva.parent, props = properties } };
        string path = Path.GetFullPath("Source/obj/eva-mounts.xml");
        try
        {
            Scribe.saver.InitSaving(path, "mounts"); embedded.PostExposeData(); Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path); loaded.ExposeData();
            Scribe.loader.initer.RegisterForPostLoadInit(loaded); Scribe.loader.FinalizeLoading();
            CompSpaceWeapon[] restored = loaded.comp.Weapons.ToArray();
            Check(restored.Select(w => w.cooldownUntilTick).SequenceEqual(new[] { 130, 110, 101 })
                && restored[1].groupNumber == 6 && restored[2].groupNumber == 2,
                "real Scribe round-trip keeps each virtual cooldown and group separate");
            Check(restored.Select(w => w.Identity).SequenceEqual(weapons.Select(w => w.Identity)),
                "rehydrated mounts retain host-local identities");
        }
        finally { Scribe.ForceStop(); }

        foreach (int margin in new[] { 0, 1, 12 })
            Check(ShipPocketMapFactory.CompactSize(new IntVec2(1, 1), margin) == new IntVec3(1 + 2 * margin, 1, 1 + 2 * margin),
                "explicit margin produces expected map size: " + margin);
        var gravship = (Gravship)Activator.CreateInstance(typeof(Gravship), true);
        Set(gravship, "bounds", new CellRect(0, 0, 1, 1));
        var future = (Thing)Raw(typeof(Thing)); future.def = TestBuildingDef("rotatingVehicle"); future.def.size = new IntVec2(2, 3);
        ((Dictionary<Thing, PositionData>)AccessTools.Field(typeof(Gravship), "things").GetValue(gravship))
            .Add(future, new PositionData(IntVec3.Zero, Rot4.North));
        CellRect bounds = ShipPocketMapFactory.PlacedBounds(gravship, true);
        foreach (int margin in new[] { 0, 1, 12 })
        {
            IntVec3 size = ShipPocketMapFactory.CompactSize(bounds.Size, margin);
            IntVec3 root = new IntVec3(margin - bounds.minX, 0, margin - bounds.minZ);
            Check(Rot4.AllRotations.All(r => GenAdj.OccupiedRect(root, r, future.def.size)
                .All(c => c.x >= margin && c.z >= margin && c.x < size.x - margin && c.z < size.z - margin)),
                "rotational footprint union and placement use the same margin: " + margin);
        }
        var groups = (eva.combat.weaponGroupsMain, eva.combat.weaponGroupsSecondary);
        universe.Controls.ToggleFireControl(ship);
        Check(ship.EffectiveFireControlMode == ShipFireControlMode.Disabled && weapons.All(w =>
            !universe.Weapons.ApplyControl(w, body, new WeaponControlCommand(new Vector2(3, 3), true))),
            "shared fire-control toggle disables every mounted tool at the execution boundary");
        universe.Controls.ToggleFireControl(ship);
        Check(ship.EffectiveFireControlMode == ShipFireControlMode.Manual
            && groups == (eva.combat.weaponGroupsMain, eva.combat.weaponGroupsSecondary), "EVA toggle skips automatic and preserves groups");
        CheckEvaGeometry(ship, body);
        CheckEvaEffects(universe);
        CheckEvaCargo(universe, eva, weapons[2]);
        CheckDepartureAndBeamFixes(universe, eva, weapons[0]);
    }

    static void CheckEvaThrusters(ShipBase ship)
    {
        Rot4 original = ship.Anchor.Rotation;
        try
        {
            foreach (Rot4 rotation in Rot4.AllRotations)
            {
                ship.Anchor.Rotation = rotation;
                var report = new ShipPhysics.MassReport { mass = 100, inertia = 30,
                    comLocal = ship.HullCenter - SpaceTransform.ShipLocalOrigin(ship.Map),
                    allowsAngularMotion = ship.AllowsAngularMotion };
                report.thrusters.AddRange(ship.BuildThrusters());
                Check(report.thrusters.All(t => {
                    Vector2 offset = t.localPos - report.comLocal;
                    return Math.Abs(offset.x) > .2f && Math.Abs(offset.y) > .2f
                        && Vector2.Dot(offset.normalized, t.dir) < -.999f;
                }), "EVA X exhaust points outward from all four corners: " + rotation);
                foreach (Vector2 direction in new[] { Vector2.right, Vector2.left, Vector2.up, Vector2.down,
                    new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(-1, -1) })
                {
                    bool diagonal = direction.x != 0 && direction.y != 0;
                    Vector2 expected = direction * (diagonal ? 150f : 300f);
                    ShipPhysics.Allocate(report, new ShipPhysics.Command { fx = direction.x, fz = direction.y },
                        out Vector2 force, out float torque);
                    Check((force - expected).magnitude < .2f && Math.Abs(torque) < .001f
                        && report.thrusters.Count(t => t.throttle > .01f) == (diagonal ? 1 : 2),
                        "EVA X allocation supplies balanced axial / diagonal force: " + rotation + " / " + direction);
                    float braking = ShipPhysics.PreviewBrakingAcceleration(report, direction);
                    Check(Math.Abs(braking - expected.magnitude / report.mass) < .01f,
                        "EVA X braking matches opposite-direction authority: " + rotation + " / " + direction);
                }
                ShipPhysics.Allocate(report, new ShipPhysics.Command(), out Vector2 idleForce, out float idleTorque);
                Check(idleForce.sqrMagnitude == 0 && idleTorque == 0 && report.thrusters.All(t => t.throttle == 0),
                    "EVA X idle clears every exhaust throttle: " + rotation);
            }
        }
        finally { ship.Anchor.Rotation = original; }
    }

    static void CheckEvaGeometry(ShipBase ship, SpaceObject body)
    {
        var host = new SpacePose(Vector3.forward, 1.2f) { angularVelocity = .3f, velocity = new Vector2(2, 3) };
        SpacePose own = SpaceTransform.RigidPointPose(host, new Vector2(10, -4), -.7f);
        own.angularVelocity = 0;
        Check(LandingApproachController.MatchesMotion(own, host, ship)
            && !LandingApproachController.MatchesMotion(own, host), "EVA docking accepts unmatched heading/spin at the correct rigid-point velocity");
        own.velocity += Vector2.one;
        Check(!LandingApproachController.MatchesMotion(own, host, ship), "EVA docking still rejects unmatched linear velocity");
        var report = new ShipPhysics.MassReport { mass = 100, inertia = 30, allowsAngularMotion = false };
        bool drag = DeltaVMod.CurrentSettings.passiveDrag;
        DeltaVMod.CurrentSettings.passiveDrag = false;
        try
        {
            foreach (float torque in new[] { 0f, 900f, -900f })
            {
                body.pose.angularVelocity = .8f;
                float angle = body.pose.angle;
                ShipPhysics.Integrate(body, report, Vector2.zero, torque, 1f / 60, false, false, false);
                Check(body.pose.angularVelocity == 0 && Math.Abs(body.pose.angle - angle) < .00001,
                    "common integration removes spin for drift / thrust: " + torque);
            }
            foreach (float angle in new[] { 0f, .37f, 1.6f, -2.1f })
            {
                // Roll plus tilt: screen axes need not be perpendicular in the map plane.
                var basis = new DriveInputBasis(SpaceTransform.Rotate(new Vector2(.25f, 1), angle), SpaceTransform.Rotate(new Vector2(-1, .2f), angle));
                var input = new Vector2(.3f, -.6f);
                Check((basis.FromMap(basis.ToMap(input)) - input).magnitude < .00001f,
                    "camera input and braking share the projected axes at angle " + angle);
                Vector2 Project(Rot4 facing) => basis.FromMap(SpaceTransform.Rotate(Vector2.right, ShipOrientation.AngleFromRot4(facing)));
                foreach (Rot4 facing in Rot4.AllRotations)
                {
                    Rot4 chosen = ArcadeShipAimHandler.SelectFacing(Rot4.North, Project(facing), Project);
                    Check(chosen == facing && ArcadeShipAimHandler.SelectFacing(chosen, Project(facing), Project) == chosen,
                        "projected four-way aim is correct and stable: " + angle + " / " + facing.AsInt);
                }
            }
        }
        finally { DeltaVMod.CurrentSettings.passiveDrag = drag; }
    }

    sealed class EvaCutBuilding : Building
    {
        internal int cuts;
        private int hp;
        public override int HitPoints { get => hp; set => hp = value; }
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Check(mode == DestroyMode.Deconstruct, "cutter invokes the native deconstruction mode");
            GenLeaving.DoLeavingsFor(this, Map, mode);
            cuts++;
            Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2);
        }
    }
    static bool NoSceneMesh() => false;
    static void CheckEvaEffects(UniverseManager universe)
    {
        var patch = new Harmony("DeltaV.Regression.EvaEffects");
        Map map = TestMap(58020);
        var container = new MapSpaceObject(58020, map, new SpacePose(Vector3.forward, 0));
        universe.RegisterObject(container);
        var steel = TestBuildingDef("evaSteel"); steel.category = ThingCategory.Item; steel.stackLimit = 75;
        var wall = TestBuildingDef("evaWall"); wall.destroyable = wall.useHitPoints = true;
        wall.resourcesFractionWhenDeconstructed = .5f;
        wall.costList = new List<ThingDefCountClass> { new ThingDefCountClass(steel, 20) };
        var building = (EvaCutBuilding)Raw(typeof(EvaCutBuilding)); building.def = wall;
        Set(typeof(ThingWithComps), building, "comps", new List<ThingComp>());
        ProbeSpawnCargo(building, new IntVec3(2, 0, 2), map);
        var evt = new SpaceCollision.ImpactEvent { defender = container, defenderBuilding = building,
            hitCell = building.Position, damageScale = 1 };
        ProgramState state = Current.ProgramState;
        Current.ProgramState = ProgramState.Playing;
        Current.Game.storyteller = (Storyteller)Raw(typeof(Storyteller));
        Current.Game.storyteller.difficulty = new Difficulty();
        try
        {
            patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "MaxHitPoints"), prefix: new HarmonyMethod(typeof(Program), nameof(TestHullMaxHitPoints)));
            patch.Patch(Patch_SalvageOutput_Placement.TargetMethod(), prefix: new HarmonyMethod(typeof(Program), nameof(PlaceSalvage)));
            patch.Patch(AccessTools.Method(typeof(ThingMaker), nameof(ThingMaker.MakeThing)), prefix: new HarmonyMethod(typeof(Program), nameof(MakeSalvage)));
            patch.Patch(AccessTools.Method(typeof(Filth), "UpdateMesh"), prefix: new HarmonyMethod(typeof(Program), nameof(NoSceneMesh)));
            building.HitPoints = building.MaxHitPoints - 4;
            var weld = new ImpactOutcomePart { kind = ImpactEffectKind.Weld, damage = 10 };
            SpaceImpactEffects.ApplyToDefender(evt, weld);
            Check(building.HitPoints == building.MaxHitPoints, "weld clamps building durability to its native maximum");
            var filth = (Filth)Raw(typeof(Filth)); filth.def = TestBuildingDef("evaFilth");
            Set(filth, "thickness", 3); ProbeSpawnCargo(filth, building.Position, map);
            SpaceImpactEffects.ApplyToDefender(evt, weld);
            Check((int)AccessTools.Field(typeof(Filth), "thickness").GetValue(filth) == 3,
                "a building hit does not also clean the underlying deck");
            evt.defenderBuilding = null; evt.defenderIsSubstructure = true;
            SpaceImpactEffects.ApplyToDefender(evt, weld);
            Check((int)AccessTools.Field(typeof(Filth), "thickness").GetValue(filth) == 2, "foundation hit removes exactly one filth layer");
            evt.defenderIsSubstructure = false;
            SpaceImpactEffects.ApplyToDefender(evt, weld);
            Check((int)AccessTools.Field(typeof(Filth), "thickness").GetValue(filth) == 2, "empty hit has no implicit cleaning effect");
            evt.defenderBuilding = building;
            building.HitPoints = 200;
            var cut = new ImpactOutcomePart { kind = ImpactEffectKind.CutStructure, damage = 120 };
            salvagePlacementSucceeds = true; salvagePlacements = 0;
            SpaceImpactEffects.ApplyToDefender(evt, cut);
            Check(building.HitPoints == 80 && salvagePlacements == 0, "partial cut changes HP without premature material payout");
            SpaceImpactEffects.ApplyToDefender(evt, cut);
            Check(building.cuts == 1 && salvagePlacements == 1 && lastSalvageOutput.stackCount == 10 && salvageOutputMap == map,
                "final cut pays the native dismantling quantity once on the target map");
            SpaceImpactEffects.ApplyToDefender(evt, cut);
            Check(building.cuts == 1 && salvagePlacements == 1, "duplicate impact on destroyed building cannot duplicate materials");
        }
        finally { patch.UnpatchAll(patch.Id); Current.ProgramState = state; }
    }

    sealed class EvaCargoItem : Thing
    {
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish) => ProbeRemoveCargo(this);
        // Only scene-dependent mesh/list notifications are replaced; receiver splitting,
        // capacity calculation, ownership and request/frame validation remain production code.
        public override Thing SplitOff(int count)
        {
            if (count >= stackCount) { if (Spawned) DeSpawn(); return this; }
            stackCount -= count;
            var part = (EvaCargoItem)Raw(typeof(EvaCargoItem)); part.def = def; part.stackCount = count;
            Set(typeof(Thing), part, "mapIndexOrState", (sbyte)-1); return part;
        }
    }
    static bool EvaDesignations(ref bool __result) { __result = false; return false; }
    static bool EvaMass(Thing thing, ref float __result)
    { if (thing is not EvaCargoItem) return true; __result = 2; return false; }
    static bool EvaStack(Thing __instance, ref bool __result)
    { if (__instance is not EvaCargoItem) return true; __result = false; return false; }
    static bool EvaCargoSpawn(Thing newThing, IntVec3 loc, Map map, ref Thing __result)
    {
        if (newThing is not EvaCargoItem) return true;
        ProbeSpawnCargo(newThing, loc, map); __result = newThing; return false;
    }
    static void CheckEvaCargo(UniverseManager universe, CompEvaSuit eva, CompSpaceWeapon weapon)
    {
        var patch = new Harmony("DeltaV.Regression.EvaCargo");
        var transporter = (CompTransporter)Raw(typeof(CompTransporter));
        transporter.parent = eva.parent; transporter.props = new CompProperties_Transporter();
        transporter.innerContainer = new ThingOwner<Thing>(transporter);
        eva.parent.AllComps.Add(transporter);
        var cargo = new CompVehicleCargo { parent = eva.parent, props = new CompProperties_VehicleCargo() };
        eva.parent.AllComps.Add(cargo);
        var item = (EvaCargoItem)Raw(typeof(EvaCargoItem)); item.def = TestBuildingDef("evaCargo");
        item.def.category = ThingCategory.Item; item.def.stackLimit = 75; item.stackCount = 15;
        Set(typeof(Thing), item, "mapIndexOrState", (sbyte)-1);
        SpaceObject receiver = universe.FrameOf(eva.parent.Map);
        var target = universe.CapturePayload(item,
            new SpacePose(SpaceWeaponService.TargetingMuzzle(weapon, receiver), 0), null);
        var context = new TractorReceiveContext(universe, weapon, target, receiver);
        CompCargoTractor tractor = weapon.Component<CompCargoTractor>();
        try
        {
            patch.Patch(AccessTools.Method(typeof(ThingOwnerUtility), "ShouldRemoveDesignationsOnAddedThings"), prefix: new HarmonyMethod(typeof(Program), nameof(EvaDesignations)));
            patch.Patch(AccessTools.Method(typeof(StatExtension), "GetStatValue", new[] { typeof(Thing), typeof(StatDef), typeof(bool), typeof(int) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(EvaMass)));
            patch.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.CanStackWith)), prefix: new HarmonyMethod(typeof(Program), nameof(EvaStack)));
            patch.Patch(AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(EvaCargoSpawn)));
            Check(tractor.HasReachedReceiver(context) && SpaceTractor.CanOperate(weapon), "cargo receiver operates without a moonpool or power component");
            Check(tractor.TryReceive(context) == TractorReceiveResult.Partial && target.Payload.Entity == item && item.stackCount == 5
                && transporter.innerContainer.Sum(t => t.stackCount) == 10 && cargo.Remaining == 0, "partial receipt splits only the 20 kg that fit; remainder stays on the source");
            Check(tractor.TryReceive(context) == TractorReceiveResult.Rejected && item.stackCount == 5, "full cargo rejects without consuming or duplicating the remainder");
            transporter.innerContainer.Clear();
            ((CompProperties_VehicleCargo)cargo.props).cargoCapacity = 6;
            transporter.innerContainer = new ThingOwner<Thing>(transporter, oneStackOnly: true);
            var blocker = (EvaCargoItem)Raw(typeof(EvaCargoItem)); blocker.def = item.def; blocker.stackCount = 1;
            Set(typeof(Thing), blocker, "mapIndexOrState", (sbyte)-1); transporter.innerContainer.TryAdd(blocker);
            Check(tractor.TryReceive(context) == TractorReceiveResult.Rejected
                && target.Payload.Entity?.stackCount == 5,
                "receiver refusal after splitting restores every leftover item to the source");
            transporter.innerContainer.Clear();
            transporter.innerContainer = new ThingOwner<Thing>(transporter);
            ((CompProperties_VehicleCargo)cargo.props).cargoCapacity = 20;
            Check(universe.Scatter.Pull(target, weapon), "manual cargo enters the common paid pull path");
            universe.Scatter.Tick();
            eva.combat.fireControlMode = ShipFireControlMode.Disabled;
            Call(universe, "RunFrameActions");
            Check(!transporter.innerContainer.Any && target.Payload.Entity?.stackCount == 5,
                "disabling fire control invalidates queued cargo handoff without loss");
            eva.combat.fireControlMode = ShipFireControlMode.Manual;
            Check(tractor.TryReceive(context) == TractorReceiveResult.Complete
                && transporter.innerContainer.Sum(t => t.stackCount) == 5 && target.Payload.Entity == null,
                "remaining contents are transferred exactly once when room is available");
            CheckEvaCargoEjection(universe, transporter, receiver);
            CheckPassengerEjection(universe);
        }
        finally { patch.UnpatchAll(patch.Id); }
    }

    static void CheckEvaCargoEjection(UniverseManager universe, CompTransporter transporter, SpaceObject receiver)
    {
        Thing item = transporter.innerContainer.Single();
        Map map = transporter.parent.Map;
        var offset = new IntVec3(map.Size.x, 0, 0);
        IntVec3 hatch = transporter.parent.Position + offset;
        Check(!hatch.InBounds(map), "EVA discard fixture puts the hatch outside the flight map");
        Check(!universe.Scatter.TryQueueTransporterEjection(transporter, item, 2, IntVec3.Zero)
            && item.stackCount == 5 && transporter.innerContainer.Contains(item),
            "an in-bounds cargo drop stays on the native unloading path");

        var existing = universe.Scatter.Candidates.ToHashSet();
        Check(universe.Scatter.TryQueueTransporterEjection(transporter, item, 2, offset)
            && item.stackCount == 5 && transporter.innerContainer.Contains(item),
            "the cargo remains owned until the safe frame");
        receiver.RepresentationVersion++;
        Call(universe, "RunFrameActions");
        Check(universe.Scatter.Candidates.All(existing.Contains) && item.stackCount == 5
            && transporter.innerContainer.Contains(item), "a stale ejection request leaves the original stack in the cargo hold");

        SpacePose previousPose = receiver.pose;
        receiver.pose.velocity = new Vector2(2f, 3f);
        Check(universe.Scatter.TryQueueTransporterEjection(transporter, item, 2, offset), "EVA accepts partial cargo jettison");
        Check(universe.Scatter.TryQueueTransporterEjection(transporter, item, 2, offset), "a duplicate partial request is safely intercepted");
        Call(universe, "RunFrameActions");
        SpaceObject part = universe.Scatter.Candidates.Single(obj => !existing.Contains(obj));
        var point = new Vector2(hatch.x + .5f, hatch.z + .5f);
        SpacePose expected = SpaceTransform.RigidPointPose(receiver.pose, SpaceTransform.MapToShipLocal(map, point), 0f);
        Check(part.map == null && part.Payload.Entity.stackCount == 2 && item.stackCount == 3
            && transporter.innerContainer.Contains(item), "only the requested quantity becomes a mapless tractor candidate");
        Check((part.pose.position - expected.position).sqrMagnitude < 1e-8f
            && (part.pose.velocity - expected.velocity).sqrMagnitude < 1e-8f,
            "ejected cargo inherits the position and velocity at the hatch outside the map");

        Set(transporter, "massUsageDirty", false);
        Check(universe.Scatter.TryQueueTransporterEjection(transporter, item, 3, offset), "EVA accepts whole-stack jettison");
        Call(universe, "RunFrameActions");
        Check(!transporter.innerContainer.Any && universe.Scatter.Candidates.Any(obj => obj.Payload?.Entity == item)
            && (bool)AccessTools.Field(typeof(CompTransporter), "massUsageDirty").GetValue(transporter),
            "whole stack keeps its identity as recoverable cargo and invalidates the transporter's mass usage");
        receiver.pose = previousPose;
    }

    static Lord ejectionLoadLord;
    static int ejectionLordExits;
    static bool EjectionSceneGraphics() => false;
    static bool EjectionPawnLost(Lord __instance, Pawn pawn, PawnLostCondition cond)
    {
        if (__instance != ejectionLoadLord) return true;
        Check(cond == PawnLostCondition.LeftVoluntarily, "ejected passengers leave the loading lord with the native reason");
        __instance.ownedPawns.Remove(pawn);
        pawn.lord = null;
        ejectionLordExits++;
        return false;
    }
    static bool EjectionCaptureDenied(ref SpaceObject __result) { __result = null; return false; }

    static void CheckPassengerEjection(UniverseManager universe)
    {
        var map = TestMap(58040, 1);
        var hull = (Building)Raw(typeof(Building));
        hull.def = TestBuildingDef("passengerEjectionHull");
        hull.def.interactionCellOffset = new IntVec3(1, 0, 0);
        Set(typeof(Thing), hull, "positionInt", IntVec3.Zero);
        Set(typeof(Thing), hull, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        var transporter = new CompTransporter { parent = hull, props = new CompProperties_Transporter() };
        transporter.innerContainer = new ThingOwner<Thing>(transporter);
        var shuttle = new CompLocalShuttle { parent = hull, props = new CompProperties_LocalShuttle() };
        Set(typeof(ThingWithComps), hull, "comps", new List<ThingComp> { transporter, shuttle });
        var frame = new MapSpaceObject(58040, map, new SpacePose(Vector3.forward, .4f));
        var tab = new ITab_ContentsTransporter();
        MethodInfo drop = AccessTools.Method(typeof(ITab_ContentsTransporter), "OnDropThing");
        Pawn Passenger(string name, Intelligence intelligence)
        {
            var pawn = (Pawn)Raw(typeof(Pawn));
            pawn.def = TestBuildingDef(name + "Species");
            pawn.def.category = ThingCategory.Pawn;
            pawn.def.race = new RaceProperties { intelligence = intelligence };
            pawn.Name = new NameSingle(name);
            pawn.stackCount = 1;
            pawn.health = new Pawn_HealthTracker(pawn);
            Set(typeof(Thing), pawn, "mapIndexOrState", (sbyte)-1);
            transporter.innerContainer.TryAdd(pawn, canMergeWithExistingStacks: false);
            return pawn;
        }

        var patch = new Harmony("DeltaV.Regression.PassengerEjection");
        // Lord's type initializer loads the squad flag material through Unity.
        patch.Patch(typeof(Lord).TypeInitializer, prefix: new HarmonyMethod(typeof(Program), nameof(EjectionSceneGraphics)));
        patch.Patch(AccessTools.PropertyGetter(typeof(Find), "Selector"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorSelector)));
        patch.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorWindowStack)));
        patch.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorOpenWindow)));
        patch.Patch(AccessTools.Method(typeof(Lord), nameof(Lord.Notify_PawnLost)), prefix: new HarmonyMethod(typeof(Program), nameof(EjectionPawnLost)));
        patch.CreateClassProcessor(typeof(Patch_TransporterCargoDrop)).Patch();
        tractorSelector = (Selector)Raw(typeof(Selector));
        Set(tractorSelector, "selected", new List<object> { hull });
        try
        {
            Pawn person = Passenger("Pyrrha", Intelligence.Humanlike);
            var offset = new IntVec3(1, 0, 0);
            Check(!universe.Scatter.TryQueueTransporterEjection(transporter, person, 1, offset),
                "an unregistered map cannot redirect a passenger into space");
            universe.RegisterObject(frame);
            Check(tab.DropOffset == offset && !(hull.Position + tab.DropOffset).InBounds(map),
                "the native transporter tab points outside the real 1x1 fixture map");
            Check(!universe.Scatter.TryQueueTransporterEjection(transporter, person, 1, IntVec3.Zero),
                "an in-bounds passenger drop stays on the native unloading path");

            ejectionLoadLord = (Lord)Raw(typeof(Lord));
            Set(ejectionLoadLord, "curJob", new LordJob_LoadAndEnterTransporters());
            ejectionLoadLord.ownedPawns = new List<Pawn> { person };
            person.lord = ejectionLoadLord;
            ejectionLordExits = 0;
            tractorWindow = null;
            drop.Invoke(tab, new object[] { person, 1 });
            var warning = tractorWindow as Dialog_MessageBox;
            Check(warning?.buttonADestructive == true && warning.forcePause
                && transporter.innerContainer.Contains(person) && ejectionLordExits == 0,
                "the normal tab entry warns before removing a humanlike passenger or changing their loading lord");
            warning.cancelAction();
            Call(universe, "RunFrameActions");
            Check(transporter.innerContainer.Contains(person) && person.GetLord() == ejectionLoadLord,
                "cancelling the warning leaves the passenger and loading lord intact");

            drop.Invoke(tab, new object[] { person, 1 });
            warning = (Dialog_MessageBox)tractorWindow;
            frame.RepresentationVersion++;
            warning.buttonAAction();
            Call(universe, "RunFrameActions");
            Check(transporter.innerContainer.Contains(person) && ejectionLordExits == 0,
                "a representation change while the warning is open invalidates the ejection");

            var refusal = new Harmony("DeltaV.Regression.EjectionRefusal");
            refusal.Patch(AccessTools.Method(typeof(UniverseManager), "CapturePayload"),
                prefix: new HarmonyMethod(typeof(Program), nameof(EjectionCaptureDenied)));
            try
            {
                drop.Invoke(tab, new object[] { person, 1 });
                ((Dialog_MessageBox)tractorWindow).buttonAAction();
                Call(universe, "RunFrameActions");
                Check(transporter.innerContainer.Contains(person) && person.GetLord() == ejectionLoadLord
                    && ejectionLordExits == 0, "a failed passenger handoff preserves the passenger and loading lord");

                var cargo = (EvaCargoItem)Raw(typeof(EvaCargoItem));
                cargo.def = TestBuildingDef("failedEjectionCargo");
                cargo.def.category = ThingCategory.Item;
                cargo.def.stackLimit = 75;
                cargo.stackCount = 5;
                Set(typeof(Thing), cargo, "mapIndexOrState", (sbyte)-1);
                transporter.innerContainer.TryAdd(cargo, canMergeWithExistingStacks: false);
                Check(universe.Scatter.TryQueueTransporterEjection(transporter, cargo, 2, offset), "a partial stack enters the ejection transaction");
                Call(universe, "RunFrameActions");
                Check(transporter.innerContainer.Where(t => t.def == cargo.def).Sum(t => t.stackCount) == 5,
                    "a failed split-stack handoff restores every item to the original hold");
            }
            finally { refusal.UnpatchAll(refusal.Id); }

            drop.Invoke(tab, new object[] { person, 1 });
            warning = (Dialog_MessageBox)tractorWindow;
            warning.buttonAAction();
            warning.buttonAAction();
            Call(universe, "RunFrameActions");
            Check(!transporter.innerContainer.Contains(person) && ejectionLordExits == 1 && person.GetLord() == null
                && universe.Scatter.Candidates.Count(obj => obj.Payload?.Entity == person) == 1,
                "repeated confirmation ejects exactly one recoverable passenger and leaves the loading lord once");

            Pawn animal = Passenger("AnimalPassenger", Intelligence.Animal);
            tractorWindow = null;
            drop.Invoke(tab, new object[] { animal, 1 });
            Check(tractorWindow == null && transporter.innerContainer.Contains(animal),
                "a non-humanlike passenger uses the same safe-frame handoff without a human warning");
            Call(universe, "RunFrameActions");
            Check(universe.Scatter.Candidates.Count(obj => obj.Payload?.Entity == animal) == 1
                && !transporter.innerContainer.Contains(animal), "the native tab also ejects a non-humanlike passenger exactly once");

            foreach (string language in new[] { "English", "ChineseSimplified" })
            {
                var xml = XDocument.Load($"1.6/Languages/{language}/Keyed/Eva.xml");
                string text = (string)xml.Root.Element("DeltaV_EjectPassengerConfirm");
                Check(!string.IsNullOrEmpty(text) && string.Format(text, "Pyrrha").Contains("Pyrrha"),
                    language + " provides a passenger warning with their name");
            }
        }
        finally { patch.UnpatchAll(patch.Id); }
    }
}
