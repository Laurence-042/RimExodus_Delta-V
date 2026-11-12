using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using DeltaV;
using HarmonyLib;
using RimExodus.Maps;
using RimWorld;
using Verse;
using UnityEngine;

internal static partial class Program
{
    static void ApplySectorMass(JsonElement records, string layoutPath, ShipPhysics.MassReport report,
        int steelStacksPerHold = 0)
    {
        using var layout = JsonDocument.Parse(File.ReadAllText(layoutPath));
        var points = layout.RootElement.GetProperty("foundation").EnumerateArray()
            .Select(c => (position: new Vector2(c[0].GetInt32() + 20.5f, c[1].GetInt32() + 20.5f),
                          mass: ShipPhysics.MassPerDeckCell)).ToList();
        float intrinsic = 0;
        foreach (var row in records.EnumerateArray())
        {
            if (row.GetProperty("category").GetString() != "Building"
                || row.GetProperty("name").GetString() == "DeltaV_PrefabPawnMarker") continue;
            var cells = row.GetProperty("cells").EnumerateArray().ToList();
            float mass = row.GetProperty("mass").GetSingle();
            if (row.GetProperty("name").GetString() == "DeltaV_CargoStorage")
                mass += steelStacksPerHold * 75 * 0.5f;
            var position = new Vector2((float)cells.Average(c => c[0].GetInt32()) + 20.5f,
                (float)cells.Average(c => c[1].GetInt32()) + 20.5f);
            points.Add((position, mass));
            float w = row.GetProperty("size")[0].GetSingle(), h = row.GetProperty("size")[1].GetSingle();
            intrinsic += mass * (w*w + h*h) / 12f;
        }
        report.mass = points.Sum(p => p.mass);
        report.comLocal = points.Aggregate(Vector2.zero, (sum, point) => sum + point.position*point.mass) / report.mass;
        report.inertia = intrinsic + points.Sum(p => p.mass * (p.position - report.comLocal).sqrMagnitude);
    }

    static void FreighterLoads(string path, ShipPhysics.MassReport report)
    {
        using var records = JsonDocument.Parse(File.ReadAllText(path));
        using var layout = JsonDocument.Parse(File.ReadAllText(path.Replace(".records.json", ".layout.json")));
        var rows = records.RootElement.EnumerateArray().ToList();
        Vector2 Center(JsonElement row) => new Vector2((float)row.GetProperty("cells").EnumerateArray().Average(c => c[0].GetInt32())+20.5f,
            (float)row.GetProperty("cells").EnumerateArray().Average(c => c[1].GetInt32())+20.5f);
        (int,int) Cell(JsonElement cell) => (cell[0].GetInt32(),cell[1].GetInt32());
        foreach (bool full in new[] { false, true })
        {
            var points = layout.RootElement.GetProperty("foundation").EnumerateArray()
                .Select(c => (p: new Vector2(c[0].GetInt32()+20.5f,c[1].GetInt32()+20.5f), m: ShipPhysics.MassPerDeckCell)).ToList();
            float intrinsic = 0;
            foreach (var row in rows.Where(r => r.GetProperty("category").GetString() == "Building"))
            {
                float mass = row.GetProperty("mass").GetSingle();
                if (row.GetProperty("name").GetString() == "DeltaV_CargoStorage")
                {
                    // Finite stress case: every 48-stack bay filled with 75-count steel stacks (0.5 kg each).
                    if (full) mass += 48*75*.5f;
                    else
                    {
                        var cells = row.GetProperty("cells").EnumerateArray().Select(Cell).ToHashSet();
                        mass += rows.Where(r => r.GetProperty("category").GetString() == "Item" && cells.Contains(Cell(r.GetProperty("root"))))
                            .Sum(r => r.GetProperty("mass").GetSingle()*r.GetProperty("stateOverrides").GetProperty("stackCount").GetInt32());
                    }
                }
                points.Add((Center(row),mass));
                float w=row.GetProperty("size")[0].GetSingle(), h=row.GetProperty("size")[1].GetSingle();
                intrinsic += mass*(w*w+h*h)/12f;
            }
            report.mass=points.Sum(p=>p.m); report.comLocal=points.Aggregate(Vector2.zero,(a,p)=>a+p.p*p.m)/report.mass;
            report.inertia=intrinsic+points.Sum(p=>p.m*(p.p-report.comLocal).sqrMagnitude);
            float minAcceleration=float.MaxValue;
            foreach (var command in new[] { new ShipPhysics.Command { fx=1 },new ShipPhysics.Command { fx=-1 },
                new ShipPhysics.Command { fz=1 },new ShipPhysics.Command { fz=-1 },
                new ShipPhysics.Command { torque=1 },new ShipPhysics.Command { torque=-1 } })
            {
                ShipPhysics.PreviewAllocation(report,command,out var force,out float torque);
                if (command.torque!=0) Check(torque*command.torque>1,"loaded freighter turns in both directions");
                else
                {
                    float forward=Vector2.Dot(force,new Vector2(command.fx,command.fz));
                    Check(forward>1 && Mathf.Abs(torque)/report.inertia<.01f,"loaded freighter translates with bounded residual rotation");
                    minAcceleration=Math.Min(minAcceleration,forward/report.mass);
                }
            }
            Console.WriteLine($"Freighter {(full ? "192 steel stacks" : "delivered stock")}: mass={report.mass:F1}, COM=({report.comLocal.x-20:F2},{report.comLocal.y-20:F2}), min axial acceleration={minAcceleration:F3}; Def masses, healthy thrusters, production allocator.");
        }
    }

    static void ShipyardStates(string directory, Harmony harmony)
    {
        // Real Scribe for new stateful vanilla facilities and EVA containers.
        // Map registration, ASF renderer initialization and pawn generation require the game.
        var states = new List<XmlNode>();
        foreach (string name in new[] { "ArrivalStation", "Shipyard", "PrototypeShip" })
        {
            var xml = new XmlDocument(); xml.Load(Path.Combine(directory, name + ".xml"));
            states.AddRange(xml.SelectNodes("/MapPrefab/thingStates/li/state/saveable").Cast<XmlNode>());
        }
        string[] wanted = { "HydroponicsBasin", "PlantPot", "HospitalBed", "Bed", "ElectricStove", "TableMachining",
            "ElectricTailoringBench", "FabricationBench", "ElectricSmithy", "DeltaV_EvaSuit", "Cooler" };
        var selected = states.Where(n => wanted.Contains(n["def"].InnerText)).ToList();
        foreach (string name in selected.Select(n => n["def"].InnerText).Concat(new[] { "Steel", "Plant_Rice", "Plant_Daylily" }).Distinct())
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null) { def = Raw<ThingDef>(); def.defName = name; DefDatabase<ThingDef>.Add(def); }
            def.category = ThingCategory.Building; def.stackLimit = 1; def.size = IntVec2.One;
            def.comps = new List<CompProperties>(); def.building = new BuildingProperties();
            var sample = selected.FirstOrDefault(n => n["def"].InnerText == name);
            def.thingClass = sample == null ? typeof(ThingWithComps) : GenTypes.GetTypeInAnyAssembly(sample.Attributes["Class"].Value);
            if (name == "Cooler")
                def.comps.AddRange(new CompProperties[] { new CompProperties_TempControl(),
                    new CompProperties_Power { compClass = typeof(CompPowerTrader) },
                    new CompProperties_Flickable(), new CompProperties_Breakdownable() });
            if (name == "Bed" || name == "HospitalBed")
                def.comps.Add(new CompProperties_AssignableToPawn { compClass = typeof(CompAssignableToPawn_Bed), maxAssignedPawnsCount = 1 });
            if (name == "DeltaV_EvaSuit")
            {
                def.comps.Add(new CompProperties_Transporter());
                def.comps.Add(new CompProperties_LocalShuttle());
                def.comps.Add(new CompProperties_EvaSuit());
                var mounts = new CompProperties_VirtualWeapons();
                foreach (string tool in new[] { "Cutter", "Welder", "Tractor" })
                {
                    var weapon = new VirtualWeaponDef { defName = "DeltaV_Eva" + tool };
                    weapon.comps.Add(new CompProperties_SpaceWeapon());
                    if (tool == "Tractor") weapon.comps.Add(new CompProperties_CargoTractor());
                    mounts.mounts.Add(new VirtualWeaponSlot { weapon = weapon });
                }
                def.comps.Add(mounts);
            }
        }
        var entries = selected.Select(n => new MapPrefabThingEntry {
            def = DefDatabase<ThingDef>.GetNamed(n["def"].InnerText), stateXml = n.OuterXml }).ToList();
        var first = MapPrefabThingState.Instantiate(entries, "shipyard facilities", null);
        var second = MapPrefabThingState.Instantiate(entries, "shipyard repeated", null);
        Check(first.Count == entries.Count && second.Count == entries.Count, "new facilities restore in repeated native batches");
        Check(first.Concat(second).Select(t => t.ThingID).Distinct().Count() == entries.Count*2, "new facility identities are distinct");
        foreach (var plant in first.OfType<Building_PlantGrower>())
            Check(plant.GetPlantDefToGrow() != null, "greenhouse retains selected crop");
        foreach (var table in first.OfType<Building_WorkTable>())
            Check(table.BillStack != null && table.BillStack.Bills.Count == 0, "workshop has initialized empty bill stack");
        foreach (var cooler in first.OfType<Building_Cooler>())
            Check(cooler.GetComp<CompTempControl>().TargetTemperature == 15,
                "wreck cooler restores authored setpoint with native components");
        foreach (var suit in first.Where(t => t.def.defName == "DeltaV_EvaSuit"))
        {
            Check(suit.TryGetComp<CompTransporter>().innerContainer != null
                && suit.TryGetComp<CompTransporter>().innerContainer.Count == 0, "EVA has empty native cargo owner");
            Check(suit.TryGetComp<CompShuttle>().requiredPawns != null, "EVA shuttle reference lists survive Scribe");
            Check(suit.TryGetComp<CompVirtualWeapons>().Weapons.Count() == 3, "EVA restores all three virtual tools");
        }
        foreach (var state in states.Where(n => n["def"].InnerText == "DeltaV_Weapon_RapidMainSmall"))
        foreach (var kind in new[] { GunSupplyKind.Heads, GunSupplyKind.Propellant })
        {
            var supply = new CompGunSupply { props = new CompProperties_GunSupply { kind = kind, fuelCapacity = 300 }, parent = Raw<Building>() };
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "<root><supply>"+state.InnerXml+"</supply></root>");
                Scribe.loader.InitLoading(path); Scribe.EnterNode("supply"); supply.PostExposeData(); Scribe.ExitNode();
                Scribe.loader.FinalizeLoading();
                Check(supply.Fuel == 300, "shipyard cannon starts with both supply compartments full");
            }
            finally { Scribe.ForceStop(); File.Delete(path); }
        }
        foreach (var state in states.Where(n => n["gun"] != null))
        {
            var gun = state["gun"];
            Check(gun.SelectSingleNode("verbTracker/verbs/li/loadID").InnerText == "CompEquippable_"+gun["id"].InnerText+"_0",
                "nested turret gun and verb IDs agree");
        }
        Check(states.Where(n => n["gun"] != null).Select(n => n["gun"]["id"].InnerText).Distinct().Count()
            == states.Count(n => n["gun"] != null), "all authored turret internals have distinct source IDs");
    }
}
