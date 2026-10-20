using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml;
using System.Linq;
using System.Collections.Generic;
using DeltaV;
using UnityEngine;
using Verse;

internal static class Program
{
    static readonly MethodInfo Couple = typeof(ShipPhysics).GetMethod("CoupleVelocities",
        BindingFlags.Static | BindingFlags.NonPublic);
    static int checks;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++;
    }
    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    static void RunCase(float ma, float ia, float mb, float ib, Vector2 ra, Vector2 rb,
        Vector2 va, float wa, Vector2 vb, float wb, float fraction, float maxP, float maxL)
    {
        Vector2 momentum = va * ma + vb * mb;
        float angular = ia * wa + ib * wb - Cross(ra, va * ma) - Cross(rb, vb * mb);
        float energy = (ma * va.sqrMagnitude + mb * vb.sqrMagnitude + ia * wa * wa + ib * wb * wb) * 0.5f;
        object[] args = { ma, ia, mb, ib, ra, rb, va, wa, vb, wb, fraction, maxP, maxL };
        Couple.Invoke(null, args);
        Vector2 nextA = (Vector2)args[6], nextB = (Vector2)args[8];
        float nextWA = (float)args[7], nextWB = (float)args[9];
        Vector2 nextMomentum = nextA * ma + nextB * mb;
        float nextAngular = ia * nextWA + ib * nextWB - Cross(ra, nextA * ma) - Cross(rb, nextB * mb);
        float nextEnergy = (ma * nextA.sqrMagnitude + mb * nextB.sqrMagnitude
            + ia * nextWA * nextWA + ib * nextWB * nextWB) * 0.5f;
        Check((nextMomentum - momentum).magnitude < 0.003f + (ma * va.magnitude + mb * vb.magnitude) * 0.000003f,
            "linear momentum conservation");
        Check(Math.Abs(nextAngular - angular) < 0.02f + (Math.Abs(angular)
            + ia * Math.Abs(wa) + ib * Math.Abs(wb) + ra.magnitude * ma * va.magnitude
            + rb.magnitude * mb * vb.magnitude) * 0.000005f, "total angular momentum conservation");
        Check(nextEnergy <= energy + Math.Max(0.001f, energy * 0.000003f), "coupling cannot create kinetic energy");
        Check((nextA - va).magnitude * ma <= maxP + 0.003f + ma * va.magnitude * 0.000001f,
            "linear pulse limit");
        if (fraction == 0f || maxP == 0f && maxL == 0f)
            Check(nextA == va && nextB == vb && nextWA == wa && nextWB == wb, "zero coupling changes nothing");
    }
    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { Run(); CheckDefs(args[0]); Console.WriteLine($"PASS {checks} engineering checks"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void CheckDefs(string managed)
    {
        using var logs = Log.LockMessages();
        DeepProfiler.enabled = false;
        var vanillaNamed = new Dictionary<string, XmlNode>();
        string core = Path.GetFullPath(Path.Combine(managed, "../../Data/Core/Defs"));
        foreach (string path in Directory.EnumerateFiles(core, "*.xml", SearchOption.AllDirectories))
        {
            var doc = new XmlDocument(); doc.Load(path);
            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                if (node.NodeType == XmlNodeType.Element && node.Attributes["Name"] != null)
                    vanillaNamed[node.Attributes["Name"].Value] = node;
        }
        var registered = new HashSet<string>();
        void RegisterVanilla(string name)
        {
            if (!registered.Add(name)) return;
            XmlNode node = vanillaNamed[name];
            if (node.Attributes["ParentName"] != null) RegisterVanilla(node.Attributes["ParentName"].Value);
            XmlInheritance.TryRegister(node, null);
        }
        XmlInheritance.Clear();
        RegisterVanilla("BuildingBase");
        var weapons = new XmlDocument(); weapons.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml");
        foreach (XmlNode node in weapons.DocumentElement.ChildNodes)
            if (node.NodeType == XmlNodeType.Element
                && node.Attributes["Name"]?.Value is "DeltaV_SpaceTurret1" or "DeltaV_SpaceTurret2")
                XmlInheritance.TryRegister(node, null);
        var engineering = new XmlDocument(); engineering.Load("1.6/Defs/SpaceCombat/DeltaV_Engineering.xml");
        foreach (XmlNode node in engineering.DocumentElement.ChildNodes)
            if (node.NodeType == XmlNodeType.Element) XmlInheritance.TryRegister(node, null);
        XmlInheritance.Resolve();
        foreach (string name in new[] { "DeltaV_InertiaCoupler", "DeltaV_MiningBeam" })
        {
            XmlNode original = engineering.SelectSingleNode($"/Defs/ThingDef[defName='{name}']");
            XmlNode resolved = XmlInheritance.GetResolvedNodeFor(original);
            Check(resolved["size"].InnerText == "(2,2)", name + " real XML inheritance preserves footprint");
            Check(resolved["thingClass"].InnerText == "DeltaV.Building_SpaceTurretGun",
                name + " uses existing turret implementation");
            XmlNodeList weaponComps = resolved.SelectNodes("comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']");
            Check(weaponComps.Count == 1 && weaponComps[0]["manualOnly"].InnerText == "true",
                name + " inherits exactly one manual engineering weapon");
            Check(resolved.SelectNodes("comps/li[@Class='CompProperties_Mannable']").Count == 0
                && resolved["hasInteractionCell"].InnerText == "false", name + " has no phantom operator");
            Check(resolved.SelectNodes("comps/li[@Class='CompProperties_Power']").Count == 1
                && resolved.SelectNodes("comps/li[@Class='DeltaV.CompProperties_SpaceWeaponTraverse']").Count == 1,
                name + " retains power and traverse through list inheritance");
            foreach (string xpath in new[] { "graphicData/texPath", "uiIconPath",
                "building/blueprintGraphicData/texPath", "modExtensions/li/topTexPath" })
                Check(File.Exists("1.6/Textures/" + resolved.SelectSingleNode(xpath).InnerText + ".png"),
                    name + " texture exists: " + xpath);
        }
        foreach (string name in new[] { "DeltaV_Impact_CouplingStructure", "DeltaV_Impact_CouplingFoundation",
            "DeltaV_Impact_MiningStructure", "DeltaV_Impact_MiningFoundation" })
        {
            XmlNode resolved = XmlInheritance.GetResolvedNodeFor(
                engineering.SelectSingleNode($"/Defs/DeltaV.SpaceImpactEffectDef[defName='{name}']"));
            Check(resolved.SelectSingleNode("projectileDefs/li") != null
                && resolved.SelectSingleNode("defenderEffect/kind") != null,
                name + " effect inheritance resolves both projectile and outcome");
        }
        XmlNode coupler = engineering.SelectSingleNode("/Defs/ThingDef[defName='DeltaV_InertiaCoupler']/comps/li");
        XmlNode beam = engineering.SelectSingleNode("/Defs/ThingDef[defName='DeltaV_Beam_InertiaCoupler']/comps/li");
        Check(coupler["cooldownTicks"].InnerText == "1" && beam["continuousBeam"].InnerText == "true"
            && beam["ignoresShields"].InnerText == "true", "coupler ticks continuously and bypasses shields");
        float Number(XmlNode n) => float.Parse(n.InnerText, System.Globalization.CultureInfo.InvariantCulture);
        Check(Math.Abs(Number(coupler["shotEnergy"]) * 12 - 3) < 0.0001f,
            "continuous per-second energy equals the former pulse budget");
        XmlNode coupling = engineering.SelectSingleNode("/Defs/DeltaV.SpaceImpactEffectDef[@Name='DeltaV_CouplingImpact']/defenderEffect");
        Check(Math.Abs(Math.Pow(1 - Number(coupling["couplingFraction"]), 12) - 0.85) < 0.00001
            && Math.Abs(Number(coupling["maxCouplingImpulse"]) * 12 - 400) < 0.001
            && Math.Abs(Number(coupling["maxCouplingAngularImpulse"]) * 12 - 4000) < 0.01,
            "continuous damping and impulse limits preserve the former time scale");
        Check(engineering.SelectNodes("/Defs/DeltaV.SpaceImpactEffectDef[defenderEffect/kind='ShieldDrain']/projectileDefs/li[text()='DeltaV_Beam_InertiaCoupler']").Count == 0,
            "no shield-drain outcome remains assigned to the coupler");
        XmlInheritance.Clear();
    }
    static void Run()
    {
        // A zero-strength hit must not damp existing motion by projecting the full velocity twice.
        var a = new MapSpaceObject(1, null, new SpacePose(new Vector3(1, 0, 0), 0.3f)
            { velocity = new Vector2(8, -3), angularVelocity = 0.4f });
        var b = new MapSpaceObject(2, null, new SpacePose(new Vector3(0, 0, 1), 0.7f)
            { velocity = new Vector2(-5, 2), angularVelocity = -0.2f });
        var report = new ShipPhysics.MassReport { mass = 100, inertia = 500 };
        SpacePose beforeA = a.pose, beforeB = b.pose;
        typeof(ShipPhysics).GetMethod("CoupleMotion", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { a, report, b, report, new Vector3(1, 0, 1).normalized, 0f, 400f, 4000f });
        Check(a.pose.velocity == beforeA.velocity && b.pose.velocity == beforeB.velocity,
            "zero coupling preserves velocities across different tangent bases");
        Check(a.pose.angularVelocity == beforeA.angularVelocity && b.pose.angularVelocity == beforeB.angularVelocity,
            "zero coupling preserves spin");
        Check(a.pose.position == beforeA.position && b.pose.position == beforeB.position
            && a.pose.angle == beforeA.angle && b.pose.angle == beforeB.angle,
            "impact coupling does not integrate position or angle");

        // Known solution: equal masses translate together after a full pulse; angular exchange likewise.
        object[] known = { 10f, 20f, 10f, 20f, Vector2.zero, Vector2.zero,
            new Vector2(2, 0), 1f, new Vector2(0, 0), -1f, 1f, 10000f, 10000f };
        Couple.Invoke(null, known);
        Check((Vector2)known[6] == Vector2.right && (Vector2)known[8] == Vector2.right
            && (float)known[7] == 0f && (float)known[9] == 0f, "equal masses share momentum, not absolute stop");
        var random = new System.Random(1827);
        float R(float low, float high) => low + (float)random.NextDouble() * (high - low);
        for (int i = 0; i < 2000; i++)
            RunCase(R(1, 10000), R(1, 100000), R(1, 10000), R(1, 100000),
                new Vector2(R(-80, 80), R(-80, 80)), new Vector2(R(-20, 20), R(-20, 20)),
                new Vector2(R(-100, 100), R(-100, 100)), R(-5, 5),
                new Vector2(R(-100, 100), R(-100, 100)), R(-5, 5),
                i % 10 == 0 ? 0f : R(0.01f, 1), i % 11 == 0 ? 0f : 400f, i % 11 == 0 ? 0f : 4000f);

        Vector2 ra = new Vector2(30, 20), rb = new Vector2(-10, 8);
        Vector2 translation = new Vector2(4, -6);
        float omega = 0.4f;
        object[] rigid = { 200f, 1000f, 20f, 100f, ra, rb,
            translation - omega * new Vector2(-ra.y, ra.x), omega,
            translation - omega * new Vector2(-rb.y, rb.x), omega, 1f, 1000f, 10000f };
        Vector2 before = (Vector2)rigid[6];
        Couple.Invoke(null, rigid);
        Check(((Vector2)rigid[6] - before).magnitude < 0.00001f
            && Math.Abs((float)rigid[7] - omega) < 0.00001f,
            "shared rigid motion is not erased by artificial absolute drag");
    }
}
