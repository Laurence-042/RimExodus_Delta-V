using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DeltaV;
using DriveInfo = DeltaV.DriveInfo;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// Exercises the built production DLL, with an empty game and cached sphere scale.
// Does not substitute navigation/retirement logic or run map restoration without Unity.
internal static partial class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    static int checks;
    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { Run(args.Contains("--aim-input-only"), args.Contains("--player-flight-only"), args.Contains("--salvage-only"),
            args.Contains("--drive-performance-only"), args.Contains("--projectile-only"), args.Contains("--archive-recovery-only"),
            args.Contains("--docking-only"), args.Contains("--npc-only"), args.Contains("--projectile-full-only"));
            Console.WriteLine($"PASS {checks} regression checks (production DLL)"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static bool ReadSceneLanguage(ref LanguageWorker __result) { __result = new LanguageWorker_English(); return false; }
    static bool ReadSceneColonists(ref ColonistBar __result) { __result = aimColonists; return false; }
    static object Raw(Type type) => RuntimeHelpers.GetUninitializedObject(type);
    static void Set(object owner, string name, object value) => owner.GetType()
        .GetField(name, Private | BindingFlags.Public).SetValue(owner, value);
    static object Call(object owner, string name, params object[] args) => owner.GetType()
        .GetMethods(Private).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner, args);
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    static List<Action> Actions(UniverseManager universe) => (List<Action>)typeof(UniverseManager)
        .GetField("frameActions", Private).GetValue(universe);
    static SpaceObject Home(UniverseManager universe, SpaceObject own, Faction faction) =>
        (SpaceObject)Call(universe.NpcShips, "ResolveRetreatHome", own, faction);

    static void Run(bool aimInputOnly, bool playerFlightOnly, bool salvageOnly, bool drivePerformanceOnly, bool projectileOnly,
        bool archiveRecoveryOnly, bool dockingOnly, bool npcOnly, bool projectileFullOnly)
    {
        // Vanilla log rendering needs Unity; all assertions and production control paths remain active.
        using var logs = Log.LockMessages();
        DeepProfiler.enabled = false;
        var mod = (DeltaVMod)Raw(typeof(DeltaVMod));
        typeof(DeltaVMod).GetField("<Settings>k__BackingField", Private)
            .SetValue(mod, new DeltaVSettings());
        ((Dictionary<Type, Mod>)typeof(LoadedModManager).GetField("runningModClasses", Private)
            .GetValue(null))[typeof(DeltaVMod)] = mod;
        var game = (Game)Raw(typeof(Game));
        Set(game, "maps", new List<Map>());
        var world = (World)Raw(typeof(World));
        Set(game, "worldInt", world);
        Current.Game = game;
        var layer = (PlanetLayer)Raw(typeof(PlanetLayer));
        FieldInfo tiles = typeof(PlanetLayer).GetField("tileIDToNeighbors_offsets", Private | BindingFlags.Public);
        object array = Activator.CreateInstance(tiles.FieldType);
        Set(array, "m_Length", 1);
        tiles.SetValue(layer, array);
        typeof(OrbitSphere).GetField("cachedWorld", Private).SetValue(null, world);
        typeof(OrbitSphere).GetField("cachedLayer", Private).SetValue(null, layer);
        ((Dictionary<PlanetLayer, float>)typeof(OrbitSphere).GetField("RadiusByLayer", Private)
            .GetValue(null))[layer] = 10000f;
        ((Dictionary<PlanetLayer, float>)typeof(OrbitSphere).GetField("WorldPerCellByLayer", Private)
            .GetValue(null))[layer] = 1f;
        Check(OrbitSphere.Valid, "real sphere geometry enabled");

        var faction = (Faction)Raw(typeof(Faction));
        var other = (Faction)Raw(typeof(Faction));
        var universe = new UniverseManager(game);
        game.components = new List<GameComponent> { universe, new RimExodus.Maps.MapRuntime(game) };
        game.tickManager = (TickManager)Raw(typeof(TickManager));
        // Scene-free lifecycle fixtures still emit ordinary colonist-bar dirtiness notifications.
        aimColonists = (ColonistBar)Raw(typeof(ColonistBar));
        new HarmonyLib.Harmony("DeltaV.Regression.SceneUi").Patch(
            HarmonyLib.AccessTools.PropertyGetter(typeof(Find), nameof(Find.ColonistBar)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(ReadSceneColonists)));
        // The full suite outlives individual menu patches; production decision text also
        // formats translations. Supply the scene-free language boundary for the whole run.
        new HarmonyLib.Harmony("DeltaV.Regression.SceneLanguage").Patch(
            HarmonyLib.AccessTools.PropertyGetter(typeof(Find), nameof(Find.ActiveLanguageWorker)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(ReadSceneLanguage)));
        if (Environment.GetCommandLineArgs().Contains("--eva-only"))
        { CheckEva(universe, faction); return; }
        if (Environment.GetCommandLineArgs().Contains("--tractor-only"))
        { CheckTractorControl(universe, faction, other); CheckPlacementGeometry(); CheckSmallScaleMovement(); return; }
        if (Environment.GetCommandLineArgs().Contains("--thruster-pause-only"))
        {
            CheckDriveExecutionReuse(faction); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--separation-only"))
        {
            ThingDefOf.PilotConsole = TestBuildingDef("console");
            ThingDefOf.GravEngine = TestBuildingDef("gravEngine");
            CheckSeparationRules(universe, faction); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--combat-performance-only"))
        {
            DeltaVMod.CurrentSettings.logPerformance = Environment.GetCommandLineArgs().Contains("--combat-probe-enabled");
            CheckCombatPerformance(universe, faction, other); return;
        }
        CheckPerformanceProbe(universe);
        if (Environment.GetCommandLineArgs().Contains("--performance-probe-only")) return;
        if (Environment.GetCommandLineArgs().Contains("--combat-maneuver-only"))
        { CheckCombatManeuverMotion(universe, faction, other); return; }
        CheckCombatCurves();
        if (!npcOnly)
        {
        if (archiveRecoveryOnly) { CheckArchiveRecovery(universe); return; }
        if (dockingOnly)
        {
            ThingDefOf.PilotConsole = TestBuildingDef("console");
            CheckDockingSystem(universe, dockingOnly: true);
            return;
        }
        if (playerFlightOnly) { CheckPlayerFlightOrders(universe); return; }
        if (salvageOnly) { CheckSalvageOutput(universe); return; }
        if (projectileOnly) { CheckProjectileFlight(universe, faction, flightOnly: true); return; }
        if (projectileFullOnly)
        {
            ThingDefOf.PilotConsole = TestBuildingDef("console");
            CheckProjectileFlight(universe, faction);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--globe-only"))
        {
            ThingDefOf.PilotConsole = TestBuildingDef("console");
            Find.World.factionManager = (FactionManager)Raw(typeof(FactionManager));
            Set(Find.World.factionManager, "ofPlayer", faction);
            CheckGlobeAndInput(universe); CheckGlobeNavigation(universe);
            CheckSmallScaleMovement(); CheckAimUiCoordinates(); return;
        }
        CheckAimUiCoordinates();
        CheckWeaponControlFixes(universe, faction, other);
        if (aimInputOnly) return;
        CheckObjectReferences(universe, faction, other);
        CheckShipCapabilities(universe, faction);
        CheckShuttleLifecycle(universe, faction);
        CheckFoundationLifecycle(universe, faction, other);
        CheckSeparationRules(universe, faction);
        var combatStats = new HarmonyLib.Harmony("DeltaV.Regression.ExpansionStats");
        combatStats.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(Thing), "MaxHitPoints"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Program), nameof(TestHullMaxHitPoints)));
        try { CheckCombatTargets(universe, faction, other); CheckPerShipFireControl(universe, faction, other); }
        finally { combatStats.UnpatchAll(combatStats.Id); }
        CheckSharedPdCandidates(universe, faction, other);
        CheckDriveExecutionReuse(faction);
        CheckWeaponExecution(universe, faction);
        if (drivePerformanceOnly) { CheckMotionFeedback(universe); return; }
        CheckStructureSelection(universe, faction, other);
        CheckSceneReset(universe);
        CheckMapPool(universe);
        CheckDockingSystem(universe);
        CheckDriftSync();
        CheckArchiveRecovery(universe);
        CheckProjectileFlight(universe, faction);
        CheckMotionFeedback(universe);
        CheckGlobeAndInput(universe);
        CheckGlobeNavigation(universe);
        }
        SpaceObject Make(int id, float distance, string type = null, Faction owner = null)
        {
            var obj = new SpaceObject(id, new SpacePose(
                OrbitSphere.Move(Vector3.forward, new Vector2(distance, 0)), 0f))
                { contentTypeId = type, faction = owner };
            universe.objects.Add(obj);
            Set(universe, "objectById", null); // Fixture bypasses RegisterObject.
            return obj;
        }
        var ship = new ArchivedSpaceObject { objectId = 1, pose = new SpacePose(Vector3.forward, 0f) };
        universe.objects.Add(ship);
        Set(universe, "objectById", null);
        SpaceObject trade = Make(2, 10, "TradeStation", faction);
        SpaceObject factory = Make(3, 20, "FactoryStation", faction);
        SpaceObject hostile = Make(4, 30, "Shipyard", other);
        SpaceObject highId = Make(9, 100, "Shipyard", faction);
        SpaceObject lowId = Make(8, 100, "Shipyard", faction);
        Check(Home(universe, ship, faction) == lowId, "only same-faction shipyard; ties use ID");
        lowId.faction = other;
        Check(Home(universe, ship, faction) == highId, "captured home retargets");
        SpaceObject materialized = Make(10, 100, "Shipyard", faction);
        Call(universe.NpcShips, "NotifyMaterialized", highId, materialized);
        universe.objects.Remove(highId);
        Check(Home(universe, ship, faction) == materialized, "marker replacement preserves home identity");
        universe.objects.Remove(materialized);
        Set(universe, "objectById", null);
        Check(Home(universe, ship, faction) == null && ship.npcHomeObjectId == 0,
            "missing home does not select trade/factory/hostile station");

        ship.npcRetreating = true;
        ship.mapData = "opaque archive must survive navigation";

        ship.npcShips.Add(new NpcShipCombatSummary
            { shipId = 100, initialMainConsoleId = 101, mainConsoleId = 102,
                observedThrust = 2000f });
        ship.observedMainConsoleId = 102;
        ship.observedThrust = 2000f;
        var report = new ShipPhysics.MassReport { mass = 100f, inertia = 1000f };
        foreach (float sign in new[] { -1f, 1f })
        foreach (float side in new[] { -1f, 1f })
        {
            report.thrusters.Add(new ShipPhysics.Thruster { dir = new Vector2(sign, 0),
                localPos = new Vector2(0, side * 5), thrust = 2000, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { dir = new Vector2(0, sign),
                localPos = new Vector2(side * 5, 0), thrust = 2000, operational = true });
        }
        ship.dynamics = DriveSnapshot.Capture(new DriveInfo(report), 100);
        Check(ship.dynamics.positions.Count == report.thrusters.Count && report.thrusters.All(t => t.throttle == 0),
            "capture preserves complete numeric layout without commanding thrust");
        var powerless = DriveSnapshot.Capture(new DriveInfo(new ShipPhysics.MassReport { mass = 100, inertia = 1000 }), 100);
        var oneWay = new ShipPhysics.MassReport { mass = 100, inertia = 1000 };
        oneWay.thrusters.Add(new ShipPhysics.Thruster { dir = Vector2.right, thrust = 2000, operational = true });
        var oneWaySummary = DriveSnapshot.Capture(new DriveInfo(oneWay), 100);
        new DriveInfo(oneWaySummary.Report).Preview(new ShipPhysics.Command { fx = -1, fz = 1, torque = 1 },
            out Vector2 limitedForce, out float limitedTorque);
        Check(limitedForce.x >= 0 && limitedForce.y == 0 && limitedTorque == 0,
            "summary preserves missing reverse, lateral and turning authority");
        foreach (var layout in new[] { report, oneWay, powerless.Report })
        {
            // Offset COM and disable one thruster: same allocator must produce the same wrench.
            foreach (var offset in new[] { Vector2.zero, new Vector2(2.5f, -1.3f) })
            {
                layout.comLocal = offset;
                if (layout.thrusters.Count > 1) layout.thrusters[0].operational = false;
                var info = new DriveInfo(layout);
                var snapshot = DriveSnapshot.Capture(info, 100);
                var archivedInfo = new DriveInfo(snapshot.Report);
                bool equal = true;
                for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++) for (int w = -1; w <= 1; w++)
                {
                    var command = new ShipPhysics.Command { fx = x, fz = z, torque = w };
                    info.Preview(command, out var expectedF, out var expectedT);
                    archivedInfo.Preview(command, out var actualF, out var actualT);
                    equal &= (expectedF - actualF).magnitude < 0.001f && Math.Abs(expectedT - actualT) < 0.001f;
                }
                Check(equal, "numeric layout allocation matches real layout including asymmetric COM");
                info.PreviewModel(); info.PreviewBraking(Vector2.right); info.PreviewAngular(1);
                Check(layout.thrusters.All(t => t.throttle == 0f) && layout.throttlesWrittenTick == -1,
                    "all preview operations leave throttles and presentation timestamp untouched");
            }
        }
        report.comLocal = Vector2.zero;
        report.thrusters[0].operational = true;
        ship.dynamics = DriveSnapshot.Capture(new DriveInfo(report), 100);
        var driver = new ArchivedSpaceObject { objectId = 70,
            pose = new SpacePose(Vector3.forward, 0f), dynamics = DriveSnapshot.Capture(new DriveInfo(report), 700) };
        universe.objects.Add(driver);
        Check(!driver.TryGetDrive(new DriveSubject(70, 700, true), out _)
            && !driver.TryGetDrive(new DriveSubject(70, 701), out _),
            "subject identity separates owner IDs and shuttle IDs");
        var control = new DriveControl { force = new Vector2(8000, 0) };
        Set(game.tickManager, "ticksGameInt", 1);
        Check(driver.ApplyDrive(driver.PrimaryDriver, control, 1f / 60f, out _) == DriveResult.Executed,
            "object executes drive request through shared allocator");
        SpacePose afterControl = driver.pose;
        driver.AdvanceDrift(1f / 60f);
        Check(driver.pose.Equals(afterControl)
            && driver.ApplyDrive(driver.PrimaryDriver, control, 1f / 60f, out _) == DriveResult.AlreadyAdvanced,
            "drive plus drift plus another driver cannot integrate the container twice");
        Check(driver.MotionInfo.thrusters.All(t => t.throttle == 0f) && !driver.MotionInfo.HasFreshThrottles,
            "archive execution never commits real throttle or presentation state");
        foreach (var t in driver.dynamics.Report.thrusters) t.operational = false;
        driver.dynamics.Report.version++;
        driver.pose.velocity = Vector2.zero; driver.pose.angularVelocity = 0f;
        Set(game.tickManager, "ticksGameInt", 2);
        driver.ApplyDrive(driver.PrimaryDriver, control, 1f / 60f, out _);
        Check(driver.pose.velocity == Vector2.zero && driver.pose.angularVelocity == 0f,
            "capability version change invalidates cached allocation inside a microstep");
        driver.ClearDrive();
        driver.pose.velocity = Vector2.right;
        Set(game.tickManager, "ticksGameInt", 3);
        driver.AdvanceDrift(1f / 60f);
        Check(driver.pose.velocity.x > 0f, "clearing control preserves passive momentum");
        var powerlessBody = new ArchivedSpaceObject { objectId = 71,
            pose = new SpacePose(Vector3.forward, 0f), dynamics = powerless };
        powerlessBody.ApplyDrive(powerlessBody.PrimaryDriver, control, 1f / 60f, out _);
        Check(powerlessBody.pose.velocity == Vector2.zero && powerlessBody.pose.angularVelocity == 0f,
            "zero thrust cannot create propulsion");
        universe.objects.Remove(driver);
        Set(universe, "objectById", null);
        Set(game.tickManager, "ticksGameInt", 0);
        bool duplicateIntegration = false;
        void Advance(int ticks)
        {
            if (ticks == 0) ship.AdvanceDrift(0f);
            for (int tick = 0; tick < ticks; tick++)
            {
                Set(game.tickManager, "ticksGameInt", game.tickManager.TicksGame + 1);
                if (tick % 60 == 0) Call(universe.NpcShips, "DecideArchivedRoute", ship);
                universe.Autopilot.Tick();
                bool controlled = universe.Autopilot.IsActive(ship);
                Vector3 integrated = ship.pose.position;
                ship.AdvanceDrift(1f / 60f);
                Call(universe.NpcShips, "NotifyArchivedMovement", ship);
                if (controlled && integrated != ship.pose.position) duplicateIntegration = true;
            }
        }
        var start = ship.pose.position;
        Advance(0);
        Check(ship.pose.position == start, "zero elapsed ticks freeze navigation");
        DeltaVMod.CurrentSettings.lifecycleScanIntervalTicks = 1;
        Advance(60);
        Vector3 fastScanPosition = ship.pose.position;
        universe.Autopilot.ResetFlow();
        Set(game.tickManager, "ticksGameInt", 0);
        ship.pose = new SpacePose(start, 0f);
        DeltaVMod.CurrentSettings.lifecycleScanIntervalTicks = 600;
        Advance(60);
        Check(OrbitSphere.ArcCells(fastScanPosition, ship.pose.position) < 0.01f,
            "lifecycle scan interval does not change archived movement");
        Check(!duplicateIntegration, "autopilot-controlled archive is not integrated twice");
        DeltaVMod.CurrentSettings.lifecycleScanIntervalTicks = 60;
        for (int i = 0; i < 3; i++) Advance(1500);
        Check(universe.objects.Contains(ship) && ship.mapData != null
            && (Actions(universe)?.Count ?? 0) == 0,
            "long no-home voyage and repeated fallback arrivals never retire");
        ship.observedThrust = 0;
        ship.dynamics = powerless;
        ship.pose = new SpacePose(Vector3.forward, 0f) { velocity = new Vector2(1000, 0) };
        DeltaVMod.CurrentSettings.passiveDrag = false;
        Advance(1500);
        Check(OrbitSphere.ArcCells(start, ship.pose.position) > OrbitSphere.CellsPerTile
            && universe.objects.Contains(ship),
            "retreat survives travel beyond one sector scale");
        DeltaVMod.CurrentSettings.passiveDrag = true;
        ship.observedThrust = 2000;
        ship.dynamics = DriveSnapshot.Capture(new DriveInfo(report), 100);

        ship.pose = new SpacePose(Vector3.forward, 0f);
        universe.Autopilot.ResetFlow();
        SpaceObject yard = Make(11, 100, "Shipyard", faction);
        Advance(30);
        Check(OrbitSphere.ArcCells(ship.pose.position, yard.pose.position) > 60f,
            "travel does not retire ahead of arrival");
        yard.pose.position = OrbitSphere.Move(Vector3.forward, new Vector2(300, 0));
        Advance(30);
        Check(universe.Autopilot.IsActive(ship.PrimaryDriver),
            "mapless ship uses the shared autopilot task table");
        Advance(2000);
        Check(OrbitSphere.ArcCells(ship.pose.position, yard.pose.position) < 5f,
            "shared MPC follows moved shipyard and brakes at destination");
        Advance(60);
        Check(Actions(universe).Count == 1 && universe.objects.Contains(ship),
            "arrival queues one retirement and keeps archive until frame boundary");
        yard.faction = other;
        Call(universe, "RunFrameActions");
        Check(universe.objects.Contains(ship) && ship.mapData != null,
            "frame-boundary ownership recheck cancels retirement");
        Advance(60);
        Check((Actions(universe)?.Count ?? 0) == 0 && ship.npcHomeObjectId == 0,
            "lost home renews flight instead of retiring at obsolete coordinates");

        ship.observedThrust = 0f;
        ship.dynamics = powerless;
        ship.pose.velocity = new Vector2(3, 0);
        ship.pose.angularVelocity = 0f;
        start = ship.pose.position;
        Advance(60);
        Check(OrbitSphere.Offset(start, ship.pose.position).magnitude > 2f
            && !universe.Autopilot.IsActive(ship.PrimaryDriver) && universe.objects.Contains(ship),
            "no-thrust archive drifts and is retained");

        ship.observedThrust = 2000f;
        ship.dynamics = DriveSnapshot.Capture(new DriveInfo(report), 100);
        Advance(1);
        Check(universe.Autopilot.IsActive(ship.PrimaryDriver), "normal AI decision reconstructs navigation task");
        universe.Autopilot.ResetFlow();
        Advance(1);
        Check(universe.Autopilot.IsActive(ship.PrimaryDriver), "lost runtime task is rebuilt from observations by AI");
        ship.npcHomeObjectId = yard.objectId;
        ship.pose.position = yard.pose.position;
        yard.faction = faction;
        Call(universe.NpcShips, "NotifyArchivedMovement", ship);
        Check(Actions(universe).Count == 1, "retirement action exists before representation transition");
        int representationBefore = ship.RepresentationVersion;
        var mapped = ship.RestoreRepresentation(null);
        Call(universe, "ReplaceRepresentation", ship, mapped);
        Check(universe.ObjectById(ship.objectId) == mapped && mapped.pose.Equals(ship.pose)
            && mapped.RepresentationVersion == representationBefore + 1 && !universe.Autopilot.IsActive(ship.PrimaryDriver),
            "same-ID representation replacement preserves pose and removes stale task");
        Call(universe, "ReplaceRepresentation", mapped, ship);
        Call(universe, "RunFrameActions");
        Check(ship.mapData != null && Actions(universe).Count == 0,
            "old retirement closure cannot retire a newer representation with the same ID");
        Check(universe.ObjectById(ship.objectId) == ship && ship.RepresentationVersion == representationBefore + 2,
            "representation versions invalidate old queued work");
        ship.npcRetreating = true;
        ship.npcHomeObjectId = 987;
        Call(universe.NpcShips, "PrepareForArchive", mapped, ship);
        Check(ship.npcRetreating && ship.npcHomeObjectId == 987,
            "archive observation does not evaluate retreat or select a route");
        Call(universe.NpcShips, "DecideArchivedRoute", ship);
        Check(!ship.npcRetreating && ship.npcHomeObjectId == 0,
            "only the normal AI decision consumes the new observations");

        object[] evaluation = { 0f, 0f, 101, 2000f, 0f, 101, null };
        Check(!(bool)Call(universe.NpcShips, "EvaluateRetreat", evaluation),
            "unchanged main and zero initial capabilities do not trigger retreat");
        evaluation = new object[] { 0f, 0f, 101, 2000f, 0f, 102, null };
        Check((bool)Call(universe.NpcShips, "EvaluateRetreat", evaluation),
            "backup-console succession triggers retreat floor");
        ship.npcShips.Add(new NpcShipCombatSummary { shipId = 100,
            initialMainConsoleId = 101, mainConsoleId = 102 });
        ship.npcShips.Add(new NpcShipCombatSummary { shipId = 200,
            initialMainConsoleId = 201, mainConsoleId = 201 });
        evaluation = new object[] { ship, null };
        Check((bool)Call(universe.NpcShips, "EvaluateRetreat", evaluation)
            && ship.npcShips[0].retreating && !ship.npcShips[1].retreating,
            "archived retreat summaries remain independent per ship");
    }
}
