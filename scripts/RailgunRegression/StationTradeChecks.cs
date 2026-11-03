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
    sealed class StationItem : Thing
    {
        public bool discarded;
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            discarded = true;
            holdingOwner?.Remove(this);
        }
    }

    // Run real terminal binding, goods filtering and timed replacement. Replace only
    // foundation geometry, stock generation and native crate inventory in the headless map.
    static void CheckStationTrade()
    {
        var patch = new Harmony("DeltaV.Regression.StationTrade");
        var oldFactions = Find.World.factionManager;
        var oldMaker = ThingSetMakerDefOf.TraderStock;
        var oldIds = Current.Game.uniqueIDsManager;
        Current.Game.uniqueIDsManager = new UniqueIDsManager();
        int oldTick = Find.TickManager.TicksGame;
        bool oldGod = DebugSettings.godMode;
        var player = new Faction { def = new FactionDef { isPlayer = true } };
        var npc = new Faction { def = new FactionDef() };
        var stationMap = (Map)Raw(typeof(Map));
        stationMap.info = new MapInfo();
        stationMap.listerThings = new ListerThings(ListerThingsUse.Global);
        Find.Maps.Add(stationMap);
        stationMap.designationManager = (DesignationManager)Raw(typeof(DesignationManager));
        Find.World.factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(Find.World.factionManager, "ofPlayer", player);
        object call(Building_SpaceTradeTerminal t, string name, params object[] args)
            => AccessTools.Method(typeof(Building_SpaceTradeTerminal), name).Invoke(t, args);
        bool bind(Building_SpaceTradeTerminal t, Building_SealedSpaceCargoContainer c, bool delivery)
            => ((AcceptanceReport)call(t, "SetCargoBinding", c, delivery)).Accepted;
        T spawn<T>(T thing, int x) where T : Thing
        {
            thing.def = MakeDef("stationFixture" + x); thing.thingIDNumber = nextId++;
            Set(thing, "positionInt", new IntVec3(x, 0, 10));
            Set(thing, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(stationMap));
            thing.SetFactionDirect(player); stationMap.listerThings.AllThings.Add(thing);
            stationCells.Add(thing.Position); return thing;
        }
        Building_SealedSpaceCargoContainer cargo(int x, bool unlocked)
        {
            var c = spawn(new Building_SealedSpaceCargoContainer(), x);
            var hack = new CompHackable { parent = c, props = new CompProperties_Hackable() };
            Set(hack, "hacked", unlocked); Set(c, "comps", new List<ThingComp> { hack, new CompSpaceCargo { parent = c } });
            return c;
        }
        StationItem item()
        {
            var def = MakeDef("stationItem"); def.category = ThingCategory.Item;
            def.stackLimit = 1; def.thingClass = typeof(StationItem); def.useHitPoints = false;
            return new StationItem { def = def, stackCount = 1 };
        }
        try
        {
            patch.Patch(AccessTools.Method(typeof(DesignationManager), "RemoveAllDesignationsOn"),
                prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            DebugSettings.godMode = false;
            var oversize = item(); oversize.stackCount = 2;
            Check(!new Building_SealedSpaceCargoContainer().TryStore(oversize) && oversize.stackCount == 2,
                "cargo rejects an oversized input without spawning, truncating or consuming it");
            patch.Patch(AccessTools.Method(typeof(ShipRegistry), nameof(ShipRegistry.AllShipsOnMap)),
                prefix: new HarmonyMethod(typeof(Program), nameof(TradeFixtureShips)));
            patch.Patch(AccessTools.Method(typeof(SubstructureOwnership), "CollectConnected"),
                prefix: new HarmonyMethod(typeof(Program), nameof(StationConnections)));
            patch.Patch(AccessTools.Method(typeof(ThingSetMaker), "Generate", new[] { typeof(ThingSetMakerParams) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(GenerateStationStock)));
            ThingSetMakerDefOf.TraderStock = new ThingSetMakerDef { root = new ThingSetMaker_TraderStock() };
            var terminal = spawn(new Building_SpaceTradeTerminal(), 1);
            var second = spawn(new Building_SpaceTradeTerminal(), 2);
            var kind = new TraderKindDef { defName = "fixtureTrader" };
            Set(terminal, "traderKind", kind);
            var a = cargo(3, false); var b = cargo(4, false);
            var pickup = cargo(5, true); var replacement = cargo(6, true); var unbound = cargo(7, false);
            Check(!bind(terminal, a, false), "normal mode cannot configure even a player-owned terminal");
            call(terminal, "ToggleIndiscriminateSupply");
            Check(!(bool)AccessTools.Field(terminal.GetType(), "indiscriminateSupply").GetValue(terminal),
                "normal mode cannot enable indiscriminate supply");
            var placement = new PlaceWorker_TradeTerminal();
            Check(!placement.IsBuildDesignatorVisible(terminal.def)
                && !placement.AllowsPlacing(terminal.def, terminal.Position, Rot4.North, stationMap).Accepted,
                "normal mode hides terminal construction and rejects a retained placement tool");
            DebugSettings.godMode = true;
            Check(placement.IsBuildDesignatorVisible(terminal.def)
                && placement.AllowsPlacing(terminal.def, terminal.Position, Rot4.North, stationMap).Accepted,
                "God Mode permits native terminal placement");
            Check(bind(terminal, a, false) && bind(terminal, b, false), "God Mode binds multiple locked supplies without a dock");
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
            call(terminal, "RegenerateStock");
            Check(stationGenerations == 0 && !oldA.discarded, "God Mode alone cannot bypass player supply restrictions in the generator");
            terminal.SetFactionDirect(npc);
            a.SetFactionDirect(npc); b.SetFactionDirect(npc);
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
            DebugSettings.godMode = false;
            Check(!bind(terminal, b, false), "normal mode cannot reconfigure an NPC terminal");
            DebugSettings.godMode = true;
            Check(bind(terminal, b, false), "God Mode can edit NPC station bindings");
            terminal.SetFactionDirect(player);
            call(terminal, "ToggleIndiscriminateSupply");
            DebugSettings.godMode = false;
            Set(Find.TickManager, "ticksGameInt", oldTick + 30 * GenDate.TicksPerDay);
            call(terminal, "CheckRestock");
            Check(stationGenerations == 2, "explicit indiscriminate supply permits scheduled player restock after leaving God Mode");
            call(terminal, "ToggleIndiscriminateSupply");
            Check((bool)AccessTools.Field(terminal.GetType(), "indiscriminateSupply").GetValue(terminal),
                "normal mode cannot alter an enabled debug supply setting");
            DebugSettings.godMode = true;
            call(terminal, "RegenerateStock");
            Check(stationGenerations == 3, "explicit indiscriminate supply also permits manual regeneration");
            call(terminal, "ToggleIndiscriminateSupply");
            call(terminal, "RegenerateStock");
            Check(stationGenerations == 3, "disabling indiscriminate supply immediately blocks player regeneration");
            terminal.SetFactionDirect(npc);
            CheckTradeShipSelection(terminal, stationMap, player, npc, patch);
            StationItem bulk(int count, int limit)
            {
                var result = item(); result.stackCount = count; result.def.stackLimit = limit;
                return result;
            }
            var silver = bulk(3739, 500); var marble = bulk(915, 75); var meals = bulk(24, 10);
            generatedStationStock = new List<Thing> { silver, marble, meals };
            call(terminal, "RegenerateStock");
            Check(terminal.Goods.Where(t => t.def == silver.def).Sum(t => t.stackCount) == 3739
                && terminal.Goods.Where(t => t.def == marble.def).Sum(t => t.stackCount) == 915
                && terminal.Goods.Where(t => t.def == meals.def).Sum(t => t.stackCount) == 24,
                "logged silver, marble and meal quantities survive native SplitOff without truncation");
            Check(terminal.Goods.Count() == 24 && terminal.Goods.All(t => t.stackCount > 0 && t.stackCount <= t.def.stackLimit),
                "merchant totals become legal physical stacks including partial remainders");
            Check(bind(terminal, b, false), "second supply bay can be restored for bulk distribution");
            var across = bulk(34 * 75 + 1, 75);
            generatedStationStock = new List<Thing> { across };
            call(terminal, "RegenerateStock");
            Check(a.Contents.Count == 32 && b.Contents.Count == 3 && terminal.Goods.Sum(t => t.stackCount) == 2551,
                "split stock continues into the next bound bay while reserving sixteen sale slots per bay");
            var overflow = bulk(100 * 75, 75);
            generatedStationStock = new List<Thing> { overflow };
            call(terminal, "RegenerateStock");
            Check(a.Contents.Count == 32 && b.Contents.Count == 32
                && terminal.Goods.Sum(t => t.stackCount) == 64 * 75 && overflow.discarded && overflow.stackCount == 36 * 75,
                "full supply bays discard only unstored stock and preserve their reserved sale capacity");
            Check(!purchased.discarded && !untouched.discarded,
                "bulk restocking still preserves collection and unbound cargo");
        }
        finally
        {
            patch.UnpatchAll(patch.Id); Find.Maps.Remove(stationMap); stationCells.Clear();
            Find.World.factionManager = oldFactions; ThingSetMakerDefOf.TraderStock = oldMaker;
            Current.Game.uniqueIDsManager = oldIds;
            Set(Find.TickManager, "ticksGameInt", oldTick); DebugSettings.godMode = oldGod;
            generatedStationStock = null;
        }
    }
}
