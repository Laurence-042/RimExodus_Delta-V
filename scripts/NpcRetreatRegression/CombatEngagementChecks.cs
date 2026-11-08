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

internal static partial class Program
{
    static void CheckEngagementBaseline(UniverseManager universe, Faction player, Faction enemy, int scenario = 0)
    {
        using var patch = new EngagementFixturePatch();
        bool regression = Environment.GetCommandLineArgs().Contains("--engagement-regression");
        Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
        Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
        var factions = (FactionManager)Raw(typeof(FactionManager));
        Set(factions, "ofPlayer", player); Set(Find.World, "factionManager", factions);
        ThingDefOf.PilotConsole = TestBuildingDef("engagementConsole");
        var own = new ManeuverMotionObject(91000, TestMap(91000, 21),
            new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(100, 0)), Mathf.PI));
        var target = new ManeuverMotionObject(92000, TestMap(92000, 21), new SpacePose(Vector3.forward, 0));
        int id = 93000;
        foreach (var item in new[] { (obj: own, faction: player), (obj: target, faction: enemy) })
        {
            var console = AddTestConsole(item.obj.map, item.faction, id++);
            if (regression && scenario > 0) { console.parent.def.useHitPoints = true; console.parent.def.fillPercent = 1; }
            MoveCombatFixture(console.parent, new IntVec3(10, 0, 10));
            Set(typeof(Thing), console.parent, "hitPointsInt", 300);
            var hull = new CombatHull(console.Ship);
            var foundation = (TerrainDef)Raw(typeof(TerrainDef));
            foundation.tags = new List<string> { "Substructure" }; foundation.isFoundation = true;
            var owners = new int[item.obj.map.cellIndices.NumGridCells];
            Set(SubstructureOwnership.TryGet(item.obj.map), "ownerGrid", owners);
            for (int x = 4; x <= 16; x++) for (int z = 4; z <= 16; z++)
            {
                var cell = new IntVec3(x, 0, z); hull.cells.Add(cell);
                int index = item.obj.map.cellIndices.CellToIndex(cell);
                item.obj.map.terrainGrid.foundationGrid[index] = foundation; owners[index] = console.shipId;
            }
            item.obj.member = hull; universe.objects.Add(item.obj);
            universe.Controls.For(hull);
        }
        Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        var laser = TestBuildingDef("DeltaV_Projectile_Laser");
        laser.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, laserRange = 120, speed = 60 });
        var effectNode = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml")
            .Root.Elements().Single(n => (string)n.Element("defName") == "DeltaV_Impact_LaserVsStructure").Element("defenderEffect");
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "EngagementLaserStructure",
            projectileDefs = new List<ThingDef> { laser }, defenderEffect = new ImpactOutcomePart {
                kind = ImpactEffectKind.DamageMap, damage = (float)effectNode.Element("damage"),
                incidenceMinFactor = (float)effectNode.Element("incidenceMinFactor") } });
        SpaceImpactEffects.ResetCache();
        var gun = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon {
            projectileDef = laser, range = 120, intrinsicAimSkill = 20, topTurret = true }, safeFire = false };
        var building = AddTestBuilding(own.map, player, id++, TestBuildingDef("engagementLaser"), gun);
        MoveCombatFixture(building, new IntVec3(10, 0, 12));
        if (regression && scenario == 0) CheckEngagementScoreContract(gun);
        if (regression && scenario > 0)
        {
            building.def.useHitPoints = true; Set(typeof(Thing), building, "hitPointsInt", 300);
            var tuning = new CompWeaponTuning(); tuning.Initialize(new CompProperties_WeaponTuning
                { maxPower = 4, defaultInterval = 90, minInterval = 90, maxInterval = 90, heatPerShot = 0 });
            tuning.power = 4; tuning.parent = building; building.AllComps.Add(tuning);
        }
        // +X is the independently identified thin side. Other sides have three layers.
        for (int x = 4; x <= 16; x++) for (int z = 4; z <= 16; z++)
        {
            bool mirrored = regression && scenario == 2;
            bool thin = mirrored ? x == 4 : x == 16;
            bool thick = (mirrored ? x >= 14 : x <= 6) || z <= 6 || z >= 14;
            if (!thin && !thick) continue;
            var wallDef = TestBuildingDef("engagementArmor" + id);
            wallDef.useHitPoints = true; wallDef.fillPercent = 1; wallDef.passability = Traversability.Impassable;
            var wall = AddTestBuilding(target.map, enemy, id++, wallDef, new EmptyComp());
            MoveCombatFixture(wall, new IntVec3(x, 0, z));
            Set(typeof(Thing), wall, "hitPointsInt", thin ? 48 : 180);
        }
        SubstructureOwnership.BumpStructureVersion();
        var planner = new ShipAutoManeuverControl(universe, own.member.Identity);
        Set(planner, "anchorTarget", CombatTarget.ForShip(target.member.Identity));
        Set(planner, "anchorDistance", 100f); Set(planner, "offenseEnabled", true);
        var strategy = universe.Controls.For(own.member).FireControl.Strategy(own.member);
        var rule = regression ? strategy.BuiltInManeuver : new CombatStrategyRule();
        rule.tactic.distance = 100;
        strategy.maneuverRules.Add(rule);
        if (regression && scenario > 0) own.pose = new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(0, 100)), -Mathf.PI / 2);
        if (regression && scenario == 2) target.pose = new SpacePose(Vector3.forward, Mathf.PI / 3);
        if (regression && scenario == 1) CheckEngagementStrategyParity(universe, own, target, strategy, rule);
        if (regression && scenario == 1) CheckEngagementFlight(universe, own, target, gun);
        if (regression && scenario == 1) CheckEngagementTraverse(universe, own, target, gun);
        if (regression && scenario == 1) CheckEngagementEventClock(universe, own, target);
        if (regression && scenario > 0)
        {
            if (scenario == 2)
            {
                own.TryGetDrive(own.member.Identity, out var candidateDrive);
                var candidates = (System.Collections.IEnumerable)typeof(ShipAutoManeuverControl).GetMethod("EngagementCandidates", Private)
                    .Invoke(planner, new object[] { own.member, target.member, own.pose, ShipRegistry.ShipPose(target.member), candidateDrive, rule, rule.tactic, null });
                foreach (var candidate in candidates)
                {
                    var type = candidate.GetType();
                    var value = (CombatEngagementEvaluation.Result)type.GetField("value", Private).GetValue(candidate);
                    Console.WriteLine($"ENGAGEMENT_REFINED phase={(float)type.GetField("phase", Private).GetValue(candidate) * Mathf.Rad2Deg:F1} score={type.GetField("score", Private).GetValue(candidate)} damage={value.outgoing:F1} travel={value.travel:F1}");
                }
            }
            var targetFramePose = ShipRegistry.ShipPose(target.member); targetFramePose.angle -= target.member.FrameAngle;
            var evaluator = new CombatEngagementEvaluation(universe, own.member, target.member,
                targetFramePose, new[] { target.member }, rule.engagement, true);
            for (int side = 0; side < 4; side++)
            {
                float angle = side * Mathf.PI / 2;
                var evaluation = evaluator.Evaluate(new SpacePose(OrbitSphere.Move(ShipRegistry.ShipPose(target.member).position,
                    new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 100), angle + Mathf.PI));
                Console.WriteLine($"ENGAGEMENT_FACT side={side} damage={evaluation.outgoing} score={evaluation.Score(rule.engagement)} uncertain={evaluation.uncertain}");
                var rough = evaluator.Evaluate(new SpacePose(OrbitSphere.Move(ShipRegistry.ShipPose(target.member).position,
                    new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 100), angle + Mathf.PI), true);
                Console.WriteLine($"ENGAGEMENT_COARSE side={side} damage={rough.outgoing} score={rough.Score(rule.engagement)}");
            }
        }
        var method = typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);
        bool Sample(out SpacePose pose) { pose = ShipRegistry.ShipPose(target.member); return true; }
        bool Plan(SpaceObject body, SpacePose other, float radius, DeltaV.DriveInfo drive, out CombatTrajectoryIntent intent)
        {
            object[] args = { body, other, radius, drive, null };
            bool valid = (bool)method.Invoke(planner, args); intent = (CombatTrajectoryIntent)args[4]; return valid;
        }
        var subject = new DriveSubject(own.objectId, own.member.Identity.key);
        universe.Autopilot.TrackOrbit(subject, Sample, 10, Plan);
        int inSector = 0, inRange = 0, both = 0;
        float phaseTraveled = 0, lastPhase = 0, coverage = 0;
        int lastDeadline = -1;
        for (int tick = 0; tick < 10800; tick++)
        {
            RecoveryTick(universe.Autopilot, tick);
            Vector2 offset = OrbitSphere.Offset(ShipRegistry.ShipPose(target.member).position, ShipRegistry.ShipPose(own.member).position);
            float phase = Mathf.Atan2(offset.y, offset.x);
            phaseTraveled += SpaceTransform.WrapAngle(phase - lastPhase); lastPhase = phase;
            var pose = (CombatAttackPose)typeof(ShipAutoManeuverControl).GetField("attackPose", Private).GetValue(planner);
            coverage = Mathf.Max(coverage, pose.firepower);
            if (regression && tick < 1800)
            {
                var reposition = (CombatReposition)typeof(ShipAutoManeuverControl).GetField("reposition", Private).GetValue(planner);
                if (reposition.nextTick != lastDeadline)
                {
                    lastDeadline = reposition.nextTick;
                    var value = (CombatEngagementEvaluation.Result)typeof(ShipAutoManeuverControl).GetField("engagementResult", Private).GetValue(planner);
                    Console.WriteLine($"ENGAGEMENT_CHOICE tick={tick} actual={phase * Mathf.Rad2Deg:F1} goal={reposition.phase * Mathf.Rad2Deg:F1} score={reposition.selectedScore:F2} damage={value?.outgoing:F1} travel={value?.travel:F1}");
                }
            }
            if (tick < 7200) continue;
            float weakPhase = regression && scenario == 2 ? target.pose.angle + Mathf.PI : 0;
            bool sector = Mathf.Abs(SpaceTransform.WrapAngle(phase - weakPhase)) <= Mathf.PI / 6;
            bool range = offset.magnitude >= 90 && offset.magnitude <= 110;
            if (sector) inSector++; if (range) inRange++; if (sector && range) both++;
        }
        Check(coverage > 0, "engagement baseline has a real powered, aimed production weapon");
        if (regression && scenario == 1) CheckEngagementComposition(universe, own, target, gun, player, enemy);
        // The frozen movement fixture above stays unchanged. The separate risk
        // fixture must give its targetable guns the real turret cover/health contract;
        // default raw ThingDefs have zero cover and HP and cannot establish danger.
        float turretCover = (float)XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml").Root.Elements()
            .Single(n => (string)n.Attribute("Name") == "DeltaV_SpaceTurret1").Element("fillPercent");
        if (regression && scenario == 1) building.def.fillPercent = turretCover;
        // Capture a real concentrated firing sector separately, without altering the
        // stationary weak-side motion baseline above or replacing threat evaluation.
        for (int i = 0; i < 8; i++)
        {
            var enemyGun = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon {
                projectileDef = laser, range = 120, intrinsicAimSkill = 20, topTurret = true, cooldownTicks = 12 }, safeFire = false };
            var turret = AddTestBuilding(target.map, enemy, id++, TestBuildingDef("engagementThreat" + i), enemyGun);
            MoveCombatFixture(turret, new IntVec3(12, 0, 7 + i));
            if (regression && scenario == 1)
            { turret.def.useHitPoints = true; turret.def.fillPercent = turretCover; Set(typeof(Thing), turret, "hitPointsInt", 300); }
            enemyGun.coverage.arc = 60;
            enemyGun.coverage.direction = -ShipOrientation.AngleFromRot4(turret.Rotation) * Mathf.Rad2Deg;
        }
        SubstructureOwnership.BumpStructureVersion();
        var threats = new List<CombatWeaponThreat>();
        CombatWeaponThreat.CaptureRaw(target.member, target, 0f, threats);
        float Rate(float angle)
        {
            Vector3 position = OrbitSphere.Move(ShipRegistry.ShipPose(target.member).position,
                new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 100);
            float total = 0;
            foreach (var threat in threats)
            { threat.Score(position, Vector2.zero, new Vector2(6.5f, 6.5f), 0, Vector2.zero, out float rate, out _); total += rate; }
            return total;
        }
        float rearThreat = Rate(target.pose.angle), sideThreat = Rate(target.pose.angle + Mathf.PI * .5f);
        Check(threats.Count == 8 && rearThreat > 250 && sideThreat < .01f,
            "production weapon observation distinguishes concentrated weak-side fire from the safe side");
        if (regression && scenario == 1)
        {
            CheckEngagementPartBounds(universe, target.member);
            var targetFramePose = ShipRegistry.ShipPose(target.member); targetFramePose.angle -= target.member.FrameAngle;
            var evaluator = new CombatEngagementEvaluation(universe, own.member, target.member,
                targetFramePose, new[] { target.member }, rule.engagement, true);
            CombatEngagementEvaluation.Result AtSide(float phase) => evaluator.Evaluate(new SpacePose(
                OrbitSphere.Move(targetFramePose.position, new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)) * 100), phase + Mathf.PI));
            var dangerous = AtSide(0); var safer = AtSide(Mathf.PI / 2);
            Check(dangerous.outgoing > 0 && dangerous.incoming > safer.incoming
                && dangerous.Score(rule.engagement) < safer.Score(rule.engagement),
                "real concentrated fire can outweigh the thin-side damage benefit under default risk curves");
            var aggressive = rule.engagement.Copy();
            foreach (var term in aggressive.terms) if (term.side == CombatEngagementSide.Incoming) term.weight = 0;
            Console.WriteLine($"ENGAGEMENT_RISK out={dangerous.outgoing:F2}/{safer.outgoing:F2} in={dangerous.incoming:F2}/{safer.incoming:F2} default={dangerous.Score(rule.engagement):F2}/{safer.Score(rule.engagement):F2} aggressive={dangerous.Score(aggressive):F2}/{safer.Score(aggressive):F2}");
            Check(dangerous.outgoing < safer.outgoing && dangerous.Score(aggressive) < safer.Score(aggressive),
                "fatal counterfire removes future firing opportunities even when the player assigns zero damage penalty");
            // A separate, explicitly slower battery leaves an offensive advantage to
            // trade against risk. Keep the fatal 12-tick fixture above as its own gate.
            foreach (var enemyWeapon in target.member.Weapons) enemyWeapon.WeaponProps.cooldownTicks = 90;
            var survivable = new CombatEngagementEvaluation(universe, own.member, target.member,
                targetFramePose, new[] { target.member }, rule.engagement, true);
            CombatEngagementEvaluation.Result SurvivingSide(float phase) => survivable.Evaluate(new SpacePose(
                OrbitSphere.Move(targetFramePose.position, new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)) * 100), phase + Mathf.PI));
            var riskyBenefit = SurvivingSide(0); var safeBenefit = SurvivingSide(Mathf.PI / 2);
            Console.WriteLine($"ENGAGEMENT_SURVIVABLE out={riskyBenefit.outgoing:F2}/{safeBenefit.outgoing:F2} in={riskyBenefit.incoming:F2}/{safeBenefit.incoming:F2} default={riskyBenefit.Score(rule.engagement):F2}/{safeBenefit.Score(rule.engagement):F2} aggressive={riskyBenefit.Score(aggressive):F2}/{safeBenefit.Score(aggressive):F2}");
            Check(riskyBenefit.Score(rule.engagement) < safeBenefit.Score(rule.engagement)
                && riskyBenefit.Score(aggressive) > safeBenefit.Score(aggressive),
                "player-editable incoming weights reverse the survivable offense-versus-risk preference on identical facts");
        }
        string path = regression ? $"Source/obj/EngagementForecast/position-{scenario}.json" : "Source/obj/EngagementBaseline-20261108/position.json";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonSerializer.Serialize(new { baseline = regression ? "working-tree" : "c81b2eb", scenario, ticks = 10800,
            scope = "Real structure and weapon observations, production tactical planner/MPC/physics; fixed structure, no firing or damage during flight. Damage oracle is the separate production collision fixture.",
            weakSectorDegrees = 30, range = new[] { 90, 110 }, steadyTicks = 3600,
            sectorFraction = inSector / 3600f, rangeFraction = inRange / 3600f,
            jointFraction = both / 3600f, requiredJointFraction = .8f,
            traveledDegrees = phaseTraveled * Mathf.Rad2Deg, maximumCoverage = coverage,
            rearThreatRate = rearThreat, sideThreatRate = sideThreat }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"ENGAGEMENT_BASELINE sector={inSector / 3600f:F3} range={inRange / 3600f:F3} joint={both / 3600f:F3} required=0.800");
        Check(regression ? both >= 2880 : both < 2880, regression
            ? "production decisions and MPC reach and hold the independently verified weak side"
            : "baseline reproduces failure to remain at the independently verified weak side");
        universe.Autopilot.Cancel(subject); universe.Controls.Reset(); universe.objects.Clear(); Find.Maps.Clear();
        ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
    }

    static void CheckEngagementScoreContract(CompSpaceWeapon weapon)
    {
        var value = new CombatEngagementEvaluation.Result();
        var laser = new CombatEngagementEvaluation.Contribution { weapon = weapon,
            family = CombatWeaponFamily.Laser, side = CombatEngagementSide.Outgoing, damage = 30 };
        var kinetic = new CombatEngagementEvaluation.Contribution { weapon = weapon,
            family = CombatWeaponFamily.Kinetic, side = CombatEngagementSide.Outgoing, damage = 70 };
        var incoming = new CombatEngagementEvaluation.Contribution { weapon = weapon,
            family = CombatWeaponFamily.Laser, side = CombatEngagementSide.Incoming, damage = 80 };
        value.contributions.AddRange(new[] { laser, kinetic, incoming });
        var term = new CombatEngagementTerm { metric = CombatEngagementMetric.StructureDamage,
            curve = new CombatScoreCurve(true, new CombatScorePoint(0, 0),
                new CombatScorePoint(50, 1), new CombatScorePoint(100, 5)) };
        var settings = new CombatEngagementSettings { terms = new List<CombatEngagementTerm> { term }, burstSeconds = 3 };
        Check(Math.Abs(value.Score(settings) - 5) < .0001,
            "engagement curves apply after aggregation, not once per gun or projectile family");
        term.weapons.family = CombatWeaponFamily.Kinetic;
        Check(Math.Abs(value.Score(settings) - 2.6) < .0001,
            "family filter reads the fired projectile category even when the live weapon currently selects a laser");
        term.metric = CombatEngagementMetric.BurstFraction; term.weapons.family = CombatWeaponFamily.All;
        term.curve = new CombatScoreCurve(true, new CombatScorePoint(0, 0), new CombatScorePoint(1, 1));
        value.damageEvents.AddRange(new[] { (0f, laser, .1f), (2f, kinetic, .2f), (2f, incoming, .8f), (6f, laser, .15f) });
        Check(Math.Abs(value.Score(settings) - .3f) < .0001,
            "outgoing burst term uses its own sliding damage window and excludes incoming events");
        term.side = CombatEngagementSide.Incoming;
        Check(Math.Abs(value.Score(settings) - .8f) < .0001,
            "incoming burst uses the same configurable window contract");
        term.side = CombatEngagementSide.Outgoing; settings.burstSeconds = 1;
        Check(Math.Abs(value.Score(settings) - .2f) < .0001,
            "editing the burst window distinguishes separated volleys from concentrated damage");
        term.weight = 0;
        Check(value.Score(settings) == 0, "zero burst weight removes that term without a hidden risk floor");
    }

    static void CheckEngagementTraverse(UniverseManager universe, ManeuverMotionObject own,
        ManeuverMotionObject target, CompSpaceWeapon weapon)
    {
        var traverse = new CompSpaceWeaponTraverse { parent = weapon.parent,
            props = new CompProperties_SpaceWeaponTraverse { mechanicalArcDeg = 360 } };
        weapon.parent.AllComps.Add(traverse);
        int originalTick = Find.TickManager.TicksGame, originalInterval = weapon.Tuning.interval;
        int originalRemaining = weapon.Tuning.RemainingTicks;
        float deviation = weapon.fireDeviationDeg;
        try
        {
            weapon.Tuning.interval = 1000; Set(weapon.Tuning, "remainingTicks", 90);
            weapon.fireDeviationDeg = 1;
            var initial = own.pose;
            SpacePose Motion(float time)
            {
                var pose = initial;
                pose.angle += time < 1.5f ? 2 * Mathf.Sin(time * Mathf.PI / 1.5f) : 0;
                return pose;
            }
            var other = ShipRegistry.ShipPose(target.member); other.angle -= target.member.FrameAngle;
            foreach (float rate in new[] { 30f, 60f, 120f })
            {
                traverse.TraverseProps.turnRateDegPerSec = rate;
                Set(traverse, "currentAngle", 0f); Set(traverse, "rotationTick", -1);
                var settings = CombatEngagementSettings.Default(); settings.windowSeconds = 4;
                var predicted = new CombatEngagementEvaluation(universe, own.member, target.member, other,
                    new[] { target.member }, settings, true).Evaluate(initial, trajectory: Motion);
                Check(traverse.CurrentAngle == 0 && weapon.Tuning.RemainingTicks == 90,
                    "moving-target traverse forecast leaves live barrel and cooldown untouched at " + rate);
                float actualFirst = float.PositiveInfinity;
                var copy = new CompSpaceWeaponTraverse.Forecast(traverse);
                bool identical = true;
                for (int tick = 0; tick < 240; tick++)
                {
                    Set(Find.TickManager, "ticksGameInt", tick);
                    var source = Motion(tick / 60f);
                    var muzzle = SpaceTransform.RigidPointPose(source,
                        SpaceWeaponGeometry.MuzzleMapPoint(weapon) - own.member.HullCenter, 0);
                    var line = OrbitSphere.Offset(muzzle.position, other.position);
                    float desired = weapon.ClampAim(SpaceTransform.WrapAngle(Mathf.Atan2(line.y, line.x) - source.angle));
                    traverse.AimAt(desired); copy.AimAt(desired, tick, true);
                    if (tick >= 90 && SpaceWeaponGeometry.WithinFireDeviation(desired, traverse.CurrentAngle, weapon.fireDeviationDeg))
                        actualFirst = Mathf.Min(actualFirst, tick / 60f);
                    // Repeating an aim command in the same tick cannot double turn rate.
                    traverse.AimAt(desired); copy.AimAt(desired, tick, true);
                    identical &= traverse.CurrentAngle == copy.Angle;
                }
                float forecastFirst = predicted.damageEvents.Where(e => e.fraction > 0).Select(e => e.time).DefaultIfEmpty(float.PositiveInfinity).Min();
                Check(identical && actualFirst > 1.6f && Math.Abs(forecastFirst - actualFirst) <= .101f,
                    "cooldown tracking and same-tick commands match production traverse before first hit: rate=" + rate
                    + " forecast=" + forecastFirst + " actual=" + actualFirst);
            }
        }
        finally
        {
            weapon.parent.AllComps.Remove(traverse); weapon.Tuning.interval = originalInterval;
            Set(weapon.Tuning, "remainingTicks", originalRemaining); weapon.fireDeviationDeg = deviation;
            Set(Find.TickManager, "ticksGameInt", originalTick);
        }
    }

    static void CheckEngagementPartBounds(UniverseManager universe, ShipBase target)
    {
        var observation = new CombatDamageForecast.Observation(universe.FrameOf(target.Map), target);
        var candidates = ShipFireControl.PartCandidates(observation.health.Keys, CombatPart.Automatic);
        bool same = true;
        foreach (var omitted in candidates.Select(c => c.thing).Prepend(null))
        for (int angle = 0; angle < 36; angle++)
        {
            var removed = new HashSet<Thing>(); if (omitted != null) removed.Add(omitted);
            Vector2 source = target.HullCenter + SpaceTransform.Rotate(new Vector2(100, 0), angle * Mathf.PI / 18);
            Thing expected = null; float best = float.NegativeInfinity;
            foreach (var part in candidates)
            {
                if (removed.Contains(part.thing)) continue;
                float score = part.value / (1 + SpaceCollision.TargetObstruction(target, source, part.point, part.thing, removed))
                    - .001f * (part.point - source).magnitude;
                if (score <= best) continue;
                best = score; expected = part.thing;
            }
            same &= ShipFireControl.ChoosePart(candidates, target, source, (part, point) => true, removed, observation.query) == expected;
        }
        Check(same && candidates.Count > 1, "bounded part selection exactly matches unpruned live obstruction scoring across bearings and destroyed parts");
    }

    static void CheckEngagementFlight(UniverseManager universe, ManeuverMotionObject own,
        ManeuverMotionObject target, CompSpaceWeapon weapon)
    {
        var projectile = TestBuildingDef("forecastFlightRound");
        projectile.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, speed = 60, mass = 1, timeoutTicks = 600 });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "ForecastFlightImpact",
            projectileDefs = new List<ThingDef> { projectile },
            attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 24,
                kineticMassScaling = true, incidenceMinFactor = .5f } });
        SpaceImpactEffects.ResetCache();
        var originalProjectile = weapon.WeaponProps.projectileDef;
        float originalRange = weapon.WeaponProps.range;
        var tuning = weapon.Tuning; int originalInterval = tuning.interval, originalPower = tuning.power;
        try
        {
            weapon.WeaponProps.projectileDef = projectile;
            weapon.WeaponProps.range = 240;
            tuning.interval = 1000; tuning.power = 1;
            var settings = CombatEngagementSettings.Default(); settings.windowSeconds = 4;
            CombatEngagementEvaluation.Result Evaluate(Vector2 shooterVelocity, Vector2 targetVelocity, float window = 4)
            {
                settings.windowSeconds = window;
                var candidate = own.pose; candidate.velocity = shooterVelocity;
                var targetPose = ShipRegistry.ShipPose(target.member); targetPose.angle -= target.member.FrameAngle;
                targetPose.velocity = targetVelocity;
                return new CombatEngagementEvaluation(universe, own.member, target.member, targetPose,
                    new[] { target.member }, settings, true).Evaluate(candidate);
            }
            var shortWindow = Evaluate(Vector2.zero, Vector2.zero, .5f);
            var stationary = Evaluate(Vector2.zero, Vector2.zero);
            var closing = Evaluate(new Vector2(0, -20), Vector2.zero);
            var receding = Evaluate(new Vector2(0, 20), Vector2.zero);
            var commonDrift = Evaluate(new Vector2(100, 0), new Vector2(100, 0));
            float First(CombatEngagementEvaluation.Result result) => result.damageEvents.Where(e => e.fraction > 0).Min(e => e.time);
            Check(shortWindow.outgoing == 0 && shortWindow.damageEvents.Count == 0,
                "projectiles outside the observation window cannot contribute premature damage");
            Check(stationary.damageEvents.Count == 1 && First(stationary) > 1.4f && First(stationary) < 1.9f,
                "single kinetic shot lands after its muzzle-to-target flight time");
            Check(First(closing) < First(stationary) && First(receding) > First(stationary)
                && closing.outgoing > stationary.outgoing * 1.5f && receding.outgoing < stationary.outgoing * .7f,
                "inherited muzzle velocity changes both impact delay and kinetic damage in the correct direction");
            Check(Math.Abs(commonDrift.outgoing - stationary.outgoing) < .02f * stationary.outgoing
                && Math.Abs(First(commonDrift) - First(stationary)) < .04f,
                "common drift does not turn into extra kinetic damage or a different flight deadline");
        }
        finally
        {
            weapon.WeaponProps.projectileDef = originalProjectile;
            weapon.WeaponProps.range = originalRange;
            tuning.interval = originalInterval; tuning.power = originalPower;
            SpaceImpactEffects.ResetCache();
        }
    }

    static void CheckEngagementStrategyParity(UniverseManager universe, ManeuverMotionObject own,
        ManeuverMotionObject enemy, CombatStrategy strategy, CombatStrategyRule savedRule)
    {
        var method = typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);
        T Field<T>(ShipAutoManeuverControl planner, string name) => (T)typeof(ShipAutoManeuverControl).GetField(name, Private).GetValue(planner);
        (CombatTrajectoryIntent reference, CombatEngagementEvaluation.Result value, CombatReposition state) Plan()
        {
            Set(Find.TickManager, "ticksGameInt", 0);
            var planner = new ShipAutoManeuverControl(universe, own.member.Identity);
            Set(planner, "anchorTarget", CombatTarget.ForShip(enemy.member.Identity));
            Set(planner, "anchorDistance", 100f); Set(planner, "offenseEnabled", true);
            own.TryGetDrive(own.member.Identity, out var drive);
            object[] args = { own, ShipRegistry.ShipPose(enemy.member), 10f, drive, null };
            Check((bool)method.Invoke(planner, args), "strategy parity fixture uses the production tactical planner");
            return ((CombatTrajectoryIntent)args[4], Field<CombatEngagementEvaluation.Result>(planner, "engagementResult"),
                Field<CombatReposition>(planner, "reposition"));
        }
        try
        {
            strategy.maneuverRules.Clear();
            var baseline = Plan();
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(CombatStrategyRule));
            using var writer = new StringWriter(); serializer.Serialize(writer, strategy.BuiltInManeuver);
            var rebuilt = (CombatStrategyRule)serializer.Deserialize(new StringReader(writer.ToString()));
            rebuilt.name = "Player rebuilt from editable fields";
            strategy.maneuverRules.Add(rebuilt);
            var copy = Plan();
            Check(baseline.reference.Equals(copy.reference) && baseline.state.phase == copy.state.phase
                && baseline.state.selectedScore == copy.state.selectedScore && baseline.state.nextTick == copy.state.nextTick
                && rebuilt.engagement.terms.All(t => baseline.value.Raw(t, rebuilt.engagement.burstSeconds)
                    == copy.value.Raw(t, rebuilt.engagement.burstSeconds)),
                "builtin and renamed XML-rebuilt editor configuration produce identical choices, terms, deadline and full reference");
            foreach (var behavior in new[] { CombatTactic.Automatic, CombatTactic.HoldRange, CombatTactic.Circle })
            {
                rebuilt.tactic = new CombatTacticSettings { behavior = behavior, distance = 100,
                    bearing = CombatBearing.World, bearingDegrees = 75, facing = CombatFacing.Relative, facingDegrees = 30 };
                rebuilt.engagement.terms.Clear();
                var term = new CombatEngagementTerm { metric = CombatEngagementMetric.StructureDamage,
                    curve = new CombatScoreCurve(true, new CombatScorePoint(0, 0), new CombatScorePoint(100, 3)) };
                rebuilt.engagement.terms.Add(term);
                var first = Plan(); term.weight = 2; var second = Plan();
                Check(first.value != null && first.value.outgoing > 0 && second.value.outgoing == first.value.outgoing
                    && Math.Abs(second.state.selectedScore - 2 * first.state.selectedScore) < .001,
                    behavior + " reads player engagement weights through the shared candidate evaluator");
                Check(Math.Abs(SpaceTransform.WrapAngle(first.state.phase - 75 * Mathf.Deg2Rad)) < .001
                    && first.reference.targetRadius == 100 && Math.Abs(first.reference.frameAngle + 30 * Mathf.Deg2Rad - own.member.FrameAngle) < .001,
                    behavior + " preserves explicit world bearing, range and relative facing while scoring");
            }
            rebuilt.engagement.terms.Clear();
            rebuilt.tactic = new CombatTacticSettings { behavior = CombatTactic.HoldRange,
                bearing = CombatBearing.World, bearingDegrees = 90, facing = CombatFacing.Target };
            rebuilt.maneuverCurves = new CombatManeuverCurves { distance = new CombatScoreCurve(true,
                new CombatScorePoint(0, 0), new CombatScorePoint(50, 1)) };
            var opening = Plan();
            opening.reference.Sample(4, out var openingPosition, out _, out _, out _);
            Check(opening.reference.targetRadius > 100
                && OrbitSphere.ArcCells(openingPosition, ShipRegistry.ShipPose(enemy.member).position) > 100,
                "a player increasing distance curve can open beyond its final knot through the common flight reference");
            rebuilt.tactic.distance = 100;
            Check(Plan().reference.targetRadius == 100,
                "an explicit distance still constrains an extrapolating distance preference");
        }
        finally { strategy.maneuverRules.Clear(); strategy.maneuverRules.Add(savedRule); }
    }

    sealed class EngagementFixturePatch : IDisposable
    {
        readonly Harmony patch = new Harmony("DeltaV.Regression.Engagement");
        internal EngagementFixturePatch()
        {
            patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "MaxHitPoints"),
                prefix: new HarmonyMethod(typeof(Program), nameof(TestHullMaxHitPoints)));
            patch.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
                prefix: new HarmonyMethod(typeof(Program), nameof(StructureFoundationWithOdyssey)));
        }
        public void Dispose() => patch.UnpatchAll(patch.Id);
    }
}
