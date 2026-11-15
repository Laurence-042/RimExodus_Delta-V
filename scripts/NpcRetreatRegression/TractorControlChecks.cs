using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static readonly Dictionary<SpaceObject, ShipPhysics.MassReport> tractorMasses = new();
    static bool TractorMass(SpaceObject container, ref ShipPhysics.MassReport __result)
    { if (!tractorMasses.TryGetValue(container, out __result)) return true; return false; }
    static bool TractorSourceReady(ref bool __result) { __result = true; return false; }
    static IEnumerable<CodeInstruction> TractorDlcFixture(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is System.Reflection.MethodInfo method
                && method.DeclaringType == typeof(ModsConfig) && method.Name == "get_AnomalyActive")
            { instruction.opcode = System.Reflection.Emit.OpCodes.Ldc_I4_0; instruction.operand = null; }
            yield return instruction;
        }
    }
    static HashSet<IntVec3> tractorTransferred;
    static Dictionary<IntVec3, int> tractorTransferredOwners;
    static HashSet<Building> tractorTransferredBuildings;
    static bool tractorTransferredTerrain;
    static bool TractorTransfer(Map sourceMap, SpaceObject sourceFrame, HashSet<IntVec3> cells,
        Dictionary<IntVec3, int> sourceOwners, HashSet<Building> buildings, bool includeTerrain,
        ref SpaceObject __result)
    {
        tractorTransferred = new HashSet<IntVec3>(cells);
        tractorTransferredOwners = sourceOwners;
        tractorTransferredBuildings = buildings;
        tractorTransferredTerrain = includeTerrain;
        __result = null; // Inspect the real transfer request without allocating a Unity map.
        return false;
    }
    sealed class TractorSave : IExposable
    {
        internal CompSpaceTractor comp;
        public void ExposeData() => comp.PostExposeData();
    }
    static void CheckTractorControl(UniverseManager universe, Faction player, Faction enemy)
    {
        var harmony = new Harmony("DeltaV.Regression.TractorControl");
        harmony.Patch(AccessTools.Method(typeof(ThingFilter), "Allows", new[] { typeof(Thing) }),
            transpiler: new HarmonyMethod(typeof(Program), nameof(TractorDlcFixture)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(RaceProperties), "IsAnomalyEntity"),
            transpiler: new HarmonyMethod(typeof(Program), nameof(TractorDlcFixture)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Find), "ActiveLanguageWorker"),
            prefix: new HarmonyMethod(typeof(Program), nameof(WeaponMenuLanguage)));
        var factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(factionManager, "ofPlayer", player); Set(Find.World, "factionManager", factionManager);
        player.def = new FactionDef { isPlayer = true }; enemy.def = new FactionDef();
        var previousMoon = AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").GetValue(null);
        var moon = (TerrainDef)Raw(typeof(TerrainDef)); moon.defName = "tractor_control_moon";
        AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, moon);
        ThingDefOf.PilotConsole = TestBuildingDef("tractor_control_console");
        ThingDefOf.GravEngine = TestBuildingDef("tractor_control_engine");
        DeltaVDefOf.DeltaV_DockingPort = TestBuildingDef("tractor_control_port");
        Set(player, "relations", new List<FactionRelation> { new() { other = enemy, kind = FactionRelationKind.Hostile } });
        Set(enemy, "relations", new List<FactionRelation> { new() { other = player, kind = FactionRelationKind.Hostile } });
        Current.Game.currentMapIndex = -1;
        Set(Find.TickManager, "ticksGameInt", 45000);
        var itemDef = TestBuildingDef("tractor_control_item"); itemDef.category = ThingCategory.Item;
        itemDef.virtualDefs = new List<ThingDef>();
        DefDatabase<ThingDef>.Add(itemDef);
        AccessTools.Field(typeof(StorageSettings), "cachedEverStorableFixedSettings").SetValue(null, new StorageSettings());
        StorageSettings.EverStorableFixedSettings().filter.SetAllow(itemDef, true);

        (SpaceObject body, CompSpaceWeapon weapon, CompSpaceTractor tractor, CompPowerTrader power) Emitter(int id, float x)
        {
            Map map = TestMap(id);
            var hull = AddTestHull(map, player, id + 1);
            hull.combat.weaponsArmed = true;
            var weapon = new CompSpaceWeapon { parent = hull.parent, aimMode = SpaceWeaponAimMode.PointDefense,
                props = new CompProperties_SpaceWeapon { tractor = true, range = 60, shotEnergy = 0 } };
            var tractor = new CompSpaceTractor { parent = hull.parent };
            var power = new CompPowerTrader { parent = hull.parent };
            Set(power, "powerOnInt", true);
            hull.parent.AllComps.AddRange(new ThingComp[] { weapon, tractor, power });
            map.terrainGrid.topGrid[map.cellIndices.CellToIndex(hull.parent.Position)] = moon;
            var body = new MapSpaceObject(id, map, new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(x, 0)), 0));
            universe.RegisterObject(body);
            tractor.GetStoreSettings().filter.SetAllow(itemDef, true);
            Check(weapon.GroupActive, "tractor fixture uses real ship group activation");
            return (body, weapon, tractor, power);
        }
        SpaceObject Cargo(int id, float x)
        {
            Thing item = NewMaplessCargo("tractorCargo");
            item.def = itemDef;
            return universe.CapturePayload(item,
                new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(x, 0)), 0), null);
        }
        try
        {
            var a = Emitter(51000, 0); var b = Emitter(51010, 1);
            CheckTractorSelection(a.tractor, b.tractor, itemDef, enemy);
            var first = Cargo(51020, 15); var second = Cargo(51021, 25);
            var fire = new ShipFireControl(universe, b.weapon.OwnerShip.Identity);
            Vector3 muzzle = SpaceWeaponService.TargetingMuzzle(b.weapon, b.body);
            Check(fire.FindPdTarget(b.weapon, muzzle) == first, "automatic tractor initially selects nearest allowed cargo");
            Check(universe.Scatter.ClaimAutomatic(first, a.weapon), "first ship claims cargo while aiming");
            SpacePose untouched = first.pose;
            universe.Scatter.Tick();
            Check(first.pose.direction.Equals(untouched.direction), "unpaid aiming claim never moves cargo");
            Check(!universe.Scatter.ClaimAutomatic(first, b.weapon) && !universe.Scatter.Pull(first, b.weapon),
                "another ship cannot steal or pull an active aiming claim");
            Check(fire.FindPdTarget(b.weapon, muzzle) == second, "another ship immediately selects a free alternative");
            b.weapon.cooldownUntilTick = 46000;
            fire.TickPointDefense(b.weapon.OwnerShip, b.body, true);
            var locks = (IDictionary)AccessTools.Field(typeof(ShipFireControl), "pointDefense").GetValue(fire);
            object savedLock = locks[b.weapon.Identity];
            Check(((CombatTarget)AccessTools.Field(savedLock.GetType(), "target").GetValue(savedLock)).ObjectId == second.objectId,
                "real controller assigns a free target during cooldown instead of sharing another ship's lock");
            Set(savedLock, "target", CombatTarget.ForContainer(first));
            Set(savedLock, "retargetAt", 46000);
            locks[b.weapon.Identity] = savedLock;
            fire.TickPointDefense(b.weapon.OwnerShip, b.body, true);
            savedLock = locks[b.weapon.Identity];
            Check(((CombatTarget)AccessTools.Field(savedLock.GetType(), "target").GetValue(savedLock)).ObjectId == second.objectId,
                "contested cached lock reselects a free target immediately, before its retry deadline");
            Check(!fire.PdTargetValid(b.weapon, CombatTarget.ForContainer(first), muzzle, out _),
                "an already cached lock is invalid when another tractor owns its target");
            Check(universe.Scatter.Pull(first, a.weapon), "claim owner can pay and start pulling");
            universe.Scatter.Tick();
            Check(!first.pose.direction.Equals(untouched.direction), "paid automatic request moves its cargo");
            a.tractor.GetStoreSettings().filter.SetAllow(itemDef, false);
            Check(universe.Scatter.AutoAvailable(first, b.weapon), "filter changes immediately release the previous claim");
            Check(universe.Scatter.ClaimAutomatic(first, b.weapon), "released target is available across ships");
            Set(b.power, "powerOnInt", false);
            a.tractor.GetStoreSettings().filter.SetAllow(itemDef, true);
            Check(universe.Scatter.ClaimAutomatic(first, a.weapon), "power loss releases claims before the other ship's turn");
            Set(b.power, "powerOnInt", true);
            Set(Find.TickManager, "ticksGameInt", 45002);
            Check(universe.Scatter.ClaimAutomatic(first, b.weapon), "unrefreshed claims expire without a rescan timer");
            a.tractor.GetStoreSettings().filter.SetAllow(itemDef, false);
            Check(fire.FindPdTarget(a.weapon, muzzle) == null, "item filter excludes every disallowed cargo candidate");

            Pawn PawnOf(Faction faction, int kind)
            {
                var pawn = (Pawn)Raw(typeof(Pawn));
                pawn.def = TestBuildingDef("tractor_species_" + kind);
                pawn.def.race = new RaceProperties { intelligence = kind == 0 ? Intelligence.Humanlike : Intelligence.Animal };
                Set(pawn.def.race, "fleshType", kind == 2 ? FleshTypeDefOf.Mechanoid : FleshTypeDefOf.Normal);
                pawn.health = new Pawn_HealthTracker(pawn);
                pawn.mindState = new Verse.AI.Pawn_MindState(pawn);
                Set(typeof(Thing), pawn, "factionInt", faction);
                return pawn;
            }
            FleshTypeDefOf.Mechanoid ??= new FleshTypeDef { defName = "tractorMech" };
            FleshTypeDefOf.Normal ??= new FleshTypeDef { defName = "tractorFlesh" };
            var ally = (Faction)Raw(typeof(Faction));
            ally.def = new FactionDef();
            Set(ally, "relations", new List<FactionRelation> { new() { other = player, kind = FactionRelationKind.Ally } });
            foreach (int kind in Enumerable.Range(0, 3))
            {
                var hostile = PawnOf(enemy, kind);
                Check(!a.tractor.Allows(hostile), "default excludes hostile pawn type " + kind);
                foreach (Faction faction in new[] { player, ally, null })
                    Check(a.tractor.Allows(PawnOf(faction, kind)), "default permits non-hostile pawn type " + kind);
                a.tractor.pawnMask |= 1 << kind;
                Check(a.tractor.Allows(hostile), "individual enemy/type cell permits only the selected type");
            }
            a.tractor.collectLiving = false;
            Check(!a.tractor.Allows(PawnOf(player, 0)), "living master toggle overrides faction/type selection");
            a.tractor.GetStoreSettings().filter.SetAllow(itemDef, true);
            Check(a.tractor.Allows(first.Payload.Entity), "living toggle does not change item filter");
            a.tractor.pawnMask = 0x801;
            string path = Path.GetFullPath("Source/obj/tractor-settings.xml");
            var restored = new TractorSave { comp = new CompSpaceTractor { parent = a.weapon.parent } };
            try
            {
                Scribe.saver.InitSaving(path, "tractor"); a.tractor.PostExposeData(); Scribe.saver.FinalizeSaving();
                Scribe.loader.InitLoading(path); restored.ExposeData();
                Scribe.loader.initer.RegisterForPostLoadInit(restored); Scribe.loader.FinalizeLoading();
                Check(!restored.comp.collectLiving && restored.comp.pawnMask == 0x801
                    && restored.comp.GetStoreSettings().filter.Allows(itemDef), "Scribe retains item filter and independent pawn settings");
            }
            finally { Scribe.ForceStop(); }

            a.weapon.WeaponProps.tractor = false; a.weapon.WeaponProps.range = 500;
            CheckPointDefenseTargets(universe, a.weapon.OwnerShip, a.body, a.weapon, player, enemy);
            a.weapon.WeaponProps.tractor = true; a.weapon.WeaponProps.range = 60;

            // Only native environment endpoints are replaced. Real manual request admission and impulse code run.
            harmony.Patch(AccessTools.Method(typeof(ShipFlightTransitionService), "RequestThrust"),
                prefix: new HarmonyMethod(typeof(Program), nameof(TractorSourceReady)));
            harmony.Patch(AccessTools.Method(typeof(ShipPhysics), "GetReport"),
                prefix: new HarmonyMethod(typeof(Program), nameof(TractorMass)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
                prefix: new HarmonyMethod(typeof(Program), nameof(RegressionSubstructure)));
            var targetMap = TestMap(51030);
            var deck = (TerrainDef)Raw(typeof(TerrainDef)); deck.isFoundation = true;
            IntVec3 hitCell = new(2, 0, 2);
            targetMap.terrainGrid.foundationGrid[targetMap.cellIndices.CellToIndex(hitCell)] = deck;
            var target = new MapSpaceObject(51030, targetMap,
                new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(35, 0)), 0));
            universe.RegisterObject(target);
            foreach (var body in new[] { a.body, b.body, target })
                tractorMasses[body] = new ShipPhysics.MassReport { mass = 100, inertia = 150 };
            Vector3 hit = SpaceTransform.MapPointToUniverse(target, targetMap, new Vector2(2.7f, 2.3f));
            a.weapon.aimMode = b.weapon.aimMode = SpaceWeaponAimMode.Volley;
            universe.Scatter.ReleaseAutomatic(a.weapon.parent); universe.Scatter.ReleaseAutomatic(b.weapon.parent);
            var tractorXml = System.Xml.Linq.XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_Tractor.xml");
            var weaponXml = tractorXml.Descendants("li").Single(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_SpaceWeapon");
            Check((int)weaponXml.Element("defaultGroupNumber") == 4 && (string)weaponXml.Element("defaultSalvoSet") == "Secondary",
                "tractor defaults to secondary group 4");
            float force = (float)tractorXml.Descendants("tractorForce").Single();
            Check(force == ShipPhysics.ThrusterThrust * .25f, "one tractor uses a quarter of full large-thruster force");
            var beam = TestBuildingDef("tractor_ray_fixture");
            beam.comps = new List<CompProperties> { new CompProperties_SpaceProjectile { ignoresShields = true,
                continuousBeam = true, collisionRadius = (float?)tractorXml.Descendants("collisionRadius").SingleOrDefault() ?? -1f } };
            var pullEffect = new ImpactOutcomePart { kind = ImpactEffectKind.TractorPull, tractorForce = force };
            foreach (bool foundation in new[] { false, true })
                DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "tractor_ray_" + foundation,
                    projectileDefs = new List<ThingDef> { beam }, targetsSubstructure = foundation,
                    buildingHitChance = 1, substructureHitChance = 1, defenderEffect = pullEffect,
                    attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf } });
            SpaceImpactEffects.ResetCache();
            SpaceCollision.SweptBody Fire(CompSpaceWeapon weapon, SpaceObject source, Vector3 aim)
            {
                Vector3 from = SpaceWeaponService.TargetingMuzzle(weapon, source);
                Vector3 end = OrbitSphere.Move(from, OrbitSphere.Offset(from, aim).normalized * 60f);
                var body = new SpaceCollision.SweptBody { attacker = new SpaceObject(-51030, new SpacePose(from, 0)),
                    excludeContainer = source, requiredDefender = target, projectileDef = beam, launcher = weapon.parent,
                    from = from, to = end, sweepRadius = SpaceProjectileService.FootprintRadius(beam) };
                SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { body });
                return body;
            }
            var shotA = Fire(a.weapon, a.body, hit);
            Check(shotA.resolvedHit && shotA.resolvedHitDefender == target && shotA.resolvedHitCell == hitCell,
                "beam finds real structure along its ray before the empty endpoint");
            Vector2 one = target.pose.velocity;
            float oneSpin = target.pose.angularVelocity;
            Check(Math.Abs(one.magnitude - force / 60f / 100f) < .0001f,
                "one hit transfers exactly one tick of configured force");
            target.pose.velocity = a.body.pose.velocity = b.body.pose.velocity = Vector2.zero;
            target.pose.angularVelocity = a.body.pose.angularVelocity = b.body.pose.angularVelocity = 0;
            Check(Fire(a.weapon, a.body, hit).resolvedHit && Fire(b.weapon, b.body, hit).resolvedHit,
                "two emitters can hit the same structure");
            Check(target.pose.velocity.magnitude > one.magnitude * 1.9f
                && a.body.pose.velocity.magnitude > 0 && b.body.pose.velocity.magnitude > 0,
                "manual forces add and each receiving ship gets its own reaction impulse");
            Check(Math.Abs(oneSpin) > .00001f && Math.Abs(target.pose.angularVelocity) > Math.Abs(oneSpin) * 1.8f,
                "off-center beams apply torque at the current collision point");
            Vector2 total = SpaceTransform.TransportVelocity(a.body.pose, hit) * 100
                + SpaceTransform.TransportVelocity(b.body.pose, hit) * 100 + target.pose.velocity * 100;
            Check(total.magnitude < .02f, "manual multibeam impulses conserve total transported linear momentum");
            Vector2 beforeMiss = target.pose.velocity;
            var miss = Fire(a.weapon, a.body, OrbitSphere.Move(a.body.pose.position, new Vector2(0, -55)));
            Check(!miss.resolvedHit && target.pose.velocity == beforeMiss,
                "empty ray has no force and retains its full-range endpoint");
            a.weapon.WeaponProps.projectileDef = beam;
            a.weapon.WeaponProps.baseSpreadDeg = 0;
            var beamProps = beam.GetCompProperties<CompProperties_SpaceProjectile>();
            beamProps.laserRange = 60; beamProps.hasMap = false; beamProps.visualStyle = CombatVisualStyle.Tractor;
            SpacePose beforeEmptyShot = a.body.pose;
            a.body.pose.position = OrbitSphere.Move(a.body.pose.position, new Vector2(10000, 10000));
            Vector2 emission = SpaceWeaponGeometry.MuzzleMapPoint(a.weapon);
            Vector3 emissionWorld = SpaceTransform.MapPointToUniverse(a.body, a.body.map, emission);
            var emptyShot = new SpaceWeaponShot(universe, a.weapon, a.body,
                new WeaponFireRequest(OrbitSphere.Move(emissionWorld, new Vector2(0, -120)), default, true))
                { projectileDef = beam, projectileProps = beamProps, muzzleMap = emission, muzzleUniverse = emissionWorld,
                  launchMapAngle = -Mathf.PI / 2, speed = 2000 };
            Check(a.tractor.TryLaunch(emptyShot), "manual tractor emits a prepared beam toward empty space");
            universe.Projectiles.ResolveCollisions();
            var beams = (IList)AccessTools.Field(typeof(SpaceProjectileService), "beams").GetValue(universe.Projectiles);
            object drawn = beams.Cast<object>().Single(x => (Thing)AccessTools.Field(x.GetType(), "continuousLauncher").GetValue(x) == a.weapon.parent);
            Vector3 drawnFrom = (Vector3)AccessTools.Field(drawn.GetType(), "from").GetValue(drawn);
            Vector3 drawnTo = (Vector3)AccessTools.Field(drawn.GetType(), "to").GetValue(drawn);
            Check(Math.Abs(OrbitSphere.ArcCells(drawnFrom, drawnTo) - 60f) < .02f,
                "unhit manual beam is drawn at full range regardless of cursor distance");
            a.body.pose = beforeEmptyShot;
            emissionWorld = SpaceTransform.MapPointToUniverse(a.body, a.body.map, emission);
            Vector2 hitDirection = SpaceTransform.UniverseTangentToMapDirection(a.body, a.body.map,
                emissionWorld, OrbitSphere.Offset(emissionWorld, hit).normalized);
            var hitShot = new SpaceWeaponShot(universe, a.weapon, a.body, new WeaponFireRequest(hit, default, true))
            {
                projectileDef = beam, projectileProps = beamProps, muzzleMap = emission, muzzleUniverse = emissionWorld,
                launchMapAngle = Mathf.Atan2(hitDirection.y, hitDirection.x), speed = 2000
            };
            var impacts = (IList)AccessTools.Field(typeof(SpaceProjectileService), "impacts").GetValue(universe.Projectiles);
            var particles = (IList)AccessTools.Field(typeof(SpaceProjectileService), "particles").GetValue(universe.Projectiles);
            int impactCount = impacts.Count, particleCount = particles.Count;
            Vector2 beforeHit = target.pose.velocity;
            SpacePose beforeClearShot = b.body.pose;
            b.body.pose.position = OrbitSphere.Move(b.body.pose.position, new Vector2(0, 20));
            var fragmentMap = TestMap(51031);
            fragmentMap.terrainGrid.foundationGrid[fragmentMap.cellIndices.CellToIndex(hitCell)] = deck;
            Vector2 beamOffset = OrbitSphere.Offset(emissionWorld, hit);
            var fragment = new MapSpaceObject(universe.AllocateOwnerId(), fragmentMap,
                new SpacePose(OrbitSphere.Move(emissionWorld, beamOffset * .5f), .45f));
            universe.RegisterObject(fragment);
            tractorMasses[fragment] = tractorMasses[target];
            Check(a.tractor.TryLaunch(hitShot), "manual tractor emits a prepared beam toward a structure");
            universe.Projectiles.ResolveCollisions();
            drawnTo = (Vector3)AccessTools.Field(drawn.GetType(), "to").GetValue(drawn);
            Check(fragment.pose.velocity != Vector2.zero && target.pose.velocity == beforeHit
                && OrbitSphere.ArcCells(drawnTo, fragment.pose.position) < 1f,
                "a later-registered rotated fragment in front gets the impulse and beam endpoint");
            fragment.pose.position = OrbitSphere.Move(emissionWorld, beamOffset * 1.4f);
            Vector2 fragmentVelocity = fragment.pose.velocity;
            Check(a.tractor.TryLaunch(hitShot), "manual tractor emits with the larger target in front");
            universe.Projectiles.ResolveCollisions();
            Check(fragment.pose.velocity == fragmentVelocity, "the larger structure occludes a fragment behind it");
            universe.UnregisterObject(fragment);
            b.body.pose = beforeClearShot;
            drawnTo = (Vector3)AccessTools.Field(drawn.GetType(), "to").GetValue(drawn);
            Check(target.pose.velocity != beforeHit, "structural contact still applies force");
            Check(OrbitSphere.ArcCells(drawnTo, hit) < 1.5f, "structural contact draws the beam to the hit point");
            Check(impacts.Count == impactCount && particles.Count == particleCount,
                "structural contact emits no combat flash or particles");
            Check(!a.tractor.TryLaunch(new SpaceWeaponShot(universe, a.weapon, a.body,
                new WeaponFireRequest(hit))), "automatic fire cannot launch a tractor in structural mode");
            CheckScatteredSweep(universe, first);

            harmony.Patch(AccessTools.Method(typeof(ShipFlightTransitionService), "TransferDomainToNewPocket"),
                prefix: new HarmonyMethod(typeof(Program), nameof(TractorTransfer)));
            foreach (IntVec3 cell in new CellRect(0, 0, 2, 2))
            {
                targetMap.terrainGrid.foundationGrid[targetMap.cellIndices.CellToIndex(cell)] = deck;
                universe.NotifyFoundationChanged(targetMap, cell);
            }
            SubstructureOwnership.BumpStructureVersion();
            foreach (ImpactEffectKind kind in new[] { ImpactEffectKind.TractorPull, ImpactEffectKind.CoupleMotion })
            {
                pullEffect.kind = kind;
                // A coupler has no tractor component requirement and need not stand on a moonpool.
                a.weapon.WeaponProps.tractor = kind == ImpactEffectKind.TractorPull;
                a.body.map.terrainGrid.topGrid[a.body.map.cellIndices.CellToIndex(a.weapon.parent.Position)] =
                    kind == ImpactEffectKind.TractorPull ? moon : null;
                tractorTransferred = null;
                Vector2 beforeSplit = target.pose.velocity;
                var splitShot = Fire(a.weapon, a.body, hit);
                Check(splitShot.resolvedHit && splitShot.resolvedHitCell == hitCell,
                    kind + " hits the small block while grazing a larger block on the same map");
                Check(target.pose.velocity == beforeSplit, kind + " never applies force to the old multi-block map");
                Call(universe, "RunFrameActions");
                Check(tractorTransferred?.SetEquals(new[] { hitCell }) == true
                    && tractorTransferredOwners[hitCell] > 0, kind + " extracts only the hit engine-free block");
            }
            tractorTransferred = null;
            Fire(a.weapon, a.body, hit);
            targetMap.terrainGrid.foundationGrid[targetMap.cellIndices.CellToIndex(hitCell)] = null;
            universe.NotifyFoundationChanged(targetMap, hitCell);
            SubstructureOwnership.BumpStructureVersion();
            Call(universe, "RunFrameActions");
            Check(tractorTransferred == null, "frame action rejects a component destroyed after targeting");
            var shuttle = AddTestHull(targetMap, enemy, 51032);
            var hull = (Building)shuttle.parent;
            targetMap.thingGrid.Deregister(hull);
            hull.def.size = new IntVec2(3, 3);
            hull.def.bringAlongOnGravship = true;
            Set(typeof(Thing), hull, "rotationInt", Rot4.North);
            Set(typeof(Thing), hull, "positionInt", new IntVec3(2, 0, 2));
            targetMap.thingGrid.Register(hull);
            var firstContact = new IntVec3(3, 0, 3);
            var secondContact = new IntVec3(3, 0, 2);
            void SetDeck(IntVec3 cell, TerrainDef value)
            {
                targetMap.terrainGrid.foundationGrid[targetMap.cellIndices.CellToIndex(cell)] = value;
                universe.NotifyFoundationChanged(targetMap, cell);
                SubstructureOwnership.BumpStructureVersion(targetMap);
            }
            SetDeck(firstContact, deck);
            SetDeck(secondContact, deck);
            var transitions = universe.ShipTransitions;
            var overlap = new IntVec3(1, 0, 1);
            SetDeck(overlap, null);
            foreach (Building struck in new Building[] { hull, null })
            {
                tractorTransferred = null;
                IntVec3 cell = struck == null ? firstContact : hull.Position;
                Check(!transitions.PrepareExternalForceTarget(target, cell, a.weapon, struck),
                    "attached hull and base both defer force to the same separation transaction");
                Call(universe, "RunFrameActions");
                Check(tractorTransferred?.SetEquals(new[] { firstContact, secondContact }) == true
                    && tractorTransferredBuildings.SetEquals(new[] { hull }) && tractorTransferredTerrain,
                    "center outside the domain still moves the hull with its two foundation cells");
            }
            var previewCells = universe.FoundationTopology.DomainAt(targetMap, firstContact);
            var oldGravshipDef = WorldObjectDefOf.Gravship;
            var oldIds = Current.Game.uniqueIDsManager;
            RimWorld.Planet.Gravship preview, capture;
            try
            {
                WorldObjectDefOf.Gravship ??= new WorldObjectDef();
                Current.Game.uniqueIDsManager ??= new UniqueIDsManager();
                var members = universe.FoundationTopology.BuildingsIn(targetMap, previewCells);
                preview = GravshipInterop.BuildPreview(previewCells, null, targetMap, buildings: members);
                targetMap.storyState = new StoryState(targetMap);
                targetMap.autoSlaughterManager = new AutoSlaughterManager(targetMap);
                targetMap.zoneManager = new ZoneManager(targetMap);
                targetMap.areaManager = new AreaManager(targetMap);
                targetMap.storageGroups = new StorageGroupManager(targetMap);
                capture = GravshipInterop.CaptureDomain(targetMap, previewCells, null, null,
                    includeTerrain: false, buildings: members);
            }
            finally
            {
                WorldObjectDefOf.Gravship = oldGravshipDef;
                Current.Game.uniqueIDsManager = oldIds;
            }
            Check(preview.ThingPlacements.ContainsKey(hull)
                && ShipPocketMapFactory.PlacedBounds(preview).Width >= hull.def.size.x,
                "preview places the full overhanging hull within the compact map bounds");
            Check(capture.Things.Contains(hull)
                && capture.ThingPlacements[hull].local == preview.ThingPlacements[hull].local,
                "Gravship capture and preview record the same overhanging building instance and anchor");
            SetDeck(overlap, deck);
            tractorTransferred = null;
            transitions.PrepareExternalForceTarget(target, firstContact, a.weapon);
            Call(universe, "RunFrameActions");
            Check(tractorTransferred?.SetEquals(new[] { firstContact, secondContact }) == true
                && !tractorTransferredBuildings.Contains(hull), "cross-domain hull stays on the source map");
            tractorTransferred = null;
            transitions.PrepareExternalForceTarget(target, hull.Position, a.weapon, hull);
            Call(universe, "RunFrameActions");
            Check(tractorTransferred?.SetEquals(hull.OccupiedRect()) == true && !tractorTransferredTerrain
                && tractorTransferredBuildings.SetEquals(new[] { hull }), "beam on the shared hull moves only the hull");

            tractorTransferred = null;
            Check(transitions.LaunchShuttleToPocket(universe, hull) == null
                && tractorTransferredBuildings.SetEquals(new[] { hull }) && !tractorTransferredTerrain,
                "explicit takeoff uses the same transaction and selects only the hull");
            SetDeck(firstContact, null);
            SetDeck(secondContact, null);
            tractorTransferred = null;
            Check(transitions.PrepareExternalForceTarget(target, hull.Position, a.weapon, hull)
                && universe.FoundationTopology.BuildingDomain(targetMap, hull).Contains(overlap),
                "after one domain leaves the hull and remaining foundation take force together without another move");
            SetDeck(overlap, null);
            Check(!transitions.PrepareExternalForceTarget(target, hull.Position, a.weapon, hull),
                "unsupported hull still needs separation while another domain remains in the source");
            Call(universe, "RunFrameActions");
            Check(tractorTransferredBuildings.SetEquals(new[] { hull }) && !tractorTransferredTerrain,
                "unsupported hull is independently towable");

            CheckTractorReceive(universe, receiveOnly: true);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); tractorMasses.Clear();
            AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, previousMoon);
        }
    }
}
