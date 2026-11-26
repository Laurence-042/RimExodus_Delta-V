using System;
using System.Linq;
using System.Reflection;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    sealed class PersonnelHitFrame : MapSpaceObject
    {
        public override System.Collections.Generic.IEnumerable<ShipBase> Ships => Array.Empty<ShipBase>();
        protected override SpaceObjectTag MemberTags => SpaceObjectTag.Personnel;
    }
    sealed class PersonnelImpactProbe : Projectile
    {
        internal Thing hit;
        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        { hit = hitThing; Destroy(); }
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish) =>
            Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2);
    }
    static bool SpawnPersonnelImpactProbe(Thing newThing, IntVec3 loc, Map map, ref Thing __result)
    {
        if (newThing is not PersonnelImpactProbe) return true;
        Set(typeof(Thing), newThing, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        Set(typeof(Thing), newThing, "positionInt", loc);
        __result = newThing;
        return false;
    }
    static System.Collections.Generic.List<IntVec3> PersonnelExplosionCells(IntVec3 center) =>
        new System.Collections.Generic.List<IntVec3> { center };
    static bool PersonnelExplosionCellBoundary(IntVec3 center, ref System.Collections.Generic.List<IntVec3> __result)
    { __result = PersonnelExplosionCells(center); return false; }

    static void CheckPersonnelImpactCell(UniverseManager universe)
    {
        Map map = TestMap(65120);
        var pawn = (Pawn)Raw(typeof(Pawn));
        pawn.def = (ThingDef)Raw(typeof(ThingDef));
        pawn.def.defName = "personnelImpactReceiver";
        pawn.def.size = IntVec2.One;
        pawn.thingIDNumber = 65121;
        IntVec3 cell = new IntVec3(2, 0, 3);
        Set(typeof(Thing), pawn, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        Set(typeof(Thing), pawn, "positionInt", cell);
        var frame = new PersonnelHitFrame { objectId = 65122, pose = new SpacePose(Vector3.forward, .3f) };
        AccessTools.Property(typeof(MapSpaceObject), "map").SetValue(frame, map);
        AccessTools.Field(typeof(MapSpaceObject), "personnelPawn").SetValue(frame, pawn);
        var def = TestBuildingDef("personnelImpactRound");
        def.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, timeoutTicks = 60 });
        var records = ProjectileRecords(universe.Projectiles);
        object proxy = CollisionFlight(universe, frame, def, frame.pose.position, pawn);
        proxy.GetType().GetField("personnelProxy").SetValue(proxy, true);
        var patch = new Harmony("DeltaV.Regression.PersonnelImpactCell");
        SpaceObject nativeOwner = null;
        object nativeFlight = null;
        int explosionsBefore = collisionExplosions.Count;
        try
        {
            patch.Patch(typeof(Projectile).TypeInitializer,
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            universe.objects.Add(frame);
            Set(universe, "objectById", null);
            var view = CollisionView(frame);
            Check(CollisionValid(view), "mapped personnel fixture has a live Pawn and persistent collision proxy");
            object[] ray = { null, new Vector2(-2, 0), Vector2.right, 4f, null, 0f,
                null, null, null, null, null, false, null, null, false };
            Check((bool)Call(view, "TryRay", ray), "mapped personnel ray enters the production target adapter");
            object contact = ray[10];
            Check((IntVec3)contact.GetType().GetField("cell", Private | BindingFlags.Public).GetValue(contact) == cell,
                "mapped personnel ray supplies the real Pawn cell rather than Invalid");
            Vector2 point = (Vector2)contact.GetType().GetField("point", Private | BindingFlags.Public).GetValue(contact);
            Check((point - new Vector2(-.5f, 0)).magnitude < .0001f,
                "mapped entity contact preserves the exact rigid-local surface point");
            var body = new SpaceCollision.SweptBody {
                from = SpaceTransform.ShipLocalToUniverse(frame.pose, new Vector2(-2, 0)),
                to = SpaceTransform.ShipLocalToUniverse(frame.pose, new Vector2(2, 0)), projectileDef = def };
            object[] sweep = { body, false, null, null };
            Check((bool)Call(view, "TrySweep", sweep), "mapped personnel sweep enters the production target adapter");
            object sweepRay = sweep[3].GetType().GetField("ray", Private | BindingFlags.Public).GetValue(sweep[3]);
            IntVec3 damageCell = (IntVec3)sweepRay.GetType().GetField("cell", Private | BindingFlags.Public).GetValue(sweepRay);
            Check(damageCell == cell,
                "mapped personnel sweep supplies the same valid damage cell as the ray");
            var projectile = (PersonnelImpactProbe)Raw(typeof(PersonnelImpactProbe));
            projectile.def = def; projectile.stackCount = 1; projectile.thingIDNumber = 65123;
            Set(typeof(Thing), projectile, "mapIndexOrState", (sbyte)-1);
            nativeOwner = universe.CapturePayload(projectile, new SpacePose(Vector3.forward, 0), null);
            Check(nativeOwner?.Payload?.Entity == projectile, "native impact fixture owns its real projectile payload");
            nativeFlight = CollisionFlight(universe, nativeOwner, def, Vector3.forward);
            nativeFlight.GetType().GetField("nativeProjectile").SetValue(nativeFlight, projectile);
            patch.Patch(AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn),
                new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(WipeMode) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(SpawnPersonnelImpactProbe)));
            Call(universe.Projectiles, "QueueNativeImpact", nativeFlight, frame, pawn, damageCell, frame.pose.position, Vector2.right);
            foreach (Action action in Actions(universe).ToArray()) { Actions(universe).Remove(action); action(); }
            Check(projectile.hit == pawn && projectile.Destroyed,
                "confirmed personnel contact survives deferred validation and reaches the original virtual Impact with the real Pawn");
            patch.Patch(AccessTools.Method(typeof(GenExplosion), nameof(GenExplosion.DoExplosion)),
                prefix: new HarmonyMethod(typeof(Program), nameof(CollisionExplosion)));
            patch.Patch(AccessTools.Method(typeof(SpaceImpactEffects), "HighExplosiveCells"),
                prefix: new HarmonyMethod(typeof(Program), nameof(PersonnelExplosionCellBoundary)));
            var part = new ImpactOutcomePart { damageDef = DamageDefOf.Bomb, highExplosive = new HighExplosiveProfile {
                damageByMass = new SimpleCurve { new CurvePoint(1, 32) },
                radiusByMass = new SimpleCurve { new CurvePoint(1, .75f) } } };
            var evt = new SpaceCollision.ImpactEvent { defender = frame, defenderBuilding = pawn,
                hitCell = damageCell, mass = 1, projectileDef = def };
            part.kind = ImpactEffectKind.DamageMap;
            SpaceImpactEffects.ApplyToDefender(evt, part);
            Check(collisionExplosions.Count == explosionsBefore + 1
                && collisionExplosions.Last().cell == pawn.Position && collisionExplosions.Last().amount == 32,
                "personnel damage cell reaches the real high-explosive map endpoint with positive damage");
            ray[1] = new Vector2(-2, 2);
            Check(!(bool)Call(view, "TryRay", ray) && collisionExplosions.Count == explosionsBefore + 1,
                "a ray missing personnel produces no contact or extra explosion");
        }
        finally
        {
            patch.UnpatchAll(patch.Id);
            records.Remove(proxy);
            if (nativeFlight != null) records.Remove(nativeFlight);
            universe.objects.Remove(frame);
            if (nativeOwner != null) universe.objects.Remove(nativeOwner);
            Set(universe, "objectById", null);
            collisionExplosions.RemoveRange(explosionsBefore, collisionExplosions.Count - explosionsBefore);
            Find.Maps.Remove(map);
        }
    }

    static void CheckPersonnelHitChance()
    {
        var pawn = (Pawn)Raw(typeof(Pawn));
        pawn.def = (ThingDef)Raw(typeof(ThingDef));
        pawn.def.defName = "personnelHitChance";
        pawn.def.category = ThingCategory.Pawn;
        pawn.def.size = IntVec2.One;
        pawn.def.race = new RaceProperties { baseBodySize = 1f,
                lifeStageAges = new System.Collections.Generic.List<LifeStageAge> {
                    new LifeStageAge { def = new LifeStageDef { bodySizeFactor = 1f } } } };
        pawn.ageTracker = new Pawn_AgeTracker(pawn);
        Set(pawn.ageTracker, "cachedLifeStageIndex", 0);
        var hitChance = AccessTools.Method(typeof(SpaceImpactEffects), "HitChance");
        float Chance(SpaceImpactEffectDef effect = null) => (float)hitChance.Invoke(null,
            new object[] { null, SpaceCellKind.Building, pawn, effect });
        foreach (var sample in new[] { (.01f, .04f), (.5f, .2f), (1f, .4f), (2f, .8f), (4f, .8f) })
        {
            pawn.def.race.baseBodySize = sample.Item1;
            Check(Math.Abs(Chance() - sample.Item2) < .00001f,
                $"personnel body size {sample.Item1} yields interception chance {sample.Item2} with zero fill");
        }
        pawn.def.race.baseBodySize = 1f;
        pawn.def.race.lifeStageAges[0].def.bodySizeFactor = .5f;
        Check(Math.Abs(Chance() - .2f) < .00001f, "personnel interception reads the real life-stage body size");
        pawn.def.race.lifeStageAges[0].def.bodySizeFactor = 1f;
        Check(Math.Abs(Chance(new SpaceImpactEffectDef { buildingHitChance = 0f }) - .4f) < .00001f
            && Math.Abs(Chance(new SpaceImpactEffectDef { buildingHitChance = 1f }) - .4f) < .00001f,
            "building chance overrides cannot suppress or guarantee a personnel hit");
        pawn.def.fillPercent = 1f;
        Check(Math.Abs(Chance() - .4f) < .00001f, "personnel fill percent does not affect interception");
        object[] ray = { pawn, new Vector2(-2f, 0f), Vector2.right, 4f, 0f, null };
        var footprint = AccessTools.Method(typeof(SpaceCollision), "TryEntityFootprint");
        Check((bool)footprint.Invoke(null, ray), "a crossing ray contacts the personnel footprint");
        ray[1] = new Vector2(-2f, 2f);
        Check(!(bool)footprint.Invoke(null, ray), "a ray outside the personnel footprint never reaches hit testing");
        Rand.PushState(67291);
        try
        {
            int hits = 0;
            for (int i = 0; i < 1000; i++)
                if (SpaceImpactEffects.HitTest(null, SpaceCellKind.Building, pawn, null)) hits++;
            Check(hits > 330 && hits < 470, "actual personnel HitTest produces both hits and pass-throughs near forty percent");
        }
        finally { Rand.PopState(); }
        Check(CollisionCalls(AccessTools.Method(typeof(SpaceCollision), "TryFindFirstContact"))
                .Any(method => method.DeclaringType == typeof(SpaceImpactEffects) && method.Name == "HitTest")
            && CollisionCalls(AccessTools.Method(typeof(SpaceCollision).GetNestedType("TargetView", BindingFlags.NonPublic), "ApplyResolvedDamage"))
                .Any(method => method.Name == nameof(Thing.TakeDamage)),
            "real collision confirmation uses shared HitTest and resolved entity damage reaches TakeDamage");
    }

    static bool PawnSpaceflightWalkable(ref bool __result)
    {
        __result = true;
        return false;
    }

    // The source Verb's range reaches the native launch authorization as a spread reference; it never gates the local cast.
    static bool RangeFeedsOnlyNativeAuthorization(MethodInfo tryFire)
    {
        var instructions = PatchProcessor.GetOriginalInstructions(tryFire).ToList();
        int authorize = instructions.FindIndex(i => i.operand is MethodInfo method && method.Name == "AuthorizeNativeFire");
        int firstRange = instructions.FindIndex(i => i.operand is MethodInfo method && method.Name == "get_EffectiveRange");
        return firstRange >= 0 && authorize > firstRange
            && !instructions.Skip(firstRange).Take(authorize - firstRange)
                .Any(i => i.opcode.FlowControl == System.Reflection.Emit.FlowControl.Cond_Branch);
    }

    static void CheckPawnSpaceflight(UniverseManager universe)
    {
        CheckPersonnelHitChance();
        CheckPersonnelImpactCell(universe);
        var foundation = new DriveSubject(41, 77, DriveSubjectKind.Foundation);
        var shuttle = new DriveSubject(42, 77, DriveSubjectKind.Shuttle);
        var personnel = new DriveSubject(43, 77, DriveSubjectKind.Personnel);
        Check(foundation != shuttle && foundation != personnel && shuttle != personnel,
            "drive subjects separate foundation, shuttle and personnel with the same numeric key");
        Check(personnel.InContainer(99).kind == DriveSubjectKind.Personnel
            && personnel.InContainer(99).objectId == 99,
            "personnel drive identity survives container rebinding");
        Check(personnel.personnel && !personnel.shuttle && personnel.ToString() == "pawn:77",
            "personnel drive identity reports its explicit kind");

        var obj = new SpaceObject(500, new SpacePose())
            { baseTag = SpaceObjectTag.Container | SpaceObjectTag.Personnel | SpaceObjectTag.Scattered };
        PropertyInfo targetTag = typeof(SpaceObject).GetProperty("InterceptionTargetTag",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Check((SpaceObjectTag)targetTag.GetValue(obj) == 0,
            "personnel never classify an interception target, regardless of object id or container shape");
        Check(ShipBase.Invalid.RolesIn(null) == (SpaceObjectTag.Ship | SpaceObjectTag.Foundation)
            && new ShuttleShip(null).RolesIn(null) == (SpaceObjectTag.Ship | SpaceObjectTag.Shuttle)
            && new PawnSpaceflightShip(null, null).RolesIn(null) == (SpaceObjectTag.Ship | SpaceObjectTag.Personnel),
            "every hull returns the overall Ship role plus its own class role");
        Check(SpaceObject.IsVesselRole(ShipBase.Invalid.RolesIn(null))
            && SpaceObject.IsVesselRole(SpaceObjectTag.Ship | SpaceObjectTag.Shuttle)
            && !SpaceObject.IsVesselRole(SpaceObjectTag.Ship | SpaceObjectTag.Personnel)
            && !SpaceObject.IsVesselRole(SpaceObjectTag.Container) && obj.PersonnelOnly,
            "ship weapons only lock the integrated or foundation vessel roles");
        var pdProperties = new CompProperties_SpaceWeapon();
        var pdWeapon = new CompSpaceWeapon();
        Check(pdProperties.defaultPdTargets == (SpaceObjectTag.Projectile | SpaceObjectTag.Shuttle)
            && pdWeapon.pdTargets == (SpaceObjectTag.Projectile | SpaceObjectTag.Shuttle),
            "point defense defaults to projectiles and shuttles and never admits personnel");
        Check(typeof(SpaceObject).Assembly.GetType("DeltaV.Dialog_PointDefenseTargets")
                ?.GetField("personnel", BindingFlags.Instance | BindingFlags.NonPublic) == null
            && new[] { "ChineseSimplified", "English" }.SelectMany(language => new[]
            {
                $"1.6/Languages/{language}/Keyed/PawnSpaceflight.xml",
                $"1.7/Languages/{language}/Keyed/PawnSpaceflight.xml"
            }).All(path => !System.IO.File.ReadAllText(path).Contains("DeltaV_PointDefensePersonnel")),
            "the point-defense target dialog and its translations no longer offer a personnel category");

        Check(universe.TractorLinks != null && universe.Personnel != null,
            "grapple and personnel transition services are universe-instance state");
        Check(CollisionCalls(AccessTools.Method(typeof(ShipAutopilot), "Step"))
                .Any(method => method.DeclaringType == typeof(TractorLinkService)
                    && method.Name == "HasOperatorForce"),
            "autopilot yields its drive output while a maintained grapple supplies operator force");
        Check(typeof(TractorLinkService).GetMethod("MaintainDockingLink",
                BindingFlags.Instance | BindingFlags.NonPublic) != null
            && typeof(TractorLinkService).GetMethod("DockingAttached",
                BindingFlags.Instance | BindingFlags.NonPublic) != null
            && CollisionCalls(AccessTools.Method(typeof(HardDockingService), "Tick"))
                .Any(method => method.Name == "MaintainDockingLink"),
            "hard docking owns a persistent light-link capture instead of synthesizing held fire");
        Check(universe.HardDocking != null
            && typeof(ShipBase).GetProperty("HardDockingExecutor",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "shuttle and personnel expose one universe-instance hard-docking executor contract");
        Check(typeof(CompPawnGrapple).GetInterface(nameof(ISpaceWeaponFireGate)) != null
            && typeof(CompPawnGrapple).GetInterface(nameof(ISpaceWeaponLaunch)) != null,
            "personal grapple is a manual space-weapon capability instead of an independent gizmo flow");
        Type access = typeof(UniverseManager).Assembly.GetType("DeltaV.PersonnelAccess");
        Check(access?.GetMethod("TryResolveExit", BindingFlags.Static | BindingFlags.NonPublic) != null
            && access.GetMethod("TryResolveLanding", BindingFlags.Static | BindingFlags.NonPublic) != null
            && access.GetMethod("TryFindLandingNear", BindingFlags.Static | BindingFlags.NonPublic) == null,
            "personnel egress and exact-cell landing use the shared on-demand access resolver without a near fallback");
        MethodInfo shellPicker = typeof(ShellPicking).GetMethod("ShellAtFramePoint",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { typeof(UniverseManager), typeof(Map), typeof(Vector3),
                typeof(Func<Map, IntVec3, bool>) }, null);
        Check(shellPicker != null,
            "shell picking accepts an on-demand hidden-cell predicate for empty moon-pool terrain");
        TerrainDef previousMoon = (TerrainDef)AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").GetValue(null);
        Map moonMap = TestMap(65010);
        moonMap.mapPawns = new MapPawns(moonMap);
        var moon = (TerrainDef)Raw(typeof(TerrainDef));
        moon.defName = "pawnSpaceflightEmptyMoonPool";
        moon.passability = Traversability.Standable;
        IntVec3 moonCell = new IntVec3(2, 0, 2);
        moonMap.terrainGrid.topGrid[moonMap.cellIndices.CellToIndex(moonCell)] = moon;
        AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, moon);
        var walkablePatch = new Harmony("DeltaV.Regression.PawnSpaceflightWalkable");
        walkablePatch.Patch(AccessTools.Method(typeof(GenGrid), nameof(GenGrid.Walkable),
            new[] { typeof(IntVec3), typeof(Map) }),
            prefix: new HarmonyMethod(typeof(Program), nameof(PawnSpaceflightWalkable)));
        try
        {
            object[] landingArgs = { moonMap, moonCell, null };
            Check(moonMap.thingGrid.ThingsListAtFast(moonCell).Count == 0
                && (bool)access.GetMethod("TryResolveLanding", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, landingArgs),
                "an empty moon-pool terrain cell resolves as an exact personnel landing target");
        }
        finally
        {
            walkablePatch.UnpatchAll(walkablePatch.Id);
            AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, previousMoon);
            Find.Maps.Remove(moonMap);
        }
        Check(typeof(SpaceProjectileService).GetMethod("AdoptHardDocker",
                BindingFlags.Instance | BindingFlags.NonPublic) != null
            && typeof(SpaceProjectileService).GetMethod("ReleaseHardDocker",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "collision-completed hard docking keeps the common projectile work item");
        Type executor = typeof(UniverseManager).Assembly.GetType("DeltaV.IHardDockingExecutor");
        MethodInfo navigateToReference = typeof(ShipAutopilot).GetMethod("NavigateToReference",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Type autopilotFlight = typeof(ShipAutopilot).GetNestedType("Flight", BindingFlags.NonPublic);
        Check(executor?.GetProperty("Completion") != null
            && executor.GetProperty("RequiresAvoidanceDisabled") != null
            && executor.GetProperty("CaptureTether") != null
            && executor.GetProperty("ArrivalVelocityTolerance") != null
            && executor.GetProperty("ArrivalApproachSpeed") != null
            && executor.GetMethod("TryResolveTarget") != null
            && navigateToReference?.GetParameters().TakeLast(2)
                .Select(parameter => parameter.Name)
                .SequenceEqual(new[] { "arrivalVelocityTolerance", "arrivalApproachSpeed" }) == true
            && autopilotFlight?.GetField("arrivalVelocityTolerance",
                BindingFlags.Instance | BindingFlags.Public) != null
            && autopilotFlight.GetField("arrivalApproachSpeed",
                BindingFlags.Instance | BindingFlags.Public) != null,
            "hard docking shares targeting and cancellation while executors select collision or arrival completion");
        MethodInfo autopilotStep = AccessTools.Method(typeof(ShipAutopilot), "Step");
        Check(CollisionCalls(autopilotStep).Any(method => method.DeclaringType
                == typeof(LandingApproachController) && method.Name == "ApplyMotionContract")
            && autopilotFlight.GetField("arrivalGate",
                BindingFlags.Instance | BindingFlags.Public) != null,
            "reference navigation consumes the common EVA/personnel motion contract and a capture-ready arrival gate");
        Type hardDockOperation = typeof(HardDockingService).GetNestedType("Operation",
            BindingFlags.NonPublic);
        Check(hardDockOperation?.GetField("captureEstablished",
                BindingFlags.Instance | BindingFlags.NonPublic) != null
            && !HardDockingService.CaptureGate(true, false)
            && HardDockingService.CaptureGate(true, true)
            && HardDockingService.CaptureGate(false, false),
            "hard docking requires one real capture but does not revoke it when the live line releases near entry");
        FieldInfo defaultArrivalTolerance = typeof(ShipAutopilot).GetField(
            "DefaultArrivalVelocityTolerance", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo personnelArrivalTolerance = typeof(PawnSpaceflightShip).GetField(
            "BoardingArrivalVelocityTolerance", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo engageArrival = typeof(HardDockingService).GetMethod("EngageArrival",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Check(defaultArrivalTolerance?.GetRawConstantValue() is float defaultTolerance
            && Math.Abs(defaultTolerance - 0.15f) < 0.0001f
            && personnelArrivalTolerance?.GetRawConstantValue() is float personnelTolerance
            && Math.Abs(personnelTolerance - 2f) < 0.0001f
            && CollisionCalls(engageArrival).Any(method => method.Name == "get_ArrivalVelocityTolerance"),
            "personnel arrival accepts limited relative speed without widening ordinary navigation tolerance");
        FieldInfo personnelApproachSpeed = typeof(PawnSpaceflightShip).GetField(
            "BoardingApproachSpeed", BindingFlags.Static | BindingFlags.NonPublic);
        Check(personnelApproachSpeed?.GetRawConstantValue() is float approachSpeed
            && Math.Abs(approachSpeed - 1.5f) < 0.0001f
            && CollisionCalls(engageArrival).Any(method => method.Name == "get_ArrivalApproachSpeed")
            && typeof(GuidanceRequest).GetField("arrivalVelocity") != null,
            "personnel hard docking supplies a real pass-through velocity target to shared guidance");
        var pawnFlightXml = System.Xml.Linq.XDocument.Load(
            "1.6/Defs/SpaceCombat/DeltaV_PawnSpaceflight.xml");
        Check(Math.Abs(CompPawnGrapple.Range - 32f) < 0.001f
            && (float)pawnFlightXml.Descendants("range").Single() == 32f
            && (float)pawnFlightXml.Descendants("laserRange").Single() == 32f,
            "personnel grapple logic, weapon range, and beam reach share the 32-cell contract");
        Check(TractorLinkService.DockingPulling(true, 20f, 2f, 4f)
            && !TractorLinkService.DockingPulling(true, .7f, 1.5f, 4f)
            && !TractorLinkService.DockingPulling(false, 1.2f, 1.5f, 4f)
            && TractorLinkService.DockingPulling(false, 1.6f, 1.5f, 4f),
            "docking tether pulls at range, slackens for braking, and retensions with hysteresis");
        Type projectileRecord = typeof(SpaceProjectileService).GetNestedType("Projectile",
            BindingFlags.NonPublic);
        Check(projectileRecord?.GetField("personnelProxy",
                BindingFlags.Instance | BindingFlags.Public) != null
            && projectileRecord.GetField("hardDockingActive",
                BindingFlags.Instance | BindingFlags.Public) != null
            && typeof(SpaceProjectileService).GetMethod("EnsurePersonnelProxy",
                BindingFlags.Instance | BindingFlags.NonPublic) != null
            && typeof(SpaceProjectileService).GetMethod("RebuildPersonnelProxies",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "mapped personnel keep one runtime projectile proxy separate from hard-docking activation");
        Check(projectileRecord.GetField("nativeProjectile", BindingFlags.Instance | BindingFlags.Public) != null
            && typeof(SpaceProjectileService).GetMethod("TryCapturePendingNativeProjectile",
                BindingFlags.Instance | BindingFlags.NonPublic) != null
            && typeof(SpaceCollision.SweptBody).GetField("onStructureImpact") != null
            && typeof(AbstractThing).GetMethod("HandoffTransient",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "real Pawn projectiles are held as transient payloads and replayed through confirmed structure contact");
        var personalRound = new SpaceObject(65011, new SpacePose())
        {
            baseTag = SpaceObjectTag.Projectile,
            Payload = new AbstractProjectile()
        };
        Check(!personalRound.InterceptionEligible
            && personalRound.InterceptionTargetTag == SpaceObjectTag.Projectile,
            "personnel weapon rounds remain projectiles for collision but are intentionally excluded from point defense");
        Check(typeof(UniverseManager).Assembly.GetType("DeltaV.PawnSpaceWeaponProfile") == null
            && typeof(UniverseManager).Assembly.GetType("DeltaV.PawnSpaceWeaponBridge") != null,
            "personnel primary fire uses the real Verb bridge without the removed virtual weapon profile");
        Type weaponBridge = typeof(UniverseManager).Assembly.GetType("DeltaV.PawnSpaceWeaponBridge");
        MethodInfo tryPersonalFire = weaponBridge.GetMethod("TryFire",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo supportsCapturedProjectile = weaponBridge.GetMethod("SupportsCapturedProjectile",
            BindingFlags.Static | BindingFlags.NonPublic);
        Check(typeof(PawnSpaceflightShip).GetProperty("WeaponAimDirection",
                BindingFlags.Instance | BindingFlags.NonPublic) != null
            && CollisionCalls(tryPersonalFire).Any(method => method.Name == "AimPersonalWeapon")
            && CollisionCalls(tryPersonalFire).Any(method => method.Name == "get_Position")
            && RangeFeedsOnlyNativeAuthorization(tryPersonalFire),
            "personnel fire keeps universe aim separate from an in-bounds local Verb target");
        MethodInfo notifyAim = typeof(ShipBase).GetMethod("NotifyManualAim",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo aimPersonalWeapon = typeof(PawnSpaceflightShip).GetMethod("AimPersonalWeapon",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Check(notifyAim != null
            && CollisionCalls(AccessTools.Method(typeof(ShipAimHandler), nameof(ShipAimHandler.Aim)))
                .Any(method => method.Name == "NotifyManualAim")
            && aimPersonalWeapon != null
            && !CollisionCalls(aimPersonalWeapon).Any(method => method.Name == "set_Rotation"),
            "manual aim refreshes the saved personnel weapon direction without overriding arcade body facing");
        Type pawnPatches = typeof(UniverseManager).Assembly.GetType("DeltaV.PawnSpaceflightPatches");
        MethodInfo manualAimControls = pawnPatches?.GetMethod("ManualAimControls",
            BindingFlags.Static | BindingFlags.NonPublic);
        Type rotationPatch = typeof(UniverseManager).Assembly.GetType("DeltaV.Patch_PawnSpaceflight_Rotation");
        MethodInfo rotationPrefix = rotationPatch?.GetMethod("Prefix",
            BindingFlags.Static | BindingFlags.NonPublic);
        Check(manualAimControls != null
            && CollisionCalls(manualAimControls).Any(method => method.Name == "ManualAimOwner")
            && rotationPrefix != null
            && CollisionCalls(rotationPrefix).Any(method => method.Name == "ManualAimControls"),
            "manual personnel aim suppresses only the vanilla drafted-pawn rotation fallback");
        Check(typeof(SpaceAimController).GetMethod("TracePersonnelAimGate",
                BindingFlags.Instance | BindingFlags.NonPublic) == null
            && typeof(ArcadeShipAimHandler).GetMethod("TracePersonnelAim",
                BindingFlags.Instance | BindingFlags.NonPublic) == null
            && typeof(PawnSpaceflightShip).GetMethod("TraceWeaponDraw",
                BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "resolved personnel aim diagnostics leave no temporary per-frame trace state");
        var sprayProjectile = (ThingDef)Raw(typeof(ThingDef));
        sprayProjectile.defName = "pawnSpaceflightSprayProjectile";
        sprayProjectile.projectile = (ProjectileProperties)Raw(typeof(ProjectileProperties));
        var sprayVerb = new Verb_ArcSprayProjectile
        {
            verbProps = new VerbProperties { defaultProjectile = sprayProjectile }
        };
        Check(supportsCapturedProjectile != null
            && (bool)supportsCapturedProjectile.Invoke(null, new object[] { sprayVerb })
            && CollisionCalls(tryPersonalFire).Any(method => method.Name == "SupportsCapturedProjectile")
            && CollisionCalls(typeof(SpaceProjectileService).GetMethod("RegisterNativeLaunch",
                BindingFlags.Instance | BindingFlags.NonPublic)).Any(method => method.Name == "IsSpacePrimaryVerb"),
            "projectile-emitting spray Verbs use the same captured native-projectile bridge");
        MethodInfo tryVerbAimAngle = weaponBridge.GetMethod("TryAimAngle",
            BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(Verb), typeof(float).MakeByRefType() }, null);
        MethodInfo tryEquipmentAimAngle = weaponBridge.GetMethod("TryAimAngle",
            BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(Thing), typeof(float).MakeByRefType() }, null);
        MethodInfo tryAimPose = weaponBridge.GetMethod("TryAimPose",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo reorientDrawReach = weaponBridge.GetMethod("ReorientDrawReach",
            BindingFlags.Static | BindingFlags.NonPublic);
        Type aimAnglePatch = typeof(UniverseManager).Assembly.GetType(
            "DeltaV.Patch_PawnSpaceWeapon_AimAngleOverride");
        var aimGetters = (aimAnglePatch?.GetMethod("TargetMethods",
            BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null)
            as System.Collections.IEnumerable)?.Cast<MethodBase>().ToList();
        bool HasAimGetter(MethodBase expected) => expected != null && aimGetters?.Any(method =>
            method.Module == expected.Module && method.MetadataToken == expected.MetadataToken) == true;
        Check(tryVerbAimAngle != null && tryEquipmentAimAngle != null
            && HasAimGetter(AccessTools.PropertyGetter(typeof(Verb), nameof(Verb.AimAngleOverride)))
            && HasAimGetter(AccessTools.PropertyGetter(typeof(Verb_Spray), nameof(Verb.AimAngleOverride)))
            && aimGetters.GroupBy(method => (method.Module, method.MetadataToken))
                .All(group => group.Count() == 1),
            "base and overriding Verb aim getters are patched once and read the saved personnel universe aim");
        Type drawAimPatch = typeof(UniverseManager).Assembly.GetType(
            "DeltaV.Patch_PawnSpaceWeapon_DrawEquipmentAiming");
        MethodInfo drawAimPrefix = drawAimPatch?.GetMethod("Prefix",
            BindingFlags.Static | BindingFlags.NonPublic);
        CustomAttributeData drawPriority = drawAimPrefix?.CustomAttributes
            .FirstOrDefault(attribute => attribute.AttributeType == typeof(HarmonyPriority));
        Check(tryAimPose != null
            && drawAimPrefix?.GetParameters().Any(parameter => parameter.Name == "drawLoc"
                && parameter.ParameterType == typeof(Vector3).MakeByRefType()) == true
            && drawAimPrefix.GetParameters().Any(parameter => parameter.Name == "aimAngle"
                && parameter.ParameterType == typeof(float).MakeByRefType()) == true
            && CollisionCalls(drawAimPrefix).Any(method => method.Name == "TryAimPose")
            && drawPriority?.ConstructorArguments.Count == 1
            && (int)drawPriority.ConstructorArguments[0].Value == Priority.Last,
            "the last final-equipment-draw prefix restores saved personnel aim and placement after replacement renderers");
        Vector3 correctedReach = reorientDrawReach == null ? Vector3.zero
            : (Vector3)reorientDrawReach.Invoke(null,
                new object[] { new Vector3(0.15f, 8.25f, 0.2f), 0f, 90f, 0.4f });
        Check(Mathf.Abs(correctedReach.x - 0.55f) < 0.0001f
            && Mathf.Abs(correctedReach.y - 8.25f) < 0.0001f
            && Mathf.Abs(correctedReach.z + 0.2f) < 0.0001f,
            "degenerate north reach rotates toward universe aim while height and residual offsets stay unchanged");
        MethodInfo captureNativeProjectile = typeof(SpaceProjectileService).GetMethod("TryCapturePendingNativeProjectile",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo retireNativeProjectile = typeof(SpaceProjectileService).GetMethod("RetireNativeProjectile",
            BindingFlags.Static | BindingFlags.NonPublic);
        Type burstCompletedPatch = typeof(UniverseManager).Assembly.GetType("DeltaV.Patch_NativeSpaceWeapon_BurstCompleted");
        MethodInfo burstCompletedPostfix = burstCompletedPatch?.GetMethod("Postfix",
            BindingFlags.Static | BindingFlags.NonPublic);
        Check(captureNativeProjectile != null && retireNativeProjectile != null && burstCompletedPostfix != null
            && CollisionCalls(tryPersonalFire).Any(method => method.Name == "CancelNativeFire")
            && CollisionCalls(captureNativeProjectile).Any(method => method.Name == "RetireNativeProjectile")
            && CollisionCalls(retireNativeProjectile).Any(method => method.Name == nameof(Thing.Destroy))
            && CollisionCalls(burstCompletedPostfix).Any(method => method.Name == "CompleteNativeBurst"),
            "a personnel shot that fails to start, is unauthorized or ends its burst leaves no local projectile or firing authorization behind");
        Type launchPatch = typeof(UniverseManager).Assembly.GetType("DeltaV.Patch_NativeSpaceProjectile_Launch");
        var launchTargets = (launchPatch?.GetMethod("TargetMethods",
            BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null)
            as System.Collections.IEnumerable)?.Cast<MethodBase>().ToList();
        Check(launchTargets?.Any(method => method.DeclaringType == typeof(Projectile)
                && method.Name == nameof(Projectile.Launch)) == true
            && burstCompletedPatch?.GetMethod("TargetMethod", BindingFlags.Static | BindingFlags.NonPublic)
                ?.Invoke(null, null) is MethodBase burstMethod
            && burstMethod.DeclaringType == typeof(Verb),
            "the unified native fire Harmony targets resolve against the production game assemblies");
        Type targetView = typeof(SpaceCollision).GetNestedType("TargetView",
            BindingFlags.NonPublic);
        Check(targetView?.GetField("requiresPersonnelProxy",
                BindingFlags.Instance | BindingFlags.NonPublic) != null
            && targetView.GetMethod("ApplyResolvedDamage",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "personnel interception requires the proxy and forwards resolved damage to the real Pawn");
        FieldInfo diagonal = typeof(PawnSpaceflightSnapshot).GetField("DiagonalThrusters",
            BindingFlags.Static | BindingFlags.NonPublic);
        Check(diagonal?.GetValue(null) is Array layout && layout.Length == 4
            && typeof(PawnSpaceflightSnapshot).GetField("CardinalDirections",
                BindingFlags.Static | BindingFlags.NonPublic) == null,
            "personnel propulsion uses the EVA four-corner diagonal layout");
        MethodInfo eligibility = typeof(PawnSpaceflightSnapshot).GetMethod("Eligibility",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo mobilityApparel = typeof(PawnSpaceflightSnapshot).GetMethod("IsMobilityApparel",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo canMove = typeof(PawnSpaceflightSnapshot).GetMethod("CanMove",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Check(typeof(DefModExtension_PawnSpaceflight).GetField("requiredForFlight") == null
            && typeof(PawnSpaceflightSnapshot).GetField("BaseFlightThrust",
                BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue() is float baseThrust
            && Math.Abs(baseThrust - 800f) < 0.001f
            && mobilityApparel != null
            && CollisionCalls(mobilityApparel).Any(method => method.DeclaringType == typeof(VacuumUtility)
                && method.Name == nameof(VacuumUtility.IsProtectiveApparel))
            && eligibility != null
            && !CollisionCalls(eligibility).Any(method => method.Name == "get_HarmedByVacuum")
            && canMove != null
            && !CollisionCalls(canMove).Any(method => method.Name == "get_HarmedByVacuum"),
            "torso protective apparel grants one base mobility source while vacuum risk remains separate from promotion and movement");
        Check(typeof(SpaceScatterService).GetMethod("PersonnelReleaseWarning",
                BindingFlags.Static | BindingFlags.NonPublic) != null,
            "active personnel egress evaluates mobility and vacuum warnings only from the click path");
        MethodInfo activateLoadedFocus = typeof(UniverseManager).GetMethod("ActivateLoadedFocus",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Check(activateLoadedFocus != null
            && CollisionCalls(activateLoadedFocus).Any(method => method.Name == "ActivateFocusedVessel")
            && CollisionCalls(typeof(UniverseManager).GetMethod(nameof(UniverseManager.LoadedGame)))
                .Any(method => method.Name == "QueueFrameAction"),
            "load completion reapplies the ordinary focused-vessel manual control and fire-control policy");
        MethodInfo bilateral = typeof(ShipPhysics).GetMethod("ApplyTractorImpulse",
            BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(SpaceObject), typeof(UnityEngine.Vector2), typeof(SpaceObject),
                typeof(UnityEngine.Vector2), typeof(float), typeof(bool) }, null);
        Check(bilateral != null,
            "shared tractor impulse exposes explicit movable-target policy for grapples and fixed anchors");
        Type lightEmitter = typeof(UniverseManager).Assembly.GetType("DeltaV.ILightTractorEmitter");
        Check(lightEmitter != null && lightEmitter.IsAssignableFrom(typeof(CompPawnGrapple))
            && lightEmitter.IsAssignableFrom(typeof(CompCargoTractor)),
            "pawn grapples and EVA cargo grapples share the bilateral light-link capability");
        Check(typeof(SpaceScatterService).GetMethod("TryDropPawnInventory",
                BindingFlags.Instance | BindingFlags.NonPublic) != null
            && typeof(PawnSpaceflightService).GetMethod("DescribePromotion",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "active personnel cargo ejection and staged promotion diagnostics are exposed by their instance services");

        var blocker = new SpaceObject(65000, new SpacePose(Vector3.forward, 0f));
        Check(universe.RegisterObject(blocker), "fixture publishes the conflicting object");
        Set(universe, "nextFreeOwnerId", 65000);
        var orphanProbe = (Thing)Raw(typeof(Thing));
        orphanProbe.def = TestBuildingDef("pawnSpaceflightOrphanProbe");
        orphanProbe.def.category = ThingCategory.Item;
        orphanProbe.def.stackLimit = 1;
        orphanProbe.stackCount = 1;
        Set(typeof(Thing), orphanProbe, "mapIndexOrState", (sbyte)-1);
        Check(universe.CapturePayload(orphanProbe, new SpacePose(Vector3.forward, 0f), null) == null
            && orphanProbe.Destroyed && universe.ObjectById(65000) == blocker,
            "failed payload publication cannot leave an unregistered held entity");
        universe.UnregisterObject(blocker);
        Set(universe, "nextFreeOwnerId", 65001);
    }
}
