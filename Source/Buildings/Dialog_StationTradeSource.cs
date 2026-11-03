using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_StationTradeSource : Window
    {
        private readonly Building_SpaceTradeTerminal terminal;
        private readonly Pawn negotiator;
        private Vector2 scroll;
        public override Vector2 InitialSize => new Vector2(620, 440);

        internal Dialog_StationTradeSource(Building_SpaceTradeTerminal terminal, Pawn negotiator)
        {
            this.terminal = terminal;
            this.negotiator = negotiator;
            forcePause = true;
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect rect)
        {
            if (!terminal.Spawned || negotiator?.Map != terminal.Map) { Close(); return; }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, rect.width, 34), "DeltaV_TradeSourceTitle".Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, 42, rect.width, 72), "DeltaV_TradeSourceHelp".Translate());
            var sources = terminal.TradeSources().ToList();
            Rect viewport = new Rect(0, 122, rect.width, rect.height - 180);
            Rect view = new Rect(0, 0, viewport.width - 18, Mathf.Max(viewport.height, sources.Count * 58));
            Map chosen = null;
            Widgets.BeginScrollView(viewport, ref scroll, view);
            for (int i = 0; i < sources.Count; i++)
            {
                string names = string.Join(" / ", sources[i].Select(ship => ship.Label));
                string connection = (sources[i].Key == terminal.Map ? "DeltaV_TradeHardDock" : "DeltaV_TradeSoftDock").Translate();
                if (Widgets.ButtonText(new Rect(0, i * 58, view.width, 50), names + "\n" + connection))
                    chosen = sources[i].Key;
            }
            Widgets.EndScrollView();
            if (sources.Count == 0) Widgets.Label(viewport, "DeltaV_TradeNoDock".Translate());
            if (chosen != null) { Close(); terminal.OpenTradeForMap(negotiator, chosen); }
        }
    }
}
