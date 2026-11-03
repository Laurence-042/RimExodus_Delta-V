using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    public partial class Building_SpaceTradeTerminal
    {
        internal bool CanConfigure => Spawned && (Faction == Faction.OfPlayer || DebugSettings.godMode);
        internal int RestockDays => restockDays;
        internal IReadOnlyList<Building_SpaceCargoContainer> SupplyCargo => supplyCargo;
        internal Building_SpaceCargoContainer DeliveryCargo => deliveryCargo;

        private void OpenConfiguration()
        {
            if (CanConfigure) Find.WindowStack.Add(new Dialog_SpaceTradeTerminal(this));
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
            if (TradingMap() == null) return "DeltaV_TradeNoDock".Translate();
            return true;
        }

        internal IEnumerable<Building_SpaceCargoContainer> CargoCandidates()
            => Map.listerThings.AllThings.OfType<Building_SpaceCargoContainer>()
                .OrderBy(c => c.Position.x).ThenBy(c => c.Position.z);

        internal AcceptanceReport BindingReport(Building_SpaceCargoContainer cargo, bool delivery)
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

        internal AcceptanceReport SetCargoBinding(Building_SpaceCargoContainer cargo, bool delivery)
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
