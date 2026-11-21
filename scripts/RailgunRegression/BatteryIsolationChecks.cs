using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    static int idleRailEnergyPlans;
    static void CountIdleRailEnergyPlan() { idleRailEnergyPlans++; }
    static bool CaptureShortCircuitBranch(System.Reflection.MethodBase __originalMethod)
        => throw new InvalidOperationException("short-circuit-branch:" + __originalMethod.Name);

    public sealed class BatterySaveFixture : IExposable
    {
        public CompSwitchableBattery battery;
        public BatterySaveFixture()
        {
            var parent = new Building { def = MakeDef("batterySaveFixture") };
            battery = new CompSwitchableBattery { parent = parent,
                props = new CompProperties_SwitchableBattery { storedEnergyMax = 3600, efficiency = 1 } };
            Set(parent, "comps", new List<ThingComp> { battery });
        }
        public void ExposeData() => battery.PostExposeData();
    }

    static void CheckBatteryIsolation()
    {
        Reset();
        var patch = new Harmony("DeltaV.Regression.BatteryIsolation");
        patch.CreateClassProcessor(typeof(Patch_SwitchableBatteryPowerConnection)).Patch();
        patch.CreateClassProcessor(typeof(Patch_SwitchableBatteryConnect)).Patch();
        patch.CreateClassProcessor(typeof(Patch_SpaceCapacitorCharging)).Patch();
        patch.CreateClassProcessor(typeof(Patch_SafeBatteryShortCircuit)).Patch();
        patch.CreateClassProcessor(typeof(Patch_SafeBatteryRain)).Patch();
        // Only map drawing and the final short-circuit explosion are suppressed.
        patch.Patch(AccessTools.Method(typeof(PowerNetManager), nameof(PowerNetManager.NotifyDrawersForWireUpdate)),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        patch.Patch(AccessTools.Method(typeof(GenExplosion), nameof(GenExplosion.DoExplosion)),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        try
        {
            map.powerNetManager = new PowerNetManager(map);
            map.gameConditionManager = new GameConditionManager(map);
            var net = new PowerNet(Array.Empty<CompPower>()) { powerNetManager = map.powerNetManager };
            var transmitter = new CompPowerTrader { props = new CompProperties_Power { transmitsPower = true }, transNet = net };
            Add(10, 5, 100, transmitter);
            var battery = new CompSwitchableBattery { props = new CompProperties_SwitchableBattery { storedEnergyMax = 3600, efficiency = 1 } };
            var attachment = new CompSpaceWeaponCapacitor { props = new CompProperties_SpaceWeaponCapacitor() };
            Set(attachment, "enabled", true); Set(attachment, "initialized", true);
            var parent = Add(11, 5, 100, battery, attachment);
            parent.def = MakeDef("connectedCapacitor"); parent.def.comps.Add(battery.props);
            Check(battery.GridConnected && battery.StoredEnergy == 0,
                "new battery starts empty and requests a public grid connection");
            var connectionColor = HarmonyLib.AccessTools.Method(typeof(Graphic_SafetyBattery), "ConnectionColor");
            UnityEngine.Color Tint() => (UnityEngine.Color)connectionColor.Invoke(null, new object[] { battery });
            Check(Tint().r == Tint().g && Tint().g == Tint().b, "battery status stays neutral gray while connection is requested but no grid exists");
            battery.ConnectToTransmitter(transmitter);
            Check(Tint().b > Tint().g && Tint().g > Tint().r && battery.StoredEnergy == 0,
                "battery status turns cyan gray on actual grid connection independently of charge");
            Check(net.batteryComps.Single() == battery && net.powerComps.Count == 0,
                "real vanilla registration recognizes the subclass as a battery, never a fixed load");
            var generator = new CompPowerTrader { props = new CompProperties_Power(), PowerOutput = 4000 };
            var load = new CompPowerTrader { props = new CompProperties_Power(), PowerOutput = -1000 };
            Add(12, 5, 100, generator); Add(13, 5, 100, load);
            Set(generator, "powerOnInt", true); Set(load, "powerOnInt", true);
            net.powerComps.Add(generator); net.powerComps.Add(load);
            net.PowerNetTick();
            Check(Math.Abs(battery.StoredEnergy - 3000 * CompPower.WattsToWattDaysPerTick) < .00001f,
                "real PowerNetTick stores only the 3000 W surplus after the 1000 W consumer");
            battery.SetStoredEnergyPct(.5f);
            generator.PowerOutput = 0; load.PowerOutput = -12000;
            float before = battery.StoredEnergy;
            net.PowerNetTick();
            Check(Math.Abs(before - battery.StoredEnergy - 12000 * CompPower.WattsToWattDaysPerTick) < .0001f,
                "vanilla battery meets 12000 W deficit without the former 6000 W output cap");
            battery.SetGridConnected(false);
            Check(Tint().r == Tint().g && Tint().g == Tint().b, "isolated charged battery immediately displays reserved gray");
            Check(battery.PowerNet == null && !net.batteryComps.Contains(battery)
                && !net.connectors.Contains(battery) && transmitter.connectChildren == null,
                "isolation removes the real connector, parent link and public battery membership");
            before = battery.StoredEnergy;
            generator.PowerOutput = 4000; load.PowerOutput = -1000; net.PowerNetTick();
            Check(battery.StoredEnergy == before && net.CurrentStoredEnergy() == 0,
                "isolated reserve neither charges nor appears in public stored-energy queries");
            Check(attachment.CanSupplyShotEnergy(50), "isolated capacitor remains available to its bound weapon");
            attachment.ConsumeShotEnergy(50);
            Check(battery.StoredEnergy == before - 50, "direct weapon draw debits the same vanilla stored energy");
            battery.ConnectToTransmitter(transmitter);
            PowerConnectionMaker.ConnectAllConnectorsToTransmitter(transmitter);
            Check(battery.PowerNet == null && net.batteryComps.Count == 0,
                "manual connection and newly placed transmitter cannot reconnect an isolated battery");
            battery.connectParent = transmitter;
            battery.ConnectToTransmitter(transmitter, true);
            Check(battery.connectParent == null && net.batteryComps.Count == 0,
                "load-time reconnection cannot leave a stale parent on an isolated battery");
            before = battery.StoredEnergy; battery.CompTick();
            Check(Math.Abs(before - battery.StoredEnergy - 5 * CompPower.WattsToWattDaysPerTick) < .0001f,
                "isolated battery retains vanilla 5 W self-discharge");
            battery.SetGridConnected(true);
            PowerConnectionMaker.ConnectAllConnectorsToTransmitter(transmitter);
            Check(battery.PowerNet == net && net.batteryComps.Count == 1,
                "re-enabling the switch permits the real automatic connector path");
            Set(attachment, "enabled", false); battery.RefreshConnection();
            battery.ConnectToTransmitter(transmitter);
            Check(battery.GridConnected && battery.PowerNet == null && !attachment.CanSupplyShotEnergy(1),
                "disabled attachment isolates both public and direct supply while retaining the user's setting");
            Set(attachment, "enabled", true); battery.RefreshConnection();
            PowerConnectionMaker.ConnectAllConnectorsToTransmitter(transmitter);
            Check(battery.PowerNet == net, "attachment re-enable restores the selected connected mode");
            var ordinary = new CompPowerBattery { props = new CompProperties_Battery() };
            Add(14, 5, 100, ordinary); ordinary.ConnectToTransmitter(transmitter);
            ordinary.SetStoredEnergyPct(.5f);
            battery.SetGridConnected(false); before = battery.StoredEnergy;
            var shortCircuit = AccessTools.Method(typeof(ShortCircuitUtility), "DrainBatteriesAndCauseExplosion");
            object[] arguments = { net, transmitter.parent, 0f, 0f };
            shortCircuit.Invoke(null, arguments);
            Check(ordinary.StoredEnergy == 0 && battery.StoredEnergy == before && (float)arguments[2] == 500,
                "real short-circuit drain empties connected ordinary battery and leaves isolated reserve untouched");
            battery.SetGridConnected(true); battery.ConnectToTransmitter(transmitter);
            ordinary.SetStoredEnergyPct(.5f);
            arguments = new object[] { net, transmitter.parent, 0f, 0f }; shortCircuit.Invoke(null, arguments);
            Check(battery.StoredEnergy == before && ordinary.StoredEnergy == 0 && (float)arguments[2] == 500,
                "connected safe reserve survives real short-circuit drainage and adds no explosion energy");
            Check(net.batteryComps.Contains(battery) && net.CurrentStoredEnergy() == before,
                "short-circuit protection leaves public battery registration and usable energy intact");
            Check(!Patch_SafeBatteryShortCircuit.EligibleBatteries(net).Any(b => b.StoredEnergy > 20),
                "charged safe battery alone cannot select the stored-energy explosion branch");
            Check(!ShortCircuitUtility.TryShortCircuitInRain(parent) && battery.StoredEnergy == before,
                "charged safe capacitor cannot short circuit in rain even while connected");
            patch.Patch(shortCircuit, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureShortCircuitBranch)));
            patch.Patch(AccessTools.Method(typeof(ShortCircuitUtility), "TryStartFireNear"),
                prefix: new HarmonyMethod(typeof(Program), nameof(CaptureShortCircuitBranch)));
            foreach (bool ordinaryCharged in new[] { false, true })
            {
                ordinary.SetStoredEnergyPct(ordinaryCharged ? .5f : 0);
                string branch = null;
                try { ShortCircuitUtility.DoShortCircuit((Building)transmitter.parent); }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("short-circuit-branch:")) { branch = ex.Message; }
                Check(branch == "short-circuit-branch:" + (ordinaryCharged ? "DrainBatteriesAndCauseExplosion" : "TryStartFireNear"),
                    "real short-circuit entry chooses explosion only for charged ordinary batteries: " + ordinaryCharged);
            }

            foreach (bool connected in new[] { false, true })
            {
                var saved = new BatterySaveFixture();
                saved.battery.SetStoredEnergyPct(.375f); saved.battery.SetGridConnected(connected);
                string path = Path.Combine(AppContext.BaseDirectory, "battery-roundtrip.xml");
                Scribe.saver.InitSaving(path, "fixture"); Scribe_Deep.Look(ref saved, "battery"); Scribe.saver.FinalizeSaving();
                string state = File.ReadAllText(path);
                Check(state.Contains("storedPower") && !state.Contains("storedEnergy") && !state.Contains("gridOutput"),
                    "save uses vanilla energy key without a parallel reservoir or legacy output state");
                BatterySaveFixture restored = null;
                Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref restored, "battery"); Scribe.loader.FinalizeLoading();
                Check(restored.battery.StoredEnergy == 1350 && restored.battery.GridConnected == connected,
                    "real Scribe roundtrip preserves energy and connection mode: " + connected);
            }
            var paths = new[] { "1.6/Defs/ThingDefs_Buildings/DeltaV_SafetyBattery.xml", "1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml" };
            var safeVisual = XDocument.Load(paths[0]).Root.Element("ThingDef");
            Check((string)safeVisual.Element("drawerType") == "RealtimeOnly"
                && (string)safeVisual.Element("graphicData").Element("graphicClass") == typeof(Graphic_SafetyBattery).FullName
                && (string)safeVisual.Element("graphicData").Element("shaderType") == "CutoutComplex",
                "battery Def uses the realtime masked graphic, so both map and shell can refresh without mesh invalidation");
            var chargeBar = HarmonyLib.AccessTools.Method(typeof(Graphic_SafetyBattery), "ChargeBar");
            var indicator = new BatterySaveFixture().battery;
            foreach (float fraction in new[] { 0f, .5f, 1f })
            {
                indicator.SetStoredEnergyPct(fraction);
                for (int r = 0; r < 4; r++)
                {
                    var loc = new UnityEngine.Vector3(17.5f, 3f, 22.5f);
                    var bar = (GenDraw.FillableBarRequest)chargeBar.Invoke(null, new object[] { loc, new Rot4(r), indicator });
                    Check(bar.center == loc + UnityEngine.Vector3.up * .1f && bar.rotation == new Rot4(r)
                        && bar.fillPercent == fraction && indicator.StoredEnergy == fraction * 3600,
                        "charge bar follows the supplied map/shell position and rotation without mutating stored energy: " + fraction + "/" + r);
                }
            }
            foreach (var node in paths.SelectMany(p => XDocument.Load(p).Descendants("li"))
                .Where(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_SwitchableBattery"))
                Check((float)node.Element("storedEnergyMax") > 0 && (float)node.Element("efficiency") == 1
                    && node.Element("chargingWatts") == null && node.Element("maxOutputWatts") == null,
                    "actual storage Def uses vanilla capacity and efficiency with no fixed charge/output rate");
            CheckRailPowerAndCharging(patch);
        }
        finally { Scribe.ForceStop(); patch.UnpatchAll(patch.Id); }
    }

    static void CheckRailPowerAndCharging(Harmony patch)
    {
        Reset(); map.edificeGrid = new EdificeGrid(map);
        patch.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.Notify_ColorChanged)),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        patch.Patch(AccessTools.Method(typeof(SpaceWeaponBallistics), "TryPlanEnergy"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountIdleRailEnergyPlan)));
        var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon { intrinsicAimSkill = 10 } };
        var demand = new CompPowerTrader { props = new CompProperties_Power { idlePowerDraw = 5 } };
        var appearance = new CompRailgunReadinessAppearance { props = new CompProperties_RailgunReadinessAppearance() };
        Set(demand.props, "basePowerConsumption", 220f); Set(demand, "powerOnInt", true);
        var loader = Add(5, 5, 500, weapon, demand, appearance);
        map.edificeGrid.Register(loader);
        var console = new CompDeltaVPilotConsole { isMain = true, shipId = nextId++ };
        Add(4, 5, 100, console);
        var ship = new FoundationShip(new Ship(console.shipId, map, console)); hull.members.Add(ship);
        Set(weapon, "cachedOwner", ship); Set(weapon, "ownerMap", map);
        Set(weapon, "ownerPosition", loader.Position); Set(weapon, "ownerRotation", loader.Rotation);
        Set(weapon, "ownerVersion", CompSpaceWeapon.OwnerBinding(weapon.parent));
        Set(weapon, "ownerCacheValid", true); Set(weapon, "ownerWasValid", true);
        universe.Weapons.RegisterWeapon(weapon);
        var capacitor = new CompSpaceWeaponCapacitor { props = new CompProperties_SpaceWeaponCapacitor { requiresArmedHost = true } };
        var battery = new CompSwitchableBattery { props = new CompProperties_SwitchableBattery { storedEnergyMax = 675, efficiency = 1 } };
        Add(5, 5, 100, capacitor, battery);
        Set(capacitor, "enabled", true); Set(capacitor, "initialized", true);
        weapon.SyncPowerRequest(); battery.AddEnergy(100);
        Check(demand.PowerOutput == -5 && battery.StoredEnergy == 0 && appearance.Tint.r == appearance.Tint.b,
            "unarmed rail loader idles, remains gray and refuses real battery AddEnergy");
        console.combat.weaponsArmed = true; weapon.SyncPowerRequest(); battery.AddEnergy(100);
        Check(demand.PowerOutput == -220 && battery.StoredEnergy == 100 && appearance.Tint.b > appearance.Tint.r,
            "arming restores wartime load, ready color and native capacitor charging");
        universe.Controls.SetWeaponGroupMask(ship, SpaceWeaponSalvoSet.Main, 0);
        Check(demand.PowerOutput == -5 && appearance.Tint.r == appearance.Tint.b,
            "inactive rail group idles and displays unavailable");
        battery.AddEnergy(10);
        Check(battery.StoredEnergy == 110, "group selection does not silently change the armed-host charging rule");
        console.combat.weaponsArmed = false; weapon.SyncPowerRequest(); battery.AddEnergy(100);
        Check(battery.StoredEnergy == 110, "disarming stops charging without erasing the existing reserve");
        idleRailEnergyPlans = 0;
        for (int i = 0; i < 10000; i++)
        { Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1); appearance.CompTick(); }
        Check(idleRailEnergyPlans == 0, "ten thousand unarmed status ticks perform no energy planning");
        var net = new PowerNet(Array.Empty<CompPower>()) { powerNetManager = new PowerNetManager(map) };
        net.batteryComps.Add(battery);
        var ordinary = new CompPowerBattery { props = new CompProperties_Battery { storedEnergyMax = 1000, efficiency = 1 } };
        Add(12, 5, 100, ordinary); net.batteryComps.Add(ordinary);
        AccessTools.Method(typeof(PowerNet), "DistributeEnergyAmongBatteries").Invoke(net, new object[] { 50f });
        Check(battery.StoredEnergy == 110 && ordinary.StoredEnergy == 50,
            "native grid charging allocates all surplus to eligible batteries without losing energy");
        ((CompProperties_SpaceWeaponCapacitor)capacitor.props).requiresArmedHost = false;
        battery.AddEnergy(20);
        Check(battery.StoredEnergy == 130, "ordinary laser-capacitor policy retains native charging while unarmed");
    }
}
