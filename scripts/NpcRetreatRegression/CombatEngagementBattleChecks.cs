using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

internal static partial class Program
{
    // Only scene notifications and destruction bookkeeping are adapted. TakeDamage,
    // DamageWorker, fire control, shot preparation, collision and guidance stay live.
    sealed class BattleBuilding : Building
    {
        public override int HitPoints
        {
            get => (int)AccessTools.Field(typeof(Thing), "hitPointsInt").GetValue(this);
            set => Set(typeof(Thing), this, "hitPointsInt", value);
        }
        public override void Kill(DamageInfo? info = null, Hediff culprit = null)
        {
            var map = Map;
            map.thingGrid.Deregister(this);
            Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2);
            SubstructureOwnership.BumpStructureVersion();
        }
    }
    static bool BattleNotFlammable(ref bool __result) { __result = false; return false; }
    static bool BattleSceneOnly() => false;
    static bool BattleRoof(IntVec3 c, RoofDef def, Map ___map, RoofDef[] ___roofGrid)
    { ___roofGrid[___map.cellIndices.CellToIndex(c)] = def; return false; }

    static void CheckEngagementBattle(UniverseManager universe, Faction player, Faction enemy)
    {
        using var fixture = new EngagementFixturePatch();
        var patch = new Harmony("DeltaV.Regression.EngagementBattle");
        patch.Patch(typeof(ModsConfig).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        foreach (var method in new[] {
            AccessTools.Method(typeof(ImpactSoundUtility), "PlayImpactSound"),
            AccessTools.Method(typeof(DamageWatcher), "Notify_DamageTaken"),
            AccessTools.Method(typeof(Faction), "Notify_BuildingTookDamage"),
            AccessTools.Method(typeof(Building), "PostApplyDamage"),
            AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
            AccessTools.Method(typeof(SpaceProjectileService), "NotifyFoundationDestroyed"),
            AccessTools.Method(typeof(SpaceProjectileService), "RegisterImpactFx"),
            AccessTools.Method(typeof(SpaceProjectileService), "RegisterShotVisuals"),
            AccessTools.Method(typeof(SpaceProjectileService), "RecordBeamSegment") })
            patch.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "FlammableNow"),
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleNotFlammable)));
        patch.Patch(AccessTools.Method(typeof(RoofGrid), "SetRoof"),
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleRoof)));
        patch.CreateClassProcessor(typeof(SpaceImpactEffects).Assembly.GetType("DeltaV.Patch_TerrainGrid_RemoveFoundation_Ownership")).Patch();
        try
        {
            Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
            Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
            var factions = (FactionManager)Raw(typeof(FactionManager));
            Set(factions, "ofPlayer", player); Set(Find.World, "factionManager", factions);
            DamageDefOf.Bullet = new DamageDef { defName = "BattleBullet", workerClass = typeof(DamageWorker), harmsHealth = true };
            var laser = TestBuildingDef("battleLaser");
            laser.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, laserRange = 120, speed = 60 });
            foreach (string name in new[] { "DeltaV_Impact_LaserVsStructure", "DeltaV_Impact_LaserVsSubstructure" })
            {
                var node = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml").Root.Elements()
                    .Single(n => (string)n.Element("defName") == name);
                var part = node.Element("defenderEffect");
                DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "Battle_" + name,
                    projectileDefs = new List<ThingDef> { laser }, targetsSubstructure = (bool?)node.Element("targetsSubstructure") ?? false,
                    substructureHitChance = (float?)node.Element("substructureHitChance") ?? SpaceImpactEffectDef.DefaultSubstructureHitChance,
                    attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf },
                    defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = (float)part.Element("damage"),
                        damageDef = DamageDefOf.Bullet, incidenceMinFactor = (float?)part.Element("incidenceMinFactor") ?? .35f,
                        armorPenetration = (float?)part.Element("armorPenetration") ?? 10 } });
            }
            SpaceImpactEffects.ResetCache();
            var results = new List<object>();
            var outcomes = new List<(int seed, bool mirrored, bool automatic, int disabled, float loss, bool alive)>();
            foreach (int seed in new[] { 17, 29 })
            foreach (bool mirrored in new[] { false, true })
            foreach (bool automatic in new[] { false, true })
            {
                Rand.PushState(seed);
                try
                {
                    var outcome = RunEngagementBattle(universe, player, enemy, laser, seed, mirrored, automatic);
                    results.Add(outcome.report);
                    outcomes.Add((seed, mirrored, automatic, outcome.disabled, outcome.loss, outcome.alive));
                }
                finally { Rand.PopState(); }
            }
            foreach (var pair in outcomes.GroupBy(o => (o.seed, o.mirrored)))
            {
                var frontal = pair.Single(o => !o.automatic); var maneuver = pair.Single(o => o.automatic);
                Check(frontal.disabled > 0 && maneuver.disabled > 0 && maneuver.disabled < frontal.disabled
                    && maneuver.loss < frontal.loss && maneuver.alive,
                    $"weak-side maneuver disables real enemy weapons earlier with lower damage taken: seed={pair.Key.seed}, mirror={pair.Key.mirrored}");
            }
            CheckForecastFoundationBatch(universe);
            Directory.CreateDirectory("Source/obj/EngagementForecast");
            File.WriteAllText("Source/obj/EngagementForecast/battle.json", JsonSerializer.Serialize(new {
                scope = "Production fire control, shot transactions, laser collision, native building damage and MPC. Fixed target motion, physical thrust fixture, no rendering, map topology splitting, crews, native power-net ticking or lifecycle simulation.",
                results }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { patch.UnpatchAll(patch.Id); }
    }

    static void CheckForecastFoundationBatch(UniverseManager universe)
    {
        var map = TestMap(159000, 9); map.roofGrid = new RoofGrid(map);
        var vacuum = (TerrainDef)Raw(typeof(TerrainDef)); vacuum.exposesToVacuum = true;
        Array.Fill(map.terrainGrid.topGrid, vacuum);
        var foundation = (TerrainDef)Raw(typeof(TerrainDef));
        foundation.isFoundation = true; foundation.tags = new List<string> { "Substructure" };
        var cell = new IntVec3(4, 0, 4); int index = map.cellIndices.CellToIndex(cell);
        map.terrainGrid.foundationGrid[index] = foundation;
        var frame = new MapSpaceObject(159001, map, new SpacePose(Vector3.forward, 0));
        universe.objects.Add(frame); Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        var projectile = TestBuildingDef("foundationProbabilityBeam");
        projectile.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, laserRange = 10, speed = 60 });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "FoundationProbabilityImpact",
            projectileDefs = new List<ThingDef> { projectile }, targetsSubstructure = true, substructureHitChance = .5f,
            attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 30,
                substructureBreakThreshold = 60, incidenceMinFactor = 1 } });
        SpaceImpactEffects.ResetCache(); SubstructureOwnership.BumpStructureVersion();
        var observation = new CombatDamageForecast.Observation(frame);
        var broken = AccessTools.Field(typeof(CombatDamageForecast), "brokenFoundation");
        int predicted = 0, actual = 0;
        const int samples = 2048;
        Rand.PushState(53441);
        try
        {
            for (int i = 0; i < samples; i++)
            {
                map.terrainGrid.foundationGrid[index] = foundation;
                SubstructureOwnership.BumpStructureVersion();
                var forecast = new CombatDamageForecast(observation);
                forecast.Fire(new Vector2(2.5f, 4.5f), Vector2.right, 4, projectile, 1, 60,
                    coverSample: (i + .5f) / samples);
                if (((HashSet<IntVec3>)broken.GetValue(forecast)).Contains(cell)) predicted++;
                if (map.terrainGrid.foundationGrid[index] != foundation)
                    throw new Exception("forecast wrote the native foundation grid");
                Vector3 from = SpaceTransform.MapPointToUniverse(frame, map, new Vector2(2.5f, 4.5f));
                var body = new SpaceCollision.SweptBody { attacker = new SpaceObject(159002, new SpacePose(from, 0)),
                    from = from, to = SpaceTransform.MapPointToUniverse(frame, map, new Vector2(6.5f, 4.5f)),
                    projectileDef = projectile, mass = 1, damageScale = 1,
                    sweepRadius = SpaceProjectileService.FootprintRadius(projectile) };
                SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { body });
                if (map.terrainGrid.foundationGrid[index] == null) actual++;
                foreach (var action in Actions(universe)?.ToArray() ?? Array.Empty<Action>()) { Actions(universe).Remove(action); action(); }
            }
            Check(Math.Abs(predicted / (float)samples - .25f) < .04f
                && Math.Abs(predicted - actual) / (float)samples < .04f,
                $"independent floor hit/break forecast matches native destruction batch: predicted={predicted}/{samples}, actual={actual}/{samples}");
        }
        finally
        {
            Rand.PopState(); universe.objects.Remove(frame); Find.Maps.Remove(map);
            Set(universe, "objectById", null); Set(universe, "objectByMap", null);
            SpaceCollision.ClearCaches(); ShipRegistry.ClearCaches();
        }
    }

    static (object report, int disabled, float loss, bool alive) RunEngagementBattle(UniverseManager universe, Faction player, Faction enemy,
        ThingDef laser, int seed, bool mirrored, bool automatic)
    {
        int id = 140000;
        ThingDefOf.PilotConsole = TestBuildingDef("battleConsole");
        ThingDefOf.GravEngine = TestBuildingDef("battleUnusedEngine");
        DeltaVDefOf.DeltaV_Weapon_MissileLauncher = TestBuildingDef("battleUnusedLauncher");
        ThingDefOf.PilotConsole.useHitPoints = true; ThingDefOf.PilotConsole.fillPercent = 1;
        var own = new ManeuverMotionObject(id++, TestMap(id++, 21),
            new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(0, 100)), -Mathf.PI / 2));
        var target = new ManeuverMotionObject(id++, TestMap(id++, 21), new SpacePose(Vector3.forward, 0));
        var buildings = new List<(BattleBuilding thing, bool own, int hp)>();
        var guns = new List<CompSpaceWeapon>();
        BattleBuilding Add(Map map, Faction faction, IntVec3 cell, ThingDef def, int hp, ThingComp comp)
        {
            def.useHitPoints = true;
            var thing = (BattleBuilding)AddTestBuilding(map, faction, id++, def, comp, new BattleBuilding());
            MoveCombatFixture(thing, cell); thing.HitPoints = hp;
            buildings.Add((thing, map == own.map, hp)); return thing;
        }
        foreach (var item in new[] { (body: own, faction: player), (body: target, faction: enemy) })
        {
            item.body.faction = item.faction;
            item.body.map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher));
            item.body.map.roofGrid = new RoofGrid(item.body.map);
            var vacuum = (TerrainDef)Raw(typeof(TerrainDef)); vacuum.exposesToVacuum = true;
            Array.Fill(item.body.map.terrainGrid.topGrid, vacuum);
            item.body.map.lordManager = (LordManager)Raw(typeof(LordManager));
            item.body.map.lordManager.lords = new List<Lord>();
            var console = new CompDeltaVPilotConsole { isMain = true, shipId = id };
            Add(item.body.map, item.faction, new IntVec3(10, 0, 10), ThingDefOf.PilotConsole, 300, console);
            var hull = new CombatHull(console.Ship);
            hull.Combat.weaponsArmed = true;
            var foundation = (TerrainDef)Raw(typeof(TerrainDef));
            foundation.tags = new List<string> { "Substructure" }; foundation.isFoundation = true;
            var owners = new int[item.body.map.cellIndices.NumGridCells];
            Set(SubstructureOwnership.TryGet(item.body.map), "ownerGrid", owners);
            for (int x = 4; x <= 16; x++) for (int z = 4; z <= 16; z++)
            {
                var cell = new IntVec3(x, 0, z); hull.cells.Add(cell);
                int index = item.body.map.cellIndices.CellToIndex(cell);
                item.body.map.terrainGrid.foundationGrid[index] = foundation; owners[index] = console.shipId;
            }
            item.body.member = hull; universe.objects.Add(item.body);
            var gun = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon {
                projectileDef = laser, range = 120, intrinsicAimSkill = 20, topTurret = true,
                cooldownTicks = 90, impactScale = item.body == own ? 4 : .25f }, safeFire = false };
            var turret = Add(item.body.map, item.faction, new IntVec3(10, 0, 12), TestBuildingDef("battleGun" + id), 300, gun);
            turret.def.fillPercent = 1;
            var traverse = new CompSpaceWeaponTraverse { parent = turret,
                props = new CompProperties_SpaceWeaponTraverse() };
            turret.AllComps.Add(traverse); guns.Add(gun);
            universe.Controls.For(hull);
        }
        for (int x = 4; x <= 16; x++) for (int z = 4; z <= 16; z++)
        {
            bool thin = mirrored ? x == 4 : x == 16;
            bool thick = (mirrored ? x >= 14 : x <= 6) || z <= 6 || z >= 14;
            if (!thin && !thick) continue;
            var def = TestBuildingDef("battleArmor" + id); def.fillPercent = 1; def.passability = Traversability.Impassable;
            Add(target.map, enemy, new IntVec3(x, 0, z), def, thin ? 48 : 180, new EmptyComp());
        }
        SubstructureOwnership.BumpStructureVersion(); Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        var strategy = universe.Controls.For(own.member).FireControl.Strategy(own.member);
        var rule = strategy.BuiltInManeuver; rule.tactic.distance = 100;
        if (!automatic) { rule.tactic.bearing = CombatBearing.World; rule.tactic.bearingDegrees = 90; }
        strategy.maneuverRules.Add(rule);
        var planner = new ShipAutoManeuverControl(universe, own.member.Identity);
        Set(planner, "anchorTarget", CombatTarget.ForShip(target.member.Identity));
        Set(planner, "anchorDistance", 100f); Set(planner, "offenseEnabled", true);
        var method = typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);
        bool Sample(out SpacePose pose) { pose = ShipRegistry.ShipPose(target.member); return target.member.IsValid; }
        bool Plan(SpaceObject body, SpacePose other, float radius, DeltaV.DriveInfo drive, out CombatTrajectoryIntent intent)
        { object[] args = { body, other, radius, drive, null }; bool valid = (bool)method.Invoke(planner, args); intent = (CombatTrajectoryIntent)args[4]; return valid; }
        var subject = new DriveSubject(own.objectId, own.member.Identity.key);
        universe.Autopilot.TrackOrbit(subject, Sample, 10, Plan);
        int ticks = 0, ownShots = 0, enemyShots = 0, firstHit = -1, firstLoss = -1, enemyDisarmedTick = -1;
        int rangeTicks = 0, weakTicks = 0;
        var timeline = new List<object>();
        float Damage(bool ours) => buildings.Where(b => b.own == ours).Sum(b => b.hp - Math.Max(0, b.thing.HitPoints));
        for (; ticks < 10800; ticks++)
        {
            RecoveryTick(universe.Autopilot, ticks);
            foreach (var body in new[] { own, target })
            {
                var control = universe.Controls.For(body.member).FireControl;
                control.Decide(body.member, body);
                int before = universe.Projectiles.InFlightCount;
                control.TickAutoVolley(body.member, body, false);
                int launched = universe.Projectiles.InFlightCount - before;
                if (body == own) ownShots += launched; else enemyShots += launched;
            }
            universe.Projectiles.Tick(); universe.Projectiles.ResolveCollisions();
            foreach (var action in Actions(universe)?.ToArray() ?? Array.Empty<Action>()) { Actions(universe).Remove(action); action(); }
            if (firstHit < 0 && Damage(false) > 0) firstHit = ticks;
            if (firstLoss < 0 && buildings.Any(b => b.thing.Destroyed)) firstLoss = ticks;
            if (enemyDisarmedTick < 0 && guns[1].parent.Destroyed) enemyDisarmedTick = ticks;
            Vector2 offset = OrbitSphere.Offset(ShipRegistry.ShipPose(target.member).position, ShipRegistry.ShipPose(own.member).position);
            float phase = Mathf.Atan2(offset.y, offset.x);
            if (offset.magnitude >= 90 && offset.magnitude <= 110) rangeTicks++;
            if (Math.Abs(SpaceTransform.WrapAngle(phase - (mirrored ? Mathf.PI : 0))) <= Mathf.PI / 6) weakTicks++;
            if (ticks % 300 == 0)
            {
                var state = (CombatReposition)AccessTools.Field(typeof(ShipAutoManeuverControl), "reposition").GetValue(planner);
                var evaluation = (CombatEngagementEvaluation.Result)AccessTools.Field(typeof(ShipAutoManeuverControl), "engagementResult").GetValue(planner);
                timeline.Add(new { ticks, distance = offset.magnitude, bearing = phase * Mathf.Rad2Deg,
                    selectedBearing = state.phase * Mathf.Rad2Deg, state.selectedScore, forecastOut = evaluation?.outgoing,
                    forecastIn = evaluation?.incoming, ownShots, enemyShots, outgoing = Damage(false), incoming = Damage(true) });
            }
            if (!own.member.IsValid || !target.member.IsValid) { ticks++; break; }
        }
        float outgoing = Damage(false), incoming = Damage(true);
        Console.WriteLine($"BATTLE seed={seed} mirror={mirrored} auto={automatic} ticks={ticks} shots={ownShots}/{enemyShots} damage={outgoing}/{incoming} disarmed={enemyDisarmedTick} firstHit={firstHit} firstLoss={firstLoss} weak={weakTicks / (float)ticks:F3} range={rangeTicks / (float)ticks:F3}");
        Check(ownShots > 0 && outgoing > 0, "battle runs production launch and damage for " + seed + "/" + mirrored + "/" + automatic);
        Check(enemyShots > 0 && incoming > 0, "battle includes actual enemy fire and incoming damage");
        var result = new { seed, mirrored, automatic, ticks, ownShots, enemyShots, outgoing, incoming, firstHit, firstLoss, enemyDisarmedTick, rangeTicks, weakTicks, timeline,
            ownAlive = own.member.IsValid, targetAlive = target.member.IsValid,
            destroyedOwn = buildings.Count(b => b.own && b.thing.Destroyed), destroyedEnemy = buildings.Count(b => !b.own && b.thing.Destroyed) };
        universe.Autopilot.Cancel(subject); universe.Controls.Reset(); universe.objects.Clear(); Find.Maps.Clear();
        ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
        Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        return (result, enemyDisarmedTick, incoming, result.ownAlive);
    }
}
