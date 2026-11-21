using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static List<object> fleetCandidateScores;
    static BinaryWriter fleetRangeTrace;
    static long fleetRangeCalls, fleetInstantGates, fleetOtherGates, fleetRejectedGates;
    static bool fleetRangeRejected;
    static int fleetFullBuilds, fleetBlockUpdates;
    static void CountFleetFullBuild() => fleetFullBuilds++;
    static void CountFleetBlockUpdate() => fleetBlockUpdates++;

    static void RunFleetCollisionEdit(ManeuverMotionObject ship, int pattern, ThingDef projectile,
        bool verify)
    {
        var map = ship.map;
        int center = map.Size.x / 2;
        int origin = pattern % 2 == 0 ? 2 : map.Size.x - 3;
        int sign = origin == 2 ? 1 : -1;
        var edits = new List<IntVec3> { new IntVec3(origin, 0, center) };
        if (pattern >= 2)
        {
            edits.Add(new IntVec3(origin, 0, center - 1));
            edits.Add(new IntVec3(origin + sign, 0, center - 1));
            if (map.Size.x > 16)
                for (int offset = 2; offset <= 6; offset++)
                    edits.Add(new IntVec3(origin + sign * offset, 0, center - 1));
        }
        if (pattern >= 4)
        {
            edits.Add(new IntVec3(origin, 0, map.Size.z - 3));
            edits.Add(new IntVec3(map.Size.x - 1 - origin, 0, 2));
            if (map.Size.x > 16)
                edits.Add(new IntVec3(center, 0, 9));
        }
        using (Current.Game.GetComponent<UniverseManager>().ShipStructure.BeginChanges())
            foreach (IntVec3 cell in edits)
                if (SubstructureOwnership.HasFoundation(map, cell))
                    map.terrainGrid.RemoveFoundation(cell, doLeavings: false);
        var query = RunFleetCollisionRays(ship, projectile, origin, sign, verify);
        if (verify)
        {
            var hull = ship.member.HullCells;
            Check(edits.All(cell => !query.foundationCells.Contains(cell) && !hull.Contains(cell)
                && SubstructureOwnership.For(map).OwnerAt(cell) == SubstructureOwnership.Unassigned),
                "the first query sees each removal in collision, hull membership and ownership");
        }
    }

    static SpaceCollision.RayQuery RunFleetCollisionRays(ManeuverMotionObject ship, ThingDef projectile,
        int origin, int sign, bool verify)
    {
        var map = ship.map;
        int center = map.Size.x / 2;
        var query = new SpaceCollision.RayQuery(ship);
        var direction = new Vector2(sign, 0);
        float radius = SpaceProjectileService.FootprintRadius(projectile);
        for (int lane = -3; lane <= 4; lane++)
        {
            Vector2 start = new Vector2(origin - sign * 3, center + lane + .5f);
            bool hit = query.Next(start, direction, map.Size.x, projectile,
                radius, null, null, null, _ => false, out var contact);
            if (!verify) continue;
            fleetRangeTrace.Write(hit);
            fleetRangeTrace.Write(contact.cell.x);
            fleetRangeTrace.Write(contact.cell.z);
            fleetRangeTrace.Write((byte)contact.kind);
            fleetRangeTrace.Write(contact.point.x);
            fleetRangeTrace.Write(contact.point.y);
        }
        return query;
    }

    delegate bool FleetPlan(SpaceObject body, SpacePose target, float clearance, DeltaV.DriveInfo drive,
        out CombatTrajectoryIntent intent);

    sealed class StaticRayCounts
    {
        public int fire, traces, next, contacts, guardContacts, lineContacts, repeatedGeometry, pointSegments, sweptSegments;
        public int pointVisits, pointDuplicates, sweptVisits, sweptDuplicates, inBoundsVisits, nonEmptyHullSegments, sweepSamples;
        public int pointClassified, pointClassifiedDuplicates, sweptClassified, sweptClassifiedDuplicates;
        public int arcQueries, arcsInspected, lineQueries, linesInspected;
        public int penetrationWalks, penetrationCellLookups, coverBuilds;
    }

    static StaticRayCounts staticCounts;
    static bool staticWide;
    static readonly HashSet<IntVec3> staticVisited = new HashSet<IntVec3>();
    static readonly HashSet<IntVec3> staticClassified = new HashSet<IntVec3>();
    static readonly HashSet<(SpaceCollision.RayQuery query, int x, int y, int dx, int dy, int length, int radius)>
        staticSegments = new();
    static FieldInfo staticArcs, staticLines;
    static bool staticPenetrating;

    static void CountStaticFire() => staticCounts.fire++;
    static void CountStaticTrace() => staticCounts.traces++;
    static void CountStaticContact(bool __result, ref SpaceCollision.RayContact contact)
    {
        if (!__result) return;
        staticCounts.contacts++;
        if (contact.guard != null) staticCounts.guardContacts++;
        if (contact.line != null) staticCounts.lineContacts++;
    }
    static void CountStaticNext(SpaceCollision.RayQuery __instance, Vector2 from, Vector2 direction, float length, float radius)
    {
        staticCounts.next++;
        if (!staticSegments.Add((__instance, BitConverter.SingleToInt32Bits(from.x), BitConverter.SingleToInt32Bits(from.y),
            BitConverter.SingleToInt32Bits(direction.x), BitConverter.SingleToInt32Bits(direction.y),
            BitConverter.SingleToInt32Bits(length), BitConverter.SingleToInt32Bits(radius))))
            staticCounts.repeatedGeometry++;
    }

    static void SetStaticFixtureHealth(Thing thing, int hp)
    {
        thing.def.useHitPoints = true;
        thing.def.fillPercent = 1f;
        SetFixtureHitPoints(thing, hp);
    }

    static void AddStaticFleetShield(Map map, Faction faction, int id, int width)
    {
        var shield = new CompSpaceShield { props = new CompProperties_SpaceShield { regularCapacity = 50 },
            mode = SpaceShieldMode.Guard };
        var building = AddTestBuilding(map, faction, id, TestBuildingDef("staticGuard" + id), shield);
        MoveCombatFixture(building, new IntVec3(width / 2 + 4, 0, width / 2 + 8));
        SetStaticFixtureHealth(building, 300);
        var power = new CompPowerTrader { parent = building, props = new CompProperties_Power() };
        Set(power, "powerOnInt", true);
        var interceptor = (CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
        interceptor.parent = building;
        interceptor.Initialize(new CompProperties_ProjectileInterceptor { radius = 10f, hitPoints = 0 });
        interceptor.PostPostMake();
        interceptor.currentHitPoints = -1;
        Set(interceptor, "lastInterceptTicks", -999999);
        Set(interceptor, "startedChargingTick", -1);
        building.AllComps.AddRange(new ThingComp[] { power, interceptor });
        shield.InitializeGuard();
        ShipStructureChanges.Notify(map, building, null, building.OccupiedRect());
    }

    static void RunStaticFleet(UniverseManager universe, List<Action> plans, List<ManeuverMotionObject> ships,
        string mode, int width, int gunCount)
    {
        var perf = universe.Performance;
        var poses = ships.Select(ship => ship.pose).ToArray();
        var things = ships.SelectMany(ship => ship.map.listerThings.AllThings).ToArray();
        var durability = things.Select(thing => (thing.HitPoints, thing.Destroyed)).ToArray();
        var shields = things.OfType<ThingWithComps>().Select(thing => thing.GetComp<CompSpaceShield>())
            .Where(comp => comp != null).ToArray();
        var shieldCapacity = shields.Select(shield => (shield.RegularCapacity, shield.EmergencyCapacity)).ToArray();
        var guns = things.OfType<ThingWithComps>().Select(thing => thing.GetComp<CompSpaceWeapon>())
            .Where(comp => comp != null).ToArray();
        var cooldowns = guns.Select(gun => gun.cooldownUntilTick).ToArray();
        var rows = new List<object>();
        var warm = new List<double>();
        var idle = new List<double>();
        var patch = new Harmony("DeltaV.Regression.StaticEngagementCounts");
        const int ticks = 1200;
        double coldMs = 0;
        long startSearches = perf.engagementCompleted, startCandidates = perf.engagementCandidates;
        int observedFire = 0, observedHull = 0, observedGuardContacts = 0, observedPenetration = 0;
        bool count = mode == "count", time = mode == "time";
        try
        {
            if (count)
            {
                staticCounts = new StaticRayCounts();
                PatchStaticRayCounts(patch);
            }
            for (int tick = 0; tick < ticks; tick++)
            {
                Set(Find.TickManager, "ticksGameInt", tick);
                long completed = perf.engagementCompleted, candidates = perf.engagementCandidates;
                if (count)
                {
                    staticCounts = new StaticRayCounts();
                    staticSegments.Clear();
                }
                long begin = time ? Stopwatch.GetTimestamp() : 0;
                foreach (var plan in plans) plan();
                double elapsed = time ? (Stopwatch.GetTimestamp() - begin) * (1000d / Stopwatch.Frequency) : 0;
                long searches = perf.engagementCompleted - completed;
                if (tick == 0)
                {
                    Check(searches == 2, "both static ships complete a real search on the same initial tick");
                    coldMs = elapsed;
                }
                else if (time)
                {
                    if (searches > 0) warm.Add(elapsed); else idle.Add(elapsed);
                }
                if (count && (searches > 0 || staticCounts.fire > 0))
                {
                    rows.Add(new { tick, searches, candidates = perf.engagementCandidates - candidates, scan = staticCounts });
                    observedFire += staticCounts.fire;
                    observedHull += staticCounts.pointSegments + staticCounts.sweptSegments;
                    observedGuardContacts += staticCounts.guardContacts;
                    observedPenetration += staticCounts.penetrationWalks;
                }
            }
        }
        finally
        {
            if (count) patch.UnpatchAll(patch.Id);
            staticCounts = null; staticSegments.Clear();
        }
        int structuresPerShip = (width - 4) * (width - 4);
        Check(ships.Select(ship => ship.pose).SequenceEqual(poses), "static ships stay in their original poses");
        Check(universe.objects.Count == ships.Count && universe.Projectiles.ActiveCount == 0
            && guns.Select(gun => gun.cooldownUntilTick).SequenceEqual(cooldowns),
            "static planning spawns no projectiles and changes no weapon cooldown");
        Check(things.Select(thing => (thing.HitPoints, thing.Destroyed)).SequenceEqual(durability)
            && shields.Select(shield => (shield.RegularCapacity, shield.EmergencyCapacity)).SequenceEqual(shieldCapacity),
            "static planning preserves live durability and shield capacity");
        if (count)
        {
            Check(observedFire > 0 && observedHull > 0, "counting reaches production forecasts and hull queries");
            if (shields.Length > 0)
            {
                foreach (var ship in ships)
                {
                    var query = new SpaceCollision.RayQuery(ship, ship.member);
                    foreach (var shield in shields.Where(shield => shield.parent.Map == ship.map))
                    {
                        var arcs = new List<SpaceCollision.ShieldArc>();
                        var center = shield.parent.Position.ToVector3Shifted();
                        query.ScanGuardSurfaces(new Vector2(center.x, center.z), Vector2.right, arcs);
                        Check(arcs.Any(arc => arc.building == shield.parent && arc.interceptor.Active)
                            && shield.RegularCapacity > 0, "dense protection queries see the active mounted guard shield");
                    }
                }
                Check(observedPenetration > 0, "dense forecasts exercise layered penetration");
                Console.WriteLine($"STATIC_DENSE guardContacts={observedGuardContacts} penetrationWalks={observedPenetration}");
            }
        }
        if (time) Check(warm.Count > 0 && idle.Count > 0, "static timing samples both replans and quiet ticks");
        if (!count && !time) return;
        object Stats(List<double> values)
        {
            values.Sort();
            return new { samples = values.Count, p50Ms = values[values.Count / 2],
                p95Ms = values[(int)((values.Count - 1) * .95)], maxMs = values[^1] };
        }
        var summary = new { mode, ticks, gunCount, structuresPerShip,
            searches = perf.engagementCompleted - startSearches, candidates = perf.engagementCandidates - startCandidates,
            timings = time ? new { coldMs, warm = Stats(warm), idle = Stats(idle) } : null, rows };
        string output = StaticFleetOutput(shields.Length > 0);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, $"static-{mode}.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        Console.WriteLine($"STATIC_ENGAGEMENT {mode}: {summary.searches} searches, {summary.candidates} candidates, {output}");
    }

    static string StaticFleetOutput(bool dense) => Environment.GetEnvironmentVariable("DELTAV_ENGAGEMENT_OUTPUT")
        ?? Path.Combine(AppContext.BaseDirectory, "StaticEngagement", dense ? "Dense" : "Normal");

    static void CountStaticHull(SpaceCollision.SweptBody body)
    {
        staticVisited.Clear();
        staticClassified.Clear();
        staticWide = body.sweepRadius != 0f || body.noseHalfWidth != 0f;
        if (staticWide) staticCounts.sweptSegments++;
        else staticCounts.pointSegments++;
    }

    static void RecordStaticGridVisit(IntVec3 cell, bool inBounds)
    {
        if (staticVisited.Count == 0) staticCounts.nonEmptyHullSegments++;
        bool repeated = !staticVisited.Add(cell);
        if (staticWide)
        {
            staticCounts.sweptVisits++;
            if (repeated) staticCounts.sweptDuplicates++;
        }
        else
        {
            staticCounts.pointVisits++;
            if (repeated) staticCounts.pointDuplicates++;
        }
        if (inBounds) staticCounts.inBoundsVisits++;
    }

    static bool CountStaticPointGridVisit(IntVec3 cell, Map map)
    {
        bool inBounds = cell.InBounds(map);
        RecordStaticGridVisit(cell, inBounds);
        return inBounds;
    }

    static IEnumerable<CodeInstruction> TraceStaticGridVisits(IEnumerable<CodeInstruction> instructions)
    {
        var inBounds = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds), new[] { typeof(IntVec3), typeof(Map) });
        var counter = AccessTools.Method(typeof(Program), nameof(CountStaticPointGridVisit));
        int sites = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(inBounds)) { instruction.operand = counter; sites++; }
            yield return instruction;
        }
        if (sites != 1) throw new InvalidOperationException("Static engagement probe expected one cell iteration site.");
    }

    static void CountStaticSample() => staticCounts.sweepSamples++;
    static void CountStaticClassification(IntVec3 cell)
    {
        // The swept path clamps its loop bounds before entering classification.
        if (staticWide) RecordStaticGridVisit(cell, inBounds: true);
        bool repeated = !staticClassified.Add(cell);
        if (staticWide)
        {
            staticCounts.sweptClassified++;
            if (repeated) staticCounts.sweptClassifiedDuplicates++;
        }
        else
        {
            staticCounts.pointClassified++;
            if (repeated) staticCounts.pointClassifiedDuplicates++;
        }
    }
    static void CountStaticArc(object __0)
    {
        staticCounts.arcQueries++;
        staticCounts.arcsInspected += ((System.Collections.ICollection)staticArcs.GetValue(__0)).Count;
    }
    static void CountStaticLine(object __0)
    {
        staticCounts.lineQueries++;
        staticCounts.linesInspected += ((System.Collections.ICollection)staticLines.GetValue(__0)).Count;
    }
    static void CountStaticPenetration()
    { staticCounts.penetrationWalks++; staticPenetrating = true; }
    static void EndStaticPenetration() => staticPenetrating = false;
    static void CountStaticPenetrationCell()
    { if (staticPenetrating) staticCounts.penetrationCellLookups++; }
    static void CountStaticCoverBuild() => staticCounts.coverBuilds++;

    static void PatchStaticRayCounts(Harmony patch)
    {
        var collision = typeof(SpaceCollision);
        var surface = AccessTools.Inner(collision, "Surface");
        staticArcs = AccessTools.Field(surface, "arcs");
        staticLines = AccessTools.Field(surface, "lineShields");
        void Hook(Type owner, string method, string prefix, string postfix = null, string transpiler = null)
        {
            HarmonyMethod FindHook(string name) => name == null ? null : new HarmonyMethod(typeof(Program), name);
            patch.Patch(AccessTools.Method(owner, method), FindHook(prefix), FindHook(postfix), FindHook(transpiler));
        }
        Hook(typeof(CombatDamageForecast), "Fire", nameof(CountStaticFire));
        Hook(typeof(CombatDamageForecast), "Trace", nameof(CountStaticTrace));
        patch.Patch(AccessTools.Constructor(typeof(CombatDamageForecast.CoverMask)),
            postfix: new HarmonyMethod(typeof(Program), nameof(CountStaticCoverBuild)));
        Hook(typeof(SpaceCollision.RayQuery), "Next", nameof(CountStaticNext), nameof(CountStaticContact));
        Hook(collision, "TryFindHullContact", nameof(CountStaticHull));
        patch.Patch(AccessTools.Method(collision, "TryFindGridContact"),
            transpiler: new HarmonyMethod(typeof(Program), nameof(TraceStaticGridVisits)));
        Hook(collision, "FindBestCell", nameof(CountStaticSample));
        Hook(collision, "TryGetHullCell", nameof(CountStaticClassification));
        Hook(collision, "TryFindArcHit", nameof(CountStaticArc));
        Hook(collision, "TryFindLineHit", nameof(CountStaticLine));
        Hook(typeof(SpaceImpactEffects), "WalkPenetration", nameof(CountStaticPenetration), nameof(EndStaticPenetration));
        patch.Patch(AccessTools.Method(typeof(SpaceStructureRules), nameof(SpaceStructureRules.ClassifyCell),
            new[] { typeof(Map), typeof(IntVec3), typeof(Thing).MakeByRefType(), typeof(HashSet<Thing>), typeof(ShipStructureIndex.MapView) }),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountStaticPenetrationCell)));
    }

    static float CountFleetArcCells(Vector3 source, Vector3 target)
    {
        fleetRangeCalls++;
        return OrbitSphere.ArcCells(source, target);
    }

    static bool ObserveFleetRange(ref CombatAttackWeapon gun, float distance, float margin)
    {
        fleetRangeRejected = gun.OutsideInstantRange(distance, margin);
        return fleetRangeRejected;
    }

    static IEnumerable<CodeInstruction> TraceFleetRange(IEnumerable<CodeInstruction> instructions)
    {
        var arc = AccessTools.Method(typeof(OrbitSphere), nameof(OrbitSphere.ArcCells), new[] { typeof(Vector3), typeof(Vector3) });
        var gate = AccessTools.Method(typeof(CombatAttackWeapon), nameof(CombatAttackWeapon.OutsideInstantRange));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(arc)) instruction.operand = AccessTools.Method(typeof(Program), nameof(CountFleetArcCells));
            else if (instruction.Calls(gate)) instruction.operand = AccessTools.Method(typeof(Program), nameof(ObserveFleetRange));
            yield return instruction;
        }
    }

    static void StartFleetRangeGate() => fleetRangeRejected = false;

    static void RecordFleetRangeGate(object __0, bool __result)
    {
        var gun = (CombatAttackWeapon)AccessTools.Field(__0.GetType(), "gun").GetValue(__0);
        if (gun.instant) fleetInstantGates++; else fleetOtherGates++;
        if (fleetRangeRejected) fleetRejectedGates++;
        fleetRangeTrace.Write(gun.source.parent.thingIDNumber);
        fleetRangeTrace.Write(Find.TickManager.TicksGame);
        fleetRangeTrace.Write(fleetRangeRejected);
        fleetRangeTrace.Write(__result);
    }

    static void CaptureFleetCandidate(CombatEngagementEvaluation.Result __instance, float __result)
    {
        fleetCandidateScores.Add(new { score = __result, __instance.outgoing, __instance.incoming,
            __instance.travel, __instance.velocity, __instance.reverse, __instance.flank, __instance.uncertain,
            contributions = __instance.contributions.Select(c => new { weapon = c.weapon?.parent.ThingID,
                c.family, c.side, c.damage, c.guard, c.critical, c.uncertainty, c.armorProgress }).ToArray() });
    }

    static void CheckEngagementFleetBaseline(UniverseManager universe, Faction player, Faction enemy)
    {
        using var patch = new EngagementFixturePatch(editTerrain: true);
        Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
        Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
        var factions = (FactionManager)Raw(typeof(FactionManager));
        Set(factions, "ofPlayer", player); Set(Find.World, "factionManager", factions);
        ThingDefOf.PilotConsole = TestBuildingDef("fleetConsole");
        var args = Environment.GetCommandLineArgs();
        int staticArg = Array.IndexOf(args, "--engagement-static");
        bool staticBattle = staticArg >= 0;
        string staticMode = staticBattle ? args[staticArg + 2].ToLowerInvariant() : null;
        bool staticDense = staticBattle && args[staticArg + 1].Equals("Dense", StringComparison.OrdinalIgnoreCase);
        bool collisionEdits = args.Contains("--engagement-collision-edits");
        bool pointCollision = args.Contains("--engagement-collision-point");
        bool parity = args.Contains("--engagement-cover-parity");
        bool rangeParity = args.Contains("--engagement-range-parity") || staticMode == "parity";
        bool mixed = args.Contains("--engagement-range-mixed") || staticDense
            || (collisionEdits && args.Contains("--engagement-cover-dense"));
        parity |= rangeParity;
        bool dense = args.Contains("--engagement-cover-dense") || staticDense;
        if (staticBattle) DeltaVMod.CurrentSettings.logPerformance = false;
        bool profileFirst24 = args.Contains("--engagement-profile-first-24");
        bool timeFirst24 = args.Contains("--engagement-time-first-24");
        bool first24 = profileFirst24 || timeFirst24;
        if (args.Contains("--engagement-verify-targeting"))
        {
            Check(typeof(SpaceCollision).GetMethods(Private).All(m => m.Name != "TargetObstruction"),
                "engagement has no obstruction ranking or sweep entry point");
            Check(new CombatStrategy().BuiltInFire.part == CombatPart.Weapons,
                "default engagement prioritizes exterior weapons");
        }
        int width = dense ? 43 : 9, gunCount = dense ? 16 : 4;
        var parityPatch = new Harmony("DeltaV.Regression.FleetParity");
        bool withShield = staticDense || (collisionEdits && dense);
        if (withShield)
        {
            parityPatch.Patch(typeof(CompProjectileInterceptor).TypeInitializer,
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            parityPatch.Patch(AccessTools.PropertyGetter(typeof(StunHandler), nameof(StunHandler.Hypnotized)),
                transpiler: new HarmonyMethod(typeof(Program), nameof(TractorDlcFixture)));
        }
        var refreshBlocks = AccessTools.Method(AccessTools.Inner(typeof(SpaceCollision), "Surface"), "RefreshBlocks");
        if (collisionEdits && parity)
        {
            fleetFullBuilds = fleetBlockUpdates = 0;
            var rebuild = AccessTools.Method(typeof(SpaceCollision), refreshBlocks == null ? "Rebuild" : "RebuildHull");
            parityPatch.Patch(rebuild, prefix: new HarmonyMethod(typeof(Program), nameof(CountFleetFullBuild)));
            if (refreshBlocks != null)
                parityPatch.Patch(refreshBlocks, prefix: new HarmonyMethod(typeof(Program), nameof(CountFleetBlockUpdate)));
        }
        var decisions = new List<object>();
        using var gateBytes = rangeParity ? new MemoryStream() : null;
        using var gateWriter = rangeParity ? new BinaryWriter(gateBytes) : null;
        if (rangeParity)
        {
            fleetRangeTrace = gateWriter;
            fleetRangeCalls = fleetInstantGates = fleetOtherGates = fleetRejectedGates = 0;
            var transpiler = new HarmonyMethod(typeof(Program), nameof(TraceFleetRange));
            parityPatch.Patch(AccessTools.Method(typeof(CombatEngagementEvaluation), "Evaluate",
                new[] { typeof(SpacePose) }), transpiler: transpiler);
            parityPatch.Patch(AccessTools.Method(typeof(CombatEngagementEvaluation), "TryEstimate"),
                prefix: new HarmonyMethod(typeof(Program), nameof(StartFleetRangeGate)),
                postfix: new HarmonyMethod(typeof(Program), nameof(RecordFleetRangeGate)), transpiler: transpiler);
        }
        if (parity)
        {
            fleetCandidateScores = new List<object>();
            parityPatch.Patch(AccessTools.Method(typeof(CombatEngagementEvaluation.Result), "Score"),
                postfix: new HarmonyMethod(typeof(Program), nameof(CaptureFleetCandidate)));
        }
        var projectile = TestBuildingDef("fleetLaser");
        float radius = -1f;
        if (pointCollision)
        {
            var xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml");
            radius = (float)xml.Root.Elements().Single(node => (string)node.Element("defName") == "DeltaV_Projectile_Laser")
                .Descendants("collisionRadius").Single();
        }
        projectile.comps.Add(new CompProperties_SpaceProjectile {
            laserRange = 150, speed = 60, hasMap = false, collisionRadius = radius });
        var rounds = new[] { projectile, projectile, projectile, projectile };
        if (mixed)
        {
            rounds[0] = TestBuildingDef("fleetBallistic");
            rounds[0].comps.Add(new CompProperties_SpaceProjectile {
                speed = 80, hasMap = false, collisionRadius = staticDense || pointCollision ? 0f : -1f });
            rounds[2] = TestBuildingDef("fleetGuided");
            rounds[2].comps.Add(new CompProperties_SpaceProjectile { guided = true, speed = 60, hasMap = false });
        }
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "FleetLaserEffect",
            projectileDefs = rounds.Distinct().Where(round => !staticDense || round != rounds[0]).ToList(),
            targetsSubstructure = dense,
            substructureHitChance = dense ? .01f : SpaceImpactEffectDef.DefaultSubstructureHitChance,
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 8 } });
        if (staticDense)
            DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef {
                defName = "StaticFleetLayeredEffect", projectileDefs = new List<ThingDef> { rounds[0] },
                defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 35,
                    penetrationLayers = new List<ImpactPenetrationLayer> {
                        new ImpactPenetrationLayer { damageFactor = 1f, halfWidth = 1 },
                        new ImpactPenetrationLayer { damageFactor = .5f, halfWidth = 1 } } } });
        SpaceImpactEffects.ResetCache();
        var rows = new List<object>();
        bool profile = Environment.GetCommandLineArgs().Contains("--engagement-profile");
        bool profileWindow = Environment.GetCommandLineArgs().Contains("--engagement-profile-window");
        foreach (int count in staticBattle ? new[] { 2 } : collisionEdits ? new[] { dense ? 4 : 24 }
            : dense || mixed || profile || profileWindow ? new[] { 4 } : new[] { 1, 4, 12, 24 })
        {
            var ships = new List<ManeuverMotionObject>();
            for (int i = 0; i < Math.Max(2, count); i++)
            {
                int id = 110000 + i * 100;
                var faction = i % 2 == 0 ? player : enemy;
                var map = TestMap(id, width);
                var body = new ManeuverMotionObject(id, map, new SpacePose(OrbitSphere.Move(Vector3.forward,
                    new Vector2(i % 2 == 0 ? 50 : -50, i / 2 * (dense ? 120 : 40))), 0));
                if (staticBattle) body.faction = faction;
                var console = AddTestConsole(map, faction, id + 1);
                MoveCombatFixture(console.parent, new IntVec3(width / 2, 0, width / 2));
                Set(typeof(Thing), console.parent, "hitPointsInt", 300);
                if (collisionEdits) SetEngagementVacuum(map);
                SetEngagementFoundation(map, new CellRect(2, 2, width - 4, width - 4), EngagementFoundation(), console.shipId);
                ShipBase hull = console.Vessel;
                hull.Combat.weaponsArmed = true;
                body.member = hull;
                universe.RegisterObject(body);
                ships.Add(body);
                for (int gunIndex = 0; gunIndex < gunCount; gunIndex++)
                {
                    var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon {
                        projectileDef = rounds[gunIndex % rounds.Length], intrinsicAimSkill = 20,
                        range = mixed && gunIndex == 3 ? 55 : 150, minRange = mixed && gunIndex == 1 ? 90 : 0,
                        cooldownTicks = 12, launchKineticEnergy = withShield ? SpaceKinetics.Energy(4, 80) : 0f },
                        safeFire = false };
                    var gun = AddTestBuilding(map, faction, id + 2 + gunIndex, TestBuildingDef("fleetGun" + id + "_" + gunIndex), weapon);
                    int columns = dense ? 4 : 2;
                    int offset = dense ? width / 2 - 5 : 3;
                    MoveCombatFixture(gun, new IntVec3(offset + gunIndex % columns * 2, 0, offset + gunIndex / columns * 2));
                    if (staticBattle || withShield) SetStaticFixtureHealth(gun, 160);
                }
                if (withShield) AddStaticFleetShield(map, faction, id + 50, width);
            }
            var method = typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);
            var staticPlans = new List<Action>();
            foreach (var own in ships.Take(count))
            {
                var target = ships[ships.IndexOf(own) ^ 1];
                universe.Controls.For(own.member);
                var planner = new ShipAutoManeuverControl(universe, own.member.Identity);
                Set(planner, "anchorTarget", CombatTarget.ForShip(target.member.Identity));
                Set(planner, "anchorDistance", 100f); Set(planner, "offenseEnabled", true);
                var plan = staticBattle ? (FleetPlan)method.CreateDelegate(typeof(FleetPlan), planner) : null;
                bool Sample(out SpacePose pose) { pose = ShipRegistry.ShipPose(target.member); return true; }
                bool Plan(SpaceObject body, SpacePose other, float radius, DeltaV.DriveInfo drive, out CombatTrajectoryIntent intent)
                {
                    long done = universe.Performance.engagementCompleted;
                    bool result;
                    if (plan != null) result = plan(body, other, radius, drive, out intent);
                    else
                    {
                        object[] call = { body, other, radius, drive, null };
                        result = (bool)method.Invoke(planner, call);
                        intent = (CombatTrajectoryIntent)call[4];
                    }
                    if (parity && done != universe.Performance.engagementCompleted)
                    {
                        var pose = (CombatAttackPose)typeof(ShipAutoManeuverControl).GetField("attackPose", Private).GetValue(planner);
                        var state = (CombatReposition)typeof(ShipAutoManeuverControl).GetField("reposition", Private).GetValue(planner);
                        int tick = Find.TickManager.TicksGame;
                        Check(result && intent.startTick == tick && fleetCandidateScores.Count > 0,
                            $"fleet search applies a scored intent on the same tick ({own.objectId}, {tick})");
                        decisions.Add(new { ship = own.objectId, tick, result,
                            intent.startTick, intent.startPhase, intent.targetRadius, intent.headingError, intent.holdCurrent,
                            pose.distance, pose.heading, pose.preference, pose.behavior, state.phase, state.nextTick,
                            state.selectedScore, state.continuationScore, state.candidateCount,
                            trajectory = rangeParity ? FleetTrajectoryBits(intent) : null,
                            gateEnd = rangeParity ? fleetInstantGates + fleetOtherGates : 0,
                            scores = fleetCandidateScores.ToArray() });
                        fleetCandidateScores.Clear();
                    }
                    return result;
                }
                if (staticBattle)
                {
                    var drive = new DeltaV.DriveInfo(own.layout);
                    SpacePose other = ShipRegistry.ShipPose(target.member);
                    staticPlans.Add(() => Plan(own, other, 10, drive, out _));
                }
                else universe.Autopilot.TrackOrbit(new DriveSubject(own.objectId, own.member.Identity.key), Sample, 10, Plan);
            }
            if (staticBattle)
            {
                RunStaticFleet(universe, staticPlans, ships, staticMode, width, gunCount);
                foreach (var ship in ships) RemoveEngagementMap(universe, ship);
                universe.Controls.Reset();
                break;
            }
            var times = new List<double>();
            var damageMs = new List<double>[3] { new List<double>(), new List<double>(), new List<double>() };
            var watch = new Stopwatch();
            double coldMs = 0, peakMs = 0;
            int peakTick = 0;
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            TimeSpan gcPauseStart = GC.GetTotalPauseDuration();
            long started = universe.Performance.engagementStarted, completed = universe.Performance.engagementCompleted;
            long candidates = universe.Performance.engagementCandidates;
            long events = universe.ShipStructure.Events, publications = universe.ShipStructure.Publications;
            long arcStart = fleetRangeCalls, gatesStart = fleetInstantGates + fleetOtherGates;
            long instantStart = fleetInstantGates, rejectsStart = fleetRejectedGates;
            if (first24 && count == 24)
            {
                Array.Clear((long[])typeof(DeltaVPerformanceProbe).GetField("totals", Private).GetValue(universe.Performance));
                Array.Clear((int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(universe.Performance));
                DeltaVMod.CurrentSettings.logPerformance = profileFirst24;
            }
            for (int tick = 0; tick < 1800; tick++)
            {
                if (profile || profileWindow) DeltaVMod.CurrentSettings.logPerformance = true;
                watch.Restart();
                int editIndex = (tick - 720) / 120;
                int damageKind = collisionEdits && tick >= 720 && tick <= 1680 && (tick - 720) % 120 == 0
                    ? editIndex % 3 : -1;
                if (damageKind >= 0)
                {
                    int targetIndex = dense ? editIndex % 4 : editIndex;
                    int buildsBefore = fleetFullBuilds, updatesBefore = fleetBlockUpdates;
                    RunFleetCollisionEdit(ships[targetIndex], damageKind * 2 + editIndex / 4 % 2,
                        projectile, rangeParity);
                    damageMs[damageKind].Add(watch.Elapsed.TotalMilliseconds);
                    if (parity)
                    {
                        int builds = fleetFullBuilds - buildsBefore, updates = fleetBlockUpdates - updatesBefore;
                        Check(refreshBlocks == null ? builds == 1 && updates == 0 : builds == 0 && updates == 1,
                            $"first query batch consumes one {(refreshBlocks == null ? "full build" : "block update")} at tick {tick}");
                    }
                }
                RecoveryTick(universe.Autopilot, tick); watch.Stop();
                if (rangeParity && tick == 0)
                    Console.WriteLine($"RANGE_FIRST ships={count} calls={fleetRangeCalls - arcStart} gates={fleetInstantGates + fleetOtherGates - gatesStart} instant={fleetInstantGates - instantStart} rejects={fleetRejectedGates - rejectsStart}");
                if (first24 && count == 24)
                {
                    Console.WriteLine(FormattableString.Invariant($"FIRST_24 wallMs={watch.Elapsed.TotalMilliseconds:F5} searches={universe.Performance.engagementCompleted - completed}/{universe.Performance.engagementStarted - started} candidates={universe.Performance.engagementCandidates - candidates}"));
                    if (timeFirst24) return;
                }
                if (watch.Elapsed.TotalMilliseconds > peakMs) { peakMs = watch.Elapsed.TotalMilliseconds; peakTick = tick; }
                if (profile || profileWindow && tick == 1799 || profileFirst24 && count == 24)
                {
                    Console.WriteLine($"PROFILE allocationBytes={GC.GetAllocatedBytesForCurrentThread() - allocationStart} gen0={GC.CollectionCount(0) - gen0} gen1={GC.CollectionCount(1) - gen1} gen2={GC.CollectionCount(2) - gen2} gcPauseMs={(GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds:F3} searches={universe.Performance.engagementCompleted - completed}/{universe.Performance.engagementStarted - started} candidates={universe.Performance.engagementCandidates - candidates}");
                    var totals = (long[])typeof(DeltaVPerformanceProbe).GetField("totals", Private).GetValue(universe.Performance);
                    var calls = (int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(universe.Performance);
                    foreach (PerformanceStage stage in Enum.GetValues(typeof(PerformanceStage)))
                        if (stage != PerformanceStage.Count && calls[(int)stage] > 0)
                            Console.WriteLine(FormattableString.Invariant($"PROFILE {stage} calls={calls[(int)stage]} ms={totals[(int)stage] * 1000d / Stopwatch.Frequency:F3}"));
                    return;
                }
                if (tick == 0) coldMs = watch.Elapsed.TotalMilliseconds;
                if (tick >= 300) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double mean = times.Average(), p95 = times[(int)(times.Count * .95)], p99 = times[(int)(times.Count * .99)];
            rows.Add(new { ships = count, gunCount, floorsPerShip = (width - 4) * (width - 4), ticks = 1800,
                warmup = 300, peakMs, peakTick, meanTickMs = mean, p95TickMs = p95,
                p99TickMs = p99, maxTickMs = times.Last(), coldMs,
                damageToFirstBatchMs = collisionEdits ? damageMs : null,
                allocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart,
                gcPauseMs = (GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds,
                searchesStarted = universe.Performance.engagementStarted - started,
                searchesCompleted = universe.Performance.engagementCompleted - completed,
                candidatesEvaluated = universe.Performance.engagementCandidates - candidates,
                structureEvents = universe.ShipStructure.Events - events,
                structurePublications = universe.ShipStructure.Publications - publications,
                collections = new[] { GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2 } });
            Check(universe.Performance.engagementStarted - started == universe.Performance.engagementCompleted - completed,
                "fleet completes every started search at " + count + " ships");
            Console.WriteLine($"ENGAGEMENT_FLEET ships={count} meanMs={mean:F3} p95Ms={p95:F3} p99Ms={p99:F3} maxMs={times.Last():F3}");
            Check(ships.Take(count).All(s => !float.IsNaN(s.pose.velocity.x)), "fleet production guidance remains finite at " + count + " ships");
            foreach (var own in ships.Take(count)) universe.Autopilot.Cancel(new DriveSubject(own.objectId, own.member.Identity.key));
            foreach (var ship in ships) RemoveEngagementMap(universe, ship);
            universe.Controls.Reset();
        }
        string output = staticBattle ? StaticFleetOutput(staticDense)
            : Environment.GetEnvironmentVariable("DELTAV_ENGAGEMENT_OUTPUT") ?? (args.Contains("--engagement-fleet-regression")
                ? "Source/obj/EngagementForecast" : "Source/obj/EngagementBaseline-20261108");
        Directory.CreateDirectory(output);
        if (parity)
        {
            parityPatch.UnpatchAll(parityPatch.Id);
            Check(decisions.Count > 0, "fleet parity recorded production decisions");
            File.WriteAllText(Path.Combine(output, "fleet-parity.json"), JsonSerializer.Serialize(decisions,
                new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            fleetCandidateScores = null;
            if (rangeParity)
            {
                gateWriter.Flush();
                string hash = Convert.ToHexString(SHA256.HashData(gateBytes.GetBuffer().AsSpan(0, (int)gateBytes.Length)));
                File.WriteAllText(Path.Combine(output, "range-gates.txt"), $"{fleetInstantGates} {fleetOtherGates} {fleetRejectedGates} {hash}");
                Console.WriteLine($"RANGE_TOTAL calls={fleetRangeCalls} instant={fleetInstantGates} other={fleetOtherGates} rejects={fleetRejectedGates}");
                fleetRangeTrace = null;
            }
        }
        else if (!staticBattle)
            File.WriteAllText(Path.Combine(output, "fleet.json"), JsonSerializer.Serialize(new {
                scope = collisionEdits ? "Real tactics and MPC with scripted foundation removal and an eight-ray first query batch."
                    : "Real tactics, weapon/threat queries, MPC, thruster allocation and integration. No projectile launch, damage, render or UI.",
                rows }, new JsonSerializerOptions { WriteIndented = true }));
        if (withShield && !parity) parityPatch.UnpatchAll(parityPatch.Id);
    }

    static int[] FleetTrajectoryBits(CombatTrajectoryIntent intent)
    {
        var bits = new List<int>();
        foreach (float time in new[] { 0f, .1f, 1f, 2f, 3f, 4f })
        {
            intent.Sample(time, out var position, out var velocity, out float angle, out float angularVelocity);
            foreach (float value in new[] { position.x, position.y, position.z, velocity.x, velocity.y, angle, angularVelocity })
                bits.Add(BitConverter.SingleToInt32Bits(value));
        }
        return bits.ToArray();
    }
}
