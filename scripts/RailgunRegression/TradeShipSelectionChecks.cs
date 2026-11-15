using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    static readonly Dictionary<Map, List<ShipBase>> tradeFixtureShips = new();
    static readonly Dictionary<int, HashSet<IntVec3>> tradeFixtureDomains = new();
    static bool TradeFixtureShips(Map map, ref IEnumerable<ShipBase> __result)
    { __result = tradeFixtureShips.TryGetValue(map, out var ships) ? ships : Array.Empty<ShipBase>(); return false; }
    static bool TradeFixtureOwner(Thing thing, ref Ship __result)
    {
        if (thing?.MapHeld is not Map map || !tradeFixtureShips.TryGetValue(map, out var ships)) return true;
        __result = Ship.Invalid;
        foreach (var vessel in ships.OfType<FoundationShip>())
        {
            Ship ship = vessel.Structure;
            if (!ship.IsValid || !tradeFixtureDomains[ship.ownerId].Contains(thing.PositionHeld)) continue;
            __result = ship;
            break;
        }
        return false;
    }

    static readonly Dictionary<Building_OrbitalTradeBeacon, List<IntVec3>> beaconCells = new();
    static readonly HashSet<Thing> unsellableTradeItems = new();
    static bool FixtureBeaconCells(Building_OrbitalTradeBeacon __instance, ref IEnumerable<IntVec3> __result)
    { __result = beaconCells[__instance]; return false; }
    static bool FixturePlayerSellable(Thing t, ref bool __result)
    { __result = !unsellableTradeItems.Contains(t); return false; }
    static bool FixtureShipLabel(FoundationShip __instance, ref string __result)
    { __result = "Ship " + __instance.Identity.key; return false; }

    // Compile this caller only after installing the patch; otherwise the tiny vanilla
    // iterator factory can be inlined while the enclosing setup method is first JIT-compiled.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static IEnumerable<Thing> BeaconStock(Map map, ITrader trader = null)
        => TradeUtility.AllLaunchableThingsForTrade(map, trader);

    // Real AllPowered and AllLaunchableThingsForTrade select items from actual thing grids.
    // Replace room geometry, price-based sellability, and ship topology in this headless map.
    static void CheckTradeShipSelection(Building_SpaceTradeTerminal terminal, Map stationMap,
        Faction player, Faction npc, Harmony patch)
    {
        var maps = new List<Map>();
        var oldTrader = TradeSession.trader; var oldNegotiator = TradeSession.playerNegotiator;
        object oldService = AccessTools.Field(typeof(UniverseManager), "stationTrade").GetValue(universe);
        var service = universe.StationTrade;
        object invoke(object target, string name, params object[] args)
            => AccessTools.Method(target.GetType(), name).Invoke(target, args);
        void initGrid(Map m)
        {
            m.info ??= new MapInfo(); m.info.Size = new IntVec3(240, 1, 32);
            m.cellIndices = new CellIndices(m); m.thingGrid = new ThingGrid(m);
            m.listerBuildings = new ListerBuildings();
            m.designationManager = (DesignationManager)Raw(typeof(DesignationManager));
            m.haulDestinationManager = new HaulDestinationManager(m);
        }
        Map makeMap()
        {
            var m = (Map)Raw(typeof(Map)); m.listerThings = new ListerThings(ListerThingsUse.Global);
            Find.Maps.Add(m); maps.Add(m); initGrid(m); return m;
        }
        T place<T>(T thing, Map m, int x, Faction faction) where T : Thing
        {
            thing.def ??= MakeDef("tradeFixture" + nextId); thing.thingIDNumber = nextId++;
            Set(thing, "positionInt", new IntVec3(x, 0, 10));
            Set(thing, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(m)); thing.SetFactionDirect(faction);
            m.listerThings.AllThings.Add(thing); m.thingGrid.Register(thing); return thing;
        }
        FoundationShip ship(Map m, int x, Faction faction)
        {
            var console = place(new Building(), m, x, faction);
            var comp = new CompDeltaVPilotConsole { parent = console, isMain = true, shipId = nextId++ };
            Set(console, "comps", new List<ThingComp> { comp });
            var result = new FoundationShip(new Ship(comp.shipId, m, comp));
            if (!tradeFixtureShips.TryGetValue(m, out var ships)) tradeFixtureShips[m] = ships = new();
            ships.Add(result); tradeFixtureDomains[comp.shipId] = new HashSet<IntVec3> { console.Position };
            return result;
        }
        Thing goods(FoundationShip owner, int x, string name)
        {
            var def = MakeDef(name); def.category = ThingCategory.Item;
            return place(new Thing { def = def, stackCount = 75 }, owner.Map, x, null);
        }
        Building_DockingPort dock(FoundationShip owner, int x, Faction portFaction)
        {
            var peer = place(new Building_DockingPort(), owner.Map, x, portFaction);
            Set(peer, "comps", new List<ThingComp> { new CompSoftDocking { parent = peer } });
            tradeFixtureDomains[owner.Structure.ownerId].Add(peer.Position);
            var port = place(new Building_DockingPort(), stationMap, x + 100, npc);
            var comp = new CompSoftDocking { parent = port };
            Set(port, "comps", new List<ThingComp> { comp });
            Set(comp, "peer", peer); Set(comp, "active", true); Set(comp, "connected", true);
            Set(peer.GetComp<CompSoftDocking>(), "incomingOwner", comp);
            stationCells.Add(port.Position); return port;
        }
        Building_OrbitalTradeBeacon beacon(Map m, int x, Faction faction, params Thing[] items)
        {
            var b = place(new Building_OrbitalTradeBeacon(), m, x, faction);
            var power = new CompPowerTrader { parent = b };
            Set(power, "powerOnInt", true); Set(b, "comps", new List<ThingComp> { power });
            (faction == player ? m.listerBuildings.allBuildingsColonist : m.listerBuildings.allBuildingsNonColonist).Add(b);
            beaconCells[b] = items.Select(t => t.Position).ToList(); return b;
        }
        bool validDeal(TradeDeal deal) => terminal.DealStillValid(deal, out _);
        try
        {
            initGrid(stationMap);
            patch.Patch(AccessTools.PropertyGetter(typeof(FoundationShip), "Label"),
                prefix: new HarmonyMethod(typeof(Program), nameof(FixtureShipLabel)));
            patch.Patch(AccessTools.PropertyGetter(typeof(Building_OrbitalTradeBeacon), "TradeableCells"),
                prefix: new HarmonyMethod(typeof(Program), nameof(FixtureBeaconCells)));
            patch.Patch(AccessTools.Method(typeof(TradeUtility), "PlayerSellableNow"),
                prefix: new HarmonyMethod(typeof(Program), nameof(FixturePlayerSellable)));
            patch.Patch(AccessTools.Method(typeof(ShipRegistry), nameof(ShipRegistry.ShipByThing)),
                prefix: new HarmonyMethod(typeof(Program), nameof(TradeFixtureOwner)));
            var hardA = ship(stationMap, 20, player); var hardB = ship(stationMap, 30, player);
            var host = ship(stationMap, 40, player);
            tradeFixtureDomains[host.Structure.ownerId].Add(terminal.Position);
            var enemy = ship(makeMap(), 50, npc); dock(enemy, 51, player);
            var softA = ship(makeMap(), 60, player); var portA = dock(softA, 61, npc);
            var extraPort = dock(softA, 62, npc);
            var softB = ship(makeMap(), 70, player); var portB = dock(softB, 71, player);
            ship(makeMap(), 80, player);
            var sideShip = ship(softA.Map, 90, player);
            var sources = terminal.TradeSources().ToDictionary(group => group.Key, group => group.Select(s => s.Identity).ToArray());
            Check(sources.Count == 3 && sources.TryGetValue(stationMap, out var localShips)
                && localShips.Length == 3 && localShips.ToHashSet().SetEquals(new[] { hardA.Identity, hardB.Identity, host.Identity })
                && sources.TryGetValue(softA.Map, out var remoteShips) && remoteShips.SequenceEqual(new[] { softA.Identity })
                && sources.TryGetValue(softB.Map, out var otherShips) && otherShips.SequenceEqual(new[] { softB.Identity }),
                "trade sources group same-map ships, deduplicate soft ports and exclude ineligible ships");
            int beforeRefresh = stationGenerations;
            invoke(terminal, "RegenerateStock");
            Check(stationGenerations == beforeRefresh,
                "NPC terminal welded onto a player ship cannot regenerate even in God Mode");
            host.Anchor.SetFactionDirect(npc);
            Check(((AcceptanceReport)invoke(terminal, "RestockPermissionReport")).Accepted,
                "NPC station can restock with unrelated player ships hard docked on the same map");
            host.Anchor.SetFactionDirect(player);
            tradeFixtureDomains[host.Structure.ownerId].Remove(terminal.Position);
            var supply = (Building_SealedSpaceCargoContainer)terminal.Goods.First().ParentHolder;
            tradeFixtureDomains[host.Structure.ownerId].Add(supply.Position);
            invoke(terminal, "RegenerateStock");
            Check(stationGenerations == beforeRefresh,
                "NPC terminal cannot refresh an NPC cargo bay transferred to a player ship");
            tradeFixtureDomains[host.Structure.ownerId].Remove(supply.Position);
            tradeFixtureDomains[host.Structure.ownerId].Add(terminal.Position);
            stationCells.Remove(portB.Position);
            Check(!terminal.TradeShips().Any(s => s.Identity == softB.Identity),
                "a station-side port on a disconnected structure is excluded");
            stationCells.Add(portB.Position);

            var pawn = place((Pawn)Raw(typeof(Pawn)), stationMap, 2, player);
            var otherPawn = place((Pawn)Raw(typeof(Pawn)), stationMap, 3, player);
            var silverA = goods(hardA, 21, "Silver"); var cargoA = goods(hardA, 22, "Steel");
            var silverB = goods(hardB, 31, "Silver"); var hostSilver = goods(host, 41, "Silver");
            var softSilver = goods(softA, 63, "Silver"); var sideGoods = goods(sideShip, 91, "Steel");
            var playerBeacon = beacon(stationMap, 101, player, silverA, cargoA, silverB);
            beacon(stationMap, 102, player, silverA);
            beacon(stationMap, 103, npc, hostSilver);
            beacon(softA.Map, 104, player, softSilver, sideGoods);
            TradeSession.trader = terminal; TradeSession.playerNegotiator = pawn;
            var selectedA = service.Begin(terminal, pawn, stationMap);
            var localGoods = terminal.ColonyThingsWillingToBuy(pawn).ToArray();
            Check(selectedA != null && localGoods.Length == 3
                && localGoods.ToHashSet().SetEquals(new[] { silverA, cargoA, silverB }),
                "selected map exposes only player beacon goods and lists overlapping stock once");
            Check(!terminal.ColonyThingsWillingToBuy(otherPawn).Any(), "source belongs to its original negotiator");
            var deal = (TradeDeal)Raw(typeof(TradeDeal)); Set(deal, "tradeables", new List<Tradeable>());
            var row = new Tradeable(); row.thingsColony.Add(cargoA);
            Set(row, "countToTransfer", -1); deal.AllTradeables.Add(row);
            Check(validDeal(deal), "beacon-covered sale passes final quantity and capacity checks");
            var sealedBay = place(new Building_SealedSpaceCargoContainer(), stationMap, 22, player);
            var hack = new CompHackable { parent = sealedBay, props = new CompProperties_Hackable() };
            Set(sealedBay, "comps", new List<ThingComp> { hack, new CompSpaceCargo { parent = sealedBay } });
            IntVec3 cargoCell = cargoA.Position;
            stationMap.thingGrid.Deregister(cargoA);
            Set(cargoA, "mapIndexOrState", (sbyte)-1);
            cargoA.def.stackLimit = 75;
            if (!sealedBay.TryStore(cargoA)) throw new InvalidOperationException("Could not stock the sealed trade fixture.");
            Check(!BeaconStock(stationMap, new TradeShip()).Contains(cargoA)
                && !terminal.ColonyThingsWillingToBuy(pawn).Contains(cargoA) && !validDeal(deal),
                "native beacon lists cannot see locked crate contents, without a filtering patch");
            Set(hack, "hacked", true);
            Check(!terminal.ColonyThingsWillingToBuy(pawn).Contains(cargoA) && !validDeal(deal),
                "unlocking the crate leaves its contents ineligible for sale");
            if (sealedBay.Take(cargoA, cargoA.stackCount) == null)
                throw new InvalidOperationException("Could not empty the sealed trade fixture.");
            Set(cargoA, "positionInt", cargoCell);
            Set(cargoA, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(stationMap));
            stationMap.thingGrid.Register(cargoA);
            Check(BeaconStock(stationMap, new TradeShip()).Contains(cargoA) && validDeal(deal),
                "returning the original stack to map storage restores native beacon availability");
            Set(playerBeacon.GetComp<CompPowerTrader>(), "powerOnInt", false);
            Check(terminal.ColonyThingsWillingToBuy(pawn).SequenceEqual(new[] { silverA }) && !validDeal(deal),
                "power loss removes uncovered stock and rejects an already configured sale");
            Set(playerBeacon.GetComp<CompPowerTrader>(), "powerOnInt", true);
            unsellableTradeItems.Add(cargoA);
            Check(!terminal.ColonyThingsWillingToBuy(pawn).Contains(cargoA) && !validDeal(deal),
                "native launchable enumeration respects PlayerSellableNow at opening and execution");
            unsellableTradeItems.Clear();
            beaconCells[playerBeacon].Remove(cargoA.Position);
            Check(!validDeal(deal), "leaving beacon coverage rejects a pending sale");
            beaconCells[playerBeacon].Add(cargoA.Position);
            var merchantItem = place(terminal.Goods.First(), stationMap, 44, null);
            beaconCells[playerBeacon].Add(merchantItem.Position);
            Check(!terminal.ColonyThingsWillingToBuy(pawn).Contains(merchantItem),
                "merchant inventory is not duplicated onto the player side even under a player beacon");
            hardA.Anchor.SetFactionDirect(npc);
            Check(validDeal(deal), "map selection remains valid while another eligible player ship stays on the same map");
            hardA.Anchor.SetFactionDirect(player);
            var selectedSoft = service.Begin(terminal, pawn, softA.Map);
            Check(selectedSoft != null && terminal.ColonyThingsWillingToBuy(pawn).ToHashSet().SetEquals(new[] { softSilver, sideGoods }),
                "soft-docked source uses all player beacon goods on its map rather than one ship domain");
            service.End(selectedA);
            row.thingsColony.Clear();
            row.thingsColony.Add(softSilver);
            Check(service.MapFor(terminal, pawn) == softA.Map && validDeal(deal),
                "closing an older window preserves the newer source and its pending sale");
            softA.Structure.console.isMain = false;
            Check(!validDeal(deal), "losing the primary console invalidates a pending sale");
            softA.Structure.console.isMain = true;
            softA.Anchor.SetFactionDirect(npc);
            Check(!validDeal(deal), "source loses eligibility when its last connected player console changes faction");
            softA.Anchor.SetFactionDirect(player);
            Check(validDeal(deal), "restoring source eligibility revalidates the pending sale");
            Set(portA.Docking, "connected", false);
            Set(extraPort.Docking, "connected", false);
            Check(!terminal.ColonyThingsWillingToBuy(pawn).Any() && !validDeal(deal),
                "soft disconnection invalidates the source map without falling back to another map");
            Check(service.Begin(terminal, pawn, softA.Map) == null,
                "a map disconnected after the source dialog opened cannot start a trade");
            var localSelection = service.Begin(terminal, pawn, stationMap);
            Check(localSelection != null && service.MapFor(terminal, pawn) == stationMap,
                "another eligible map can still be selected after the old source disconnects");
            service.Forget(stationMap);
            Check(service.MapFor(terminal, pawn) == null, "map release clears the trade session");
            var final = service.Begin(terminal, pawn, stationMap);
            if (final == null) throw new InvalidOperationException("Could not reopen the trade fixture.");
            service.End(final);
            Check(service.MapFor(terminal, pawn) == null, "closing trade clears the selected source map");

        }
        finally
        {
            TradeSession.trader = oldTrader; TradeSession.playerNegotiator = oldNegotiator;
            Set(universe, "stationTrade", oldService);
            foreach (Map m in maps) Find.Maps.Remove(m);
            tradeFixtureShips.Clear(); tradeFixtureDomains.Clear(); beaconCells.Clear(); unsellableTradeItems.Clear();
        }
    }
}
