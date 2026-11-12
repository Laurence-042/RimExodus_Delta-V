using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static int accelerationResolveCalls;
    static bool TestHullMaxHitPoints(ref int __result)
    { __result = 100; return false; }
    static bool CountAccelerationResolve(ref SpaceWeaponAccelerationSolution __result)
    {
        accelerationResolveCalls++;
        __result = new SpaceWeaponAccelerationSolution(0f, null, new List<CompSpaceRailgun>());
        return false;
    }

    static void BindWeaponOwnerFixture(CompSpaceWeapon fixture, ShipBase owner)
    {
        Set(typeof(CompSpaceWeapon), fixture, "cachedOwner", owner);
        Set(typeof(CompSpaceWeapon), fixture, "ownerMap", owner.Map);
        Set(typeof(CompSpaceWeapon), fixture, "ownerPosition", fixture.parent.Position);
        Set(typeof(CompSpaceWeapon), fixture, "ownerRotation", fixture.parent.Rotation);
        Set(typeof(CompSpaceWeapon), fixture, "ownerVersion", SubstructureOwnership.StructureVersion(owner.Map));
        Set(typeof(CompSpaceWeapon), fixture, "ownerCacheValid", true);
        Set(typeof(CompSpaceWeapon), fixture, "ownerWasValid", true);
    }

    static void CheckPerShipFireControl(UniverseManager universe, Faction player, Faction enemy, bool coDriftOnly = false)
    {
        Map ownMap = TestMap(1960), enemyMap = TestMap(1970);
        var hull = AddTestHull(ownMap, player, 1961);
        hull.combat.weaponsArmed = true;
        MoveCombatFixture(hull.parent, new IntVec3(2, 0, 2));
        var own = new MapSpaceObject(1960, ownMap, new SpacePose(Vector3.forward, 0f));
        var opponent = new CombatObject(1970, enemyMap,
            new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(0f, 60f)), 0f));
        universe.objects.Add(own); universe.objects.Add(opponent);
        var first = AddTestConsole(enemyMap, enemy, 1971);
        var second = AddTestConsole(enemyMap, enemy, 1972);
        MoveCombatFixture(second.parent, new IntVec3(3, 0, 2));
        var firstHull = new CombatHull(first.Ship); firstHull.cells.Add(first.parent.Position);
        var secondHull = new CombatHull(second.Ship); secondHull.cells.Add(second.parent.Position);
        // Reverse the container order to test identity tie breaking rather than enumeration order.
        opponent.members.Add(secondHull); opponent.members.Add(firstHull);
        var engineDef = TestBuildingDef("memberEngine"); engineDef.comps.Add(new CompProperties_GravshipThruster());
        var engine1 = AddTestBuilding(enemyMap, enemy, 1973, engineDef, new EmptyComp());
        var engine2 = AddTestBuilding(enemyMap, enemy, 1974, engineDef, new EmptyComp());
        MoveCombatFixture(engine2, second.parent.Position);
        Set(typeof(Thing), engine1, "hitPointsInt", 1); Set(typeof(Thing), engine2, "hitPointsInt", 1);
        Set(typeof(Thing), first.parent, "hitPointsInt", 100); Set(typeof(Thing), second.parent, "hitPointsInt", 100);
        var projectileDef = TestBuildingDef("memberTestBullet");
        projectileDef.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, speed = 100f });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "memberTestImpact",
            projectileDefs = new List<ThingDef> { projectileDef },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 100 } });
        SpaceImpactEffects.ResetCache();
        var weapon = new CompSpaceWeapon { parent = hull.parent,
            props = new CompProperties_SpaceWeapon { projectileDef = projectileDef, range = 500f, baseSpreadDeg = 0f }, safeFire = false };
        var traverse = new CompSpaceWeaponTraverse { parent = hull.parent, props = new CompProperties_SpaceWeaponTraverse() };
        hull.parent.AllComps.Add(weapon); hull.parent.AllComps.Add(traverse);
        var resolveProbe = new Harmony("DeltaV.Regression.AccelerationTiming");
        resolveProbe.Patch(AccessTools.Method(typeof(CompSpaceRailgun), "TraceConnections"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountAccelerationResolve)));
        weapon.WeaponProps.acceleratorStarts.Add(new IntVec3(1, 0, 0));
        try
        {
            accelerationResolveCalls = 0;
            SpaceWeaponService.AimInfo(weapon, own);
            Check(accelerationResolveCalls == 1,
                "first aim query builds the shared connection layout once");
            SpaceWeaponService.AimInfo(weapon, own);
            Check(accelerationResolveCalls == 1,
                "subsequent exact aim queries reuse the same connection layout");
        }
        finally
        {
            weapon.WeaponProps.acceleratorStarts.Clear();
            resolveProbe.UnpatchAll(resolveProbe.Id);
        }
        void Refresh()
        {
            SubstructureOwnership.BumpStructureVersion();
            ShipRegistry.ShipByIdentity(hull.Vessel.Identity);
            universe.Controls.For(firstHull); universe.Controls.For(secondHull);
        }
        Refresh();
        var fire = universe.Controls.For(hull.Vessel).FireControl;
        fire.Strategy(hull.Vessel).targetRules.Add(new CombatStrategyRule { includeDisarmed = true });
        if (coDriftOnly)
        {
            CheckCoDriftingTurret(universe, own, opponent, hull.Vessel,
                CombatTarget.ForShip(firstHull.Identity, first.parent.thingIDNumber), fire, weapon, traverse);
            universe.Projectiles.ResetFlow();
            foreach (var action in Actions(universe).ToArray()) { Actions(universe).Remove(action); action(); }
            universe.Controls.Reset(); universe.objects.Clear(); Find.Maps.Clear();
            ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
            return;
        }
        Check(NpcCombatAi.CountEligibleTargets(universe, hull.Vessel, own) == 2,
            "two hostile ships in one container are separate automatic candidates");
        weapon.WeaponProps.fireArcDeg = 0f;
        weapon.WeaponProps.lockArcDeg = 360f;
        weapon.cooldownUntilTick = Find.TickManager.TicksGame + 1000;
        float coolingAngle = traverse.CurrentAngle;
        for (int i = 0; i < 15; i++)
        {
            Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
            fire.TickAutoVolley(hull.Vessel, own, false);
        }
        var assigned = (IDictionary)typeof(ShipFireControl)
            .GetField("automaticTargets", Private).GetValue(fire);
        Check(assigned.Contains(weapon.Identity),
            "fixed-axis weapon acquires a target within its lock arc before the hull aligns");
        Check(Mathf.Abs(SpaceTransform.WrapAngle(traverse.CurrentAngle - coolingAngle)) > 10f * Mathf.Deg2Rad,
            "automatic cooled turret advances on every tick");
        weapon.WeaponProps.fireArcDeg = 360f;
        weapon.WeaponProps.lockArcDeg = -1f;
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
        fire.Decide(hull.Vessel, own);
        Check(fire.Target.Ship == firstHull.Identity, "equal candidate scores use stable ship identity rather than member order");
        var cooldownCondition = new CombatCriterion { metric = CombatMetric.WeaponCooldownSeconds,
            scope = CombatScope.WeaponGroup, comparison = CombatComparison.Greater, value = 0f,
            weapons = new CombatWeaponFilter { set = CombatWeaponSet.Main, group = weapon.groupNumber } };
        var liveFacts = new CombatStrategyFacts(universe, hull.Vessel, own);
        Check(cooldownCondition.Matches(liveFacts.Context(firstHull)), "maneuver weapon-group conditions read the actual cooling gun");
        cooldownCondition.weapons.weaponDef = "absentWeapon"; cooldownCondition.allWeapons = true;
        Check(!cooldownCondition.Matches(liveFacts.Context(firstHull)), "an empty weapon group never passes an all-weapons condition");
        var holdRule = new CombatStrategyRule { name = "hold test", fireAction = CombatFireAction.Hold };
        fire.Strategy(hull.Vessel).fireRules.Add(holdRule);
        fire.CommandTarget(CombatTarget.ForShip(firstHull.Identity, first.parent.thingIDNumber));
        weapon.cooldownUntilTick = 0;
        fire.TickAutoVolley(hull.Vessel, own, false);
        Check(assigned.Count == 0 && weapon.CooldownTicksLeft == 0 && fire.QueueCount == 1,
            "hold fire prevents a ready gun from launching even with a queued part command");
        fire.Strategy(hull.Vessel).fireRules.Clear(); fire.CancelCommands();
        weapon.cooldownUntilTick = Find.TickManager.TicksGame + 1000;
        weapon.coverage.range = 20f; universe.Weapons.NotifyWeaponModeChanged(weapon);
        fire.TickAutoVolley(hull.Vessel, own, false);
        Check(assigned.Count == 0 && SpaceWeaponService.AimInfo(weapon, own).Range == 20f,
            "per-weapon shortened range gates assignment and the shared aiming information");
        weapon.coverage.range = 10000f;
        Check(weapon.EffectiveRange == weapon.WeaponProps.range, "custom range cannot expand hardware capability");
        weapon.coverage.arc = 30f; weapon.coverage.direction = 20f;
        float mountAngle = ShipOrientation.AngleFromRot4(weapon.parent.Rotation);
        Check(weapon.CoverageError(mountAngle + 20f * Mathf.Deg2Rad) == 0f
            && weapon.CoverageError(mountAngle + 60f * Mathf.Deg2Rad) > 0f,
            "custom arc shares the hardware coverage predicate");
        weapon.coverage.fixedDirection = true;
        Check(Mathf.Abs(SpaceTransform.WrapAngle(weapon.ClampAim(mountAngle + 2f)
            - mountAngle - 20f * Mathf.Deg2Rad)) < .001f, "fixed aim direction overrides tracking without widening hardware");
        weapon.coverage = new CombatWeaponCoverage(); universe.Weapons.NotifyWeaponModeChanged(weapon);
        fire.CancelCommands(); fire.Clear(); fire.Decide(hull.Vessel, own);
        weapon.WeaponProps.manualOnly = true;
        weapon.cooldownUntilTick = Find.TickManager.TicksGame + 1000;
        float beforeEngineeringAim = traverse.CurrentAngle;
        fire.TickVolley(hull.Vessel, own, new ShipVolleyIntent(CombatTarget.ForShip(firstHull.Identity), true));
        Check(traverse.CurrentAngle == beforeEngineeringAim,
            "manual engineering emitter ignores automatic combat aiming and firing");
        fire.TickVolley(hull.Vessel, own, new ShipVolleyIntent(opponent.pose.position, true, false));
        Check(traverse.CurrentAngle != beforeEngineeringAim,
            "manual engineering emitter still consumes the existing player volley command");
        Check(universe.Weapons.EstimateFirepower(hull.Vessel, 60f, 10f) == 0f,
            "engineering emitter does not count as NPC combat firepower");
        weapon.WeaponProps.manualOnly = false;
        fire.Clear(); Set(typeof(Thing), first.parent, "hitPointsInt", 500000);
        fire.Decide(hull.Vessel, own);
        Check(fire.Target.Ship == secondHull.Identity,
            "per-member hull durability changes the score instead of using whole-map durability");
        var debris = AddTestBuilding(enemyMap, enemy, 1975, TestBuildingDef("durableDebris"), new EmptyComp());
        MoveCombatFixture(debris, new IntVec3(4, 0, 4)); Set(typeof(Thing), debris, "hitPointsInt", 1000000);
        Refresh(); fire.Clear(); fire.Decide(hull.Vessel, own);
        Check(fire.Target.Ship == secondHull.Identity, "unowned same-map debris cannot contaminate either ship score");
        Set(typeof(Thing), first.parent, "hitPointsInt", 1); Set(typeof(Thing), second.parent, "hitPointsInt", 500000);
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 179);
        Refresh(); fire.Decide(hull.Vessel, own);
        Check(fire.Target.Ship == firstHull.Identity, "periodic reassessment switches to a substantially better target");
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
        Refresh(); fire.Decide(hull.Vessel, own);
        Check(fire.Target.Ship == firstHull.Identity, "unchanged scores preserve the newly selected target");
        Set(typeof(Thing), second.parent, "factionInt", player);
        Refresh();
        Check(fire.TryGetIntent(hull.Vessel, own, false, out _)
            && fire.Target.Ship == firstHull.Identity,
            "cached target hostility loss reselects another enemy in the same combat tick");
        Set(typeof(Thing), first.parent, "factionInt", player);
        Set(typeof(Thing), second.parent, "factionInt", enemy);
        Refresh();
        Check(fire.TryGetIntent(hull.Vessel, own, false, out _)
            && fire.Target.Ship == secondHull.Identity && fire.LeadPoints(hull.Vessel).Count == 1,
            "mixed-faction map selects and previews only its eligible hostile member");
        enemyMap.listerThings.AllThings.Remove(engine2); Refresh();
        Check(fire.TryGetIntent(hull.Vessel, own, false, out _) && fire.LeadPoints(hull.Vessel).Count == 1,
            "propulsion loss preserves attackable hull and lead markers");

        var near = new MapSpaceObject(1980, TestMap(1980),
            new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(0f, 30f)), 0f))
            { baseTag = SpaceObjectTag.Projectile, faction = enemy };
        var far = new MapSpaceObject(1981, TestMap(1981),
            new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(0f, 60f)), 0f))
            { baseTag = SpaceObjectTag.Projectile, faction = enemy };
        universe.objects.Add(near); universe.objects.Add(far);
        weapon.aimMode = SpaceWeaponAimMode.PointDefense;
        weapon.cooldownUntilTick = Find.TickManager.TicksGame + 1000;
        universe.Weapons.NotifyWeaponModeChanged(weapon);
        weapon.WeaponProps.manualOnly = true;
        fire.TickPointDefense(hull.Vessel, own, false);
        Check(((IDictionary)typeof(ShipFireControl).GetField("pointDefense", Private).GetValue(fire)).Count == 0,
            "engineering emitter cannot acquire a point-defense lock");
        weapon.WeaponProps.manualOnly = false;
        fire.TickPointDefense(hull.Vessel, own, false);
        IDictionary locks = (IDictionary)typeof(ShipFireControl).GetField("pointDefense", Private).GetValue(fire);
        CombatTarget Locked() => (CombatTarget)locks[weapon.Identity].GetType()
            .GetField("target", Private).GetValue(locks[weapon.Identity]);
        Check(locks.Count == 1 && Locked().ObjectId == near.objectId && traverse.CurrentAngle > 0f,
            "point defense owns a per-weapon lock and turns the weapon during cooldown");
        far.pose.velocity = new Vector2(0f, -30f);
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 29);
        fire.TickPointDefense(hull.Vessel, own, false);
        Check(Locked().ObjectId == near.objectId, "point defense retains a valid lock while turning during cooldown");
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
        fire.TickPointDefense(hull.Vessel, own, false);
        Check(Locked().ObjectId == near.objectId, "point defense retains a valid lock after the old 30-tick reselection deadline");
        near.faction = player;
        fire.TickPointDefense(hull.Vessel, own, false);
        Check(Locked().ObjectId == far.objectId, "point defense immediately replaces a lock that becomes friendly in the same tick");
        far.faction = player;
        Check(!fire.TickPointDefense(hull.Vessel, own, false) && locks.Count == 0,
            "friendly projectiles and friendly shuttle do not keep interception weapons awake");
        far.faction = enemy;
        Check(fire.TickPointDefense(hull.Vessel, own, false) && Locked().ObjectId == far.objectId,
            "a new hostile candidate wakes point defense in the same tick");
        weapon.aimMode = SpaceWeaponAimMode.Volley; universe.Weapons.NotifyWeaponModeChanged(weapon);
        Check(locks.Count == 0, "weapon mode changes immediately discard fire-control point-defense state");
        weapon.aimMode = SpaceWeaponAimMode.PointDefense; universe.Weapons.NotifyWeaponModeChanged(weapon);
        fire.TickPointDefense(hull.Vessel, own, false);
        fire.ForgetMap(far.map);
        Check(locks.Count == 0, "target map unloading clears point-defense locks owned by another ship");
        fire.TickPointDefense(hull.Vessel, own, false);
        var archivedPd = new ArchivedSpaceObject { objectId = far.objectId, pose = far.pose };
        universe.ReplaceRepresentation(far, archivedPd);
        fire.ForgetMap(far.map);
        Check(locks.Count == 0, "point-defense cleanup also releases a target whose mapped representation has already been replaced");
        universe.ReplaceRepresentation(archivedPd, far);
        fire.TickPointDefense(hull.Vessel, own, false);
        universe.Weapons.UnregisterWeapon(weapon, ownMap);
        Check(locks.Count == 0, "weapon removal clears its point-defense lock through the existing unregister event");
        fire.TickPointDefense(hull.Vessel, own, false);
        // Seed the retained lock of a captured standalone weapon, independent of controller replacement.
        var capturedWeapon = new CompSpaceWeapon { props = weapon.props };
        var capturedBuilding = AddTestBuilding(ownMap, player, 1963, TestBuildingDef("capturedWeapon"), capturedWeapon);
        locks[capturedWeapon.Identity] = locks[weapon.Identity];
        Set(typeof(Thing), capturedBuilding, "factionInt", enemy);
        typeof(Patch_ShipFaction).GetMethod("Postfix", System.Reflection.BindingFlags.Static | Private)
            .Invoke(null, new object[] { capturedBuilding });
        Check(!locks.Contains(capturedWeapon.Identity) && locks.Count == 1,
            "standalone weapon faction change releases only its lock without replacing the ship controller");
        hull.parent.thingIDNumber = 1962;
        SubstructureOwnership.BumpStructureVersion();
        universe.Controls.NotifyWeaponOwnershipChanged(ownMap);
        Check(locks.Count == 0, "changing weapon ship identity discards the previous owner's point-defense lock");
        hull.parent.thingIDNumber = 1961;
        SubstructureOwnership.BumpStructureVersion();
        fire.TickPointDefense(hull.Vessel, own, false);
        universe.Controls.Reset();
        Check(locks.Count == 0, "controller disposal and game reset clear all point-defense target state");
        universe.objects.Clear(); Find.Maps.Clear();
        ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
    }

    static void CheckCoDriftingTurret(UniverseManager universe, SpaceObject own, SpaceObject opponent,
        ShipBase shooter, CombatTarget handle, ShipFireControl fire, CompSpaceWeapon weapon, CompSpaceWeaponTraverse traverse)
    {
        var projectile = weapon.WeaponProps.projectileDef.GetCompProperties<CompProperties_SpaceProjectile>();
        var start = own.pose; var targetStart = opponent.pose;
        var direction = start.position;
        var relative = new Vector2(0, 100);
        var targetPosition = OrbitSphere.Move(direction, relative);
        var drifting = new SpacePose(direction, start.angle) { velocity = new Vector2(300, 0) };
        var target = new SpacePose(targetPosition, targetStart.angle)
            { velocity = SpaceTransform.TransportTangent(new Vector2(304, 0), direction, targetPosition) };
        int tick = Find.TickManager.TicksGame, originalCooldown = weapon.cooldownUntilTick;
        int interval = weapon.WeaponProps.cooldownTicks, intrinsic = weapon.WeaponProps.intrinsicAimSkill;
        float deviation = weapon.fireDeviationDeg, originalAngle = traverse.CurrentAngle, laserRange = projectile.laserRange;
        bool oldPerformance = DeltaVMod.CurrentSettings.logPerformance;
        var harness = new Harmony("DeltaV.Regression.CoDriftingTurret");
        harness.Patch(AccessTools.Method(typeof(CompSpaceWeaponTraverse), "AimAt"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CaptureWeaponAngle)));
        harness.Patch(AccessTools.Method(typeof(SpaceProjectileService), "RegisterShotVisuals"),
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        var failures = new List<string>();
        var probe = universe.Performance;
        var rejected = (int[])AccessTools.Field(typeof(DeltaVPerformanceProbe), "rejected").GetValue(probe);
        var totals = (long[])AccessTools.Field(typeof(DeltaVPerformanceProbe), "totals").GetValue(probe);
        var calls = (int[])AccessTools.Field(typeof(DeltaVPerformanceProbe), "calls").GetValue(probe);
        try
        {
            DeltaVMod.CurrentSettings.logPerformance = true;
            weapon.fireDeviationDeg = 1;
            weapon.WeaponProps.cooldownTicks = 30;
            weapon.WeaponProps.intrinsicAimSkill = 20;
            foreach (bool laser in new[] { false, true })
            {
                projectile.laserRange = laser ? 500 : 0;
                fire.Clear(); fire.CommandTarget(handle);
                Set(traverse, "currentAngle", 0f);
                Set(traverse, "rotationTick", -1);
                weapon.cooldownUntilTick = tick + 120;
                int previousShots = universe.Projectiles.InFlightCount;
                int previousRejected = rejected[(int)WeaponReject.Alignment];
                int parameterReads = 0;
                long previousAim = totals[(int)PerformanceStage.WeaponAim];
                int duplicateTicks = 0, missedAimTicks = 0, firstShotTick = -1;
                float trackingError = 0, maxStep = 0;
                var watch = new System.Diagnostics.Stopwatch();
                for (int i = 0; i < 360; i++)
                {
                    Set(Find.TickManager, "ticksGameInt", tick + i);
                    own.pose = CombatEngagementEvaluation.At(drifting, i / 60f);
                    opponent.pose = CombatEngagementEvaluation.At(target, i / 60f);
                    float before = traverse.CurrentAngle;
                    weaponAimCommands = 0;
                    int beforeReads = calls[(int)PerformanceStage.WeaponParameters];
                    watch.Start();
                    fire.TickAutoVolley(shooter, own, false);
                    watch.Stop();
                    parameterReads += calls[(int)PerformanceStage.WeaponParameters] - beforeReads;
                    if (weaponAimCommands > 1) duplicateTicks++;
                    if (weaponAimCommands == 0) missedAimTicks++;
                    if (firstShotTick < 0 && universe.Projectiles.InFlightCount > previousShots) firstShotTick = i;
                    maxStep = Mathf.Max(maxStep, Mathf.Abs(SpaceTransform.WrapAngle(traverse.CurrentAngle - before)));
                    if (i < 115 || i >= 120) continue;
                    if (!handle.TryResolve(universe, out var resolved)) throw new Exception("drifting target did not resolve");
                    var info = SpaceWeaponService.AimInfo(weapon, own);
                    if (!SpaceWeaponBallistics.TryAim(info, own.pose, resolved.Pose, out Vector3 point))
                        throw new Exception("drifting intercept should be in range");
                    Vector2 line = SpaceTransform.UniverseToMapPoint(own, shooter.Map, point) - info.MuzzleMap;
                    trackingError = Mathf.Max(trackingError,
                        Mathf.Abs(SpaceTransform.WrapAngle(traverse.CurrentAngle - Mathf.Atan2(line.y, line.x))));
                }
                int shots = universe.Projectiles.InFlightCount - previousShots;
                int alignment = rejected[(int)WeaponReject.Alignment] - previousRejected;
                double aimMs = (totals[(int)PerformanceStage.WeaponAim] - previousAim) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                Console.WriteLine($"CO_DRIFT laser={laser} shots={shots} alignment={alignment} duplicateTicks={duplicateTicks} missedAimTicks={missedAimTicks} firstShotTick={firstShotTick} "
                    + $"trackingErrorDeg={trackingError * Mathf.Rad2Deg:F3} maxStepDeg={maxStep * Mathf.Rad2Deg:F3} parameterReads={parameterReads} aimMs={aimMs:F3} tickMs={watch.Elapsed.TotalMilliseconds:F3}");
                if (duplicateTicks != 0 || missedAimTicks != 0 || trackingError > .2f * Mathf.Deg2Rad
                    || alignment != 0 || shots != 8 || firstShotTick != 120 || maxStep > (traverse.TraverseProps.turnRateDegPerSec / 60f + .001f) * Mathf.Deg2Rad)
                    failures.Add(laser ? "laser" : "ballistic");
            }
            Check(failures.Count == 0, "co-drifting automatic turrets track once per tick, respect traverse and resume fire"
                + (failures.Count == 0 ? "" : ": " + string.Join(", ", failures)));
        }
        finally
        {
            harness.UnpatchAll(harness.Id);
            DeltaVMod.CurrentSettings.logPerformance = oldPerformance;
            projectile.laserRange = laserRange;
            weapon.WeaponProps.cooldownTicks = interval;
            weapon.WeaponProps.intrinsicAimSkill = intrinsic;
            weapon.fireDeviationDeg = deviation;
            weapon.cooldownUntilTick = originalCooldown;
            Set(traverse, "currentAngle", originalAngle); Set(traverse, "rotationTick", -1);
            Set(Find.TickManager, "ticksGameInt", tick);
            own.pose = start; opponent.pose = targetStart;
            fire.Clear();
        }
    }

    static void CheckTargetContracts(UniverseManager universe, CombatHull attacker, SpaceObject own,
        CombatObject victim, CompDeltaVPilotConsole console, CombatHull hull, Building engine, Faction player, Faction enemy)
    {
        attacker.Combat.weaponsArmed = true;
        void Refresh()
        {
            SubstructureOwnership.BumpStructureVersion();
            ShipRegistry.ShipByIdentity(attacker.Identity);
            universe.Controls.For(attacker);
            foreach (ShipBase member in victim.Ships) universe.Controls.For(member);
        }
        Refresh();
        CombatTarget identity = CombatTarget.ForShip(hull.Identity);
        var attackerControl = universe.Controls.For(attacker);
        attackerControl.FireControl.Strategy(attacker).targetRules.Add(new CombatStrategyRule { includeDisarmed = true });
        attackerControl.FireControl.Decide(attacker, own);
        Check(attackerControl.FireControl.Target == identity, "automatic lock is seeded before target separation");
        CombatTarget system = CombatTarget.ForShip(hull.Identity, engine.thingIDNumber);
        Check(system.TryResolve(universe, out var systemPose)
            && OrbitSphere.Offset(systemPose.Pose.position, SpaceTransform.MapPointToUniverse(victim, victim.map,
                new Vector2(engine.DrawPos.x, engine.DrawPos.z))).magnitude < 0.001f,
            "ship system target resolves its current rigid point");
        CombatTarget lostSystem = CombatTarget.ForShip(hull.Identity, 999999);
        Check(!lostSystem.TryResolve(universe, out _),
            "missing part invalidates its runtime lock so fire control and queues reselect");
        var free = CombatTarget.At(own.pose.position);
        Check(!free.HasLock && free.TryResolve(universe, out var freePose) && freePose.Container == null,
            "free aim has a position but no implicit guidance lock");
        Vector2 direction = OrbitSphere.Offset(own.pose.position, ShipRegistry.ShipPose(hull).position).normalized;
        CombatTarget manual = ShipFireControl.ManualTargetAlongRay(universe, own, own.pose.position, direction, player);
        Check(manual.Kind == CombatTargetKind.Ship && manual.Ship == hull.Identity,
            "manual ray lock records the enemy ship inside the nearest intersected container");

        var projectileDef = TestBuildingDef("targetContractMissile");
        var props = new CompProperties_SpaceProjectile { guided = true, hasMap = false, speed = 100f };
        projectileDef.comps.Add(props);
        var weapon = new CompSpaceWeapon { parent = (ThingWithComps)attacker.Anchor,
            props = new CompProperties_SpaceWeapon { projectileDef = projectileDef, range = 500f,
                topTurret = true, intrinsicAimSkill = 10 }, safeFire = false };
        BindWeaponOwnerFixture(weapon, attacker);
        Check(weapon.OwnerShip.IsValid && weapon.Armed && weapon.HasFireControl,
            "guided-shot fixture has an armed ship and an intrinsic fire-control source");
        var request = new WeaponFireRequest(systemPose.Pose.position);
        var shot = new SpaceWeaponShot(universe, weapon, own, request);
        Check((bool)Call(shot, "Prepare") && !shot.guidanceTarget.HasLock && shot.target == null,
            "guided shot preparation never acquires a target when request explicitly has no lock");
        WeaponAimInfo aim = SpaceWeaponService.AimInfo(weapon, own);
        Check(aim.Guided && !aim.Laser && aim.Projectile == shot.projectileDef && aim.Speed == shot.speed
            && aim.MuzzleMap == shot.muzzleMap
            && OrbitSphere.Offset(aim.Muzzle, shot.muzzleUniverse).magnitude < 0.001f,
            "read-only aim capability agrees with prepared projectile, muzzle and speed");
        var directDef = TestBuildingDef("manualAxisContractBullet");
        directDef.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, speed = 100f });
        var fixedWeapon = new CompSpaceWeapon { parent = (ThingWithComps)attacker.Anchor,
            props = new CompProperties_SpaceWeapon { projectileDef = directDef, topTurret = false,
                fireArcDeg = 0f, lockArcDeg = 360f, minRange = 20f, range = 500f,
                intrinsicAimSkill = 10 },
            fireDeviationDeg = 1f, manualFireDeviationDeg = 180f, safeFire = false };
        BindWeaponOwnerFixture(fixedWeapon, attacker);
        Vector2 fixedMuzzle = SpaceWeaponGeometry.MuzzleMapPoint(fixedWeapon);
        float fixedAxis = ShipOrientation.AngleFromRot4(fixedWeapon.parent.Rotation);
        Vector2 axisDirection = SpaceTransform.Rotate(Vector2.right, fixedAxis);
        Vector3 behindCursor = SpaceTransform.MapPointToUniverse(own, own.map,
            fixedMuzzle - axisDirection * 10f);
        Vector3 nearCursor = SpaceTransform.MapPointToUniverse(own, own.map,
            fixedMuzzle + axisDirection * 5f);
        Vector3 farCursor = SpaceTransform.MapPointToUniverse(own, own.map,
            fixedMuzzle + axisDirection * 600f);
        var axisShot = new SpaceWeaponShot(universe, fixedWeapon, own,
            new WeaponFireRequest(behindCursor, manualFire: true));
        Check((bool)Call(axisShot, "Prepare")
            && Mathf.Abs(SpaceTransform.WrapAngle(axisShot.launchMapAngle - fixedAxis)) < 0.0001f,
            "manual 180-degree fixed-axis fire ignores a cursor behind the weapon and keeps its mounting axis");
        Check(!(bool)Call(new SpaceWeaponShot(universe, fixedWeapon, own,
                new WeaponFireRequest(nearCursor)), "Prepare"),
            "automatic fixed-axis fire still checks target range");
        fixedWeapon.manualFireDeviationDeg = 1f;
        Check((bool)Call(new SpaceWeaponShot(universe, fixedWeapon, own,
                new WeaponFireRequest(nearCursor, manualFire: true)), "Prepare")
            && (bool)Call(new SpaceWeaponShot(universe, fixedWeapon, own,
                new WeaponFireRequest(farCursor, manualFire: true)), "Prepare"),
            "manual fire ignores both minimum and maximum mouse distances at a narrow angle");
        Check(!(bool)Call(new SpaceWeaponShot(universe, fixedWeapon, own,
                new WeaponFireRequest(behindCursor, manualFire: true)), "Prepare"),
            "narrow manual deviation still requires the mouse direction to match the fixed axis");
        weapon.cooldownUntilTick = Find.TickManager.TicksGame + 100;
        Check(!(bool)Call(new SpaceWeaponShot(universe, weapon, own, request), "Prepare")
            && weapon.cooldownUntilTick == Find.TickManager.TicksGame + 100,
            "failed shot preparation preserves cooldown and does not commit costs");
        weapon.cooldownUntilTick = 0;
        Check(!(bool)Call(new SpaceWeaponShot(universe, weapon, victim, request), "Prepare"),
            "shot rejects a stale container after the weapon has moved to another map");

        // Real launch and tick use the prescribed handle; no Unity map creation is involved.
        OrbitSphere.Basis(own.pose.position, out var east, out _);
        Check(universe.Projectiles.Launch(projectileDef, own, own.pose.position, east, player, null, identity),
            "guided projectile launches with an explicit ship identity");
        IList flights = (IList)typeof(SpaceProjectileService).GetField("projectiles", Private).GetValue(universe.Projectiles);
        object flight = flights[flights.Count - 1];
        int flightId = (int)flight.GetType().GetField("containerId").GetValue(flight);
        SpaceObject missile = universe.ObjectById(flightId);
        universe.Projectiles.Tick();
        Check((int)flight.GetType().GetField("targetContainerId").GetValue(flight) == victim.objectId,
            "in-flight guidance resolves the original ship container");

        // Disable this member without removing its identity; manual/in-flight policies remain independent.
        victim.map.listerThings.AllThings.Remove(engine);
        Refresh();
        Check(ShipFireControl.Candidates(attacker, own, victim).Any() && identity.TryResolve(universe, out _)
            && ShipFireControl.ManualTargetAlongRay(universe, own, own.pose.position, direction, player).Ship == hull.Identity,
            "powerless ship retains automatic, manual and in-flight target eligibility");
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
        missile.pose.velocity = Vector2.up * props.speed;
        universe.Projectiles.Tick();
        Check(missile.pose.velocity.x > 0.01f,
            "already launched missile still turns toward the ship after all propulsion is lost");
        victim.map.listerThings.AllThings.Add(engine);
        Refresh();

        // Rehome the same identity; the old container remains as debris.
        Map oldMap = victim.map;
        Map newMap = TestMap(1940);
        var separated = new CombatObject(1940, newMap,
            new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(90f, 40f)), 0f));
        universe.objects.Add(separated);
        Set(universe, "objectById", null); // The fixture bypasses RegisterObject.
        RemoveConsoleFixture(console, oldMap);
        PlaceConsoleFixture(console, newMap);
        oldMap.thingGrid.Deregister(engine);
        oldMap.listerThings.AllThings.Remove(engine);
        Set(typeof(Thing), engine, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(newMap));
        newMap.listerThings.AllThings.Add(engine);
        newMap.thingGrid.Register(engine);
        victim.members.Remove(hull);
        var moved = new CombatHull(console.Ship);
        moved.cells.UnionWith(hull.cells);
        separated.members.Add(moved);
        Refresh(); universe.Controls.For(moved);
        Check(identity.TryResolve(universe, out var movedTarget) && movedTarget.Container == separated
            && movedTarget.Ship.Identity == hull.Identity,
            "ship target follows separation to the new container without acquiring debris");
        universe.Controls.ForgetMap(oldMap);
        universe.Projectiles.ForgetMap(oldMap);
        Check(attackerControl.FireControl.Target == identity
            && ((CombatTarget)flight.GetType().GetField("target").GetValue(flight)) == identity,
            "old-map cleanup preserves automatic and in-flight locks on a ship already moved to a new map");
        Refresh(); universe.Controls.For(moved);
        var playerLanding = CombatTarget.ForContainer(victim, new Vector2(1f, 2f));
        Check(playerLanding.TryResolve(universe, out var landing) && landing.Container == victim
            && OrbitSphere.Offset(landing.Pose.position, SpaceTransform.MapPointToUniverse(victim, oldMap,
                new Vector2(1f, 2f))).magnitude < 0.001f,
            "player boarding retains the explicitly chosen container-local landing point");
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
        universe.Projectiles.Tick();
        Check((int)flight.GetType().GetField("targetContainerId").GetValue(flight) == separated.objectId,
            "in-flight missile follows the same ship into its new container");
        Set(flight, "adoptedShuttle", true);
        universe.Projectiles.ResolveCollisions();
        var sweep = (SpaceCollision.SweptBody)flight.GetType().GetField("body").GetValue(flight);
        Check(sweep.requiredDefender == separated,
            "boarding collision restriction follows the same resolved container used by guidance");
        Set(flight, "adoptedShuttle", false);
        var maneuver = new ShipAutoManeuverControl(universe, attacker.Identity);
        Set(maneuver, "anchorTarget", identity);
        object[] sample = { default(SpacePose) };
        Check((bool)Call(maneuver, "SampleTarget", sample)
            && OrbitSphere.Offset(((SpacePose)sample[0]).position, movedTarget.Pose.position).magnitude < 0.001f,
            "maneuver sampler consumes the same current ship pose as guidance and fire control");

        var movedController = universe.Controls.For(moved);
        movedController.Enter(PilotTarget.Of(moved));
        var replacement = AddTestConsole(newMap, enemy, 1941);
        replacement.shipId = console.shipId;
        console.isMain = false;
        var successor = new CombatHull(replacement.Ship);
        successor.cells.UnionWith(moved.cells);
        separated.members.Clear(); separated.members.Add(successor);
        Refresh(); universe.Controls.ReconcileOwner(successor.Identity);
        Check(ReferenceEquals(universe.Controls.For(successor), movedController)
            && movedController.target.Vessel.Anchor == replacement.parent
            && attackerControl.FireControl.Target == identity,
            "primary succession rebinds the existing controller session without dropping an attacker's ship lock");
        Check(identity.TryResolve(universe, out var afterSuccession) && afterSuccession.Ship.Anchor == replacement.parent,
            "same-identity main-console succession preserves the combat target");
        replacement.isMain = false;
        Refresh();
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
        Vector2 coasting = missile.pose.velocity;
        universe.Projectiles.Tick();
        Check(!identity.TryResolve(universe, out _) && missile.pose.velocity == coasting
            && !((CombatTarget)flight.GetType().GetField("target").GetValue(flight)).HasLock,
            "lost ship identity clears guidance and preserves the current flight direction");
        replacement.isMain = true;
        Refresh(); universe.Controls.For(successor);
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
        universe.Projectiles.Tick();
        Check(missile.pose.velocity == coasting,
            "a missile never reacquires a ship after its prescribed identity became unavailable");
        Check(universe.Projectiles.Launch(projectileDef, own, own.pose.position, east, player, null, identity),
            "second missile launches for map-unload cleanup regression");
        object unloading = flights[flights.Count - 1];
        SpaceObject unloadingMissile = universe.ObjectById((int)unloading.GetType().GetField("containerId").GetValue(unloading));
        Call(universe.Projectiles, "ResolveTarget", unloading);
        Set(unloading, "adoptedShuttle", true);
        unloadingMissile.projectileRoleOverride = true;
        var pendingSweep = (SpaceCollision.SweptBody)unloading.GetType().GetField("body").GetValue(unloading);
        pendingSweep.requiredDefender = separated;
        universe.Projectiles.ForgetMap(newMap);
        Check(!((CombatTarget)unloading.GetType().GetField("target").GetValue(unloading)).HasLock
            && (int)unloading.GetType().GetField("targetContainerId").GetValue(unloading) == 0,
            "target map unloading immediately clears the in-flight handle and same-tick resolution cache");
        Check(pendingSweep.dead && pendingSweep.requiredDefender == null,
            "target invalidation disables an already submitted boarding sweep instead of allowing it to hit another ship");
        Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
        Check(!(bool)Call(universe.Projectiles, "ResolveTarget", unloading),
            "map-unloaded projectile never reacquires the still-resolvable identity");
        universe.Projectiles.ResolveCollisions();
        Check(!flights.Contains(unloading) && !unloadingMissile.projectileRoleOverride,
            "boarding target loss during collision preparation safely releases the shuttle without mutating a foreach");
        Call(universe, "RunFrameActions");
        universe.objects.Remove(unloadingMissile);
        Check(universe.Projectiles.Launch(projectileDef, own, own.pose.position, east, player, null,
            CombatTarget.ForContainer(victim)), "container-target missile launches for removal cleanup regression");
        object removedTarget = flights[flights.Count - 1];
        SpaceObject removalMissile = universe.ObjectById((int)removedTarget.GetType().GetField("containerId").GetValue(removedTarget));
        Call(universe.Projectiles, "ResolveTarget", removedTarget);
        universe.Projectiles.NotifyContainerRemoving(victim);
        Check(!((CombatTarget)removedTarget.GetType().GetField("target").GetValue(removedTarget)).HasLock
            && !(bool)Call(universe.Projectiles, "ResolveTarget", removedTarget),
            "container removal invalidates a cached guidance target in the same tick");
        flights.Remove(removedTarget); universe.objects.Remove(removalMissile);
        Check(universe.Projectiles.Launch(projectileDef, own, own.pose.position, east, player, null,
            CombatTarget.ForContainer(victim)), "wreck-target missile launches for archive cleanup regression");
        object archiveFlight = flights[flights.Count - 1];
        SpaceObject archiveMissile = universe.ObjectById((int)archiveFlight.GetType().GetField("containerId").GetValue(archiveFlight));
        var archivedWreck = new ArchivedSpaceObject { objectId = victim.objectId, pose = victim.pose };
        universe.ReplaceRepresentation(victim, archivedWreck);
        universe.Projectiles.ForgetMap(oldMap);
        Check(!((CombatTarget)archiveFlight.GetType().GetField("target").GetValue(archiveFlight)).HasLock,
            "manual wreck lock clears even when archive replacement detached the old map before cleanup");
        universe.ReplaceRepresentation(archivedWreck, victim);
        flights.Remove(archiveFlight); universe.objects.Remove(archiveMissile);
        CombatTarget wreck = ShipFireControl.ManualTargetAlongRay(universe, own, own.pose.position, direction, player);
        Check(wreck.Kind == CombatTargetKind.Container && wreck.ObjectId == victim.objectId,
            "manual ray locking can still select console-free debris in the nearest container");
        Check(!ShipFireControl.ManualTargetAlongRay(universe, own, own.pose.position, -direction, player).HasLock,
            "manual ray miss remains unlocked");
        Check(typeof(CompSpaceWeapon).GetField("pdTargetId", Private) == null
            && typeof(SpaceWeaponService).GetMethod("TargetAlongRay") == null,
            "weapon components and weapon service no longer own tactical target state or selection");
        // Remove this test's live projectile without invoking map rendering/cleanup.
        flights.Remove(flight);
        universe.objects.Remove(missile);
        CheckPointDefenseTargets(universe, attacker, own, weapon, player, enemy);
    }

    static void CheckPointDefenseTargets(UniverseManager universe, ShipBase ship, SpaceObject own,
        CompSpaceWeapon weapon, Faction player, Faction enemy)
    {
        var nearest = new MapSpaceObject(1950, TestMap(1950),
            new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(20f, 0f)), 0f))
            { baseTag = SpaceObjectTag.Projectile, faction = enemy };
        var incoming = new MapSpaceObject(1951, TestMap(1951),
            new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(60f, 0f)), 0f))
            { baseTag = SpaceObjectTag.Projectile, faction = enemy };
        incoming.pose.velocity = new Vector2(-30f, 0f);
        universe.objects.Add(nearest); universe.objects.Add(incoming);
        Set(universe, "objectById", null); // Container handle lookup follows the fixture list.
        var fire = new ShipFireControl(universe, ship.Identity);
        Vector3 muzzle = SpaceWeaponService.AimInfo(weapon, own).Muzzle;
        fire.CollectInterceptionCandidates(ship, own);
        Check(fire.FindPdTarget(weapon, muzzle) == incoming,
            "point defense prioritizes an approaching projectile over a nearer receding candidate");
        var bodyDef = TestBuildingDef("pdCoverageMissile");
        bodyDef.comps.Add(new CompProperties_SpaceProjectile { guided = true });
        var body = AddTestBuilding(incoming.map, enemy, 1952, bodyDef, new EmptyComp());
        Set(typeof(Thing), body, "hitPointsInt", 25);
        var reserved = (Dictionary<int, float>)typeof(ShipFireControl)
            .GetField("pdReservedDamage", Private).GetValue(fire);
        reserved[incoming.objectId] = 20f;
        Check(fire.FindPdTarget(weapon, muzzle) == incoming,
            "two point-defense laser hits do not prematurely abandon a 25 HP missile");
        reserved[incoming.objectId] = 30f;
        Check(fire.FindPdTarget(weapon, muzzle) == nearest,
            "a missile with three assigned laser hits gives another target the next weapon");
        var candidates = (List<SpaceObject>)typeof(ShipFireControl)
            .GetField("interceptionCandidates", Private).GetValue(fire);
        candidates.Remove(nearest);
        Check(fire.FindPdTarget(weapon, muzzle) == incoming,
            "when every reachable threat is covered, point defense keeps a fallback target");
        candidates.Add(nearest);
        reserved.Clear();
        incoming.pose.velocity = Vector2.right * 10f;
        Check(fire.FindPdTarget(weapon, muzzle) == nearest,
            "point defense falls back to the nearest target when none is approaching");
        CombatTarget handle = CombatTarget.ForContainer(nearest);
        Check(fire.PdTargetValid(weapon, handle, muzzle, out _), "point defense does not require a ship or propulsion");
        universe.objects.Remove(nearest);
        Set(universe, "objectById", null);
        Check(!fire.PdTargetValid(weapon, handle, muzzle, out _), "point-defense lock becomes invalid immediately when its object is removed");
        incoming.faction = player;
        fire.CollectInterceptionCandidates(ship, own);
        Check(fire.FindPdTarget(weapon, muzzle) == null, "point defense never selects a friendly projectile");
        universe.objects.Remove(incoming);
        Set(universe, "objectById", null);
    }
}
