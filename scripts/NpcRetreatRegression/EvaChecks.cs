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
        AddFixtureComponents(eva.parent, embedded);
        embedded.PostSpawnSetup(true);
        CompSpaceWeapon[] weapons = embedded.Weapons.ToArray();
        var physical = new CompSpaceWeapon { parent = eva.parent, props = new CompProperties_SpaceWeapon() };
        AddFixtureComponents(eva.parent, physical);
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
        SpacePose selectedPoint = SpaceTransform.RigidPointPose(host, new Vector2(-3, 7), 0f);
        SpacePose contracted = LandingApproachController.ApplyMotionContract(own, selectedPoint, ship);
        Check(contracted.position == selectedPoint.position
            && contracted.velocity == selectedPoint.velocity
            && contracted.angle == own.angle && contracted.angularVelocity == own.angularVelocity,
            "EVA capture preserves a selected rigid point while ignoring target hull heading and spin");
        Check(ship.HardDockingExecutor != null && ship.HardDockingExecutor.CaptureTether != null
            && !ship.CanBoard && !ship.CanDisableCollisionAvoidance,
            "EVA exposes non-breaching tethered arrival without becoming a collision boarding shuttle");
        Check(!HardDockingService.CaptureGate(true, false)
            && HardDockingService.CaptureGate(true, true),
            "EVA boarding keeps the shared capture qualification after its live line slackens or releases");
        var evaXml = System.Xml.Linq.XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_Eva.xml");
        var tractor = evaXml.Descendants("DeltaV.VirtualWeaponDef")
            .Single(node => (string)node.Element("defName") == "DeltaV_EvaTractor");
        var tractorBeam = evaXml.Descendants("ThingDef")
            .Single(node => (string)node.Element("defName") == "DeltaV_Beam_EvaTractor");
        Check((float)tractor.Descendants("range").Single() == 32f
            && (float)tractorBeam.Descendants("laserRange").Single() == 32f,
            "EVA tractor weapon and beam reach share the 32-cell capture range");
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
        Thing launcher = AddTestHull(map, null, 58021).parent;
        var evt = new SpaceCollision.ImpactEvent { launcher = launcher, defender = container, defenderBuilding = building,
            weapon = new CompSpaceWeapon { parent = (ThingWithComps)launcher,
                props = new CompProperties_MiningBeam { cutDamage = 120 } },
            hitCell = building.Position, hitDefenderLocal = new Vector2(2f, 2.5f), incidentDir = Vector2.right, damageScale = 1 };
        ProgramState state = Current.ProgramState;
        Current.ProgramState = ProgramState.Playing;
        Current.Game.storyteller = (Storyteller)Raw(typeof(Storyteller));
        Current.Game.storyteller.difficulty = new Difficulty();
        try
        {
            patch.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
                prefix: new HarmonyMethod(typeof(Program), nameof(RegressionSubstructure)));
            patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "MaxHitPoints"), prefix: new HarmonyMethod(typeof(Program), nameof(TestHullMaxHitPoints)));
            patch.Patch(Patch_SalvageOutput_Placement.TargetMethod(), prefix: new HarmonyMethod(typeof(Program), nameof(PlaceSalvage)));
            patch.CreateClassProcessor(typeof(Patch_CombatLeavings)).Patch();
            patch.CreateClassProcessor(typeof(Patch_SalvageTerrainYield)).Patch();
            patch.Patch(AccessTools.PropertyGetter(typeof(MapInfo), nameof(MapInfo.Tile)),
                prefix: new HarmonyMethod(typeof(Program), nameof(SalvageFixtureTile)));
            patch.Patch(typeof(ModsConfig).TypeInitializer,
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            patch.Patch(AccessTools.Method(typeof(ThingMaker), nameof(ThingMaker.MakeThing)), prefix: new HarmonyMethod(typeof(Program), nameof(MakeSalvage)));
            patch.Patch(AccessTools.Method(typeof(Filth), "UpdateMesh"), prefix: new HarmonyMethod(typeof(Program), nameof(NoSceneMesh)));
            patch.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), nameof(TerrainDef.IsSubstructure)),
                prefix: new HarmonyMethod(typeof(Program), nameof(SalvageFoundation)));
            patch.Patch(AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                prefix: new HarmonyMethod(typeof(Program), nameof(SkipWeaponMenuLayout)));
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
            var cut = new ImpactOutcomePart { kind = ImpactEffectKind.CutStructure };
            salvagePlacementSucceeds = true; salvagePlacements = 0;
            SpaceImpactEffects.ApplyToDefender(evt, cut);
            Check(building.HitPoints == 80 && salvagePlacements == 0, "partial cut changes HP without premature material payout");
            SpaceImpactEffects.ApplyToDefender(evt, cut);
            SpaceImpactEffects.ApplyToDefender(evt, cut);
            Call(universe, "RunFrameActions");
            var wallReturns = universe.Scatter.Candidates.Single(cargo => cargo.Payload.Entity.def == steel);
            Check(building.cuts == 1 && salvagePlacements == 0 && wallReturns.Payload.Entity.stackCount == 10,
                "finished EVA cut creates one native return and a repeated hit adds nothing");
            wallReturns.Payload.GetDirectlyHeldThings().Remove(wallReturns.Payload.Entity);
            universe.UnregisterObject(wallReturns);
            CheckEvaSolarCut(universe, launcher, steel);
        }
        finally { patch.UnpatchAll(patch.Id); Current.ProgramState = state; }
    }

    static void CheckEvaSolarCut(UniverseManager universe, Thing launcher, ThingDef steel)
    {
        Map map = TestMap(58022, 9);
        map.roofGrid = new RoofGrid(map);
        var frame = new MapSpaceObject(58022, map, new SpacePose(Vector3.forward, 0));
        universe.RegisterObject(frame);
        var def = TestBuildingDef("evaSolarGenerator");
        def.size = new IntVec2(4, 4);
        def.fillPercent = .5f;
        def.passability = Traversability.PassThroughOnly;
        def.destroyable = def.useHitPoints = true;
        def.resourcesFractionWhenDeconstructed = .5f;
        var component = TestBuildingDef("evaSolarComponent");
        component.category = ThingCategory.Item;
        component.stackLimit = 50;
        def.costList = new List<ThingDefCountClass> { new ThingDefCountClass(steel, 100), new ThingDefCountClass(component, 3) };
        var solar = (EvaCutBuilding)Raw(typeof(EvaCutBuilding));
        solar.def = def;
        solar.HitPoints = 300;
        ProbeSpawnCargo(solar, new IntVec3(4, 0, 4), map);
        SubstructureOwnership.For(map).UpdateSpawnedBuilding(solar);
        var cell = new IntVec3(solar.OccupiedRect().minX, 0, 4);
        var deck = FixtureFoundation("evaSolarDeck");
        deck.resourcesFractionWhenDeconstructed = .5f;
        deck.costList = new List<ThingDefCountClass> { new ThingDefCountClass(steel, 8) };
        int cellIndex = map.cellIndices.CellToIndex(cell);
        map.terrainGrid.topGrid[cellIndex] = (TerrainDef)Raw(typeof(TerrainDef));
        var oldStack = (Thing)Raw(typeof(ReceivingItem));
        oldStack.def = steel;
        oldStack.stackCount = 11;
        ProbeSpawnCargo(oldStack, cell, map);
        using (universe.ShipStructure.BeginChanges())
        {
            SetFixtureFoundation(map, cell, deck, 0);
        }
        var projectile = TestBuildingDef("evaSolarCutBeam");
        projectile.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, laserRange = 6, collisionRadius = 0 });
        var outcome = new ImpactOutcomePart { kind = ImpactEffectKind.CutStructure };
        var buildingEffect = new SpaceImpactEffectDef { defName = "EvaSolarBuildingImpact", projectileDefs = new List<ThingDef> { projectile },
            defenderEffect = outcome };
        var floorEffect = new SpaceImpactEffectDef { defName = "EvaSolarFloorImpact", projectileDefs = buildingEffect.projectileDefs,
            targetsSubstructure = true, substructureHitChance = 1f, defenderEffect = outcome };
        DefDatabase<SpaceImpactEffectDef>.Add(buildingEffect);
        DefDatabase<SpaceImpactEffectDef>.Add(floorEffect);
        SpaceImpactEffects.ResetCache();
        try
        {
            int seed = 1;
            for (;; seed++)
            {
                Rand.PushState(seed);
                bool hit = SpaceImpactEffects.HitTest(projectile, SpaceCellKind.Building, solar, buildingEffect);
                Rand.PopState();
                if (!hit) break;
            }
            var weapon = new CompSpaceWeapon { parent = (ThingWithComps)launcher,
                props = new CompProperties_MiningBeam { cutDamage = 180 } };
            void Pulse()
            {
                Vector3 from = SpaceTransform.MapPointToUniverse(frame, map, new Vector2(1.5f, 4.5f));
                var body = new SpaceCollision.SweptBody
                {
                    attacker = new SpaceObject(58023, new SpacePose(from, 0)), requiredDefender = frame,
                    from = from, to = SpaceTransform.MapPointToUniverse(frame, map, new Vector2(6.5f, 4.5f)),
                    launcher = launcher, weapon = weapon, projectileDef = projectile, damageScale = 1, mass = 1
                };
                Rand.PushState(seed);
                try { SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { body }); }
                finally { Rand.PopState(); }
                Check(body.resolvedHit && body.resolvedHitCell == cell, "EVA contact resolves at the covered deck edge");
            }
            int before = salvagePlacements;
            Pulse();
            Check(solar.HitPoints == 120 && salvagePlacements == before && SubstructureOwnership.HasFoundation(map, cell),
                "sun-facing panel takes the confirmed deck hit despite rejecting direct cover");
            Pulse();
            Call(universe, "RunFrameActions");
            var returnObject = universe.Scatter.Candidates.Single(cargo => cargo.Payload.Entity.def == steel);
            var componentReturn = universe.Scatter.Candidates.Single(cargo => cargo.Payload.Entity.def == component);
            Thing material = returnObject.Payload.Entity;
            Check(solar.Destroyed && solar.cuts == 1 && SubstructureOwnership.HasFoundation(map, cell)
                && salvagePlacements == before && returnObject.map == null && !material.Spawned && material.stackCount == 50
                && componentReturn.Payload.Entity.stackCount is 1 or 2
                && oldStack.Spawned && oldStack.Map == map && oldStack.Position == cell && oldStack.stackCount == 11,
                "EVA solar panel ejects the full native yield and leaves the deck and its cargo in place");
            var steelDrift = EvaReturnDrift(frame, returnObject);
            var componentDrift = EvaReturnDrift(frame, componentReturn);
            Check(IsEvaCutDrift(steelDrift) && IsEvaCutDrift(componentDrift),
                "both solar returns drift within the cut return angle and speed limits");
            returnObject.Payload.GetDirectlyHeldThings().Remove(material);
            componentReturn.Payload.GetDirectlyHeldThings().Remove(componentReturn.Payload.Entity);
            universe.UnregisterObject(returnObject);
            universe.UnregisterObject(componentReturn);

            ProbeRemoveCargo(oldStack);
            Pulse();
            Call(universe, "RunFrameActions");
            var deckReturns = universe.Scatter.Candidates.Single(cargo => cargo.Payload.Entity.def == steel);
            Check(!SubstructureOwnership.HasFoundation(map, cell) && salvagePlacements == before
                && deckReturns.Payload.Entity.stackCount == 4 && IsEvaCutDrift(EvaReturnDrift(frame, deckReturns)),
                "a separate EVA shot cuts the bare foundation and ejects its full native yield");
            deckReturns.Payload.GetDirectlyHeldThings().Remove(deckReturns.Payload.Entity);
            universe.UnregisterObject(deckReturns);
            CheckEvaCutSpread(universe, frame, steel);
        }
        finally
        {
            var remove = AccessTools.Method(typeof(DefDatabase<SpaceImpactEffectDef>), "Remove");
            remove.Invoke(null, new object[] { buildingEffect });
            remove.Invoke(null, new object[] { floorEffect });
            SpaceImpactEffects.ResetCache();
            universe.UnregisterObject(frame);
            Find.Maps.Remove(map);
        }
    }

    static (float angle, float speed) EvaReturnDrift(SpaceObject source, SpaceObject cargo)
    {
        Vector2 relative = cargo.pose.velocity - SpaceTransform.VelocityAtUniversePoint(source.pose, cargo.pose.position);
        Vector2 direction = SpaceTransform.UniverseTangentToMapDirection(source, source.map, cargo.pose.position, relative);
        return (Mathf.Atan2(direction.y, -direction.x) * Mathf.Rad2Deg, relative.magnitude);
    }

    static bool IsEvaCutDrift((float angle, float speed) drift) =>
        Mathf.Abs(drift.angle) <= 40.05f && drift.speed is >= .399f and <= 1.801f;

    static bool IsEvaOreDrift((float angle, float speed) drift) =>
        Mathf.Abs(drift.angle) <= 40.05f && drift.speed is >= 1.999f and <= 4.001f;

    static void CheckEvaCutSpread(UniverseManager universe, SpaceObject frame, ThingDef steel)
    {
        frame.pose.angle = .4f;
        frame.pose.angularVelocity = .3f;
        frame.pose.velocity = new Vector2(4f, -3f);
        var hitPoint = new Vector2(2.5f, 4.5f);
        for (int i = 0; i < 24; i++)
        {
            var material = new ReceivingItem { def = steel, stackCount = 1, thingIDNumber = 59000 + i };
            universe.Scatter.QueueMiningEjection(material, frame, hitPoint, Vector2.left, cutReturn: true);
        }
        Call(universe, "RunFrameActions");
        var returns = universe.Scatter.Candidates.Where(cargo => cargo.Payload.Entity.def == steel).ToList();
        var drifts = returns.Select(cargo => EvaReturnDrift(frame, cargo)).ToList();
        Check(drifts.Count == 24 && drifts.All(IsEvaCutDrift),
            "cut returns stay in the expected local cone and speed range on a drifting, rotating target");
        Check(drifts.Min(drift => drift.angle) < -15f && drifts.Max(drift => drift.angle) > 15f
            && drifts.Min(drift => drift.speed) < .75f && drifts.Max(drift => drift.speed) > 1.45f,
            "individual cut stacks fan out to both sides with visibly different speeds");
        foreach (var cargo in returns)
        {
            cargo.Payload.GetDirectlyHeldThings().Remove(cargo.Payload.Entity);
            universe.UnregisterObject(cargo);
        }

        for (int i = 0; i < 24; i++)
        {
            var mineral = new ReceivingItem { def = steel, stackCount = 1, thingIDNumber = 59100 + i };
            universe.Scatter.QueueMiningEjection(mineral, frame, hitPoint, Vector2.left);
        }
        Call(universe, "RunFrameActions");
        var ores = universe.Scatter.Candidates.Where(cargo => cargo.Payload.Entity.def == steel).ToList();
        var oreDrifts = ores.Select(cargo => EvaReturnDrift(frame, cargo)).ToList();
        Check(oreDrifts.Count == 24 && oreDrifts.All(IsEvaOreDrift),
            "mined ore uses the common local cone while retaining its faster speed range");
        Check(oreDrifts.Min(drift => drift.angle) < -15f && oreDrifts.Max(drift => drift.angle) > 15f
            && oreDrifts.Min(drift => drift.speed) < 2.5f && oreDrifts.Max(drift => drift.speed) > 3.5f,
            "individual ore stacks leave at visibly different angles and speeds");
        foreach (var cargo in ores)
        {
            cargo.Payload.GetDirectlyHeldThings().Remove(cargo.Payload.Entity);
            universe.UnregisterObject(cargo);
        }
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
        var cargo = new CompVehicleCargo { parent = eva.parent, props = new CompProperties_VehicleCargo { massCapacity = 20f } };
        CompTransporter transporter = cargo;
        transporter.Initialize(cargo.props);
        AddFixtureComponents(eva.parent, cargo, new CompStructureMass());
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
            patch.CreateClassProcessor(typeof(Patch_ShipStructureInventory_Added)).Patch();
            patch.CreateClassProcessor(typeof(Patch_ShipStructureInventory_Removed)).Patch();
            patch.CreateClassProcessor(typeof(Patch_ShipStructureInventory_TransporterRemoved)).Patch();
            patch.CreateClassProcessor(typeof(Patch_VehicleCargoMass)).Patch();
            Check(ReferenceEquals(transporter, eva.parent.GetComp<CompTransporter>()), "EVA uses the native transporter entry");
            float dryMass = ShipPhysics.GetReport(receiver).mass;
            var passenger = new Pawn { def = TestBuildingDef("evaCargoCrew") };
            passenger.def.category = ThingCategory.Pawn;
            Check(transporter.innerContainer.TryAdd(passenger) && cargo.CargoMass == 0
                && transporter.MassUsage == 0 && ShipPhysics.GetReport(receiver).mass == dryMass,
                "EVA passenger does not count against inventory or flight mass");
            transporter.innerContainer.Remove(passenger);
            Check(tractor.HasReachedReceiver(context) && SpaceTractor.CanOperate(weapon), "cargo receiver operates without a moonpool or power component");
            Check(tractor.TryReceive(context) == TractorReceiveResult.Partial && target.Payload.Entity == item && item.stackCount == 5
                && transporter.innerContainer.Sum(t => t.stackCount) == 10 && cargo.Remaining == 0, "partial receipt splits only the 20 kg that fit; remainder stays on the source");
            Check(transporter.MassUsage == 20f && ShipPhysics.GetReport(receiver).mass == dryMass + 20f,
                "EVA collected goods count toward the flight mass immediately");
            Check(tractor.TryReceive(context) == TractorReceiveResult.Rejected && item.stackCount == 5, "full cargo rejects without consuming or duplicating the remainder");
            transporter.innerContainer.Clear();
            ((CompProperties_VehicleCargo)cargo.props).massCapacity = 6;
            transporter.innerContainer = new ThingOwner<Thing>(transporter, oneStackOnly: true);
            var blocker = (EvaCargoItem)Raw(typeof(EvaCargoItem)); blocker.def = item.def; blocker.stackCount = 1;
            Set(typeof(Thing), blocker, "mapIndexOrState", (sbyte)-1); transporter.innerContainer.TryAdd(blocker);
            Check(tractor.TryReceive(context) == TractorReceiveResult.Rejected
                && target.Payload.Entity?.stackCount == 5,
                "receiver refusal after splitting restores every leftover item to the source");
            transporter.innerContainer.Clear();
            transporter.innerContainer = new ThingOwner<Thing>(transporter);
            ((CompProperties_VehicleCargo)cargo.props).massCapacity = 20;
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
        Check(!universe.Scatter.TryQueueTransporterEjection(
                transporter, item, 2, IntVec3.Zero, confirm: false)
            && item.stackCount == 5 && transporter.innerContainer.Contains(item),
            "an in-bounds cargo drop stays on the native unloading path");

        var existing = universe.Scatter.Candidates.ToHashSet();
        Check(universe.Scatter.TryQueueTransporterEjection(
                transporter, item, 2, offset, confirm: false)
            && item.stackCount == 5 && transporter.innerContainer.Contains(item),
            "the cargo remains owned until the safe frame");
        receiver.RepresentationVersion++;
        Call(universe, "RunFrameActions");
        Check(universe.Scatter.Candidates.All(existing.Contains) && item.stackCount == 5
            && transporter.innerContainer.Contains(item), "a stale ejection request leaves the original stack in the cargo hold");

        float dryMass = ShipPhysics.GetReport(receiver).mass - transporter.MassUsage;
        SpacePose previousPose = receiver.pose;
        receiver.pose.velocity = new Vector2(2f, 3f);
        Check(universe.Scatter.TryQueueTransporterEjection(
            transporter, item, 2, offset, confirm: false), "EVA accepts partial cargo jettison");
        Check(universe.Scatter.TryQueueTransporterEjection(
            transporter, item, 2, offset, confirm: false), "a duplicate partial request is safely intercepted");
        Call(universe, "RunFrameActions");
        SpaceObject part = universe.Scatter.Candidates.Single(obj => !existing.Contains(obj));
        var point = new Vector2(hatch.x + .5f, hatch.z + .5f);
        SpacePose expected = SpaceTransform.RigidPointPose(receiver.pose, SpaceTransform.MapToShipLocal(map, point), 0f);
        Check(part.map == null && part.Payload.Entity.stackCount == 2 && item.stackCount == 3
            && transporter.innerContainer.Contains(item), "only the requested quantity becomes a mapless tractor candidate");
        Check((part.pose.position - expected.position).sqrMagnitude < 1e-8f
            && (part.pose.velocity - expected.velocity).sqrMagnitude < 1e-8f,
            "ejected cargo inherits the position and velocity at the hatch outside the map");
        Check(transporter.MassUsage == 6f && ShipPhysics.GetReport(receiver).mass == dryMass + 6f,
            "partial jettison updates the remaining stack and the flight mass");

        Check(universe.Scatter.TryQueueTransporterEjection(
            transporter, item, 3, offset, confirm: false), "EVA accepts whole-stack jettison");
        Call(universe, "RunFrameActions");
        Check(!transporter.innerContainer.Any && universe.Scatter.Candidates.Any(obj => obj.Payload?.Entity == item)
            && transporter.MassUsage == 0 && ShipPhysics.GetReport(receiver).mass == dryMass,
            "whole stack keeps its identity and removes its weight from the shared mass contribution");
        receiver.pose = previousPose;
    }

    static Lord ejectionLoadLord;
    static int ejectionLordExits;
    static int ejectionDialogs;
    static Map ejectionGroundMap;
    static int ejectionGroundDrops;
    static bool EjectionSceneGraphics() => false;
    static bool EjectionOpenWindow(Window window)
    {
        tractorWindow = window;
        ejectionDialogs++;
        return false;
    }
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
    static bool EjectionGroundDrop(Thing thing, IntVec3 dropCell, Map map,
        ref Thing resultingThing, ref bool __result)
    {
        if (map != ejectionGroundMap || thing is not Pawn) return true;
        thing.holdingOwner?.Remove(thing);
        ProbeSpawnCargo(thing, dropCell, map);
        resultingThing = thing;
        __result = true;
        ejectionGroundDrops++;
        return false;
    }

    static void CheckPassengerEjection(UniverseManager universe)
    {
        var map = TestMap(58040, 1);
        var hull = (Building)Raw(typeof(Building));
        hull.def = TestBuildingDef("passengerEjectionHull");
        hull.def.interactionCellOffset = new IntVec3(1, 0, 0);
        hull.def.hasInteractionCell = true;
        Set(typeof(Thing), hull, "positionInt", IntVec3.Zero);
        Set(typeof(Thing), hull, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        var transporter = new CompTransporter { parent = hull, props = new CompProperties_Transporter() };
        transporter.innerContainer = new ThingOwner<Thing>(transporter);
        var shuttle = new CompLocalShuttle { parent = hull, props = new CompProperties_LocalShuttle() };
        Set(typeof(ThingWithComps), hull, "comps", new List<ThingComp> { transporter, shuttle });
        var frame = new MapSpaceObject(58040, map, new SpacePose(Vector3.forward, .4f));
        var tab = new ITab_ContentsTransporter();
        MethodInfo drop = AccessTools.Method(typeof(ITab_ContentsTransporter), "OnDropThing");
        Pawn Passenger(string name, Intelligence intelligence, Faction faction = null)
        {
            var pawn = (Pawn)Raw(typeof(Pawn));
            pawn.def = TestBuildingDef(name + "Species");
            pawn.def.category = ThingCategory.Pawn;
            pawn.def.race = new RaceProperties { intelligence = intelligence };
            pawn.Name = new NameSingle(name);
            pawn.stackCount = 1;
            pawn.health = new Pawn_HealthTracker(pawn);
            if (faction != null) pawn.SetFactionDirect(faction);
            Set(typeof(Thing), pawn, "mapIndexOrState", (sbyte)-1);
            transporter.innerContainer.TryAdd(pawn, canMergeWithExistingStacks: false);
            return pawn;
        }

        var patch = new Harmony("DeltaV.Regression.PassengerEjection");
        // Lord's type initializer loads the squad flag material through Unity.
        patch.Patch(typeof(Lord).TypeInitializer, prefix: new HarmonyMethod(typeof(Program), nameof(EjectionSceneGraphics)));
        patch.Patch(AccessTools.PropertyGetter(typeof(Find), "Selector"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorSelector)));
        patch.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorWindowStack)));
        patch.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(Program), nameof(EjectionOpenWindow)));
        patch.Patch(AccessTools.Method(typeof(Lord), nameof(Lord.Notify_PawnLost)), prefix: new HarmonyMethod(typeof(Program), nameof(EjectionPawnLost)));
        patch.Patch(AccessTools.Method(typeof(GenDrop), nameof(GenDrop.TryDropSpawn)),
            prefix: new HarmonyMethod(typeof(Program), nameof(EjectionGroundDrop)));
        patch.CreateClassProcessor(typeof(Patch_TransporterCargoDrop)).Patch();
        patch.CreateClassProcessor(typeof(Patch_TransporterUnload)).Patch();
        patch.CreateClassProcessor(typeof(Patch_TransporterOccupantExit)).Patch();
        tractorSelector = (Selector)Raw(typeof(Selector));
        Set(tractorSelector, "selected", new List<object> { hull });
        try
        {
            Pawn person = Passenger("Pyrrha", Intelligence.Humanlike);
            var offset = new IntVec3(1, 0, 0);
            Check(!universe.Scatter.TryQueueTransporterEjection(
                    transporter, person, 1, offset, confirm: false),
                "an unregistered map cannot redirect a passenger into space");
            universe.RegisterObject(frame);
            Check(tab.DropOffset == offset && !(hull.Position + tab.DropOffset).InBounds(map),
                "the native transporter tab points outside the real 1x1 fixture map");
            Check(!universe.Scatter.TryQueueTransporterEjection(
                    transporter, person, 1, IntVec3.Zero, confirm: false),
                "an in-bounds passenger drop stays on the native unloading path");

            var silentCargo = (EvaCargoItem)Raw(typeof(EvaCargoItem));
            silentCargo.def = TestBuildingDef("silentEjectionCargo");
            silentCargo.def.category = ThingCategory.Item;
            silentCargo.def.stackLimit = 75;
            silentCargo.stackCount = 2;
            Set(typeof(Thing), silentCargo, "mapIndexOrState", (sbyte)-1);
            transporter.innerContainer.TryAdd(silentCargo, canMergeWithExistingStacks: false);
            tractorWindow = null;
            drop.Invoke(tab, new object[] { silentCargo, 1 });
            Check(tractorWindow == null && silentCargo.stackCount == 2
                && transporter.innerContainer.Contains(silentCargo),
                "the unified transporter drop queues space cargo without a warning");
            Call(universe, "RunFrameActions");
            Check(silentCargo.stackCount == 1 && transporter.innerContainer.Contains(silentCargo),
                "the unified transporter drop ejects only the requested cargo count");

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
                Check(universe.Scatter.TryQueueTransporterEjection(
                    transporter, cargo, 2, offset, confirm: false), "a partial stack enters the ejection transaction");
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
            warning = tractorWindow as Dialog_MessageBox;
            Check(warning?.buttonADestructive == true && transporter.innerContainer.Contains(animal),
                "a living animal receives the same space-ejection warning as other pawns");
            warning.buttonAAction();
            Call(universe, "RunFrameActions");
            Check(universe.Scatter.Candidates.Count(obj => obj.Payload?.Entity == animal) == 1
                && !transporter.innerContainer.Contains(animal), "the native tab also ejects a non-humanlike passenger exactly once");

            Pawn first = Passenger("OwnPassengerA", Intelligence.Humanlike, Faction.OfPlayer);
            Pawn second = Passenger("OwnPassengerB", Intelligence.Humanlike, Faction.OfPlayer);
            Pawn ally = Passenger("AlliedPassenger", Intelligence.Humanlike);
            shuttle.requiredPawns.Add(first);
            Check(shuttle.AllRequiredThingsLoaded, "the quest shuttle initially contains its required player pawn");

            var hostile = (Faction)Raw(typeof(Faction));
            hostile.def = new FactionDef();
            Set(hostile, "relations", new List<FactionRelation> {
                new FactionRelation { other = Faction.OfPlayer, kind = FactionRelationKind.Hostile }
            });
            hull.SetFactionDirect(hostile);
            Check(!ShuttleOccupantExitService.CanShow(transporter),
                "a hostile shuttle never exposes the player-personnel exit command");
            hull.SetFactionDirect(null);
            Check(ShuttleOccupantExitService.CanShow(transporter),
                "a factionless quest shuttle exposes the command when it contains player personnel");

            var exitGizmos = Patch_TransporterOccupantExit.Append(
                Enumerable.Empty<Gizmo>(), transporter, icon: null);
            Command_Action exit = exitGizmos.OfType<Command_Action>().Single();
            Check(exit.defaultLabel == "DeltaV_ExitOwnPersonnelFromVehicle".Translate(),
                "the transporter gizmo appends the player-personnel exit command");
            ejectionLoadLord = (Lord)Raw(typeof(Lord));
            Set(ejectionLoadLord, "curJob", new LordJob_LoadAndEnterTransporters());
            ejectionLoadLord.ownedPawns = new List<Pawn> { first, second };
            first.lord = second.lord = ejectionLoadLord;
            ejectionLordExits = 0;
            ejectionDialogs = 0;
            tractorWindow = null;
            exit.action();
            warning = tractorWindow as Dialog_MessageBox;
            Check(ejectionDialogs == 1 && warning?.buttonADestructive == true
                && transporter.innerContainer.Contains(first) && transporter.innerContainer.Contains(second),
                "one destructive confirmation covers every player pawn in the vehicle");
            frame.RepresentationVersion++;
            warning.buttonAAction();
            Call(universe, "RunFrameActions");
            Check(transporter.innerContainer.Contains(first) && transporter.innerContainer.Contains(second)
                && ejectionLordExits == 0, "a stale group request preserves every player passenger");

            exit.action();
            ((Dialog_MessageBox)tractorWindow).buttonAAction();
            Call(universe, "RunFrameActions");
            Check(!transporter.innerContainer.Contains(first) && !transporter.innerContainer.Contains(second)
                && transporter.innerContainer.Contains(ally) && ejectionLordExits == 2,
                "the vehicle command ejects all and only player-faction pawns");
            Check(shuttle.requiredPawns.Contains(first) && !shuttle.AllRequiredThingsLoaded,
                "leaving a quest shuttle does not erase required pawns or satisfy its launch condition");

            Pawn groundPassenger = Passenger("GroundPassenger", Intelligence.Humanlike, Faction.OfPlayer);
            hull.def.interactionCellOffset = IntVec3.Zero;
            shuttle.shipParent = null;
            ejectionGroundMap = map;
            ejectionGroundDrops = 0;
            ejectionDialogs = 0;
            ShuttleOccupantExitService.CommandFor(transporter, icon: null).action();
            Check(shuttle.shipParent == null && ejectionDialogs == 0 && ejectionGroundDrops == 1
                && !transporter.innerContainer.Contains(groundPassenger)
                && groundPassenger.Spawned && groundPassenger.Map == map
                && transporter.innerContainer.Contains(ally),
                "an in-bounds local shuttle unloads through its transporter without creating a transport ship");

            hull.def.interactionCellOffset = new IntVec3(1, 0, 0);
            var unloadCargo = (EvaCargoItem)Raw(typeof(EvaCargoItem));
            unloadCargo.def = TestBuildingDef("batchUnloadCargo");
            unloadCargo.def.category = ThingCategory.Item;
            unloadCargo.def.stackLimit = 75;
            unloadCargo.stackCount = 3;
            Set(typeof(Thing), unloadCargo, "mapIndexOrState", (sbyte)-1);
            transporter.innerContainer.TryAdd(unloadCargo, canMergeWithExistingStacks: false);
            transporter.groupID = 58041;
            map.lordManager ??= (LordManager)Raw(typeof(LordManager));
            map.lordManager.lords ??= new List<Lord>();
            tractorWindow = null;
            ejectionDialogs = 0;
            List<Thing> unloadSnapshot = transporter.innerContainer.ToList();
            Check(transporter.LoadingInProgressOrReadyToLaunch && transporter.Shuttle == shuttle
                && unloadSnapshot.Count == transporter.innerContainer.Count
                && unloadSnapshot.All(thing => SpaceScatterService.IsCargo(thing)
                    && thing.holdingOwner == transporter.innerContainer)
                && !(hull.InteractionCell).InBounds(map),
                "native unload fixture is a loaded shuttle with an out-of-bounds hatch and cargo-only hold");
            Check(transporter.CancelLoad(), "the native shuttle unload command enters the unified transaction");
            warning = tractorWindow as Dialog_MessageBox;
            Check(ejectionDialogs == 1 && warning?.buttonADestructive == true
                && transporter.groupID == 58041 && transporter.innerContainer.Contains(ally)
                && transporter.innerContainer.Contains(unloadCargo),
                "the native unload command warns before changing cargo or native loading state");
            warning.cancelAction();
            Call(universe, "RunFrameActions");
            Check(transporter.groupID == 58041 && transporter.innerContainer.Contains(ally)
                && transporter.innerContainer.Contains(unloadCargo),
                "cancelling native unload preserves the complete hold and loading state");

            transporter.CancelLoad();
            ((Dialog_MessageBox)tractorWindow).buttonAAction();
            Call(universe, "RunFrameActions");
            Check(transporter.groupID == -1 && !transporter.innerContainer.Contains(ally)
                && !transporter.innerContainer.Contains(unloadCargo)
                && universe.Scatter.Candidates.Any(obj => obj.Payload?.Entity == ally)
                && universe.Scatter.Candidates.Any(obj => obj.Payload?.Entity == unloadCargo),
                "confirmed native unload ejects every held thing before clearing native loading state");

            var cargoOnly = (EvaCargoItem)Raw(typeof(EvaCargoItem));
            cargoOnly.def = TestBuildingDef("cargoOnlyBatchUnload");
            cargoOnly.def.category = ThingCategory.Item;
            cargoOnly.def.stackLimit = 75;
            cargoOnly.stackCount = 2;
            Set(typeof(Thing), cargoOnly, "mapIndexOrState", (sbyte)-1);
            transporter.innerContainer.TryAdd(cargoOnly, canMergeWithExistingStacks: false);
            transporter.groupID = 58042;
            tractorWindow = null;
            ejectionDialogs = 0;
            Check(transporter.CancelLoad() && tractorWindow == null && transporter.groupID == 58042,
                "cargo-only native unload queues without a warning or premature state cleanup");
            Call(universe, "RunFrameActions");
            Check(transporter.groupID == -1 && !transporter.innerContainer.Contains(cargoOnly)
                && universe.Scatter.Candidates.Any(obj => obj.Payload?.Entity == cargoOnly),
                "cargo-only native unload reaches the same recoverable space-drift chain");

            Pawn nativeGroundPassenger = Passenger("NativeGroundPassenger", Intelligence.Humanlike, Faction.OfPlayer);
            hull.def.interactionCellOffset = IntVec3.Zero;
            transporter.groupID = 58043;
            ejectionGroundDrops = 0;
            ejectionDialogs = 0;
            tractorWindow = null;
            Check(transporter.CancelLoad() && ejectionDialogs == 0 && ejectionGroundDrops == 1
                && transporter.groupID == -1 && nativeGroundPassenger.Spawned
                && nativeGroundPassenger.Map == map && !transporter.innerContainer.Contains(nativeGroundPassenger),
                "native in-bounds unload uses the shared ThingOwner.TryDrop path and then clears loading state");

            foreach (string language in new[] { "English", "ChineseSimplified" })
            {
                var xml = XDocument.Load($"1.6/Languages/{language}/Keyed/Eva.xml");
                string text = (string)xml.Root.Element("DeltaV_EjectPassengerConfirm");
                Check(!string.IsNullOrEmpty(text) && string.Format(text, "Pyrrha").Contains("Pyrrha"),
                    language + " provides a passenger warning with their name");
                string label = (string)xml.Root.Element("DeltaV_ExitOwnPersonnelFromVehicle");
                string group = (string)xml.Root.Element("DeltaV_EjectOwnPersonnelConfirm");
                Check(!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(group)
                    && string.Format(group, 2).Contains("2"),
                    language + " provides the vehicle command and group space-exit warning");
            }
        }
        finally
        {
            ejectionGroundMap = null;
            patch.UnpatchAll(patch.Id);
        }
    }
}
