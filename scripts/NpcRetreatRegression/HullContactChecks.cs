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

internal static partial class Program
{
    static bool HullContactPocketTile(ref PlanetTile __result)
    {
        __result = PlanetTile.Invalid;
        return false;
    }

    static bool HullFixtureMass(Thing thing, StatDef stat, ref float __result)
    {
        if (stat != StatDefOf.Mass || thing.def.statBases == null) return true;
        __result = thing.def.statBases.Single(modifier => modifier.stat == stat).value;
        return false;
    }

    static void CheckHullContacts(UniverseManager universe, Faction faction)
    {
        using var fixture = new EngagementFixturePatch();
        var patches = new Harmony("DeltaV.Regression.HullContact");
        var oldDamageDef = DamageDefOf.Bullet;
        var oldConsole = ThingDefOf.PilotConsole;
        var oldMass = StatDefOf.Mass;
        FieldInfo factionManagerField = AccessTools.Field(typeof(World), "factionManager");
        object oldFactionManager = factionManagerField.GetValue(Find.World);
        bool oldDrag = DeltaVMod.CurrentSettings.passiveDrag;
        var maps = new List<Map>();
        var bodies = new List<SpaceObject>();
        int id = 52100;
        try
        {
            patches.Patch(typeof(ModsConfig).TypeInitializer,
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            patches.Patch(AccessTools.PropertyGetter(typeof(MapInfo), nameof(MapInfo.Tile)),
                prefix: new HarmonyMethod(typeof(Program), nameof(HullContactPocketTile)));
            patches.Patch(AccessTools.PropertyGetter(typeof(Thing), "FlammableNow"),
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleNotFlammable)));
            foreach (var method in new[] {
                AccessTools.Method(typeof(ImpactSoundUtility), "PlayImpactSound"),
                AccessTools.Method(typeof(DamageWatcher), "Notify_DamageTaken"),
                AccessTools.Method(typeof(Faction), "Notify_BuildingTookDamage"),
                AccessTools.Method(typeof(Building), "PostApplyDamage"),
                AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                AccessTools.Method(typeof(SpaceProjectileService), "NotifyFoundationDestroyed") })
                patches.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            patches.Patch(AccessTools.Method(typeof(RoofGrid), "SetRoof"),
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleRoof)));
            patches.Patch(AccessTools.Method(typeof(StatExtension), nameof(StatExtension.GetStatValue)),
                prefix: new HarmonyMethod(typeof(Program), nameof(HullFixtureMass)));
            patches.CreateClassProcessor(typeof(SpaceImpactEffects).Assembly
                .GetType("DeltaV.Patch_TerrainGrid_RemoveFoundation_Ownership")).Patch();
            DamageDefOf.Bullet = new DamageDef { defName = "HullContactTest", workerClass = typeof(DamageWorker), harmsHealth = true };
            ThingDefOf.PilotConsole = TestBuildingDef("hullContactConsole");
            StatDefOf.Mass = new StatDef { defName = "HullFixtureMass" };
            DeltaVMod.CurrentSettings.passiveDrag = false;
            var contacts = new SpaceCollision.HullContacts();

            ThingDef Structure(string name, bool wall = false)
            {
                var def = TestBuildingDef(name);
                def.building.isWall = wall;
                def.fillPercent = wall ? 1f : .4f;
                def.useHitPoints = def.destroyable = true;
                return def;
            }

            (SpaceObject body, Building building) ContactBody(float east, float north, int hp, Vector2 velocity,
                ThingDef def, float angle = 0f, ThingComp comp = null)
            {
                Map map = TestMap(++id, 9);
                map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher));
                maps.Add(map);
                var pose = new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(east, north)), angle)
                    { velocity = velocity };
                var body = new MapSpaceObject(id, map, pose);
                var building = PlaceHullFixtureBuilding(map, new IntVec3(4, 0, 4), def, hp, ++id, comp);
                universe.RegisterObject(body);
                bodies.Add(body);
                return (body, building);
            }

            (SpaceObject body, Building wall) Wall(float east, float north, int hp, Vector2 velocity, float angle = 0f) =>
                ContactBody(east, north, hp, velocity, Structure("contactWall" + id, wall: true), angle);

            const float dt = 1f / 60f;
            void Step()
            {
                contacts.Capture(universe);
                foreach (SpaceObject body in bodies)
                    ShipPhysics.Integrate(body, ShipPhysics.GetReport(body), Vector2.zero, 0f, dt, false, false, false);
                contacts.Resolve(dt);
            }
            void Clear()
            {
                foreach (var body in bodies) universe.UnregisterObject(body);
                foreach (var map in maps) { SpaceCollision.Forget(map); Find.Maps.Remove(map); }
                bodies.Clear();
                maps.Clear();
            }

            var a = Wall(0f, 0f, 120, new Vector2(.4f, .3f));
            var b = Wall(1.002f, 0f, 300, Vector2.zero);
            Step();
            Check(a.wall.HitPoints == 120 && b.wall.HitPoints == 300, "low-speed scrape does no structural damage");
            Check(b.body.pose.velocity.x > .1f && a.body.pose.velocity.x < .3f
                && Mathf.Abs(a.body.pose.velocity.y - .3f) < .001f
                && Mathf.Abs(a.body.pose.velocity.x + b.body.pose.velocity.x - .4f) < .001f,
                "scrape conserves momentum and transfers only the normal impulse");
            Clear();

            a = Wall(0f, 0f, 120, new Vector2(120f, 0f));
            b = Wall(2f, 0f, 300, Vector2.zero);
            Step();
            Check(a.wall.Destroyed && b.wall.HitPoints == 180,
                "a fast one-tick crossing destroys the weaker wall and deducts the same HP from the stronger wall");
            Check(SpaceImpactEffects.HullCrushBudget(.499f, 100_000) == 0
                && SpaceImpactEffects.HullCrushBudget(.5f, 100_000) == 0
                && SpaceImpactEffects.HullCrushBudget(.501f, 100) < SpaceImpactEffects.HullCrushBudget(.501f, 100_000)
                && Mathf.Abs(SpaceImpactEffects.HullFoundationBreakChance(120) - .4f) < .00001f
                && SpaceImpactEffects.HullFoundationBreakChance(300) == 1f,
                "damage is gated strictly above the threshold and foundations use independent virtual 300 HP");
            Clear();

            var turretDef = Structure("contactTurret");
            var thrusterDef = Structure("contactThruster");
            var turret = ContactBody(0f, 0f, 120, new Vector2(.4f, .3f), turretDef);
            var thruster = ContactBody(1.002f, 0f, 300, Vector2.zero, thrusterDef);
            Step();
            Check(turret.building.HitPoints == 120 && thruster.building.HitPoints == 300
                && thruster.body.pose.velocity.x > .1f,
                "exposed non-wall buildings transfer a low-speed contact impulse without taking damage");
            Clear();

            foreach (ThingDef device in new[] { turretDef, thrusterDef })
            {
                var weaker = ContactBody(0f, 0f, 120, new Vector2(120f, 0f), device);
                var stronger = ContactBody(2f, 0f, 300, Vector2.zero, device == turretDef ? thrusterDef : turretDef);
                Step();
                Check(weaker.building.Destroyed && stronger.building.HitPoints == 180,
                    $"high-speed contact directly destroys {device.defName} and deducts 120 HP from the opposing device");
                Clear();
            }

            a = Wall(0f, 0f, 600, new Vector2(120f, 0f));
            b = Wall(2f, 0f, 120, Vector2.zero);
            var machinery = Structure("contactMulticellThruster");
            machinery.size = new IntVec2(2, 1);
            var interior = PlaceHullFixtureBuilding(b.body.map, new IntVec3(5, 0, 4), machinery, 150, ++id);
            Step();
            Check(b.wall.Destroyed && interior.Destroyed && a.wall.HitPoints == 330,
                "crush continues through the breached wall and consumes one HP pool for the multi-cell thruster "
                + $"(armor {b.wall.Destroyed}, thruster {interior.Destroyed}, opposing HP {a.wall.HitPoints})");
            Clear();

            foreach (bool canBoard in new[] { false, true })
            {
                a = Wall(0f, 0f, 120, new Vector2(120f, 0f));
                var shuttle = ContactBody(2f, 0f, 300, Vector2.zero, Structure("contactShuttle" + id),
                    comp: new CompSpaceShuttle { props = new CompProperties_SpaceShuttle { canBoard = canBoard } });
                Step();
                Check(a.wall.HitPoints == 120 && shuttle.building.HitPoints == 300
                    && shuttle.body.pose.velocity == Vector2.zero,
                    $"shuttle (boarding capable: {canBoard}) stays out of passive hull contacts");
                Clear();
            }

            a = Wall(0f, 0f, 300, new Vector2(.4f, 0f));
            b = Wall(1.002f, 0f, 300, Vector2.zero);
            var c = Wall(2.003f, 0f, 300, Vector2.zero);
            Step();
            Check(c.body.pose.velocity.x > .07f
                && Mathf.Abs(a.body.pose.velocity.x + b.body.pose.velocity.x + c.body.pose.velocity.x - .4f) < .001f
                && OrbitSphere.ArcCells(a.body.pose.position, b.body.pose.position) >= .9995f
                && c.wall.HitPoints == 300, "A pushes B into previously stationary C in the remaining part of the same tick");
            Clear();

            a = Wall(0f, 0f, 120, new Vector2(180f, 0f));
            b = Wall(2f, 1.25f, 300, Vector2.zero, .15f);
            Step();
            Check(!a.wall.Destroyed && b.wall.HitPoints == 300 && b.body.pose.velocity == Vector2.zero,
                "rotated corner near miss remains a miss");
            Clear();

            a = Wall(0f, 0f, 120, Vector2.zero);
            b = Wall(1.13f, 0f, 300, Vector2.zero);
            a.body.pose.angularVelocity = Mathf.PI / (2f * dt);
            Step();
            Check(a.wall.HitPoints < 120 && b.wall.HitPoints < 300
                && 120 - a.wall.HitPoints == 300 - b.wall.HitPoints,
                "a rotating corner can hit in the middle of a tick with both endpoints separated");
            Clear();

            void Hammerhead(bool narrow)
            {
                Map NewMap()
                {
                    var map = TestMap(++id, 56);
                    var terrain = map.terrainGrid.topGrid[0];
                    Array.Fill(map.terrainGrid.topGrid, terrain);
                    map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher));
                    map.roofGrid = new RoofGrid(map);
                    maps.Add(map);
                    return map;
                }
                SpaceObject AddBody(Map map, float north, float speed)
                {
                    var pose = new SpacePose(OrbitSphere.Move(Vector3.forward, Vector2.up * north), 0f)
                        { velocity = Vector2.up * speed };
                    var body = new MapSpaceObject(++id, map, pose);
                    universe.RegisterObject(body);
                    bodies.Add(body);
                    return body;
                }
                var prefab = XDocument.Load("Common/Prefabs/Special/HammerheadShark.xml").Root;
                var placements = prefab.Element("thingStates").Elements().ToArray();
                var defs = ReadHammerheadHullDefs(placements);
                var foundation = (TerrainDef)Raw(typeof(TerrainDef));
                foundation.isFoundation = true;
                foundation.tags = new List<string> { "Substructure" };
                Dictionary<IntVec3, BattleBuilding> PlaceHammerhead(Map map, bool mirror)
                {
                    IntVec3 Transform(IntVec3 cell) => mirror ? new IntVec3(cell.x, 0, 50 - cell.z) : cell;
                    foreach (var entry in prefab.Element("foundations").Elements())
                    {
                        var cell = Transform(ParseHelper.FromString<IntVec3>((string)entry.Element("pos")));
                        map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] = foundation;
                    }
                    var walls = new Dictionary<IntVec3, BattleBuilding>();
                    foreach (var entry in placements)
                    {
                        var key = ParseHelper.FromString<IntVec3>((string)entry.Element("pos"));
                        var def = defs[(string)entry.Element("def")];
                        int hp = (int)entry.Element("hitPoints");
                        var body = PlaceHullFixtureBuilding(map, Transform(key), def, hp, ++id);
                        if (def.IsWall) walls.Add(key, body);
                    }
                    return walls;
                }
                Map own = NewMap(), defender = NewMap();
                var head = PlaceHammerhead(own, false);
                var ownBody = AddBody(own, 0f, 30f);
                var targetWalls = new List<BattleBuilding>();
                if (narrow)
                {
                    const int length = 16;
                    for (int i = 0; i < length; i++)
                        targetWalls.Add(PlaceHullFixtureBuilding(defender, new IntVec3(21, 0, 13 + i),
                            defs["GravshipHull"], 420, ++id));
                    float targetMass = ShipPhysics.GetReport(ownBody).mass;
                    var ballast = TestBuildingDef("hammerheadMassMatch");
                    ballast.statBases = new List<StatModifier>
                        { new StatModifier { stat = StatDefOf.Mass, value = targetMass - length * ShipPhysics.MassPerBuilding } };
                    PlaceHullFixtureBuilding(defender, new IntVec3(21, 0, 29), ballast, 100, ++id);
                }
                else targetWalls.AddRange(PlaceHammerhead(defender, true).Values);
                AddBody(defender, 25.25f, 0f);
                Step();
                int targetLoss = targetWalls.Sum(wall => 420 - wall.HitPoints);
                if (narrow)
                {
                    int broken = targetWalls.Count(wall => wall.Destroyed);
                    Check(broken >= 10 && head.Where(pair => pair.Key.x != 21).All(pair => !pair.Value.Destroyed),
                        $"narrow 30 cells/s collision crushes at least ten column cells without stripping adjacent armor (broke {broken})");
                }
                else
                {
                    var firstThree = head.Where(pair => pair.Key.z >= 35).ToArray();
                    Check(firstThree.Length == 51 && firstThree.All(pair => pair.Value.Destroyed)
                        && head.Where(pair => pair.Key.z == 34).All(pair => !pair.Value.Destroyed)
                        && targetLoss == head.Values.Sum(wall => 420 - wall.HitPoints),
                        $"30 cells/s strips the first three Hammerhead rows, leaves the fourth, and shares damage "
                        + $"(mass {ShipPhysics.GetReport(ownBody).mass:F1} after impact, destroyed {firstThree.Count(pair => pair.Value.Destroyed)}/51, "
                        + $"fourth {head.Count(pair => pair.Key.z == 34 && pair.Value.Destroyed)}, target {targetLoss})");
                }
                Clear();
            }
            Hammerhead(false);
            Hammerhead(true);

            var pilot = AddTestHull(TestMap(++id), faction, id, new CompSpaceShuttle
                { props = new CompProperties_SpaceShuttle { canBoard = true } }).Vessel;
            maps.Add(pilot.Map);
            var target = AddTestHull(TestMap(++id), faction, id).Vessel;
            maps.Add(target.Map);
            var third = AddTestHull(TestMap(++id), faction, id).Vessel;
            maps.Add(third.Map);
            var factionManager = (FactionManager)Raw(typeof(FactionManager));
            Set(factionManager, "ofPlayer", faction);
            factionManagerField.SetValue(Find.World, factionManager);
            SpaceObject Frame(ShipBase ship, float east)
            {
                var frame = new MapSpaceObject(++id, ship.Map,
                    new SpacePose(OrbitSphere.Move(Vector3.forward, Vector2.right * east), 0f));
                universe.RegisterObject(frame);
                bodies.Add(frame);
                return frame;
            }
            SpaceObject pilotFrame = Frame(pilot, -25f);
            Frame(target, 0f);
            Frame(third, 20f);
            var pilotControl = universe.Controls.PlayerFor(pilot);
            var otherControl = universe.Controls.PlayerFor(third);
            PilotPanelAction AvoidanceButton(PlayerShipController controller, ShipBase ship)
            {
                var actions = new List<PilotPanelAction>();
                controller.CollectPanelActions(PilotTarget.Of(ship), actions);
                return actions.Find(action => action.Tooltip == "DeltaV_HullAvoidanceTip".Translate());
            }
            bool RouteClearAt(PlayerShipController controller, ShipBase ship, ShipBase obstacle)
            {
                var pose = ShipRegistry.ShipPose(obstacle);
                var route = CombatFlightPlanner.Brake(pose, null, 0);
                route.localAnchor = SpaceTransform.MapToShipLocal(ship.Map, ship.HullCenter);
                return (bool)Call(controller.AutoManeuver, "RouteClear", ship, route, default(DriveSubject), 0);
            }
            Check(!RouteClearAt(pilotControl, pilot, target) && !RouteClearAt(pilotControl, pilot, third),
                "normal combat routing avoids both obstructions");
            var disable = AvoidanceButton(pilotControl, pilot);
            Check(!pilotControl.FireControl.Target.HasLock && disable.Execute != null
                && disable.Label == "DeltaV_HullAvoidanceDisable".Translate(),
                "a boarding shuttle can disable collision avoidance without a target");
            Check(AvoidanceButton(otherControl, third).Execute == null,
                "an ordinary shuttle has no collision avoidance override");
            universe.Shuttles.ToggleIntrusionArmed(pilotFrame);
            Check(!universe.Shuttles.CanStartIntrusion(pilot, pilotFrame, true),
                "even an armed boarding shuttle cannot start a player intrusion with avoidance enabled");
            disable.Execute();
            Check(universe.Shuttles.CanStartIntrusion(pilot, pilotFrame, true)
                && !universe.Shuttles.CanStartIntrusion(third, universe.FrameOf(third.Map), false),
                "disabling avoidance enables player intrusion only on boarding-capable hulls");
            Check(RouteClearAt(pilotControl, pilot, target) && RouteClearAt(pilotControl, pilot, third),
                "disabling avoidance allows routes through all obstructions including friendly ships");
            Check(otherControl.AutoManeuver.AvoidHullCollisions && !RouteClearAt(otherControl, third, target),
                "another player's shuttle still avoids obstructions with its independent setting");
            pilotControl.AutoManeuver.Clear(pilot);
            var restore = AvoidanceButton(pilotControl, pilot);
            Check(restore.Execute != null && restore.Label == "DeltaV_HullAvoidanceRestore".Translate(),
                "clearing the combat plan retains the shuttle's setting");
            restore.Execute();
            Check(!RouteClearAt(pilotControl, pilot, target) && !RouteClearAt(pilotControl, pilot, third),
                "restoring avoidance rejects both obstructed routes again");
            Check(!universe.Shuttles.CanStartIntrusion(pilot, pilotFrame, true),
                "restoring avoidance rejects a previously selected boarding order");
            Clear();
        }
        finally
        {
            foreach (var body in bodies) universe.UnregisterObject(body);
            foreach (var map in maps) { SpaceCollision.Forget(map); Find.Maps.Remove(map); }
            DamageDefOf.Bullet = oldDamageDef;
            ThingDefOf.PilotConsole = oldConsole;
            StatDefOf.Mass = oldMass;
            factionManagerField.SetValue(Find.World, oldFactionManager);
            DeltaVMod.CurrentSettings.passiveDrag = oldDrag;
            patches.UnpatchAll(patches.Id);
        }
    }

    static BattleBuilding PlaceHullFixtureBuilding(Map map, IntVec3 cell, ThingDef def, int hp, int id,
        ThingComp comp = null)
    {
        var building = (BattleBuilding)Raw(typeof(BattleBuilding));
        building.def = def;
        building.thingIDNumber = id;
        building.HitPoints = hp;
        Set(typeof(Thing), building, "positionInt", cell);
        comp ??= new EmptyComp();
        comp.parent = building;
        Set(typeof(ThingWithComps), building, "comps", new List<ThingComp> { comp });
        PlaceFixtureBuilding(building, map);
        return building;
    }

    static Dictionary<string, ThingDef> ReadHammerheadHullDefs(IEnumerable<XElement> placements)
    {
        var names = new HashSet<string>(placements.Select(entry => (string)entry.Element("def")));
        string data = Path.Combine(Directory.GetParent(Environment.GetCommandLineArgs()[1]).Parent.FullName, "Data");
        var masses = new Dictionary<string, float>();
        var paths = new[] { Path.Combine(data, "Core", "Defs"), Path.Combine(data, "Odyssey", "Defs"), "1.6/Defs" };
        foreach (string dir in paths)
        foreach (string file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
        foreach (var node in XDocument.Load(file).Root.Elements("ThingDef"))
        {
            string name = (string)node.Element("defName");
            var mass = node.Element("statBases")?.Element("Mass");
            if (name != null && mass != null && names.Contains(name)) masses[name] = (float)mass;
        }
        return names.ToDictionary(name => name, name =>
        {
            var def = TestBuildingDef(name);
            def.building.isWall = name == "GravshipHull";
            def.useHitPoints = def.destroyable = true;
            if (masses.TryGetValue(name, out float mass))
                def.statBases = new List<StatModifier> { new StatModifier { stat = StatDefOf.Mass, value = mass } };
            return def;
        });
    }
}
