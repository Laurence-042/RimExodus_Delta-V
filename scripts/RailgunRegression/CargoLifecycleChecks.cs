using System;
using System.Collections.Generic;
using System.Linq;
using AdaptiveStorage;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
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

    // Stands in for the real registration of scattered payloads. It must report the same success:
    // CapturePayload rolls a frame-published payload back onto the map when registration fails.
    static bool RegisterCargoFixture(SpaceObject obj, ref bool __result)
    {
        if (!obj.IsScattered) return true;
        fixtureScatterObjects.Add(obj);
        __result = true;
        return false;
    }

    static int cargoMassSamples;
    static int cargoMassEntityReads;
    static void CountCargoMassSample(CompStructureMass __instance)
    {
        if (!(bool)AccessTools.Field(typeof(CompStructureMass), "initialized").GetValue(__instance)) cargoMassSamples++;
    }
    static void CountCargoMassEntityRead() => cargoMassEntityReads++;
    static bool CargoMassStat(Thing thing, StatDef stat, ref float __result)
    {
        if (stat != StatDefOf.Mass) return true;
        __result = 2f;
        return false;
    }

    static void CheckCargoMassBudget(Map source, SpaceObject frame, LifecycleCargo sealedCargo)
    {
        var patch = new Harmony("DeltaV.Regression.CargoMassBudget");
        int tick = Find.TickManager.TicksGame;
        StatDef oldMass = StatDefOf.Mass;
        var space = (TerrainDef)Raw(typeof(TerrainDef));
        TerrainDef oldSpace = TerrainDefOf.Space;
        var destination = (Map)Raw(typeof(Map));
        destination.info = new MapInfo { Size = source.Size };
        destination.cellIndices = new CellIndices(destination);
        destination.designationManager = (DesignationManager)Raw(typeof(DesignationManager));
        destination.components = new List<MapComponent> { new SubstructureOwnership(destination) };
        destination.terrainGrid = new TerrainGrid(destination);
        destination.listerThings = new ListerThings(ListerThingsUse.Global);
        Array.Fill(destination.terrainGrid.topGrid, space);
        Find.Maps.Add(destination);
        source.components = new List<MapComponent> { new SubstructureOwnership(source) };
        source.terrainGrid = new TerrainGrid(source);
        source.listerThings = new ListerThings(ListerThingsUse.Global);
        Array.Fill(source.terrainGrid.topGrid, space);
        TerrainDefOf.Space = space;
        StatDefOf.Mass ??= new StatDef { defName = "RegressionMassStat" };
        source.listerThings.AllThings.Add(sealedCargo);
        try
        {
            patch.Patch(AccessTools.Method(typeof(StatExtension), "GetStatValue",
                new[] { typeof(Thing), typeof(StatDef), typeof(bool), typeof(int) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(CargoMassStat)));
            patch.Patch(AccessTools.Method(typeof(CompStructureMass), "Initialize"),
                prefix: new HarmonyMethod(typeof(Program), nameof(CountCargoMassSample)));
            patch.Patch(AccessTools.PropertyGetter(typeof(SubstructureOwnership), "SpawnedBuildings"),
                prefix: new HarmonyMethod(typeof(Program), nameof(CountCargoMassEntityRead)));
            var itemDef = MakeDef("RegressionCargoMass");
            itemDef.category = ThingCategory.Item; itemDef.thingClass = typeof(ThingWithComps); itemDef.stackLimit = 75;
            ShipStructureIndex index = universe.ShipStructure;
            void Advance(int offset)
            {
                Set(Find.TickManager, "ticksGameInt", tick + offset);
                index.PublishDueMass(Find.TickManager.TicksGame);
            }
            CompStructureMass InstallMass(ThingWithComps building)
            {
                building.def.comps ??= new List<CompProperties>();
                building.def.comps.Add(new CompProperties_StructureMass());
                building.InitializeComps();
                return building.GetComp<CompStructureMass>();
            }
            var comp = sealedCargo.GetComp<CompStructureMass>();
            var stock = new ThingWithComps { def = itemDef, stackCount = 10 };
            var shuttle = new Building { def = MakeDef("RegressionTransporter"), thingIDNumber = 760002 };
            shuttle.def.comps = new List<CompProperties> { new CompProperties_Transporter() };
            CompStructureMass shuttleMass = InstallMass(shuttle);
            var transporter = shuttle.GetComp<CompTransporter>();
            Set(shuttle, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(source));
            source.listerThings.AllThings.Add(shuttle);
            var secondCabinet = new LifecycleCargo { def = MakeDef("RegressionSecondCabinet"), thingIDNumber = 760003 };
            secondCabinet.def.comps = new List<CompProperties> { new CompProperties_SpaceCargo() };
            var second = InstallMass(secondCabinet);
            Set(secondCabinet, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(source));
            source.listerThings.AllThings.Add(secondCabinet);
            float dryMass = ShipPhysics.MassPerBuilding * 3;
            ShipPhysics.MassReport report = ShipPhysics.GetReport(frame);
            cargoMassSamples = cargoMassEntityReads = 0;
            Check(sealedCargo.TryStore(stock) && comp.CurrentMass == 20 && comp.PublishedMass == 0,
                "cabinet starts its own deadline and separates current and published weight");
            Advance(60);
            Check(secondCabinet.TryStore(new ThingWithComps { def = itemDef, stackCount = 3 })
                && second.DueTick == tick + 660, "native inventory changes stagger cabinets on the same map");
            var passenger = new Pawn { def = MakeDef("RegressionPassenger"), stackCount = 1 };
            passenger.def.category = ThingCategory.Pawn;
            var shuttleLoad = new ThingWithComps { def = itemDef, stackCount = 4 };
            Check(transporter.innerContainer.TryAdd(shuttleLoad) && transporter.innerContainer.TryAdd(passenger)
                && shuttleMass.CurrentMass == transporter.MassUsage && shuttleMass.CurrentMass == 10f
                && ShipPhysics.GetReport(frame).mass == dryMass + 10f,
                "ordinary shuttle uses vanilla manifest weight including passengers in the live flight report");
            stock.stackCount = 11;
            comp.NotifyItem(stock, true);
            Check(comp.DueTick == tick + 600 && ShipPhysics.GetReport(frame).mass == dryMass + 10f,
                "further changes do not restart the first cabinet countdown");

            var replacement = (Building_SpaceCargoContainer)Raw(typeof(Building_SpaceCargoContainer));
            replacement.def = MakeDef("RegressionMassStorage");
            replacement.thingIDNumber = 760004;
            Set(replacement, "positionInt", new IntVec3(5, 0, 4));
            Set(replacement, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(source));
            var stored = (ThingCollection)Raw(typeof(ThingCollection));
            Set(replacement, "_storedThings", stored);
            Set(stored, "_things", new Thing[] { stock });
            Set(stored, "_count", 1);
            var storage = InstallMass(replacement);
            Advance(599);
            var publication = comp.CapturePublication();
            comp.PostDeSpawn(source);
            source.listerThings.AllThings.Remove(sealedCargo);
            Set(sealedCargo, "mapIndexOrState", (sbyte)-1);
            SubstructureOwnership.For(source).UpdateSpawnedBuilding(sealedCargo);
            source.listerThings.AllThings.Add(replacement);
            SubstructureOwnership.For(source).UpdateSpawnedBuilding(replacement);
            Check(storage.PublishedMass == 0, "registering ASF before native storage has initialized leaves its load pending");
            storage.StorageReady();
            Check(storage.PublishedMass == 22, "ASF posts its initial actual inventory once after native storage initialization");
            storage.RestorePublication(publication);
            storage.RestorePublication(storage.CapturePublication());
            Check(ReferenceEquals(report, ShipPhysics.GetReport(frame)) && report.mass == dryMass + 10
                && storage.DueTick == tick + 600,
                "cabinet mode handoff or rollback preserves publication, deadline and report identity");

            cargoMassSamples = cargoMassEntityReads = 0;
            Advance(600);
            Check(ShipPhysics.GetReport(frame).mass == dryMass + 32 && second.PublishedMass == 0
                && storage.DueTick == -1 && cargoMassSamples == 0 && cargoMassEntityReads == 0,
                "first cabinet publishes only its net delta at tick 600 without sampling inventory");
            Advance(659);
            Check(ShipPhysics.GetReport(frame).mass == dryMass + 32, "second cabinet is still waiting one tick before its deadline");
            Advance(660);
            Check(ShipPhysics.GetReport(frame).mass == dryMass + 38 && second.DueTick == -1,
                "second cabinet publishes independently at tick 660");
            var moments = new ShipPhysics.MassMoments();
            Vector2 origin = SpaceTransform.ShipLocalOrigin(source);
            foreach (IntVec3 cell in SubstructureOwnership.CellsOnMap(source))
                moments.Add(ShipPhysics.MassPerDeckCell, new Vector2(cell.x + 0.5f, cell.z + 0.5f) - origin);
            foreach (Building building in SubstructureOwnership.For(source).SpawnedBuildings)
                moments.AddBuilding(building.OccupiedRect(), origin, ShipPhysics.BuildingWeight(building));
            var measured = new ShipPhysics.MassReport();
            moments.WriteTo(measured);
            Check(measured.mass == report.mass && measured.comLocal.Equals(report.comLocal)
                && Math.Abs(measured.inertia - report.inertia) < 0.001f,
                "cargo deltas match a full measurement of mass, center and inertia");
            int version = report.version;
            stock.stackCount++;
            storage.NotifyItem(stock, true);
            stock.stackCount--;
            storage.NotifyItem(stock, true);
            Advance(1260);
            Check(ReferenceEquals(report, ShipPhysics.GetReport(frame)) && report.version == version,
                "net-zero inventory changes preserve the report and allocation version");

            var destinationFrame = new Hull(destination);
            var destinationReport = ShipPhysics.GetReport(destinationFrame);
            Set(stored, "_count", 0);
            storage.NotifyItem(stock, false);
            int moveDue = storage.DueTick;
            Advance(1300);
            storage.PostDeSpawn(source);
            source.listerThings.AllThings.Remove(replacement);
            Set(replacement, "mapIndexOrState", (sbyte)-1);
            SubstructureOwnership.For(source).UpdateSpawnedBuilding(replacement);
            destination.listerThings.AllThings.Add(replacement);
            Set(replacement, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(destination));
            SubstructureOwnership.For(destination).UpdateSpawnedBuilding(replacement);
            storage.PostSpawnSetup(false);
            storage.StorageReady();
            Check(ShipPhysics.GetReport(frame).mass == dryMass - ShipPhysics.MassPerBuilding + 16f
                && ShipPhysics.GetReport(destinationFrame).mass == ShipPhysics.MassPerBuilding + 22f
                && storage.DueTick == moveDue,
                "moving a dirty cabinet transfers the published mass and its original deadline immediately");
            Advance(1860);
            Check(ReferenceEquals(destinationReport, ShipPhysics.GetReport(destinationFrame))
                && destinationReport.mass == ShipPhysics.MassPerBuilding && storage.DueTick == -1,
                "only the new map applies the moved cabinet's net delta at the original deadline");

            Set(stored, "_count", 1);
            storage.NotifyItem(stock, true);
            storage.PostDestroy(DestroyMode.KillFinalize, destination);
            Advance(2460);
            Check(destinationReport.mass == ShipPhysics.GetReport(destinationFrame).mass && storage.PublishedMass == 0,
                "destroyed cabinet is removed from the due queue");
        }
        finally
        {
            patch.UnpatchAll(patch.Id);
            Set(Find.TickManager, "ticksGameInt", tick);
            TerrainDefOf.Space = oldSpace;
            StatDefOf.Mass = oldMass;
            Set(sealedCargo, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(source));
            foreach (Thing thing in sealedCargo.Contents)
                sealedCargo.GetDirectlyHeldThings().Remove(thing);
            source.listerThings.AllThings.Clear();
            ShipPhysics.Forget(source);
            ShipPhysics.Forget(destination);
            universe.ForgetFoundationTopology(source);
            universe.ForgetFoundationTopology(destination);
            Find.Maps.Remove(destination);
        }
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
            patch.CreateClassProcessor(typeof(Patch_ShipStructureInventory_Added)).Patch();
            patch.CreateClassProcessor(typeof(Patch_ShipStructureInventory_Removed)).Patch();
            patch.CreateClassProcessor(typeof(Patch_ShipStructureInventory_Split)).Patch();
            patch.CreateClassProcessor(typeof(Patch_ShipStructureInventory_Merge)).Patch();
            patch.Patch(AccessTools.Method(typeof(StatExtension), "GetStatValue",
                new[] { typeof(Thing), typeof(StatDef), typeof(bool), typeof(int) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(CargoMassStat)));
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
                Set(c, "comps", new List<ThingComp> { hack, comp, new CompStructureMass { parent = c } });
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
                CheckCargoMassBudget(cargoMap, cargoFrame, make(player, true));
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
