using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    static void CheckShipReactor()
    {
        var patch = new Harmony("DeltaV.Regression.ShipReactor");
        // Native texture/material creation is unavailable in this headless harness.
        patch.Patch(typeof(CompRefuelable).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        try
        {
            var node = XDocument.Load("1.6/Defs/ThingDefs_Buildings/DeltaV_ShipReactor.xml").Root.Element("ThingDef");
            var def = MakeDef((string)node.Element("defName"));
            def.tickerType = ParseHelper.FromString<TickerType>((string)node.Element("tickerType"));
            var parent = new Building { def = def };
            var comps = new List<ThingComp>();
            foreach (var entry in node.Element("comps").Elements("li"))
            {
                var xml = new XmlDocument(); xml.LoadXml(entry.ToString());
                var props = DirectXmlToObject.ObjectFromXml<CompProperties>(xml.DocumentElement, true);
                def.comps.Add(props);
                var comp = (ThingComp)Activator.CreateInstance(props.compClass);
                comp.parent = parent; comp.Initialize(props); comps.Add(comp);
            }
            Set(parent, "comps", comps);
            var power = parent.GetComp<CompPowerPlant>();
            var fuel = parent.GetComp<CompRefuelable>();
            var flick = parent.GetComp<CompFlickable>();
            var breakdown = parent.GetComp<CompBreakdownable>();
            // Populate the references normally cached by PostSpawnSetup, without a rendered map.
            Set(power, "refuelableComp", fuel); Set(power, "flickableComp", flick);
            Set(power, "breakdownableComp", breakdown); fuel.PostSpawnSetup(false);
            Set(power, "powerOnInt", true);
            Check(def.EverTransmitsPower && comps.Count == 4 && power != null && fuel != null
                && flick != null && breakdown != null && fuel.Props.functionsInVacuum,
                "reactor production XML creates vanilla transmitting generator, fuel, switch and breakdown comps");
            Check(fuel.Props.ConfigErrors(def).Count() == 0 && fuel.Fuel == 0 && fuel.TargetFuelLevel == 120,
                "reactor fuel XML is valid and new construction starts empty with a 120 target");
            power.UpdateDesiredPowerOutput();
            Check(power.PowerOutput == 0, "empty reactor cannot generate even when its power flag is on");
            fuel.Refuel(120); power.UpdateDesiredPowerOutput();
            Check(power.PowerOutput == 24000 && fuel.HasFuel, "fueled reactor outputs 24000 W through vanilla power plant");
            fuel.TargetFuelLevel = 60; fuel.ConsumeFuel(115);
            Check(fuel.GetFuelCountToFullyRefuel() == 55, "vanilla target fuel setting limits the requested refill");
            for (int i = 0; i < 60000; i++) fuel.CompTick();
            Check(Math.Abs(fuel.Fuel - 1) < .01f, "one game day consumes nominally 4 uranium using real fuel ticks");
            flick.SwitchIsOn = false; power.UpdateDesiredPowerOutput();
            float held = fuel.Fuel;
            for (int i = 0; i < 60000; i++) fuel.CompTick();
            Check(!power.PowerOn && power.PowerOutput == 0 && fuel.Fuel == held,
                "actual flick-off signal stops both generation and fuel consumption for a full day");
            Reset(); map.gameConditionManager = new GameConditionManager(map);
            var net = new PowerNet(Array.Empty<CompPower>()) { powerNetManager = new PowerNetManager(map) };
            net.powerComps.Add(power); power.transNet = net;
            Set(Find.TickManager, "ticksGameInt", 0);
            flick.SwitchIsOn = true; net.PowerNetTick(); power.UpdateDesiredPowerOutput();
            Check(power.PowerOn && power.PowerOutput == 24000,
                "real empty PowerNet restarts the switched-on reactor without a battery or outside power");
            Set(breakdown, "brokenDownInt", true); parent.BroadcastCompSignal("Breakdown");
            power.UpdateDesiredPowerOutput(); fuel.CompTick();
            Check(power.PowerOutput == 0 && !power.PowerOn && fuel.Fuel == held,
                "vanilla breakdown signal stops generation and powered-only fuel consumption");
            Set(breakdown, "brokenDownInt", false); net.PowerNetTick();
            power.UpdateDesiredPowerOutput(); fuel.ConsumeFuel(120); power.UpdateDesiredPowerOutput();
            Check(!power.PowerOn && power.PowerOutput == 0 && !fuel.HasFuel,
                "running out of fuel switches the reactor off without destroying it");
            fuel.Refuel(200);
            Check(fuel.Fuel == 120 && fuel.Props.fuelCapacity / fuel.Props.fuelConsumptionRate == 30,
                "refill caps at 120 uranium with a nominal 30-day reserve");
        }
        finally { patch.UnpatchAll(patch.Id); }
    }
}
