using RimWorld;
using System.Linq;
using Verse;

namespace DeltaV
{
    // One transient selection per vanilla trade window; owned by the current game.
    internal sealed class StationTradeService
    {
        internal sealed class Selection
        {
            internal readonly Building_SpaceTradeTerminal terminal;
            internal readonly Pawn negotiator;
            internal readonly Map stationMap, sourceMap;
            internal Selection(Building_SpaceTradeTerminal terminal, Pawn negotiator, Map sourceMap)
            {
                this.terminal = terminal;
                this.negotiator = negotiator;
                stationMap = terminal.Map;
                this.sourceMap = sourceMap;
            }
        }

        private Selection current;

        internal Selection Begin(Building_SpaceTradeTerminal terminal, Pawn negotiator, Map sourceMap)
        {
            if (sourceMap == null || !terminal.TradeShips().Any(ship => ship.Map == sourceMap)
                || negotiator?.Spawned != true || negotiator.Map != terminal.Map) return null;
            return current = new Selection(terminal, negotiator, sourceMap);
        }

        internal Map MapFor(Building_SpaceTradeTerminal terminal, Pawn negotiator)
        {
            if (current == null || current.terminal != terminal || current.negotiator != negotiator
                || terminal?.Spawned != true || terminal.Map != current.stationMap
                || negotiator?.Spawned != true || negotiator.Map != current.stationMap) return null;
            return terminal.TradeShips().Any(ship => ship.Map == current.sourceMap) ? current.sourceMap : null;
        }

        internal void End(Selection selection)
        {
            if (current == selection) current = null;
        }

        internal void Forget(Map map)
        {
            if (current != null && (current.stationMap == map || current.sourceMap == map)) current = null;
        }
    }

    internal sealed class Dialog_StationTrade : Dialog_Trade
    {
        private readonly StationTradeService service;
        private readonly StationTradeService.Selection selection;

        internal Dialog_StationTrade(StationTradeService service, StationTradeService.Selection selection)
            : base(selection.negotiator, selection.terminal)
        {
            this.service = service;
            this.selection = selection;
        }

        public override void PostClose()
        {
            base.PostClose();
            service.End(selection);
        }
    }
}
