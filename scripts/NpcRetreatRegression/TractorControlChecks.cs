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
    static bool TractorTransfer(Map sourceMap, SpaceObject sourceFrame, HashSet<IntVec3> cells,
        Dictionary<IntVec3, int> sourceOwners, ref SpaceObject __result)
    {
        tractorTransferred = new HashSet<IntVec3>(cells);
        tractorTransferredOwners = sourceOwners;
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
            var map = TestMap(id);
            var thing = (Thing)Raw(typeof(Thing)); thing.def = itemDef;
            map.listerThings.AllThings.Add(thing);
            var body = new MapSpaceObject(id, map, new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(x, 0)), 0))
                { baseTag = SpaceObjectTag.Scattered };
            universe.RegisterObject(body); return body;
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
            Check(!universe.Scatter.ClaimAutomatic(first, b.weapon) && !universe.Scatter.Pull(first, b.weapon.parent),
                "another ship cannot steal or pull an active aiming claim");
            Check(fire.FindPdTarget(b.weapon, muzzle) == second, "another ship immediately selects a free alternative");
            b.weapon.cooldownUntilTick = 46000;
            fire.TickPointDefense(b.weapon.OwnerShip, b.body, true);
            var locks = (IDictionary)AccessTools.Field(typeof(ShipFireControl), "pointDefense").GetValue(fire);
            object savedLock = locks[b.weapon.parent.thingIDNumber];
            Check(((CombatTarget)AccessTools.Field(savedLock.GetType(), "target").GetValue(savedLock)).ObjectId == second.objectId,
                "real controller assigns a free target during cooldown instead of sharing another ship's lock");
            Set(savedLock, "target", CombatTarget.ForContainer(first));
            Set(savedLock, "retargetAt", 46000);
            locks[b.weapon.parent.thingIDNumber] = savedLock;
            fire.TickPointDefense(b.weapon.OwnerShip, b.body, true);
            savedLock = locks[b.weapon.parent.thingIDNumber];
            Check(((CombatTarget)AccessTools.Field(savedLock.GetType(), "target").GetValue(savedLock)).ObjectId == second.objectId,
                "contested cached lock reselects a free target immediately, before its retry deadline");
            Check(!fire.PdTargetValid(b.weapon, CombatTarget.ForContainer(first), muzzle, out _),
                "an already cached lock is invalid when another tractor owns its target");
            Check(universe.Scatter.Pull(first, a.weapon.parent), "claim owner can pay and start pulling");
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
            Check(a.tractor.Allows(first.map.listerThings.AllThings[0]), "living toggle does not change item filter");
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
            IntVec3 hitCell = new(2, 0, 2), otherCell = new(4, 0, 4);
            targetMap.terrainGrid.foundationGrid[targetMap.cellIndices.CellToIndex(hitCell)] = deck;
            var target = new MapSpaceObject(51030, targetMap,
                new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(35, 0)), 0));
            universe.RegisterObject(target);
            foreach (var body in new[] { a.body, b.body, target })
                tractorMasses[body] = new ShipPhysics.MassReport { mass = 100, inertia = 150 };
            Vector3 hit = SpaceTransform.MapPointToUniverse(target, targetMap, new Vector2(2.7f, 2.3f));
            a.weapon.aimMode = b.weapon.aimMode = SpaceWeaponAimMode.Volley;
            universe.Scatter.ReleaseAutomatic(a.weapon.parent); universe.Scatter.ReleaseAutomatic(b.weapon.parent);
            var shotA = new SpaceWeaponShot(universe, a.weapon, a.body, new WeaponFireRequest(hit));
            var shotB = new SpaceWeaponShot(universe, b.weapon, b.body, new WeaponFireRequest(hit));
            Check(universe.Scatter.TryLaunch(shotA), "manual beam attaches to a real structure cell");
            universe.Scatter.Tick();
            Vector2 one = target.pose.velocity;
            float oneSpin = target.pose.angularVelocity;
            target.pose.velocity = a.body.pose.velocity = b.body.pose.velocity = Vector2.zero;
            target.pose.angularVelocity = a.body.pose.angularVelocity = b.body.pose.angularVelocity = 0;
            Check(universe.Scatter.TryLaunch(shotA) && universe.Scatter.TryLaunch(shotB), "two ships may attach manual beams to one structure");
            universe.Scatter.Tick();
            Check(target.pose.velocity.magnitude > one.magnitude * 1.9f
                && a.body.pose.velocity.magnitude > 0 && b.body.pose.velocity.magnitude > 0,
                "manual forces add and each receiving ship gets its own reaction impulse");
            Check(Math.Abs(oneSpin) > .00001f && Math.Abs(target.pose.angularVelocity) > Math.Abs(oneSpin) * 1.9f,
                "off-center manual beams add torque at their actual attachment points");
            Vector2 total = SpaceTransform.TransportVelocity(a.body.pose, hit) * 100
                + SpaceTransform.TransportVelocity(b.body.pose, hit) * 100 + target.pose.velocity * 100;
            Check(total.magnitude < .02f, "manual multibeam impulses conserve total transported linear momentum");
            Check(!universe.Scatter.TryLaunch(new SpaceWeaponShot(universe, a.weapon, a.body,
                new WeaponFireRequest(first.pose.position))), "manual mode does not capture scattered pawns or items");

            harmony.Patch(AccessTools.Method(typeof(ShipFlightTransitionService), "TransferDomainToNewPocket"),
                prefix: new HarmonyMethod(typeof(Program), nameof(TractorTransfer)));
            targetMap.terrainGrid.foundationGrid[targetMap.cellIndices.CellToIndex(otherCell)] = deck;
            SubstructureOwnership.BumpStructureVersion();
            tractorTransferred = null;
            Check(!universe.Scatter.TryLaunch(shotA), "detached target component blocks whole-map force before separation");
            Call(universe, "RunFrameActions");
            Check(tractorTransferred?.SetEquals(new[] { hitCell }) == true
                && tractorTransferredOwners[hitCell] > 0, "engine-free wreck sends only the pointed component through the existing transfer core");
            tractorTransferred = null;
            Check(!universe.Scatter.TryLaunch(shotA), "a subsequent separation request can retry");
            targetMap.terrainGrid.foundationGrid[targetMap.cellIndices.CellToIndex(hitCell)] = null;
            SubstructureOwnership.BumpStructureVersion();
            Call(universe, "RunFrameActions");
            Check(tractorTransferred == null, "frame action rejects a component destroyed after targeting");
            CheckTractorReceive(universe, receiveOnly: true);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); tractorMasses.Clear();
            AccessTools.Field(typeof(MoonPoolUtility), "moonPoolDef").SetValue(null, previousMoon);
        }
    }
}
