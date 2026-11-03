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
    // Exercise the real mode transaction and native ThingOwner. Only map placement,
    // ASF map bookkeeping and Unity selection are replaced in this headless fixture.
    static Selector cargoModeSelector;
    static bool failCargoModePlacement;
    sealed class ModeCrate : Building_SealedSpaceCargoContainer
    {
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish) => RemoveModeBuilding(this);
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish) => Set(this, "mapIndexOrState", (sbyte)-2);
        public override void SetFaction(Faction faction, Pawn recruiter = null) => SetFactionDirect(faction);
    }
    sealed class ModeStorage : Building_SpaceCargoContainer
    {
        protected override void OnDeSpawn(DestroyMode mode, SpawnMode spawnMode) => RemoveModeBuilding(this);
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish) => Set(this, "mapIndexOrState", (sbyte)-2);
    }
    sealed class ModeItem : ThingWithComps
    {
        public ModeStorage storage;
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Set(this, "mapIndexOrState", (sbyte)-1);
            if (storage != null)
            {
                SetModeStored(storage, storage.Contents.Where(t => t != this).ToArray());
                storage = null;
            }
        }
    }
    static void RemoveModeBuilding(Thing thing)
    {
        thing.Map.listerThings.AllThings.Remove(thing);
        Set(thing, "mapIndexOrState", (sbyte)-1);
    }
    static void SetModeStored(ModeStorage storage, Thing[] items)
    { Set(storage.StoredThings, "_things", items); Set(storage.StoredThings, "_count", items.Length); }
    static bool ModeSelector(ref Selector __result) { __result = cargoModeSelector; return false; }
    static bool MakeModeBuilding(ThingDef def, ref Thing __result)
    {
        if (def.defName != "DeltaV_CargoStorage" && def.defName != "DeltaV_CargoContainer") return true;
        ThingWithComps thing = def.defName == "DeltaV_CargoStorage" ? new ModeStorage() : new ModeCrate();
        thing.def = def; thing.thingIDNumber = nextId++; Set(thing, "mapIndexOrState", (sbyte)-1);
        var comps = new List<ThingComp> { new CompSpaceCargo { parent = thing, props = new CompProperties_SpaceCargo() } };
        if (thing is ModeCrate)
            comps.Add(new CompHackable { parent = thing, props = new CompProperties_Hackable() });
        if (thing is ModeStorage storage)
        {
            var contents = (ThingCollection)Raw(typeof(ThingCollection));
            Set(storage, "_storedThings", contents); SetModeStored(storage, Array.Empty<Thing>());
            Set(storage, "_currentSlotLimit", 48); Set(storage, "<TotalSlots>k__BackingField", 48);
            storage.settings = new StorageSettings();
        }
        Set(thing, "comps", comps); __result = thing; return false;
    }
    static bool SpawnModeBuilding(Thing newThing, IntVec3 loc, Map map, Rot4 rot, ref Thing __result)
    {
        if (newThing is not ModeCrate && newThing is not ModeStorage) return true;
        Set(newThing, "positionInt", loc); Set(newThing, "rotationInt", rot);
        Set(newThing, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        map.listerThings.AllThings.Add(newThing); __result = newThing; return false;
    }
    static bool ModeSlotLimit(ThingClass __instance, int value)
    {
        if (__instance is not ModeStorage) return true;
        Set(__instance, "_currentSlotLimit", value); return false;
    }
    static bool StoreModeItem(Building_SpaceCargoContainer __instance, Thing thing, ref bool __result)
    {
        if (__instance is not ModeStorage storage) return true;
        if (failCargoModePlacement) { failCargoModePlacement = false; __result = false; return false; }
        if (!CompSpaceCargo.IsLooseCargo(thing)) throw new Exception("Mode transfer did not detach the original item");
        SetModeStored(storage, storage.Contents.Append(thing).ToArray());
        Set(thing, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(storage.Map));
        ((ModeItem)thing).storage = storage; __result = true; return false;
    }

    static void CheckCargoModes()
    {
        var patch = new Harmony("DeltaV.Regression.CargoModes");
        var oldFactions = Find.World.factionManager;
        Find.World.factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(Find.World.factionManager, "ofPlayer", new Faction { def = new FactionDef { isPlayer = true } });
        var modeMap = (Map)Raw(typeof(Map)); modeMap.listerThings = new ListerThings(ListerThingsUse.Global);
        modeMap.designationManager = (DesignationManager)Raw(typeof(DesignationManager)); Find.Maps.Add(modeMap);
        var defs = new[] { MakeDef("DeltaV_CargoStorage"), MakeDef("DeltaV_CargoContainer") };
        foreach (var def in defs)
        { def.building.defaultStorageSettings = new StorageSettings(); DefDatabase<ThingDef>.Add(def); }
        cargoModeSelector = (Selector)Raw(typeof(Selector)); Set(cargoModeSelector, "selected", new List<object>());
        try
        {
            patch.Patch(typeof(ThingCollection).TypeInitializer, prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            patch.Patch(AccessTools.PropertyGetter(typeof(Find), "Selector"), prefix: new HarmonyMethod(typeof(Program), nameof(ModeSelector)));
            patch.Patch(AccessTools.Method(typeof(ThingMaker), "MakeThing"), prefix: new HarmonyMethod(typeof(Program), nameof(MakeModeBuilding)));
            patch.Patch(AccessTools.Method(typeof(GenSpawn), "Spawn", new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(SpawnModeBuilding)));
            patch.Patch(AccessTools.PropertySetter(typeof(ThingClass), "CurrentSlotLimit"), prefix: new HarmonyMethod(typeof(Program), nameof(ModeSlotLimit)));
            patch.Patch(AccessTools.Method(typeof(Building_SpaceCargoContainer), "TryStore"), prefix: new HarmonyMethod(typeof(Program), nameof(StoreModeItem)));
            patch.Patch(AccessTools.Method(typeof(CompHackable), "OnHacked"), prefix: new HarmonyMethod(typeof(Program), nameof(CargoHackedCallback)));
            patch.Patch(AccessTools.Method(typeof(DesignationManager), "RemoveAllDesignationsOn"), prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            patch.Patch(AccessTools.Method(typeof(Messages), "Message", new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            var crate = (ModeCrate)ThingMaker.MakeThing(defs[1]);
            GenSpawn.Spawn(crate, new IntVec3(10, 0, 10), modeMap, Rot4.East);
            crate.GetComp<CompHackable>().HackNow();
            var defItem = MakeDef("ModeCargoItem"); defItem.category = ThingCategory.Item; defItem.stackLimit = 75; defItem.virtualDefs = new List<ThingDef>();
            var item = new ModeItem { def = defItem, stackCount = 37, HitPoints = 23 };
            Set(item, "mapIndexOrState", (sbyte)-1); crate.TryStore(item);
            var terminal = new Building_SpaceTradeTerminal { def = MakeDef("ModeTerminal"), thingIDNumber = nextId++ }; modeMap.listerThings.AllThings.Add(terminal);
            Set(terminal, "deliveryCargo", crate);
            var change = AccessTools.Method(typeof(CompSpaceCargo), "SetStorageMode");
            CompSpaceCargo switchTo(ThingWithComps thing, bool storage)
                => (CompSpaceCargo)change.Invoke(thing.GetComp<CompSpaceCargo>(), new object[] { storage });
            var asf = (ModeStorage)switchTo(crate, true).parent;
            Check(item.Spawned && item.ParentHolder == null && ReferenceEquals(asf.Contents.Single(), item)
                && item.stackCount == 37 && item.HitPoints == 23 && crate.Destroyed,
                "unsealing transfers the same native-held stack to ASF without opening or copying it");
            Check(ReferenceEquals(AccessTools.Field(terminal.GetType(), "deliveryCargo").GetValue(terminal), asf),
                "mode conversion preserves terminal binding through the actual replacement building");
            asf.settings.Priority = StoragePriority.Critical; asf.settings.filter.SetAllow(defItem, true);
            Set(asf, "_currentSlotLimit", 24); asf.label = "Configured bay";
            var sealedAgain = (ModeCrate)switchTo(asf, false).parent;
            Check(!item.Spawned && item.ParentHolder == sealedAgain && sealedAgain.IsUnlocked && sealedAgain.Contents.Count == 1,
                "sealing removes ASF map items and puts the same stack in the native crate");
            Check(sealedAgain.CurrentSlotLimit == 24 && sealedAgain.Position == new IntVec3(10, 0, 10)
                && sealedAgain.Rotation == Rot4.East, "mode conversion preserves capacity and placement");
            failCargoModePlacement = true;
            Check(ReferenceEquals(switchTo(sealedAgain, true).parent, sealedAgain),
                "a full target cancels the mode change without replacing the source");
            Check(sealedAgain.Spawned && !item.Spawned && item.ParentHolder == sealedAgain
                && ReferenceEquals(AccessTools.Field(terminal.GetType(), "deliveryCargo").GetValue(terminal), sealedAgain),
                "failed unsealing restores the original crate, inventory and terminal binding");
            var restored = (ModeStorage)switchTo(sealedAgain, true).parent;
            Check(restored.settings.Priority == StoragePriority.Critical && restored.settings.filter.Allows(defItem)
                && restored.CurrentSlotLimit == 24 && restored.label == "Configured bay",
                "sealing round trip preserves ASF filtering, priority, slot limit and label");
        }
        finally
        {
            patch.UnpatchAll(patch.Id); Find.Maps.Remove(modeMap); cargoModeSelector = null;
            foreach (var def in defs) AccessTools.Method(typeof(DefDatabase<ThingDef>), "Remove").Invoke(null, new object[] { def });
            failCargoModePlacement = false;
            Find.World.factionManager = oldFactions;
        }
    }
}
