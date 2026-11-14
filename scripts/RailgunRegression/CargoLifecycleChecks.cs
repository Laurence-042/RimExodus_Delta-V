using System;
using System.Collections.Generic;
using System.Linq;
using AdaptiveStorage;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    sealed class LifecycleCargo : Building_SealedSpaceCargoContainer
    {
        public override void SetFaction(Faction faction, Pawn recruiter = null) => SetFactionDirect(faction);
    }

    sealed class OverflowCargoItem : ThingWithComps
    {
        public OverflowCargoItem() { }
        public override string Label => "Cargo fixture";
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            Set(this, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
            map.thingGrid?.Register(this);
        }
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map?.thingGrid?.Deregister(this);
            Set(this, "mapIndexOrState", (sbyte)-1);
        }
    }

    static readonly List<Thing> crateDropped = new();
    // Replace only final placement/Unity map mesh work. Native Crate.Open,
    // EjectContents, CompHackable and ThingOwner all execute in this fixture.
    static bool DropCrateCargo(ThingOwner __instance, ref bool __result)
    {
        if (__instance.Owner is not LifecycleCargo) return true;
        foreach (Thing item in __instance.Where(item => item is not OverflowCargoItem).ToArray())
        { crateDropped.Add(item); __instance.Remove(item); }
        __result = __instance.Count == 0; return false;
    }
    static bool CargoHackedCallback(CompHackable __instance)
    { ((IHackable)__instance.parent).OnHacked(); return false; }

    static Thing[] storageContentsAtBaseDestroy;
    static DestroyMode storageBaseDestroyMode;
    static bool DestroyFixtureCrate(Building __instance, DestroyMode mode)
    {
        if (__instance is LifecycleCargo) __instance.DeSpawn(mode);
        else if (__instance is Building_SpaceCargoContainer storage)
        {
            storageBaseDestroyMode = mode;
            storageContentsAtBaseDestroy = storage.AllSlotCellsList()
                .SelectMany(cell => cell.GetThingList(storage.Map)).ToArray();
        }
        else return true;
        Set(__instance, "mapIndexOrState", (sbyte)-2);
        return false;
    }

    static bool DespawnFixtureCrate(Building __instance)
    {
        if (__instance is not LifecycleCargo) return true;
        Set(__instance, "mapIndexOrState", (sbyte)-1);
        return false;
    }

    static bool BlockOverflowPlacement(Thing thing, ref Thing lastResultingThing, ref bool __result)
    {
        if (thing is not OverflowCargoItem) return true;
        lastResultingThing = null;
        __result = false;
        return false;
    }

    static readonly List<SpaceObject> fixtureScatterObjects = new();

    static bool RegisterCargoFixture(SpaceObject obj)
    {
        if (!obj.IsScattered) return true;
        fixtureScatterObjects.Add(obj);
        return false;
    }

    static bool SpawnCargoFixture(Thing newThing, IntVec3 loc, Map map, ref Thing __result)
    {
        if (newThing is not OverflowCargoItem || !fixtureScatterMaps.Contains(map)) return true;
        newThing.Position = loc;
        newThing.SpawnSetup(map, false);
        __result = newThing;
        return false;
    }

    static bool RegisterCargoFixture(SpaceObject obj)
    {
        if (!fixtureScatterMaps.Contains(obj.map)) return true;
        fixtureScatterObjects.Add(obj);
        return false;
    }

    static void CheckCargoLifecycle()
    {
        var patch = new Harmony("DeltaV.Regression.CargoLifecycle");
        var oldFactions = Find.World.factionManager;
        var oldIds = Current.Game.uniqueIDsManager;
        Current.Game.uniqueIDsManager = new UniqueIDsManager();
        bool oldGod = DebugSettings.godMode;
        var player = new Faction { def = new FactionDef { isPlayer = true } };
        var enemy = new Faction { def = new FactionDef() };
        var relation = new FactionRelation { other = player, kind = FactionRelationKind.Hostile };
        Set(enemy, "relations", new List<FactionRelation> { relation });
        Find.World.factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(Find.World.factionManager, "ofPlayer", player);
        var cargoMap = (Map)Raw(typeof(Map));
        Find.Maps.Add(cargoMap);
        foreach (Map m in Find.Maps) m.designationManager ??= (DesignationManager)Raw(typeof(DesignationManager));
        crateDropped.Clear();
        try
        {
            patch.Patch(AccessTools.Method(typeof(DesignationManager), "RemoveAllDesignationsOn"),
                prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            patch.Patch(AccessTools.Method(typeof(CompHackable), "OnHacked"),
                prefix: new HarmonyMethod(typeof(Program), nameof(CargoHackedCallback)));
            patch.Patch(AccessTools.Method(typeof(ThingOwner), "TryDropAll"),
                prefix: new HarmonyMethod(typeof(Program), nameof(DropCrateCargo)));
            patch.Patch(AccessTools.Method(typeof(Thing), "DirtyMapMesh"),
                prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            patch.Patch(AccessTools.Method(typeof(Thing), "RemoveAllReservationsAndDesignationsOnThis"),
                prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));

            LifecycleCargo make(Faction faction, bool fresh)
            {
                var c = new LifecycleCargo { def = MakeDef("LifecycleCargo") };
                c.SetFactionDirect(faction);
                Set(c, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(cargoMap));
                var hack = new CompHackable { parent = c, props = new CompProperties_Hackable { defence = 1800 }, defence = 1800 };
                var comp = new CompSpaceCargo { parent = c, props = new CompProperties_SpaceCargo() };
                Set(c, "comps", new List<ThingComp> { hack, comp });
                if (fresh) comp.PostPostMake();
                comp.PostSpawnSetup(!fresh);
                return c;
            }
            var cargo = make(player, true);
            Check(cargo.IsUnlocked, "fresh player-built crate unlocks on its first spawn");
            var npc = make(enemy, true);
            Check(!npc.IsUnlocked && npc.Faction == enemy, "fresh NPC crate keeps its native lock and faction");
            var loaded = make(player, false);
            Check(!loaded.IsUnlocked, "loading player cargo keeps a saved dev lock");
            var cargoComp = cargo.GetComp<CompSpaceCargo>();
            var tint = AccessTools.PropertyGetter(typeof(CompSpaceCargo), "TerminalColor");
            var sealedColor = (UnityEngine.Color)tint.Invoke(cargoComp, null);
            var lockMethod = AccessTools.Method(typeof(CompSpaceCargo), "LockForDebug");
            DebugSettings.godMode = false; lockMethod.Invoke(cargoComp, null);
            Check(cargo.IsUnlocked, "normal mode cannot lock cargo through the dev action");
            DebugSettings.godMode = true; lockMethod.Invoke(cargoComp, null);
            Check(!cargo.IsUnlocked && cargo.GetComp<CompHackable>().ProgressPercent == 0,
                "God Mode resets the native hackable comp");
            var lockedColor = (UnityEngine.Color)tint.Invoke(cargoComp, null);
            Check(lockedColor.g > lockedColor.r && lockedColor.g > lockedColor.b
                && sealedColor.r == sealedColor.g && sealedColor.g == sealedColor.b,
                "terminal state color follows the native lock state rather than paint or a saved visual flag");
            UnityEngine.Color NpcTint() => (UnityEngine.Color)tint.Invoke(npc.GetComp<CompSpaceCargo>(), null);
            Check(NpcTint().r > NpcTint().g && NpcTint().r > NpcTint().b,
                "locked hostile cargo uses enemy-terminal red");
            relation.kind = FactionRelationKind.Neutral;
            Check(NpcTint() == lockedColor && !npc.IsUnlocked,
                "diplomacy changes locked cargo to ancient-terminal green without unlocking it");
            relation.kind = FactionRelationKind.Hostile;
            npc.SetFactionDirect(null);
            Check(NpcTint() == lockedColor, "unowned locked cargo uses ancient-terminal green");
            npc.SetFactionDirect(enemy);
            Set(npc.GetComp<CompHackable>(), "hacked", true);
            Check(NpcTint() == sealedColor, "unlocked sealed cargo stays gray even when its owner is hostile");
            var active = new Building_SpaceCargoContainer { def = MakeDef("activePalette") };
            var activeComp = new CompSpaceCargo { parent = active, props = new CompProperties_SpaceCargo() };
            var activeColor = (UnityEngine.Color)tint.Invoke(activeComp, null);
            var palette = typeof(CompSpaceCargo).Assembly.GetType("DeltaV.StorageStateColors");
            Check(activeColor == (UnityEngine.Color)AccessTools.Field(palette, "Active").GetValue(null)
                && sealedColor == (UnityEngine.Color)AccessTools.Field(palette, "Reserved").GetValue(null),
                "cargo reads the same active/reserved palette as the battery");
            cargoComp.PostSpawnSetup(false);
            Check(!cargo.IsUnlocked, "transferring a dev-locked crate does not unlock it");

            var itemDef = MakeDef("NativeCargoItem"); itemDef.category = ThingCategory.Item;
            itemDef.stackLimit = 75; itemDef.thingClass = typeof(ThingWithComps); itemDef.useHitPoints = true;
            var goods = new ThingWithComps { def = itemDef, stackCount = 75, HitPoints = 37 };
            Set(goods, "mapIndexOrState", (sbyte)-1);
            var forbid = new CompForbiddable { parent = goods };
            Set(goods, "comps", new List<ThingComp> { forbid });
            Check(cargo.TryStore(goods) && !goods.Spawned && goods.ParentHolder == cargo,
                "sealed cargo is held by native ThingOwner and is not a map item");
            forbid.Forbidden = true; forbid.Forbidden = false;
            Check(!cargo.CanOpen && !goods.Spawned && goods.ParentHolder == cargo,
                "unforbidding cannot release locked loot or make it haulable");
            Check((object)cargo is not IHaulDestination && (object)cargo is not IHaulSource,
                "sealed crates expose no haul source or storage destination");
            Check(typeof(ThingClass).IsAssignableFrom(typeof(Building_SpaceCargoContainer)),
                "storage mode still uses ASF rather than a custom hauling implementation");
            cargo.GetComp<CompHackable>().HackNow();
            Check(cargo.IsUnlocked && cargo.Contents.Single() == goods && crateDropped.Count == 0
                && cargo.Faction == player && goods.stackCount == 75 && goods.HitPoints == 37,
                "native hacking unlocks and claims the crate with the full original inventory");
            cargo.GetComp<CompHackable>().HackNow();
            Check(crateDropped.Count == 0 && cargo.CanOpen && cargo.OpenTicks == 100,
                "unlocked sealed cargo uses native crate opening duration and availability");
            cargo.Open();
            Check(crateDropped.Single() == goods && cargo.Contents.Count == 0 && !cargo.CanOpen,
                "ordinary native Open unloads the original stack and an empty crate cannot open");

            cargoMap.info = new MapInfo { Size = new IntVec3(8, 1, 8) };
            cargoMap.cellIndices = new CellIndices(cargoMap);
            cargoMap.thingGrid = new ThingGrid(cargoMap);
            var cargoFrame = new Hull(cargoMap);
            universe.objects.Add(cargoFrame);
            fixtureScatterObjects.Clear();
            try
            {
                patch.Patch(typeof(ThingCollection).TypeInitializer,
                    prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Building), "Destroy"),
                    prefix: new HarmonyMethod(typeof(Program), nameof(DestroyFixtureCrate)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Building_Storage), "Destroy"),
                    prefix: new HarmonyMethod(typeof(Program), nameof(DestroyFixtureCrate)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Building), "DeSpawn"),
                    prefix: new HarmonyMethod(typeof(Program), nameof(DespawnFixtureCrate)));
                patch.Patch(AccessTools.Method(typeof(GenPlace), "TryPlaceThing", new[] { typeof(Thing), typeof(IntVec3),
                    typeof(Map), typeof(ThingPlaceMode), typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>),
                    typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int) }),
                    prefix: new HarmonyMethod(typeof(Program), nameof(BlockOverflowPlacement)));
                cargoMap.listerMergeables = new ListerMergeables(cargoMap);
                patch.Patch(AccessTools.Method(typeof(ListerMergeables), nameof(ListerMergeables.Notify_ThingStackChanged)),
                    prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
                patch.Patch(AccessTools.Method(typeof(UniverseManager), nameof(UniverseManager.RegisterObject)),
                    prefix: new HarmonyMethod(typeof(Program), nameof(RegisterCargoFixture)));

                var overflowDef = MakeDef("CargoOverflowItem");
                overflowDef.category = ThingCategory.Item;
                overflowDef.stackLimit = 75;
                overflowDef.thingClass = typeof(OverflowCargoItem);
                overflowDef.useHitPoints = false;
                overflowDef.destroyable = true;
                overflowDef.virtualDefs = new List<ThingDef>();
                Thing OverflowStack()
                {
                    Thing item = ThingMaker.MakeThing(overflowDef);
                    item.stackCount = 4;
                    return item;
                }
                var hacked = make(enemy, false);
                Set(hacked, "positionInt", new IntVec3(2, 0, 2));
                Thing hackOverflow = OverflowStack();
                Check(hacked.TryStore(hackOverflow) && hacked.TryStore(goods), "fixture fills a locked crate");
                hacked.EjectIntoSpace();
                AccessTools.Method(typeof(UniverseManager), "RunFrameActions").Invoke(universe, null);
                Check(hacked.Contents.Count == 2, "a locked crate cannot eject into space");
                hacked.GetComp<CompHackable>().HackNow();
                hacked.Open();
                Check(hacked.Contents.Single() == hackOverflow && crateDropped.Last() == goods && !hackOverflow.Spawned,
                    "deck unload leaves cargo that does not fit in the crate");
                var killed = make(enemy, false);
                Set(killed, "positionInt", new IntVec3(3, 0, 3));
                Thing killOverflow = OverflowStack();
                Check(killed.TryStore(killOverflow), "fixture fills a crate before destruction");
                killed.Destroy(DestroyMode.KillFinalize);
                Check(killed.Destroyed && killed.Contents.Count == 0 && killOverflow.Map == null
                    && killOverflow.holdingOwner != null && universe.HasUnpublishedPayload && killOverflow.stackCount == 1,
                    "sealed destruction preserves one quarter of the original stack for frame transfer");

                Building_SpaceCargoContainer SpawnStorage(IntVec3 position, params Thing[] items)
                {
                    var storage = new Building_SpaceCargoContainer { def = MakeDef("DestroyedCargoStorage") };
                    storage.def.size = new IntVec2(2, 3);
                    Set(storage, "positionInt", position);
                    Set(storage, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(cargoMap));
                    var stored = (ThingCollection)Raw(typeof(ThingCollection));
                    Set(storage, "_storedThings", stored);
                    Set(stored, "_things", items);
                    Set(stored, "_count", items.Length);
                    foreach (Thing item in items)
                    {
                        item.Position = storage.AllSlotCellsList()[0];
                        item.SpawnSetup(cargoMap, false);
                    }
                    return storage;
                }

                var rifleDef = MakeDef("Gun_AssaultRifle");
                rifleDef.category = ThingCategory.Item;
                rifleDef.thingClass = typeof(OverflowCargoItem);
                rifleDef.stackLimit = 1;
                rifleDef.useHitPoints = false;
                rifleDef.destroyable = true;
                rifleDef.virtualDefs = new List<ThingDef>();
                Thing firstRifle = ThingMaker.MakeThing(rifleDef);
                Thing secondRifle = ThingMaker.MakeThing(rifleDef);
                var killedStorage = SpawnStorage(new IntVec3(4, 0, 5), firstRifle, secondRifle);
                IntVec3 storageCell = firstRifle.Position;
                Thing unindexed = ThingMaker.MakeThing(rifleDef);
                unindexed.Position = storageCell;
                unindexed.SpawnSetup(cargoMap, false);
                int seed = Enumerable.Range(0, 1024).First(candidate =>
                {
                    Rand.PushState(candidate);
                    try
                    {
                        int first = GenMath.RoundRandom(.25f);
                        _ = Rand.Value; // First rifle's launch angle.
                        _ = Rand.Value; // First rifle's launch speed.
                        return first == 1 && GenMath.RoundRandom(.25f) == 1;
                    }
                    finally { Rand.PopState(); }
                });
                Rand.PushState(seed);
                try { killedStorage.Destroy(DestroyMode.KillFinalizeLeavingsOnly); }
                finally { Rand.PopState(); }
                Check(killedStorage.Destroyed && storageBaseDestroyMode == DestroyMode.KillFinalizeLeavingsOnly
                    && storageContentsAtBaseDestroy.SequenceEqual(new[] { unindexed })
                    && !firstRifle.Spawned && !secondRifle.Spawned && fixtureScatterObjects.Count == 0,
                    "native storage destruction receives the original mode after both indexed rifles leave the grid");
                Set(active, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(cargoMap));
                Set(active, "positionInt", new IntVec3(4, 0, 4));
                Thing storageOverflow = OverflowStack();
                Check(active.Eject(storageOverflow, forbid: false) == storageOverflow
                    && storageOverflow.Map == cargoMap && storageOverflow.Position == active.Position,
                    "ASF ejection uses the overflow handoff when nearby placement fails");
                AccessTools.Method(typeof(UniverseManager), "RunFrameActions").Invoke(universe, null);
                Check(killedStorage.Destroyed && fixtureScatterObjects.Count == 4
                    && fixtureScatterObjects.All(obj => obj.IsScattered && obj.map == null)
                    && fixtureScatterObjects[0].Payload.Entity == killOverflow && killOverflow.stackCount == 1
                    && fixtureScatterObjects[1].Payload.Entity == firstRifle && firstRifle.stackCount == 1
                    && fixtureScatterObjects[2].Payload.Entity == secondRifle && secondRifle.stackCount == 1
                    && fixtureScatterObjects[1].objectId != fixtureScatterObjects[2].objectId
                    && fixtureScatterObjects[3].Payload.Entity == storageOverflow && storageOverflow.stackCount == 4
                    && storageCell.GetThingList(cargoMap).SequenceEqual(new[] { unindexed }),
                    "ASF destruction transfers both original rifles to distinct mapless objects and leaves an unindexed item untouched");
                SpacePose firstPose = fixtureScatterObjects[1].pose;
                SpacePose secondPose = fixtureScatterObjects[2].pose;
                var sourcePoint = SpaceTransform.MapPointToUniverse(cargoFrame, cargoMap,
                    new UnityEngine.Vector2(storageCell.x + .5f, storageCell.z + .5f));
                Check(firstPose.position.Equals(secondPose.position)
                    && OrbitSphere.ArcCells(sourcePoint, firstPose.position) < .05f
                    && firstPose.velocity.magnitude <= 4.01f && secondPose.velocity.magnitude <= 4.01f
                    && fixtureScatterObjects[3].pose.velocity.sqrMagnitude == 0f,
                    "both rifles overlap at the original cell center with speeds in the 0–4 cells/s range");
                hacked.EjectIntoSpace();
                hacked.EjectIntoSpace();
                AccessTools.Method(typeof(UniverseManager), "RunFrameActions").Invoke(universe, null);
                Check(hacked.Contents.Count == 0 && hackOverflow.stackCount == 4
                    && fixtureScatterObjects.Count == 5 && fixtureScatterObjects[4].Payload.Entity == hackOverflow
                    && fixtureScatterObjects[4].IsScattered,
                    "manual space ejection transfers the full stack and registers a scattered object");

                Check(!universe.HasUnpublishedPayload, "successful frame publication releases the save gate");
            }
            finally
            {
                universe.objects.Remove(cargoFrame);
                Set(universe, "objectByMap", null);
                fixtureScatterObjects.Clear();
                storageContentsAtBaseDestroy = null;
            }

            Check(AccessTools.TypeByName("DeltaV.JobDriver_OpenSpaceCargo") == null
                && AccessTools.TypeByName("DeltaV.Patch_SealedCargoTradeBeacon") == null,
                "custom instant-open job and beacon exclusion patch are removed");
        }
        finally
        {
            patch.UnpatchAll(patch.Id); Find.Maps.Remove(cargoMap); crateDropped.Clear();
            Find.World.factionManager = oldFactions; DebugSettings.godMode = oldGod;
            Current.Game.uniqueIDsManager = oldIds;
        }
    }
}
