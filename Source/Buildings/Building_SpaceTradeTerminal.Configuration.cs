using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    public partial class Building_SpaceTradeTerminal
    {
        internal bool CanConfigure => Spawned && DebugSettings.godMode;
        internal int RestockDays => restockDays;
        internal bool IndiscriminateSupply => indiscriminateSupply;
        internal IReadOnlyList<ISpaceCargoContainer> SupplyCargo => supplyCargo;
        internal ISpaceCargoContainer DeliveryCargo => deliveryCargo;

        internal void ReplaceCargoBinding(ISpaceCargoContainer previous, ISpaceCargoContainer replacement)
        {
            for (int i = 0; i < supplyCargo.Count; i++)
                if (supplyCargo[i] == previous) supplyCargo[i] = replacement;
            if (deliveryCargo == previous) deliveryCargo = replacement;
        }

        private void OpenConfiguration()
        {
            if (CanConfigure) Find.WindowStack.Add(new Dialog_SpaceTradeTerminal(this));
        }

        internal void ToggleIndiscriminateSupply()
        {
            if (CanConfigure) indiscriminateSupply = !indiscriminateSupply;
        }

        private AcceptanceReport RestockPermissionReport()
        {
            if (indiscriminateSupply) return true;
            // A captured terminal may retain its old faction after being welded to a player ship.
            // Check each facility's current host, not the mixed map's overall faction.
            bool playerFacility(Thing facility) => facility.Faction == Faction.OfPlayer
                || ShipRegistry.AllShipsOnMap(facility.Map).Any(ship => ship.IsValid
                    && ship.Faction == Faction.OfPlayer && ship.Contains(facility));
            if (playerFacility(this) || ValidContainers(false).Any(c => playerFacility(c.Thing)))
                return "DeltaV_TradePlayerSupplyBlocked".Translate();
            return true;
        }

        internal AcceptanceReport StockConfigurationReport()
        {
            if (!Spawned || Faction == null) return "DeltaV_TradeNoFaction".Translate();
            if (TraderKind == null) return "DeltaV_TradeNoKind".Translate();
            if (!ValidContainers(false).Any()) return "DeltaV_TradeNoSupply".Translate();
            if (!ValidContainers(true).Any()) return "DeltaV_TradeNoDelivery".Translate();
            return true;
        }

        internal AcceptanceReport TradeAvailability()
        {
            AcceptanceReport report = StockConfigurationReport();
            if (!report.Accepted) return report;
            if (!TradeShips().Any()) return "DeltaV_TradeNoDock".Translate();
            return true;
        }

        internal IEnumerable<ISpaceCargoContainer> CargoCandidates()
            => Map.listerThings.AllThings.OfType<ISpaceCargoContainer>()
                .OrderBy(c => c.Position.x).ThenBy(c => c.Position.z);

        internal AcceptanceReport BindingReport(ISpaceCargoContainer cargo, bool delivery)
        {
            if (!Spawned || cargo?.Spawned != true || cargo.Map != Map)
                return "DeltaV_TradeCargoDisconnected".Translate();
            var connected = new HashSet<IntVec3>();
            SubstructureOwnership.CollectConnected(Map, Position, connected, null);
            if (!connected.Contains(cargo.Position)) return "DeltaV_TradeCargoDisconnected".Translate();
            if (cargo.IsUnlocked != delivery)
                return (delivery ? "DeltaV_TradeDeliveryNeedsUnlock" : "DeltaV_TradeSupplyNeedsLock").Translate();
            if (Map.listerThings.AllThings.OfType<Building_SpaceTradeTerminal>()
                .Any(t => t != this && (t.deliveryCargo == cargo || t.supplyCargo.Contains(cargo))))
                return "DeltaV_TradeCargoAlreadyBound".Translate();
            return true;
        }

        internal AcceptanceReport SetCargoBinding(ISpaceCargoContainer cargo, bool delivery)
        {
            if (!CanConfigure) return false;
            // Removing a stale or invalid link must remain possible.
            if (delivery && deliveryCargo == cargo) { deliveryCargo = null; return true; }
            if (!delivery && supplyCargo.Contains(cargo)) { supplyCargo.Remove(cargo); return true; }
            AcceptanceReport report = BindingReport(cargo, delivery);
            if (!report.Accepted) return report;
            if (delivery)
            {
                supplyCargo.Remove(cargo);
                deliveryCargo = cargo;
            }
            else
            {
                if (deliveryCargo == cargo) deliveryCargo = null;
                supplyCargo.Add(cargo);
            }
            return true;
        }

        internal void ChooseTraderKind()
        {
            if (!CanConfigure) return;
            var options = new List<FloatMenuOption>();
            foreach (TraderKindDef kind in DefDatabase<TraderKindDef>.AllDefs.OrderBy(k => k.LabelCap.ToString()))
            {
                TraderKindDef selected = kind;
                options.Add(new FloatMenuOption(kind.LabelCap, () =>
                {
                    if (CanConfigure) traderKind = selected;
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        internal void ChooseRestockInterval()
        {
            if (!CanConfigure) return;
            Find.WindowStack.Add(new FloatMenu(new[] { 1, 3, 7, 15, 30, 60 }.Select(days =>
                new FloatMenuOption("DeltaV_TradeRestockDays".Translate(days), () =>
                {
                    if (CanConfigure) restockDays = days;
                })).ToList()));
        }

        internal void RestockForDebug()
        {
            if (!DebugSettings.godMode || !CanConfigure) return;
            AcceptanceReport report = StockConfigurationReport();
            if (!report.Accepted) { Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, false); return; }
            report = RestockPermissionReport();
            if (!report.Accepted) { Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, false); return; }
            if (TradeWindowOpen) { Messages.Message("DeltaV_TradeCloseWindow".Translate(), MessageTypeDefOf.RejectInput, false); return; }
            RegenerateStock();
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (!Spawned) return text;
            AcceptanceReport report = TradeAvailability();
            return (text.NullOrEmpty() ? "" : text.TrimEndNewlines() + "\n")
                + "DeltaV_TradeKind".Translate(TraderKind?.LabelCap.ToString() ?? "—")
                + "\n" + "DeltaV_TradeCargoCounts".Translate(ValidContainers(false).Count(), ValidContainers(true).Count())
                + "\n" + "DeltaV_TradeRestockDays".Translate(restockDays)
                + "\n" + (report.Accepted ? "DeltaV_TradeReady".Translate().ToString() : report.Reason);
        }
    }
}
