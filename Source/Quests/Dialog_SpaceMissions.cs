using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_SpaceMissions : Window
    {
        private readonly Building_SpaceTradeTerminal terminal;
        private readonly Pawn pawn;
        private readonly SpaceMissionService service;
        private readonly MissionBoard board;
        private Vector2 scroll;
        public override Vector2 InitialSize => new Vector2(760, 650);
        internal Dialog_SpaceMissions(Building_SpaceTradeTerminal terminal, Pawn pawn)
        {
            this.terminal = terminal; this.pawn = pawn;
            service = Current.Game.GetComponent<UniverseManager>().Missions;
            board = service.Board(terminal);
            forcePause = true; doCloseX = true; doCloseButton = true; absorbInputAroundWindow = true;
        }
        public override void DoWindowContents(Rect rect)
        {
            if (!terminal.MissionInteraction(pawn)) { Close(); return; }
            Widgets.Label(new Rect(0, 0, rect.width, 30), "DeltaV_Mission_Board".Translate());
            var offers = board?.offers.Where(m => m.quest == null).ToList() ?? new System.Collections.Generic.List<SpaceMission>();
            var active = service.Active.Where(m => m.faction == terminal.Faction).ToList();
            Rect outer = new Rect(0, 35, rect.width, rect.height - 80);
            float total = offers.Concat(active).Sum(m => Text.CalcHeight(service.Describe(m), rect.width - 30) + 100);
            Widgets.BeginScrollView(outer, ref scroll, new Rect(0, 0, outer.width - 20, Mathf.Max(total, outer.height)));
            float y = 0;
            foreach (var m in offers.Concat(active))
            {
                Widgets.Label(new Rect(0, y, outer.width - 25, 28), m.Title); y += 28;
                string description = service.Describe(m);
                float height = Text.CalcHeight(description, outer.width - 25);
                Widgets.Label(new Rect(0, y, outer.width - 25, height), description); y += height + 6;
                if (m.quest == null)
                {
                    if (Widgets.ButtonText(new Rect(0, y, 150, 28), "DeltaV_Mission_Accept".Translate()))
                    { service.RequestAccept(terminal, pawn, m); Close(); }
                }
                else
                {
                    if (Widgets.ButtonText(new Rect(0, y, 150, 28), "DeltaV_Mission_Deliver".Translate())) service.ConfirmDelivery(terminal, pawn, m);
                    if (Widgets.ButtonText(new Rect(160, y, 150, 28), "DeltaV_NavCourse".Translate())) service.Locate(m);
                    if (Widgets.ButtonText(new Rect(320, y, 150, 28), "DeltaV_Mission_Abandon".Translate()))
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("DeltaV_Mission_AbandonConfirm".Translate(), () => service.Fail(m)));
                }
                y += 55;
            }
            if (offers.Count + active.Count == 0) Widgets.Label(new Rect(0, 0, outer.width - 25, 60), "DeltaV_Mission_Empty".Translate());
            Widgets.EndScrollView();
        }
    }
}
