using System;
using System.Reflection;
using DeltaV;
using Verse;

internal static partial class Program
{
    static void CheckBatchGizmos()
    {
        var a = Shield(); var b = Shield(); var gone = Shield();
        a.AutoRejoinPercent = 20; b.AutoRejoinPercent = 80;
        Hit(a, 500); b.EnterManualEmergency();
        var first = new Gizmo_ShieldRegular(a); var second = new Gizmo_ShieldRegular(b);
        Check(first.GroupsWith(second) && second.GroupsWith(first), "regular capacity bars group across different thresholds and emergency states");
        first.MergeWith(second); first.MergeWith(second);
        var type = typeof(Gizmo_ShieldRegular);
        var target = type.GetProperty("Target", PrivateInstance);
        var dragging = type.GetProperty("DraggingBar", PrivateInstance);
        var percent = type.GetProperty("ValuePercent", PrivateInstance);
        Check(Near((float)percent.GetValue(first), .75f), "merged capacity bar totals selected balances without duplicate members");
        target.SetValue(first, .2f);
        Check(a.AutoRejoinPercent == 20 && b.AutoRejoinPercent == 80, "idle slider repaint does not overwrite mixed thresholds");
        dragging.SetValue(first, true); target.SetValue(first, .2f);
        Check(a.AutoRejoinPercent == 20 && b.AutoRejoinPercent == 20, "clicking the representative's existing threshold still updates other selected shields");
        var nextEvent = new Gizmo_ShieldRegular(a); nextEvent.MergeWith(second);
        Check((bool)dragging.GetValue(nextEvent), "shield drag survives gizmo recreation between GUI events");
        nextEvent.MergeWith(new Gizmo_ShieldRegular(gone)); SetSpawned(gone.parent, false);
        target.SetValue(nextEvent, .63f);
        Check(a.AutoRejoinPercent == 63 && b.AutoRejoinPercent == 63 && gone.AutoRejoinPercent == 10,
            "drag updates all live selected shields and skips despawned targets");
        Check(b.EmergencyReason == GuardEmergencyReason.Manual, "batch threshold editing preserves manual emergency choice");
        dragging.SetValue(nextEvent, false); target.SetValue(nextEvent, .9f);
        Check(a.AutoRejoinPercent == 63 && b.AutoRejoinPercent == 63, "released drag no longer changes thresholds");
        Hit(a, 500); ShieldTicks(a, 1100); // 20% recovered, below 63% automatic threshold.
        Check(a.EmergencyReason == GuardEmergencyReason.Automatic, "automatic emergency still waits for its current threshold");
        dragging.SetValue(nextEvent, true); target.SetValue(nextEvent, .1f);
        Check(a.UsesRegular && b.EmergencyReason == GuardEmergencyReason.Manual,
            "lowering a batch threshold rechecks automatic members without releasing manual ones");
        dragging.SetValue(nextEvent, false);

        bool previousGod = DebugSettings.godMode;
        DebugSettings.godMode = true;
        try
        {
            var one = new CompShipCombatAI { parent = a.parent };
            var two = new CompShipCombatAI { parent = b.parent };
            var absent = new CompShipCombatAI { parent = gone.parent };
            one.strategy.name = "one"; two.strategy.name = "two"; absent.strategy.name = "absent";
            Type dialogType = typeof(CompShipCombatAI).Assembly.GetType("DeltaV.Dialog_CombatStrategy");
            object dialog = Activator.CreateInstance(dialogType, PrivateInstance, null,
                new object[] { new[] { one, two, absent, two } }, null);
            var draft = (CombatStrategy)dialogType.GetField("draft", PrivateInstance).GetValue(dialog);
            Check(draft.name == "one", "batch strategy draft starts from the representative");
            draft.name = "shared";
            Check(one.strategy.name == "one" && two.strategy.name == "two", "editing or cancelling a strategy draft leaves all originals unchanged");
            dialogType.GetMethod("Apply", PrivateInstance).Invoke(dialog, null);
            Check(one.strategy.name == "shared" && two.strategy.name == "shared" && absent.strategy.name == "absent",
                "one strategy confirmation applies to every live target and skips despawned devices");
            Check(!ReferenceEquals(one.strategy, two.strategy) && !ReferenceEquals(one.strategy, draft)
                && !ReferenceEquals(one.strategy.maneuverRules, two.strategy.maneuverRules),
                "batch strategy application gives each device an independent deep copy");
            draft.name = "later";
            Check(one.strategy.name == "shared" && two.strategy.name == "shared", "later draft edits cannot mutate applied strategies");
        }
        finally { DebugSettings.godMode = previousGod; }
    }
}
