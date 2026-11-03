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
    static readonly HashSet<IntVec3> stationCells = new();
    static List<Thing> generatedStationStock;
    static ThingSetMakerParams stationStockParams;
    static int stationGenerations;
    static bool StationConnections(ICollection<IntVec3> result)
    { result.Clear(); foreach (var c in stationCells) result.Add(c); return false; }
    static bool GenerateStationStock(ThingSetMakerParams parms, ref List<Thing> __result)
    { stationStockParams = parms; stationGenerations++; __result = generatedStationStock; return false; }
    static bool StoreStationStock(Building_SpaceCargoContainer __instance, Thing thing, ref bool __result)
    {
        var items = __instance.Contents.ToArray().Append(thing).ToArray();
        Set(__instance.StoredThings, "_things", items); Set(__instance.StoredThings, "_count", items.Length);
        ((StationItem)thing).owner = __instance;
        __result = true; return false;
    }
    sealed class StationItem : Thing
    {
        public Building_SpaceCargoContainer owner;
        public bool discarded;
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            discarded = true;
            if (owner == null) return;
            var items = owner.Contents.ToArray().Where(t => t != this).ToArray();
            Set(owner.StoredThings, "_things", items); Set(owner.StoredThings, "_count", items.Length);
        }
    }

    // Run real terminal binding, goods filtering and timed replacement. Replace only
    // foundation geometry, stock generation and physical ASF placement in the headless map.
    static void CheckStationTrade()
    {
        var patch = new Harmony("DeltaV.Regression.StationTrade");
        var oldFactions = Find.World.factionManager;
        var oldMaker = ThingSetMakerDefOf.TraderStock;
        int oldTick = Find.TickManager.TicksGame;
        bool oldGod = DebugSettings.godMode;
        var player = new Faction { def = new FactionDef { isPlayer = true } };
        var npc = new Faction { def = new FactionDef() };
        var stationMap = (Map)Raw(typeof(Map));
        stationMap.info = new MapInfo();
        stationMap.listerThings = new ListerThings(ListerThingsUse.Global);
        Find.Maps.Add(stationMap);
        Find.World.factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(Find.World.factionManager, "ofPlayer", player);
        object call(Building_SpaceTradeTerminal t, string name, params object[] args)
            => AccessTools.Method(typeof(Building_SpaceTradeTerminal), name).Invoke(t, args);
        bool bind(Building_SpaceTradeTerminal t, Building_SpaceCargoContainer c, bool delivery)
            => ((AcceptanceReport)call(t, "SetCargoBinding", c, delivery)).Accepted;
        T spawn<T>(T thing, int x) where T : Thing
        {
            thing.def = MakeDef("stationFixture" + x); thing.thingIDNumber = nextId++;
            Set(thing, "positionInt", new IntVec3(x, 0, 10));
            Set(thing, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(stationMap));
            thing.SetFactionDirect(player); stationMap.listerThings.AllThings.Add(thing);
            stationCells.Add(thing.Position); return thing;
        }
        Building_SpaceCargoContainer cargo(int x, bool unlocked)
        {
            var c = spawn(new Building_SpaceCargoContainer(), x);
            var hack = new CompHackable { parent = c, props = new CompProperties_Hackable() };
            Set(hack, "hacked", unlocked); Set(c, "comps", new List<ThingComp> { hack });
            var contents = (ThingCollection)Raw(typeof(ThingCollection));
            Set(contents, "_things", Array.Empty<Thing>()); Set(c, "_storedThings", contents);
            Set(c, "_currentSlotLimit", 48); Set(c, "<TotalSlots>k__BackingField", 48);
            return c;
        }
        StationItem item()
        {
            var def = MakeDef("stationItem"); def.category = ThingCategory.Item;
            return new StationItem { def = def, stackCount = 1 };
        }
        try
        {
            DebugSettings.godMode = false;
            patch.Patch(AccessTools.Method(typeof(SubstructureOwnership), "CollectConnected"),
                prefix: new HarmonyMethod(typeof(Program), nameof(StationConnections)));
            patch.Patch(AccessTools.Method(typeof(Building_SpaceCargoContainer), "TryStore"),
                prefix: new HarmonyMethod(typeof(Program), nameof(StoreStationStock)));
            patch.Patch(AccessTools.Method(typeof(ThingSetMaker), "Generate", new[] { typeof(ThingSetMakerParams) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(GenerateStationStock)));
            ThingSetMakerDefOf.TraderStock = new ThingSetMakerDef { root = new ThingSetMaker_TraderStock() };
            var terminal = spawn(new Building_SpaceTradeTerminal(), 1);
            var second = spawn(new Building_SpaceTradeTerminal(), 2);
            var kind = new TraderKindDef { defName = "fixtureTrader" };
            Set(terminal, "traderKind", kind);
            var a = cargo(3, false); var b = cargo(4, false);
            var pickup = cargo(5, true); var replacement = cargo(6, true); var unbound = cargo(7, false);
            Check(bind(terminal, a, false) && bind(terminal, b, false), "own terminal binds multiple locked supplies without a dock");
            Check(!bind(terminal, pickup, false) && !bind(terminal, a, true), "cargo lock state constrains each binding role");
            Check(bind(terminal, pickup, true) && bind(terminal, replacement, true)
                && ReferenceEquals(AccessTools.Field(terminal.GetType(), "deliveryCargo").GetValue(terminal), replacement),
                "binding another collection bay replaces the unique reference");
            Check(!bind(second, a, false) && !bind(second, replacement, true), "two terminals cannot share a supply or collection bay");
            var oldA = item(); var oldB = item(); var purchased = item(); var untouched = item();
            a.TryStore(oldA); b.TryStore(oldB); replacement.TryStore(purchased); unbound.TryStore(untouched);
            Check(terminal.Goods.SequenceEqual(new[] { oldA, oldB }), "trader goods include all bound supplies and exclude collection and unbound cargo");
            Set(a.GetComp<CompHackable>(), "hacked", true);
            Check(terminal.Goods.SequenceEqual(new[] { oldB }), "hacked supply immediately stops contributing trade stock");
            Set(a.GetComp<CompHackable>(), "hacked", false);
            stationCells.Remove(b.Position);
            Check(terminal.Goods.SequenceEqual(new[] { oldA }), "detached supply immediately stops contributing trade stock");
            stationCells.Add(b.Position);
            Check(((AcceptanceReport)call(terminal, "StockConfigurationReport")).Accepted && !terminal.CanTradeNow,
                "valid authored configuration does not require docking, but trading does");
            generatedStationStock = new List<Thing> { item(), item() }; stationGenerations = 0;
            call(terminal, "CheckRestock");
            Check(stationGenerations == 0 && !oldA.discarded, "player construction does not passively create merchant stock");
            terminal.SetFactionDirect(npc);
            call(terminal, "CheckRestock");
            Check(stationGenerations == 0 && terminal.Goods.Count() == 2, "first NPC restock check preserves pre-authored physical stock");
            Set(Find.TickManager, "ticksGameInt", oldTick + 15 * GenDate.TicksPerDay - 1);
            call(terminal, "CheckRestock");
            Check(stationGenerations == 0, "NPC stock persists until its configured game-tick deadline");
            Set(Find.TickManager, "ticksGameInt", oldTick + 15 * GenDate.TicksPerDay);
            call(terminal, "CheckRestock");
            Check(stationGenerations == 1 && stationStockParams.traderDef == kind && stationStockParams.makingFaction == npc,
                "due restock invokes the vanilla stock generator with the selected trader and station faction");
            Check(oldA.discarded && oldB.discarded && terminal.Goods.SequenceEqual(generatedStationStock),
                "periodic refresh replaces physical stock across bound supplies");
            Check(!purchased.discarded && replacement.Contents.ToArray().Contains(purchased) && !untouched.discarded,
                "restock preserves collected purchases and all unbound cargo");
            call(terminal, "CheckRestock");
            Check(stationGenerations == 1, "restock advances the deadline and cannot regenerate repeatedly in the same tick");
            Check(!bind(terminal, b, false), "normal mode cannot reconfigure an NPC terminal");
            DebugSettings.godMode = true;
            Check(bind(terminal, b, false), "God Mode can edit NPC station bindings");
        }
        finally
        {
            patch.UnpatchAll(patch.Id); Find.Maps.Remove(stationMap); stationCells.Clear();
            Find.World.factionManager = oldFactions; ThingSetMakerDefOf.TraderStock = oldMaker;
            Set(Find.TickManager, "ticksGameInt", oldTick); DebugSettings.godMode = oldGod;
            generatedStationStock = null;
        }
    }
}
