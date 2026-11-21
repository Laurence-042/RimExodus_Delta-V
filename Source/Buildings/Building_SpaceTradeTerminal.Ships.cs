using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    public partial class Building_SpaceTradeTerminal
    {
        private StationTradeService TradeService => Current.Game?.GetComponent<UniverseManager>()?.StationTrade;

        internal IEnumerable<ShipBase> TradeShips()
        {
            if (!Spawned) yield break;
            // Hard docking is the existing shared-map relation, not physical foundation contact.
            foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(Map))
                if (PlayerConsoleShip(ship)) yield return ship;

            var connected = ShipStructureIndex.Read(Map)?.PhysicalDomainAt(Position);
            var seen = new HashSet<DriveSubject>();
            foreach (Building_DockingPort port in ShipStructureIndex.Read(Map).BuildingsOf<Building_DockingPort>())
            {
                Building_DockingPort peer = port.Docking?.Peer;
                if (connected?.Contains(port.Position) != true || port.Docking?.Connected != true
                    || peer?.Spawned != true || peer.Map == Map) continue;
                foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(peer.Map))
                    if (PlayerConsoleShip(ship) && ship.Contains(peer) && seen.Add(ship.Identity))
                        yield return ship;
            }
        }

        private static bool PlayerConsoleShip(ShipBase ship) => ship is FoundationShip
            && ship.IsValid && ship.Anchor?.Faction == Faction.OfPlayer;

        internal IEnumerable<IGrouping<Map, ShipBase>> TradeSources()
            => TradeShips().OrderBy(ship => ship.Label).ThenBy(ship => ship.Identity.key).GroupBy(ship => ship.Map);

        internal void OpenTradeForMap(Pawn pawn, Map source)
        {
            if (Find.WindowStack.IsOpen<Dialog_Trade>()) return;
            AcceptanceReport report = NegotiatorReport(pawn);
            if (!report.Accepted)
            {
                Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, false);
                return;
            }
            StationTradeService service = TradeService;
            StationTradeService.Selection selection = service?.Begin(this, pawn, source);
            if (selection == null)
            {
                Messages.Message("DeltaV_TradeShipUnavailable".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            try { Find.WindowStack.Add(new Dialog_StationTrade(service, selection)); }
            catch { service.End(selection); throw; }
        }
    }
}
