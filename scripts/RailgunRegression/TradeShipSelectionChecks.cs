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
    static bool TradeFixtureDomain(Ship ship, ref HashSet<IntVec3> __result)
    { __result = tradeFixtureDomains[ship.ownerId]; return false; }

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
        var service = AccessTools.Property(typeof(UniverseManager), "StationTrade").GetValue(universe);
        object invoke(object target, string name, params object[] args)
            => AccessTools.Method(target.GetType(), name).Invoke(target, args);
        List<ShipBase> candidates() => ((IEnumerable<ShipBase>)invoke(terminal, "TradeShips")).ToList();
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
            Thing result = place(new Thing { def = def, stackCount = 75 }, owner.Map, x, null);
            tradeFixtureDomains[owner.Structure.ownerId].Add(result.Position); return result;
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
        bool validDeal(TradeDeal deal)
        {
            object[] args = { deal, false };
            return (bool)invoke(terminal, "DealStillValid", args);
        }
        try
        {
            initGrid(stationMap);
            patch.Patch(AccessTools.PropertyGetter(typeof(FoundationShip), "Label"),
                prefix: new HarmonyMethod(typeof(Program), nameof(FixtureShipLabel)));
            patch.Patch(AccessTools.PropertyGetter(typeof(Building_OrbitalTradeBeacon), "TradeableCells"),
                prefix: new HarmonyMethod(typeof(Program), nameof(FixtureBeaconCells)));
            patch.Patch(AccessTools.Method(typeof(TradeUtility), "PlayerSellableNow"),
                prefix: new HarmonyMethod(typeof(Program), nameof(FixturePlayerSellable)));
            patch.Patch(AccessTools.Method(typeof(ShipRegistry), nameof(ShipRegistry.ControlDomain), new[] { typeof(Ship) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(TradeFixtureDomain)));
            var hardA = ship(stationMap, 20, player); var hardB = ship(stationMap, 30, player);
            var host = ship(stationMap, 40, player);
            tradeFixtureDomains[host.Structure.ownerId].Add(terminal.Position);
            var enemy = ship(makeMap(), 50, npc); dock(enemy, 51, player);
            var softA = ship(makeMap(), 60, player); var portA = dock(softA, 61, npc);
            dock(softA, 62, npc);
            var softB = ship(makeMap(), 70, player); var portB = dock(softB, 71, player);
            var unconnected = ship(makeMap(), 80, player);
            var sideShip = ship(softA.Map, 90, player);
            Check(candidates().Select(s => s.Identity).ToHashSet().SetEquals(new[] {
                hardA.Identity, hardB.Identity, host.Identity, softA.Identity, softB.Identity }),
                "trade selection includes the player-console host and other hard/soft docks, excluding NPC and unconnected ships");
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
            Check(candidates().Count(s => s.Identity == softA.Identity) == 1,
                "multiple soft ports to one ship produce one candidate");
            Check(!candidates().Any(s => s.Identity == sideShip.Identity),
                "another ship on a soft-docked map cannot borrow its neighbor's port");
            softA.Structure.console.isMain = false;
            Check(!candidates().Any(s => s.Identity == softA.Identity), "a secondary or lost primary console is not a trade ship");
            softA.Structure.console.isMain = true;
            stationCells.Remove(portB.Position);
            Check(!candidates().Any(s => s.Identity == softB.Identity), "a station-side port on a disconnected structure is excluded");
            stationCells.Add(portB.Position);

            var sources = ((IEnumerable<IGrouping<Map, ShipBase>>)invoke(terminal, "TradeSources")).ToList();
            Check(sources.Count(g => g.Key == stationMap) == 1 && sources.Single(g => g.Key == stationMap).Count() == 3,
                "source dialog groups hard-docked ship names into one actual map choice");
            var pawn = place((Pawn)Raw(typeof(Pawn)), stationMap, 2, player);
            var otherPawn = place((Pawn)Raw(typeof(Pawn)), stationMap, 3, player);
            var silverA = goods(hardA, 21, "Silver"); var cargoA = goods(hardA, 22, "Steel");
            var silverB = goods(hardB, 31, "Silver"); var hostSilver = goods(host, 41, "Silver");
            var softSilver = goods(softA, 63, "Silver"); var sideGoods = goods(sideShip, 91, "Steel");
            var playerBeacon = beacon(stationMap, 101, player, silverA, cargoA, silverB);
            var overlapBeacon = beacon(stationMap, 102, player, silverA);
            beacon(stationMap, 103, npc, hostSilver);
            var remoteBeacon = beacon(softA.Map, 104, player, softSilver, sideGoods);
            TradeSession.trader = terminal; TradeSession.playerNegotiator = pawn;
            var selectedA = invoke(service, "Begin", terminal, pawn, stationMap);
            Check(selectedA != null && terminal.ColonyThingsWillingToBuy(pawn).ToHashSet().SetEquals(new[] { silverA, cargoA, silverB }),
                "source uses player beacon goods across the selected map, including another ship on that map");
            Check(terminal.ColonyThingsWillingToBuy(pawn).Count(t => t == silverA) == 1,
                "vanilla beacon enumeration deduplicates overlapping coverage");
            Check(!terminal.ColonyThingsWillingToBuy(pawn).Contains(hostSilver), "NPC beacons do not expose goods");
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
            Check(sealedBay.TryStore(cargoA) && cargoA.ParentHolder == sealedBay,
                "sealing transfers the same stack into the native crate ThingOwner");
            Check(!BeaconStock(stationMap, new TradeShip()).Contains(cargoA)
                && !terminal.ColonyThingsWillingToBuy(pawn).Contains(cargoA) && !validDeal(deal),
                "native beacon lists cannot see locked crate contents, without a filtering patch");
            Set(hack, "hacked", true);
            Check(!BeaconStock(stationMap).Contains(cargoA) && !validDeal(deal),
                "unlocking a sealed container does not expose its inventory to trade beacons");
            Check(BeaconStock(stationMap).Contains(silverA),
                "sealed cargo leaves ordinary goods under the same beacon unchanged");
            Check(ReferenceEquals(sealedBay.Take(cargoA, cargoA.stackCount), cargoA),
                "unsealing detaches the original stack without copying or regenerating it");
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
            tradeFixtureDomains[hardA.Structure.ownerId].Remove(cargoA.Position);
            Check(terminal.ColonyThingsWillingToBuy(pawn).Contains(cargoA) && validDeal(deal),
                "leaving a ship control domain does not exclude goods still covered by a map beacon");
            beaconCells[playerBeacon].Remove(cargoA.Position);
            Check(!validDeal(deal), "leaving beacon coverage rejects a pending sale");
            beaconCells[playerBeacon].Add(cargoA.Position);
            var merchantItem = place(terminal.Goods.First(), stationMap, 44, null);
            beaconCells[playerBeacon].Add(merchantItem.Position);
            Check(!terminal.ColonyThingsWillingToBuy(pawn).Contains(merchantItem),
                "merchant inventory is not duplicated onto the player side even under a player beacon");
            deal.AllTradeables.Clear();
            hardA.Anchor.SetFactionDirect(npc);
            Check(validDeal(deal), "map selection remains valid while another eligible player ship stays on the same map");
            hardA.Anchor.SetFactionDirect(player);
            var selectedSoft = invoke(service, "Begin", terminal, pawn, softA.Map);
            Check(selectedSoft != null && terminal.ColonyThingsWillingToBuy(pawn).ToHashSet().SetEquals(new[] { softSilver, sideGoods }),
                "soft-docked source uses all player beacon goods on its map rather than one ship domain");
            invoke(service, "End", selectedA);
            Check(terminal.ColonyThingsWillingToBuy(pawn).Any(), "closing an older window does not clear a newer source");
            softA.Anchor.SetFactionDirect(npc);
            Check(!validDeal(deal), "source loses eligibility when its last connected player console changes faction");
            softA.Anchor.SetFactionDirect(player);
            foreach (var port in stationMap.listerThings.AllThings.OfType<Building_DockingPort>())
                if (port.GetComp<CompSoftDocking>() is { } comp
                    && (Building_DockingPort)AccessTools.Field(typeof(CompSoftDocking), "peer").GetValue(comp) is { } peer
                    && peer.Map == softA.Map) Set(comp, "connected", false);
            Check(!terminal.ColonyThingsWillingToBuy(pawn).Any() && !validDeal(deal),
                "soft disconnection invalidates the source map without falling back to another map");
            Check(invoke(service, "Begin", terminal, pawn, softA.Map) == null,
                "a map disconnected after the source dialog opened cannot start a trade");
            invoke(service, "Begin", terminal, pawn, stationMap);
            invoke(service, "Forget", stationMap);
            Check(invoke(service, "MapFor", terminal, pawn) == null, "map release clears the trade session");
            var final = invoke(service, "Begin", terminal, pawn, stationMap);
            invoke(service, "End", final);
            Check(invoke(service, "MapFor", terminal, pawn) == null, "closing trade clears the selected source map");
            Check(AccessTools.TypeByName("DeltaV.Patch_SpaceStationTrade") == null,
                "station trading no longer replaces vanilla InSellablePosition");

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
