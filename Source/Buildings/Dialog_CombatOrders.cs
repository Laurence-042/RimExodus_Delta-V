using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_CombatOrders : Window
    {
        private readonly ShipBase ship;
        private readonly UniverseManager universe;
        private readonly ShipFireControl control;
        private Vector2 scroll;
        public override Vector2 InitialSize => new Vector2(720f, 550f);
        internal Dialog_CombatOrders(ShipBase ship, UniverseManager universe)
        { this.ship = ship; this.universe = universe; control = universe.Controls.ExistingFor(ship).FireControl;
            forcePause = true; doCloseX = true; }

        internal static IEnumerable<Thing> QueueableParts(ShipBase enemy)
        {
            var targets = new ShipFireControl.ExternalTargets(enemy);
            return targets.weapons.Concat(targets.propulsion);
        }

        public override void DoWindowContents(Rect rect)
        {
            Widgets.Label(new Rect(0f, 0f, rect.width, 70f), "DeltaV_CombatOrdersHelp".Translate());
            if (Widgets.ButtonText(new Rect(0f, 75f, rect.width, 30f), "DeltaV_CombatCancelOrders".Translate(control.QueueCount))) control.CancelCommands();
            var own = universe.FrameOf(ship.Map);
            var enemies = universe.objects.SelectMany(c => ShipFireControl.Candidates(ship, own, c)).ToList();
            var view = new Rect(0f, 115f, rect.width, rect.height - 120f);
            Widgets.BeginScrollView(view, ref scroll, new Rect(0f, 0f, view.width - 20f, enemies.Count * 46f));
            float y = 0f;
            foreach (var enemy in enemies)
            {
                var selected = enemy;
                Widgets.Label(new Rect(0f, y, 260f, 32f), enemy.Label);
                if (Widgets.ButtonText(new Rect(265f, y, 175f, 32f), "DeltaV_CombatEngage".Translate()))
                    control.CommandTarget(CombatTarget.ForShip(enemy.Identity));
                if (Widgets.ButtonText(new Rect(448f, y, 200f, 32f), "DeltaV_CombatQueuePart".Translate()))
                {
                    var options = new List<FloatMenuOption>();
                    foreach (var part in QueueableParts(enemy).OrderBy(part => part.LabelCap.ToString()))
                    {
                        options.Add(new FloatMenuOption(part.LabelCap + " " + part.Position, () =>
                            control.CommandTarget(CombatTarget.ForShip(selected.Identity, part.thingIDNumber), true)));
                    }
                    if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
                }
                y += 46f;
            }
            Widgets.EndScrollView();
        }
    }
}
