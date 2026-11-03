using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class Dialog_SpaceTradeTerminal : Window
    {
        private readonly Building_SpaceTradeTerminal terminal;
        private Vector2 scroll;
        public override Vector2 InitialSize => new Vector2(760, 620);

        public Dialog_SpaceTradeTerminal(Building_SpaceTradeTerminal terminal)
        {
            this.terminal = terminal;
            forcePause = true;
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect rect)
        {
            if (!terminal.CanConfigure) { Close(); return; }
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, 0, rect.width, 30), "DeltaV_TradeConfigure".Translate());
            if (Widgets.ButtonText(new Rect(0, 34, rect.width * .66f, 32),
                "DeltaV_TradeKind".Translate(terminal.TraderKind?.LabelCap.ToString() ?? "—"))) terminal.ChooseTraderKind();
            if (Widgets.ButtonText(new Rect(rect.width * .68f, 34, rect.width * .32f, 32),
                "DeltaV_TradeRestockDays".Translate(terminal.RestockDays))) terminal.ChooseRestockInterval();
            Widgets.Label(new Rect(0, 74, rect.width, 64), "DeltaV_TradeConfigureHelp".Translate());
            AcceptanceReport report = terminal.TradeAvailability();
            Widgets.Label(new Rect(0, 142, rect.width, 46), report.Accepted
                ? "DeltaV_TradeReady".Translate().ToString() : report.Reason);
            Widgets.Label(new Rect(0, 188, rect.width, 28), "DeltaV_TradeBindings".Translate());
            var cargo = terminal.CargoCandidates().ToList();
            Rect viewport = new Rect(0, 220, rect.width, rect.height - 310);
            Rect view = new Rect(0, 0, viewport.width - 18, Mathf.Max(viewport.height, cargo.Count * 58));
            Widgets.BeginScrollView(viewport, ref scroll, view);
            for (int i = 0; i < cargo.Count; i++)
            {
                var bay = cargo[i];
                float y = i * 58;
                string state = (bay.IsUnlocked ? "DeltaV_DevTradeUnlocked" : "DeltaV_DevTradeLocked").Translate();
                Widgets.Label(new Rect(0, y, view.width - 265, 54), bay.LabelCap + " " + bay.Position
                    + "\n" + state + " · " + "DeltaV_CargoCapacity".Translate(bay.Contents.Count, bay.CurrentSlotLimit));
                BindingButton(new Rect(view.width - 260, y + 6, 125, 34), bay, false,
                    terminal.SupplyCargo.Contains(bay), "DeltaV_TradeSupply");
                BindingButton(new Rect(view.width - 130, y + 6, 125, 34), bay, true,
                    terminal.DeliveryCargo == bay, "DeltaV_TradeDelivery");
            }
            Widgets.EndScrollView();
            if (cargo.Count == 0) Widgets.Label(viewport, "DeltaV_TradeNoCargoOnMap".Translate());
            if (DebugSettings.godMode && Widgets.ButtonText(new Rect(0, rect.height - 78, rect.width, 30),
                "DeltaV_DevTradeRestock".Translate())) terminal.RestockForDebug();
        }

        private void BindingButton(Rect rect, Building_SpaceCargoContainer cargo, bool delivery, bool selected, string key)
        {
            AcceptanceReport report = selected ? AcceptanceReport.WasAccepted : terminal.BindingReport(cargo, delivery);
            if (!report.Accepted) TooltipHandler.TipRegion(rect, report.Reason);
            if (Widgets.ButtonText(rect, (selected ? "✓ " : "") + key.Translate(), active: report.Accepted))
            {
                AcceptanceReport result = terminal.SetCargoBinding(cargo, delivery);
                if (!result.Accepted) Messages.Message(result.Reason, MessageTypeDefOf.RejectInput, false);
            }
        }
    }
}
