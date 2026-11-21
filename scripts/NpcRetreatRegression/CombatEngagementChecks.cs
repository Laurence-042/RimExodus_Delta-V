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
        using var patch = new EngagementFixturePatch(editTerrain: true);
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
            SetEngagementFoundation(item.obj.map, new CellRect(4, 4, 13, 13), EngagementFoundation(), console.shipId);
            ShipBase hull = console.Vessel;
            hull.Combat.weaponsArmed = true;
            item.obj.member = hull;
            universe.RegisterObject(item.obj);
            universe.Controls.For(hull);
        }
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
        if (regression && scenario == 0)
        {
            CheckEngagementScoreContract(gun);
            CheckEngagementRangeBoundaries();
        }
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
            bool symmetric = regression && scenario == 0;
            bool mirrored = regression && scenario == 2;
            bool thin = !symmetric && (mirrored ? x == 4 : x == 16);
            bool thick = (mirrored ? x >= 14 : x <= 6) || symmetric && x >= 14 || z <= 6 || z >= 14;
            if (!thin && !thick) continue;
            var wallDef = TestBuildingDef("engagementArmor" + id);
            wallDef.useHitPoints = true; wallDef.fillPercent = 1; wallDef.passability = Traversability.Impassable;
            var wall = AddTestBuilding(target.map, enemy, id++, wallDef, new EmptyComp());
            MoveCombatFixture(wall, new IntVec3(x, 0, z));
            Set(typeof(Thing), wall, "hitPointsInt", thin ? 48 : 180);
        }
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
        if (regression && scenario == 1) CheckEngagementReadiness(universe, own, target, gun);
        if (regression && scenario == 1) CheckEngagementEventClock(universe, own, target);
        if (regression && scenario == 0)
        {
            var targetPose = ShipRegistry.ShipPose(target.member);
            targetPose.angle -= target.member.FrameAngle;
            var evaluator = new CombatEngagementEvaluation(universe, own.member, target.member, targetPose,
                new[] { target.member }, true);
            var scores = Enumerable.Range(0, 4).Select(side =>
            {
                float angle = side * Mathf.PI / 2;
                var position = OrbitSphere.Move(targetPose.position, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 100);
                return evaluator.Evaluate(new SpacePose(position, angle + Mathf.PI)).Score(rule.engagement);
            }).ToArray();
            Check(scores.Max() - scores.Min() < .05f, "symmetric armor and coverage have no fixed preferred side");
        }
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
                    Console.WriteLine($"ENGAGEMENT_CANDIDATE phase={(float)type.GetField("phase", Private).GetValue(candidate) * Mathf.Rad2Deg:F1} score={type.GetField("score", Private).GetValue(candidate)} damage={value.outgoing:F1} travel={value.travel:F1}");
                }
            }
            var targetFramePose = ShipRegistry.ShipPose(target.member); targetFramePose.angle -= target.member.FrameAngle;
            var evaluator = new CombatEngagementEvaluation(universe, own.member, target.member,
                targetFramePose, new[] { target.member }, true);
            for (int side = 0; side < 4; side++)
            {
                float angle = side * Mathf.PI / 2;
                var evaluation = evaluator.Evaluate(new SpacePose(OrbitSphere.Move(ShipRegistry.ShipPose(target.member).position,
                    new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 100), angle + Mathf.PI));
                Console.WriteLine($"ENGAGEMENT_FACT side={side} damage={evaluation.outgoing} score={evaluation.Score(rule.engagement)} uncertain={evaluation.uncertain}");
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
        var observedGuns = ShipAutoManeuverControl.CaptureAttackWeapons(own.member, own);
        Check(observedGuns.Count > 0 && observedGuns.Any(w => w.gun.ready && w.gun.damageRate > 0),
            "engagement baseline has a real powered production weapon");
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
            CheckEngagementPartBounds(universe, own.member, target.member);
            var targetFramePose = ShipRegistry.ShipPose(target.member); targetFramePose.angle -= target.member.FrameAngle;
            var evaluator = new CombatEngagementEvaluation(universe, own.member, target.member,
                targetFramePose, new[] { target.member }, true);
            CombatEngagementEvaluation.Result AtSide(float phase) => evaluator.Evaluate(new SpacePose(
                OrbitSphere.Move(targetFramePose.position, new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)) * 100), phase + Mathf.PI));
            var dangerous = AtSide(0); var safer = AtSide(Mathf.PI / 2);
            var aggressive = rule.engagement.Copy();
            foreach (var term in aggressive.terms) if (term.side == CombatEngagementSide.Incoming) term.weight = 0;
            Console.WriteLine($"ENGAGEMENT_RISK out={dangerous.outgoing:F2}/{safer.outgoing:F2} in={dangerous.incoming:F2}/{safer.incoming:F2} default={dangerous.Score(rule.engagement):F2}/{safer.Score(rule.engagement):F2} aggressive={dangerous.Score(aggressive):F2}/{safer.Score(aggressive):F2}");
            Check(dangerous.outgoing > 0 && dangerous.incoming > safer.incoming
                && dangerous.Score(rule.engagement) < safer.Score(rule.engagement),
                "real concentrated fire can outweigh the thin-side damage benefit under default risk curves");
            Check(dangerous.Score(aggressive) > safer.Score(aggressive),
                "removing incoming risk favors the thin side on the same observed facts");
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
            ? scenario == 0 ? "production decisions hold a valid firing position on a symmetric target"
                : "production decisions and MPC reach and hold the independently verified weak side"
            : "baseline reproduces failure to remain at the independently verified weak side");
        universe.Autopilot.Cancel(subject);
        RemoveEngagementMap(universe, own);
        RemoveEngagementMap(universe, target);
        universe.Controls.Reset();
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
        var settings = new CombatEngagementSettings { terms = new List<CombatEngagementTerm> { term } };
        Check(Math.Abs(value.Score(settings) - 5) < .0001,
            "engagement curves apply after aggregation, not once per gun or projectile family");
        term.weapons.family = CombatWeaponFamily.Kinetic;
        Check(Math.Abs(value.Score(settings) - 2.6) < .0001,
            "family filter reads the fired projectile category even when the live weapon currently selects a laser");
        term.weight = 0;
        Check(value.Score(settings) == 0, "zero weight removes that term without a hidden risk floor");
    }

    static void CheckEngagementReadiness(UniverseManager universe, ManeuverMotionObject own,
        ManeuverMotionObject target, CompSpaceWeapon weapon)
    {
        var tuning = weapon.Tuning;
        var emptyMagazine = new CompSpaceWeaponMagazine { parent = weapon.parent,
            props = new CompProperties_SpaceWeaponMagazine { projectileDef = weapon.WeaponProps.projectileDef } };
        int originalRemaining = tuning.RemainingTicks;
        float originalEnergy = weapon.WeaponProps.shotEnergy;
        var other = ShipRegistry.ShipPose(target.member); other.angle -= target.member.FrameAngle;
        CombatEngagementEvaluation.Result Observe() =>
            new CombatEngagementEvaluation(universe, own.member, target.member, other,
                new[] { target.member }, true).Evaluate(own.pose);
        try
        {
            Set(tuning, "remainingTicks", 0);
            var ready = Observe();
            Set(tuning, "remainingTicks", 90);
            var cooling = Observe();
            Check(ready.outgoing > 0 && ready.outgoing == cooling.outgoing && tuning.RemainingTicks == 90,
                "cooldown leaves sustained single-shot output and live cooldown unchanged");
            weapon.WeaponProps.shotEnergy = 1000000;
            Check(Observe().outgoing == 0, "a weapon without base shot energy contributes no output");
            weapon.WeaponProps.shotEnergy = originalEnergy;
            weapon.parent.AllComps.Add(emptyMagazine);
            ShipStructureChanges.Notify(weapon.parent.Map, (Building)weapon.parent, null, weapon.parent.OccupiedRect());
            Check(Observe().outgoing == 0, "an empty required magazine contributes no output");
        }
        finally
        {
            weapon.WeaponProps.shotEnergy = originalEnergy;
            Set(tuning, "remainingTicks", originalRemaining);
            weapon.parent.AllComps.Remove(emptyMagazine);
            ShipStructureChanges.Notify(weapon.parent.Map, (Building)weapon.parent, null, weapon.parent.OccupiedRect());
        }
    }

    static void CheckEngagementRangeBoundaries()
    {
        const float radius = 4f, min = 25f, max = 60f;
        var laser = new CombatAttackWeapon(Vector2.zero, 0, 360, max, min, 60, 1, true, true);
        var guided = new CombatAttackWeapon(new Vector2(3, 4), 0, 90, max, min, 60, 1, true, true);
        var kinetic = new CombatAttackWeapon(Vector2.zero, 0, 90, max, min, 60, 1, false, true);
        float near = min - (radius + .001f), far = max + (radius + .001f);
        Check(!laser.OutsideInstantRange(MathF.BitIncrement(near), radius)
            && laser.OutsideInstantRange(near - .01f, radius)
            && !laser.OutsideInstantRange(far, radius) && laser.OutsideInstantRange(far + .01f, radius),
            "instant range bounds preserve inclusive cutoffs");
        Check(laser.OutsideInstantRange(far + 1f, radius) && !guided.OutsideInstantRange(far + 1f, radius)
            && !kinetic.OutsideInstantRange(far + 1f, radius),
            "guns sharing a center distance retain independent muzzle margins and ballistic eligibility");
    }

    static void CheckEngagementPartBounds(UniverseManager universe, ShipBase own, ShipBase target)
    {
        var weapon = AddTestBuilding(target.Map, target.Faction, 158010, TestBuildingDef("exteriorTurret"),
            new CompSpaceWeapon { props = new CompProperties_SpaceWeapon() });
        var engineDef = TestBuildingDef("exteriorEngine");
        engineDef.comps.Add(new CompProperties_GravshipThruster());
        var engine = AddTestBuilding(target.Map, target.Faction, 158011, engineDef, new EmptyComp());
        var hidden = AddTestBuilding(target.Map, target.Faction, 158012, TestBuildingDef("interiorWeapon"),
            new CompSpaceWeapon { props = new CompProperties_SpaceWeapon { topTurret = false } });
        var exposedCell = target.Anchor.Position + IntVec3.East;
        MoveCombatFixture(weapon, target.Anchor.Position);
        MoveCombatFixture(hidden, target.Anchor.Position);
        MoveCombatFixture(engine, exposedCell);
        target.Map.roofGrid ??= new RoofGrid(target.Map);
        var anchorCell = target.Anchor.Position;
        var oldRoof = target.Map.roofGrid.RoofAt(anchorCell);
        target.Map.roofGrid.SetRoof(anchorCell, new RoofDef { defName = "interiorTargetFixtureRoof" });
        var targets = new ShipFireControl.ExternalTargets(new[] { engine, target.Anchor, weapon, hidden });
        Check(ShipFireControl.ExternalCombatPart(hidden) == null && ShipFireControl.ExternalCombatPart(target.Anchor) == null,
            "a solid roof hides an internal gun and the console never qualifies as an aim point");
        var menu = Dialog_CombatOrders.QueueableParts(target).ToHashSet();
        Check(menu.SetEquals(target.Map.listerThings.AllThings.Where(thing =>
                thing == weapon || thing == engine || thing.def.defName.StartsWith("engagementThreat", StringComparison.Ordinal))),
            "the order menu only lists exterior weapons and propulsion from the target ship");
        var fire = universe.Controls.For(own).FireControl;
        var hiddenOrder = CombatTarget.ForShip(target.Identity, hidden.thingIDNumber);
        fire.CommandTarget(hiddenOrder, appendPart: true);
        Check(fire.QueueCount == 0, "a roofed weapon is rejected when queued directly");
        target.Map.roofGrid.SetRoof(anchorCell, oldRoof);
        foreach (Building part in new[] { weapon, engine, hidden })
            RemoveFixtureBuilding(part, target.Map);
        var visited = new List<Thing>();
        bool Select(CombatPart priority, Thing reachable)
        {
            visited.Clear();
            return targets.TrySelect(priority, part => { visited.Add(part); return part == reachable; });
        }
        Check(new CombatStrategy().BuiltInFire.part == CombatPart.Weapons && targets.weapons.SequenceEqual(new[] { weapon })
            && targets.propulsion.SequenceEqual(new[] { engine }), "only exterior weapons and thrusters can be offered as aim points");
        Check(Select(CombatPart.Weapons, weapon) && visited.SequenceEqual(new[] { weapon }),
            "default searches weapon targets first");
        Check(Select(CombatPart.Propulsion, engine) && visited.SequenceEqual(new[] { engine }),
            "propulsion rule searches thruster targets first");
        Check(Select(CombatPart.Weapons, engine) && visited.SequenceEqual(new[] { weapon, engine }),
            "unreachable weapons fall through to propulsion");
        Check(Select(CombatPart.Weapons, null) && visited.SequenceEqual(new[] { weapon, engine, null }),
            "both unreachable categories fall back to hull center");
        visited.Clear();
        Check(!targets.TrySelect(CombatPart.Weapons, part => { visited.Add(part); return false; }) && visited.Last() == null,
            "unreachable center rejects the shot");
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
            CombatEngagementEvaluation.Result Evaluate(Vector2 shooterVelocity, Vector2 targetVelocity)
            {
                var candidate = own.pose; candidate.velocity = shooterVelocity;
                var targetPose = ShipRegistry.ShipPose(target.member); targetPose.angle -= target.member.FrameAngle;
                targetPose.velocity = targetVelocity;
                return new CombatEngagementEvaluation(universe, own.member, target.member, targetPose,
                    new[] { target.member }, true).Evaluate(candidate);
            }
            var stationary = Evaluate(Vector2.zero, Vector2.zero);
            var closing = Evaluate(new Vector2(0, -20), Vector2.zero);
            var receding = Evaluate(new Vector2(0, 20), Vector2.zero);
            var commonDrift = Evaluate(new Vector2(100, 0), new Vector2(100, 0));
            Check(stationary.outgoing > 0 && closing.outgoing > stationary.outgoing * 1.5f
                && receding.outgoing < stationary.outgoing * .7f,
                "inherited muzzle velocity changes the single-shot kinetic damage in the correct direction");
            Check(Math.Abs(commonDrift.outgoing - stationary.outgoing) < .02f * stationary.outgoing,
                "common drift does not turn into extra kinetic damage");
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
                && rebuilt.engagement.terms.All(t => baseline.value.Raw(t)
                    == copy.value.Raw(t)),
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
                float raw = first.value?.Raw(term) ?? float.NaN;
                float weightedDelta = term.curve.Evaluate(float.IsNaN(raw) ? 0 : raw);
                Check(first.value != null && second.value != null && raw > 0
                    && second.value.Raw(term) == raw
                    && Math.Abs(second.state.selectedScore - first.state.selectedScore - weightedDelta) < .001f,
                    $"{behavior} applies the player weight to candidate damage (raw={raw:R}, "
                    + $"score={first.state.selectedScore:R}->{second.state.selectedScore:R}, expected delta={weightedDelta:R})");
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

    static void RemoveEngagementMap(UniverseManager universe, SpaceObject frame)
    {
        universe.UnregisterObject(frame);
        SpaceCollision.Forget(frame.map);
        ShipPhysics.Forget(frame.map);
        Find.Maps.Remove(frame.map);
    }

    static TerrainDef EngagementFoundation()
    {
        var foundation = (TerrainDef)Raw(typeof(TerrainDef));
        foundation.defName = "EngagementFoundation";
        foundation.isFoundation = true;
        foundation.tags = new List<string> { "Substructure" };
        return foundation;
    }

    static void SetEngagementFoundation(Map map, IEnumerable<IntVec3> cells, TerrainDef foundation, int? owner = null)
    {
        var ownership = SubstructureOwnership.For(map);
        if (owner.HasValue) ownership.SetPendingStamp(cells, owner.Value);
        using var changes = Current.Game.GetComponent<UniverseManager>().ShipStructure.BeginChanges();
        try
        {
            foreach (IntVec3 cell in cells)
                if (SubstructureOwnership.FoundationAt(map, cell) != foundation)
                    map.terrainGrid.SetFoundation(cell, foundation);
        }
        finally
        {
            if (owner.HasValue) ownership.ClearPendingStamp();
        }
    }

    static void SetEngagementVacuum(Map map)
    {
        var vacuum = (TerrainDef)Raw(typeof(TerrainDef));
        vacuum.defName = "EngagementVacuum";
        vacuum.exposesToVacuum = true;
        using var changes = Current.Game.GetComponent<UniverseManager>().ShipStructure.BeginChanges();
        foreach (IntVec3 cell in CellRect.WholeMap(map)) map.terrainGrid.SetTerrain(cell, vacuum);
    }

    sealed class EngagementFixturePatch : IDisposable
    {
        readonly Harmony patch = new Harmony("DeltaV.Regression.Engagement");
        internal EngagementFixturePatch(bool editTerrain = false)
        {
            patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "MaxHitPoints"),
                prefix: new HarmonyMethod(typeof(Program), nameof(TestHullMaxHitPoints)));
            patch.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
                prefix: new HarmonyMethod(typeof(Program), nameof(StructureFoundationWithOdyssey)));
            if (!editTerrain) return;
            patch.Patch(AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            patch.Patch(AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.SetRoof)),
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleRoof)) { priority = Priority.Last });
            foreach (Type entry in new[] { typeof(Patch_TerrainGrid_SetFoundation_Ownership),
                typeof(Patch_TerrainGrid_RemoveFoundation_Ownership), typeof(Patch_ShipStructure_Terrain),
                typeof(Patch_MoonPool_SetRoof) })
                patch.CreateClassProcessor(entry).Patch();
        }
        public void Dispose() => patch.UnpatchAll(patch.Id);
    }
}
