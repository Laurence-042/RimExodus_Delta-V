using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DeltaV;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// Offline control-chain probe. The production tactic planner, ShipPhysics allocator,
// trajectory sampler and OnlineMpcGuidance are used. No game loop is started.
internal static partial class Program
{
    private const float Tick = 1f / 60f;
    private static readonly Vector3 Origin = Vector3.forward;

    private static void Main(string[] args)
    {
        var mod = (DeltaVMod)RuntimeHelpers.GetUninitializedObject(typeof(DeltaVMod));
        typeof(DeltaVMod).GetField("<Settings>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(mod, new DeltaVSettings { passiveDrag = true });
        var mods = (Dictionary<Type, Mod>)typeof(LoadedModManager)
            .GetField("runningModClasses", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        mods[typeof(DeltaVMod)] = mod;
        SetupSphere();
        CheckBallisticMath();
        if (args.Contains("--expansion")) { RunExpansion(); return; }
        if (args.Contains("--fighter")) { RunActualFighter(); return; }

        ShipPhysics.MassReport report = LoadThrusters();
        Vector3 axisProbe = OrbitSphere.Move(Origin, new Vector2(150f, 0f));
        Vector2 axisX = OrbitSphere.Offset(Origin, axisProbe);
        Vector2 axisY = OrbitSphere.Offset(axisProbe, OrbitSphere.Move(axisProbe, Vector2.up * 10f));
        Console.WriteLine($"axis probe: ({axisX.x:F1},{axisX.y:F1}) then y+10="
            + $"({axisY.x:F1},{axisY.y:F1})");
        float rawSideForce = LateralReserve(report, 70f) * report.mass;
        report.mass = rawSideForce / 5.11f;
        float rawMoment = ShipPhysics.PreviewAngularAcceleration(report, 1) * report.inertia;
        report.inertia = rawMoment / 0.728f;
        RunTactic("automatic turret ship", report, false);
        RunTactic("fixed-gun hold range", report, true);
        RunTactic("two ships tracking each other", report, false, pair: true);
        report.mass *= 4f;
        RunTactic("quarter acceleration turret ship", report, false);
        report.mass /= 4f;
        Console.WriteLine($"Fighter_0 thrusters={report.thrusters.Count} thrust={report.thrusters.Sum(t => t.thrust):F0} "
            + $"calibrated mass={report.mass:F1} inertia={report.inertia:F0}");
        Console.WriteLine($"lateral reserve at 35/70 degrees="
            + $"{LateralReserve(report, 35f):F2}/{LateralReserve(report, 70f):F2}; "
            + $"angular +/−={ShipPhysics.PreviewAngularAcceleration(report, 1):F3}/"
            + $"{ShipPhysics.PreviewAngularAcceleration(report, -1):F3}");

    }

    private static void SetupSphere()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.NonPublic | BindingFlags.Public;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
        typeof(Game).GetField("worldInt", flags)!.SetValue(game, world);
        Current.Game = game;
        var layer = (PlanetLayer)RuntimeHelpers.GetUninitializedObject(typeof(PlanetLayer));
        FieldInfo tiles = typeof(PlanetLayer).GetField("tileIDToNeighbors_offsets", flags)!;
        object array = Activator.CreateInstance(tiles.FieldType)!;
        array.GetType().GetField("m_Length", flags)!.SetValue(array, 1);
        tiles.SetValue(layer, array);
        typeof(OrbitSphere).GetField("cachedWorld", flags)!.SetValue(null, world);
        typeof(OrbitSphere).GetField("cachedLayer", flags)!.SetValue(null, layer);
        ((Dictionary<PlanetLayer, float>)typeof(OrbitSphere)
            .GetField("RadiusByLayer", flags)!.GetValue(null)!)[layer] = 10000f;
        ((Dictionary<PlanetLayer, float>)typeof(OrbitSphere)
            .GetField("WorldPerCellByLayer", flags)!.GetValue(null)!)[layer] = 1f;
        if (!OrbitSphere.Valid) throw new Exception("OrbitSphere fixture did not initialize.");
    }

    private static ShipPhysics.MassReport LoadThrusters()
    {
        XDocument xml = XDocument.Load("Prefabs/Fighter_0.xml");
        var cells = xml.Descendants("foundations").Single().Elements("li")
            .Select(e => ParseCell((string)e.Element("pos")!)).ToList();
        Vector2 center = new Vector2(cells.Average(p => p.x + .5f),
            cells.Average(p => p.y + .5f));
        var report = new ShipPhysics.MassReport { mass = 1500f, inertia = 100000f };
        foreach (XElement entry in xml.Descendants("thingStates").Single().Elements("li"))
        {
            string def = (string)entry.Element("def")!;
            if (def != "SmallThruster" && def != "LargeThruster") continue;
            Vector2 cell = ParseCell((string)entry.Element("pos")!);
            int rotation = (int)entry.Element("rotation")!;
            Vector2 offset = def == "LargeThruster" ? new Vector2(1f, 1f)
                : rotation % 2 == 0 ? new Vector2(.5f, 1f)
                : new Vector2(1f, .5f);
            report.thrusters.Add(new ShipPhysics.Thruster
            {
                localPos = cell + offset - center,
                dir = ShipPhysics.ThrustDir(new Rot4(rotation)),
                thrust = ShipPhysics.ThrusterThrust,
                operational = true
            });
        }
        return report;
    }

    private static Vector2 ParseCell(string text)
    {
        string[] parts = text.Trim('(', ')').Split(',');
        return new Vector2(float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    private static float LateralReserve(ShipPhysics.MassReport report, float degrees)
    {
        Vector2 local = SpaceTransform.RotateInverse(Vector2.up, degrees * Mathf.Deg2Rad);
        return Mathf.Max(ShipPhysics.PreviewBrakingAcceleration(report, local),
            ShipPhysics.PreviewBrakingAcceleration(report, -local));
    }

}
