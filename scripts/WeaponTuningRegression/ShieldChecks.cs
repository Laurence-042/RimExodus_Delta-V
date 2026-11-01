using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DeltaV;
using RimWorld;
using Verse;

internal static partial class Program
{
    static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly MethodInfo GuardTick = typeof(CompSpaceShield).GetMethod("AdvanceGuardTick", PrivateInstance);
    static readonly MethodInfo GuardDebit = typeof(CompSpaceShield).GetMethod("ConsumeGuard", PrivateInstance);
    static void ShieldTicks(CompSpaceShield shield, int count, bool powered = true)
    { for (int i = 0; i < count; i++) GuardTick.Invoke(shield, new object[] { powered }); }
    static double Hit(CompSpaceShield shield, double damage, params CompSpaceShield[] pool) =>
        (double)GuardDebit.Invoke(shield, new object[] { damage, pool });
    static void SetSpawned(Thing thing, bool spawned) => typeof(Thing).GetField("mapIndexOrState", PrivateInstance)
        .SetValue(thing, (sbyte)(spawned ? 0 : -1));
    static CompSpaceShield Shield(bool small = false)
    {
        var body = new Building { def = new ThingDef { comps = new() {
            new CompProperties_SpaceShield { regularCapacity = small ? 0 : 1000, emergencyCapacity = small ? 1000 : 2000 }
        } } };
        body.InitializeComps();
        var shield = body.GetComp<CompSpaceShield>(); shield.PostPostMake(); SetSpawned(body, true); return shield;
    }
    static double Total(IEnumerable<CompSpaceShield> shields) => shields.Sum(s => s.RegularCapacity + s.EmergencyCapacity);
    static void CheckShieldCapacity(string repo)
    {
        Game previous = Current.Game;
        Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        typeof(Game).GetField("maps", PrivateInstance).SetValue(Current.Game, new List<Map> { null });
        try
        {
            foreach (int count in new[] { 1, 4, 8 })
            {
                var pool = Enumerable.Range(0, count).Select(_ => Shield()).ToArray();
                double start = Total(pool), absorbed = Hit(pool[0], 100, pool);
                Check(Near(absorbed, 100) && pool.All(s => Near(s.RegularCapacity, 1000 - 100d / count))
                    && Near(start - Total(pool), absorbed), $"{count} shields share equal debit without rounding loss");
                double paid = 0;
                for (int i = 0; i < 1000; i++) paid += Hit(pool[i % count], .001, pool);
                Check(Near(start - Total(pool), absorbed + paid), $"{count} shields conserve fractional repeated hits");
            }
            var a = Shield(); var b = Shield(); var c = Shield();
            Hit(a, 900); Hit(b, 600);
            double before = Total(new[] { a, b, c });
            Check(Near(Hit(a, 900, a, b, c), 900) && a.RegularCapacity == 0 && Near(b.RegularCapacity, 0)
                && Near(c.RegularCapacity, 600) && a.EmergencyCapacity == 2000
                && Near(before - Total(new[] { a, b, c }), 900), "unequal balances redistribute the unpaid equal share");
            Check(a.EmergencyReason == GuardEmergencyReason.Automatic && !a.UsesRegular,
                "depleted member enters automatic emergency immediately");
            Check(Hit(a, 2100, a, b, c) == 2000 && c.RegularCapacity == 600,
                "emergency hit cannot borrow another member's regular capacity");
            a = Shield(); b = Shield();
            Check(Hit(a, 5000, a, b) == 4000 && a.EmergencyCapacity == 0 && b.EmergencyCapacity == 2000,
                "one large hit drains shared regular then only the struck local emergency");
            Check(!a.HasGuardCapacity && b.HasGuardCapacity, "exhausted dome disables while untouched emergency dome survives");

            a = Shield(); b = Shield(); Hit(b, 50); b.EnterManualEmergency();
            c = Shield(); c.mode = SpaceShieldMode.Kinetic;
            var small = Shield(true); var gone = Shield(); SetSpawned(gone.parent, false);
            var mixed = new[] { a, b, c, small, gone };
            Hit(a, 100, mixed);
            Check(a.RegularCapacity == 900 && b.RegularCapacity == 950 && c.RegularCapacity == 1000
                && small.RegularCapacity == 0 && gone.RegularCapacity == 1000,
                "manual emergency, line, small and despawned members never contribute");
            Check(Hit(small, 1200, mixed) == 1000 && a.RegularCapacity == 900,
                "small dome never borrows regular pool and only absorbs its 1000 capacity");
            ShieldTicks(small, 4000);
            Check(small.EmergencyCapacity == 0 && small.RegularCapacity == 0 && !small.UsesRegular
                && small.WantedPowerWatts == 10, "small shield has no regeneration or recovery load");

            a = Shield(); Hit(a, 1200);
            Check(a.RegularCapacity == 0 && a.EmergencyCapacity == 1800 && a.RecoveryWaitTicks == 600
                && a.WantedPowerWatts == 10, "actual regular consumption starts a 600 tick wait at idle load");
            ShieldTicks(a, 599, false);
            Check(a.RecoveryWaitTicks == 1 && a.RegularCapacity == 0, "unpowered wait advances through tick 599");
            Hit(a, 50);
            Check(a.RecoveryWaitTicks == 1, "emergency hit does not reset the regular timer");
            ShieldTicks(a, 1, false);
            Check(a.RegularCapacity == 0 && a.RecoveryWaitTicks == 0 && a.WantedPowerWatts == 1010,
                "600 ticks complete the wait and request the charging load");
            ShieldTicks(a, 200, false);
            Check(a.RegularCapacity == 0, "requesting charge never restores capacity without supplied power");
            ShieldTicks(a, 1);
            Check(Near(a.RegularCapacity, .4), "first powered recovery tick restores 0.4");
            ShieldTicks(a, 2499);
            Check(a.RegularCapacity == 1000 && a.EmergencyCapacity == 1750 && a.UsesRegular && a.WantedPowerWatts == 10,
                "2500 powered ticks fully restore regular only and remove charging load");
            foreach (int threshold in new[] { 1, 50, 100 })
            {
                a = Shield(); a.AutoRejoinPercent = threshold; Hit(a, 1000);
                ShieldTicks(a, 600 + threshold * 25 - 1);
                Check(!a.UsesRegular, $"auto rejoin {threshold}% waits for the configured threshold");
                ShieldTicks(a, 1);
                Check(a.UsesRegular && Near(a.RegularCapacity, threshold * 10), $"auto rejoin {threshold}% occurs at the threshold");
                Hit(a, 1);
                Check(a.RecoveryWaitTicks == 600 && a.WantedPowerWatts == 10, "rejoined regular debit stops charging immediately");
            }
            a = Shield(); a.AutoRejoinPercent = 100; Hit(a, 1000); ShieldTicks(a, 1850);
            a.AutoRejoinPercent = 50;
            Check(a.UsesRegular, "lowering automatic threshold can rejoin immediately without another tick");
            a.EnterManualEmergency(); a.AutoRejoinPercent = 1; ShieldTicks(a, 1000);
            Check(!a.UsesRegular && a.EmergencyReason == GuardEmergencyReason.Manual, "manual emergency ignores the auto threshold");
            ShieldTicks(a, 250);
            Check(a.UsesRegular && a.RegularCapacity == 1000, "manual emergency rejoins only at full regular capacity");
            a.EnterManualEmergency();
            Check(a.UsesRegular, "full regular capacity cannot be permanently locked by repeated manual emergency");
            Hit(a, 1000); a.mode = SpaceShieldMode.Electromagnetic;
            ShieldTicks(a, 600);
            Check(a.WantedPowerWatts == 1041, "line-mode recovery adds 1000 W to line geometry load");
            Check(Hit(a, 10) == 0 && a.EmergencyCapacity == 2000, "line mode never uses its emergency dome");
            ShieldTicks(a, 2500);
            Check(a.RegularCapacity == 1000 && a.mode == SpaceShieldMode.Electromagnetic && !a.UsesRegular
                && a.WantedPowerWatts == 41, "line mode recovers without being switched back to guard");

            a = Shield(); Hit(a, 1234.5); a.AutoRejoinPercent = 37; a.EnterManualEmergency(); ShieldTicks(a, 719);
            a.mode = SpaceShieldMode.Kinetic;
            string path = Path.Combine(repo, "Source/obj/WeaponTuningRegression/shield.xml");
            Scribe.saver.InitSaving(path, "shield"); a.PostExposeData(); Scribe.saver.FinalizeSaving();
            var restored = Shield(); SetSpawned(restored.parent, false);
            Scribe.loader.InitLoading(path); restored.PostExposeData(); Scribe.loader.FinalizeLoading();
            Check(Near(restored.RegularCapacity, a.RegularCapacity) && restored.EmergencyCapacity == a.EmergencyCapacity
                && restored.AutoRejoinPercent == 37 && restored.EmergencyReason == GuardEmergencyReason.Manual
                && restored.mode == SpaceShieldMode.Kinetic && restored.RecoveryWaitTicks == a.RecoveryWaitTicks,
                "Scribe round trip preserves both capacities, recovery, mode, threshold and manual reason");
            a = Shield(); SetSpawned(a.parent, false);
            Check(a.ConsumeGuard(1100) == 1100 && a.RegularCapacity == 0 && a.EmergencyCapacity == 1900,
                "no console list falls back to local regular then emergency");
            a = Shield(); Hit(a, 1100.25);
            var silver = new CompDeltaVShieldSilverFuel { parent = a.parent };
            silver.Initialize(new CompProperties_Refuelable { fuelCapacity = 2000, consumeFuelOnlyWhenUsed = true });
            a.parent.AllComps.Add(silver);
            silver.Refuel(50);
            Check(a.RegularCapacity == 0 && a.EmergencyCapacity == 1949.75 && silver.Fuel == 0,
                "native silver refueling restores only emergency at exactly one point per silver");
            silver.Refuel(100);
            Check(a.EmergencyCapacity == 2000 && Near(silver.Fuel, 49.75) && silver.TargetFuelLevel == 0,
                "silver restoration caps at full and retains unused delivered fuel");
            a.mode = SpaceShieldMode.Kinetic; ShieldTicks(a, 10000);
            Check(a.EmergencyCapacity == 2000 && Near(silver.Fuel, 49.75),
                "time and mode changes never consume repair silver without missing emergency capacity");
            var xml = XDocument.Load(Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_Shields.xml"));
            Check(xml.Descendants("hitPoints").All(x => x.Value == "0"),
                "both Delta-V defs disable native HP accounting while retaining native interception");
            var smallDef = xml.Root.Elements().Single(x => (string)x.Element("defName") == "DeltaV_ShieldGeneratorSmall");
            Check((string)smallDef.Element("size") == "(1,1)" && smallDef.Descendants("maxProjection").Single().Value == "16"
                && smallDef.Descendants("maxLength").Single().Value == "16", "small shield defines 1x1 footprint and both 16-cell line limits");
        }
        finally { Current.Game = previous; }
    }
}
