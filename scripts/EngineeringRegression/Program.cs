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
    static void RunCase(float ma, float ia, float mb, float ib,
        Vector2 va, float wa, Vector2 vb, float wb, float fraction, float maxP, float maxL)
    {
        Vector2 momentum = va * ma + vb * mb;
        float angular = ia * wa + ib * wb;
        float energy = (ma * va.sqrMagnitude + mb * vb.sqrMagnitude + ia * wa * wa + ib * wb * wb) * 0.5f;
        object[] args = { ma, ia, mb, ib, va, wa, vb, wb, fraction, maxP, maxL };
        Couple.Invoke(null, args);
        Vector2 nextA = (Vector2)args[4], nextB = (Vector2)args[6];
        float nextWA = (float)args[5], nextWB = (float)args[7];
        Vector2 nextMomentum = nextA * ma + nextB * mb;
        float nextAngular = ia * nextWA + ib * nextWB;
        float nextEnergy = (ma * nextA.sqrMagnitude + mb * nextB.sqrMagnitude
            + ia * nextWA * nextWA + ib * nextWB * nextWB) * 0.5f;
        Check((nextMomentum - momentum).magnitude < 0.003f + (ma * va.magnitude + mb * vb.magnitude) * 0.000003f,
            "linear momentum conservation");
        Check(Math.Abs(nextAngular - angular) < 0.02f + (Math.Abs(angular)
            + ia * Math.Abs(wa) + ib * Math.Abs(wb)) * 0.000005f, "spin angular momentum conservation");
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
                && node.Attributes["Name"]?.Value is "DeltaV_SpaceTurret1" or "DeltaV_SpaceTurret2" or "DeltaV_SpaceTurret3")
                XmlInheritance.TryRegister(node, null);
        var engineering = new XmlDocument(); engineering.Load("1.6/Defs/SpaceCombat/DeltaV_Engineering.xml");
        foreach (XmlNode node in engineering.DocumentElement.ChildNodes)
            if (node.NodeType == XmlNodeType.Element) XmlInheritance.TryRegister(node, null);
        var eva = new XmlDocument(); eva.Load("1.6/Defs/SpaceCombat/DeltaV_Eva.xml");
        foreach (XmlNode node in eva.DocumentElement.ChildNodes)
            if (node.NodeType == XmlNodeType.Element) XmlInheritance.TryRegister(node, null);
        XmlInheritance.Resolve();
        XmlNode suit = XmlInheritance.GetResolvedNodeFor(eva.SelectSingleNode("/Defs/ThingDef[defName='DeltaV_EvaSuit']"));
        Check(suit.SelectSingleNode("comps/li[@Class='CompProperties_Transporter']/massCapacity")?.InnerText == "60"
            && suit.SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_VehicleCargo']/cargoCapacity")?.InnerText == "60",
            "EVA loading and cargo capacity both use 60 kg");
        foreach (XmlNode node in eva.SelectNodes("//*[@Class]"))
        {
            string name = node.Attributes["Class"].Value;
            Type type = XmlType(name);
            Check(type != null, "EVA component type exists: " + name);
            CheckFields(node, type);
        }
        foreach (XmlNode node in eva.SelectNodes("/Defs/DeltaV.VirtualWeaponDef")) CheckFields(node, typeof(VirtualWeaponDef));
        foreach (XmlNode node in eva.SelectNodes("/Defs/DeltaV.ShellRenderRuleDef")) CheckFields(node, typeof(ShellRenderRuleDef));
        foreach (XmlNode node in eva.SelectNodes("/Defs/ThingDef[starts-with(defName,'DeltaV_Beam_')]"))
        {
            XmlNode resolved = XmlInheritance.GetResolvedNodeFor(node);
            Check(resolved.SelectNodes("comps/li[@Class='DeltaV.CompProperties_SpaceProjectile']").Count == 1
                && resolved["category"].InnerText == "Building", "EVA beam inherits the common engineering projectile contract");
        }
        foreach (XmlNode node in eva.SelectNodes("/Defs/DeltaV.SpaceImpactEffectDef[@ParentName]"))
        {
            XmlNode resolved = XmlInheritance.GetResolvedNodeFor(node);
            CheckFields(resolved, typeof(SpaceImpactEffectDef));
            bool cutterFoundation = resolved["defName"].InnerText == "DeltaV_Impact_EvaCutFoundation";
            Check(resolved["buildingHitChance"] == null && (cutterFoundation
                    ? resolved["substructureHitChance"]?.InnerText == "1" : resolved["substructureHitChance"] == null),
                "EVA cutter always hits bare foundations; other tool probabilities remain unchanged");
        }
        foreach (var (name, size, slots, baseTexture) in new[]
        {
            ("DeltaV_InertiaCoupler", "(2,2)", 0, "Things/Building/SpaceWeapons/PointDefense_Base"),
            ("DeltaV_MiningBeam", "(2,2)", 1, "Things/Building/SpaceWeapons/Turret2_Base"),
            ("DeltaV_MiningBeamMedium", "(3,3)", 2, "Things/Building/SpaceWeapons/Turret3_Base")
        })
        {
            XmlNode original = engineering.SelectSingleNode($"/Defs/ThingDef[defName='{name}']");
            XmlNode resolved = XmlInheritance.GetResolvedNodeFor(original);
            Check(resolved["size"].InnerText == size, name + " real XML inheritance preserves footprint");
            Check(resolved["thingClass"].InnerText == "DeltaV.Building_SpaceTurretGun",
                name + " uses existing turret implementation");
            XmlNodeList weaponComps = resolved.SelectNodes(
                "comps/li[@Class='DeltaV.CompProperties_SpaceWeapon' or @Class='DeltaV.CompProperties_MiningBeam']");
            Check(weaponComps.Count == 1 && weaponComps[0]["manualOnly"].InnerText == "true",
                name + " inherits exactly one manual engineering weapon");
            Check(resolved.SelectNodes("comps/li[@Class='CompProperties_Mannable']").Count == 0
                && resolved["hasInteractionCell"].InnerText == "false", name + " has no phantom operator");
            Check(resolved.SelectNodes("comps/li[@Class='CompProperties_Power']").Count == 1
                && resolved.SelectNodes("comps/li[@Class='DeltaV.CompProperties_SpaceWeaponTraverse']").Count == 1,
                name + " retains power and traverse through list inheritance");
            Check(resolved.SelectSingleNode("graphicData/texPath").InnerText == baseTexture,
                name + " uses the intended fixed base");
            XmlNode mining = resolved.SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_MiningBeam']");
            Check((mining?.SelectNodes("attachmentSlots/li").Count ?? 0) == slots,
                name + " exposes the intended mining attachment count");
            if (slots > 0)
                Check(mining.SelectNodes("allowedAttachmentDefs/li").Cast<XmlNode>()
                        .Select(node => node.InnerText).SequenceEqual(new[]
                        { "DeltaV_Building_MiningCargoAttachment", "DeltaV_Building_MiningTargetIdentifier" }),
                    name + " accepts only mining cargo and identification attachments");
            foreach (string xpath in new[] { "graphicData/texPath", "uiIconPath",
                "building/blueprintGraphicData/texPath", "modExtensions/li/topTexPath" })
                Check(File.Exists("1.6/Textures/" + resolved.SelectSingleNode(xpath).InnerText + ".png"),
                    name + " texture exists: " + xpath);
        }
        XmlNode cargo = engineering.SelectSingleNode("/Defs/ThingDef[defName='DeltaV_Building_MiningCargoAttachment']");
        Check(cargo["thingClass"].InnerText == "DeltaV.Building_MiningCargoAttachment"
            && cargo.SelectSingleNode("building/maxItemsInCell").InnerText == "8"
            && cargo.SelectSingleNode("building/defaultStorageSettings/priority").InnerText == "Low",
            "mining cargo uses vanilla storage settings with eight stacks and the lowest player priority");
        Check(cargo.SelectNodes("comps/li[@Class='DeltaV.CompProperties_MiningCargoAttachment']/allowedHostDefs/li").Count == 2
            && cargo.SelectSingleNode("building/isEdifice").InnerText == "false"
            && cargo.SelectSingleNode("building/storageGroupTag").InnerText == "DeltaVMiningCargo"
            && cargo.SelectNodes("inspectorTabs/li").Count == 1
            && cargo.SelectSingleNode("inspectorTabs/li").InnerText == "ITab_Storage"
            && cargo.SelectNodes("modExtensions/li[@Class='AdaptiveStorage.Extension']").Count == 0
            && engineering.SelectNodes("/Defs/AdaptiveStorage.GraphicsDef[targetDef='DeltaV_Building_MiningCargoAttachment']").Count == 0,
            "mining cargo is a vanilla non-edifice storage attachment without an ASF inventory");
        Check(cargo.SelectNodes("building/fixedStorageSettings/filter/disallowedCategories/li").Cast<XmlNode>()
                .Select(node => node.InnerText).SequenceEqual(new[] { "Plants", "Buildings" })
            && cargo.SelectNodes("building/defaultStorageSettings/filter/categories/li").Cast<XmlNode>()
                .Any(node => node.InnerText == "Chunks")
            && cargo.SelectSingleNode("building/fixedStorageSettings/filter/specialFiltersToDisallow/li").InnerText
                == "AllowLargeCorpses",
            "mining cargo keeps shelf safety restrictions while accepting rock chunks by default");
        string miningAttachments = File.ReadAllText("Source/Buildings/MiningBeamAttachments.cs");
        string miningStoragePatch = File.ReadAllText("Source/Harmony/Patch_MiningCargoStorage.cs");
        string miningImpact = File.ReadAllText("Source/Core/SpaceImpactEffects.Engineering.cs");
        Check(miningAttachments.Contains("Building_MiningCargoAttachment : Building_Storage")
            && miningAttachments.Contains("ThingPlaceMode.Direct")
            && !miningAttachments.Contains("using AdaptiveStorage"),
            "mining cargo reuses vanilla Building_Storage and direct native placement");
        Check(miningStoragePatch.Contains("SlotGroupParentAt(c)")
            && miningStoragePatch.Contains("__result = cargo.MaxItemsInCell")
            && miningStoragePatch.Contains("SteadyEnvironmentEffects.ProtectedByEdifice"),
            "the non-edifice adapter is limited to the real mining-cargo slot group");
        Check(!miningImpact.Contains("QueueMiningEjection")
            && miningImpact.Split("MiningBeamOutputRouter.Deliver").Length >= 3,
            "ship and EVA mining outputs enter the same delivery service");
        XmlNode identifier = engineering.SelectSingleNode("/Defs/ThingDef[defName='DeltaV_Building_MiningTargetIdentifier']");
        Check(identifier.SelectNodes("inspectorTabs/li").Count == 0
            && identifier.SelectNodes("comps/li[@Class='DeltaV.CompProperties_MiningTargetIdentifier']/allowedHostDefs/li").Count == 2
            && miningAttachments.Contains("Command_BatchSetting<CompMiningTargetIdentifier>")
            && miningAttachments.Contains("Dialog_MiningTargetFilter"),
            "target identifier exposes a groupable filter command and only attaches to mining beams");
        foreach (string texture in new[] { "MiningCargoAttachment", "MiningTargetIdentifier" })
            Check(File.Exists("1.6/Textures/Things/Building/SpaceWeapons/" + texture + ".png"),
                texture + " texture exists");
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
        foreach (var (defs, weaponName, beamName, impactName, range, dps, yield) in new[]
        {
            (engineering, "DeltaV_MiningBeam", "DeltaV_Beam_Mining", "DeltaV_Impact_MiningStructure", 65f, 480f, 0.5f),
            (engineering, "DeltaV_MiningBeamMedium", "DeltaV_Beam_MiningMedium", "DeltaV_Impact_MiningStructure", 80f, 720f, 0.5f),
            (eva, "DeltaV_EvaCutter", "DeltaV_Beam_EvaCutter", "DeltaV_Impact_EvaCutBuilding", 6f, 360f, 1f)
        })
        {
            XmlNode weapon = XmlInheritance.GetResolvedNodeFor(defs.SelectSingleNode($"/Defs/*[defName='{weaponName}']"))
                .SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_MiningBeam']");
            XmlNode impact = XmlInheritance.GetResolvedNodeFor(defs.SelectSingleNode($"/Defs/*[defName='{impactName}']"))
                ["defenderEffect"];
            Check(impact["kind"].InnerText == "CutStructure"
                && Number(weapon["cutDamage"]) * 60 / Number(weapon["cooldownTicks"]) == dps
                && Number(weapon["miningYieldFactor"]) == yield && Number(weapon["dismantleYieldFactor"]) == yield
                && (weapon["deliverToEmitter"]?.InnerText == "true") == (yield < 1)
                && Number(weapon["range"]) == range,
                weaponName + " uses the shared cutter with configured DPS, range, yields and destination");
            XmlNode projectile = XmlInheritance.GetResolvedNodeFor(defs.SelectSingleNode($"/Defs/ThingDef[defName='{beamName}']"))
                .SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceProjectile']");
            var props = new CompProperties_SpaceProjectile { laserRange = Number(projectile["laserRange"]) };
            Check(props.laserRange == range && SpaceShieldProjectileBehavior.IsLaser(props)
                && projectile["ignoresShields"]?.InnerText != "true",
                beamName + " enters the shared electromagnetic shield path");
            XmlNode guard = defs.SelectSingleNode($"/Defs/*[defenderEffect/kind='ShieldDrain' and projectileDefs/li='{beamName}']");
            Check(guard != null && Number(guard.SelectSingleNode("defenderEffect/damage")) == 0
                && guard.SelectSingleNode("attackerEffect/kind").InnerText == "DestroySelf",
                beamName + " stops at guard shields without draining them");
        }
        Check(Math.Abs(Number(coupler["shotEnergy"]) * 60 - 6) < 0.0001f,
            "continuous coupling consumes the configured 6 Wd per second");
        XmlNode coupling = engineering.SelectSingleNode("/Defs/DeltaV.SpaceImpactEffectDef[@Name='DeltaV_CouplingImpact']/defenderEffect");
        Check(Math.Abs(Math.Pow(1 - Number(coupling["couplingFraction"]), 12) - 0.85) < 0.00001
            && Math.Abs(Number(coupling["maxCouplingImpulse"]) * 12 - 400) < 0.001
            && Math.Abs(Number(coupling["maxCouplingAngularImpulse"]) * 12 - 4000) < 0.01,
            "continuous damping and impulse limits preserve the former time scale");
        Check(engineering.SelectNodes("/Defs/DeltaV.SpaceImpactEffectDef[defenderEffect/kind='ShieldDrain']/projectileDefs/li[text()='DeltaV_Beam_InertiaCoupler']").Count == 0,
            "no shield-drain outcome remains assigned to the coupler");
        XmlInheritance.Clear();
    }
    static Type XmlType(string name) => new[] { typeof(Thing).Assembly, typeof(ShipBase).Assembly }
        .SelectMany(a => new[] { name, "RimWorld." + name, "Verse." + name }.Select(n => a.GetType(n)))
        .FirstOrDefault(t => t != null);

    static void CheckFields(XmlNode node, Type type)
    {
        if (node.Attributes?["Class"] is XmlAttribute attribute)
            type = XmlType(attribute.Value);
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;
            var field = type.GetField(child.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Check(field != null, "EVA XML field exists: " + type.Name + "." + child.Name);
            Type value = field.FieldType;
            if (value.IsEnum) Check(Enum.IsDefined(value, Enum.Parse(value, child.InnerText)), "EVA XML enum resolves");
            if (value.IsGenericType && value.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type entry = value.GetGenericArguments()[0];
                if (entry == typeof(CompProperties) || entry == typeof(VirtualWeaponSlot))
                    foreach (XmlNode item in child.ChildNodes)
                        if (item.NodeType == XmlNodeType.Element) CheckFields(item, entry);
            }
            else if (value == typeof(ShellRenderConfig) || value == typeof(ImpactOutcomePart)) CheckFields(child, value);
        }
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
        object[] known = { 10f, 20f, 10f, 20f,
            new Vector2(2, 0), 1f, new Vector2(0, 0), -1f, 1f, 10000f, 10000f };
        Couple.Invoke(null, known);
        Check((Vector2)known[4] == Vector2.right && (Vector2)known[6] == Vector2.right
            && (float)known[5] == 0f && (float)known[7] == 0f, "equal masses share momentum, not absolute stop");
        var random = new System.Random(1827);
        float R(float low, float high) => low + (float)random.NextDouble() * (high - low);
        for (int i = 0; i < 2000; i++)
            RunCase(R(1, 10000), R(1, 100000), R(1, 10000), R(1, 100000),
                new Vector2(R(-100, 100), R(-100, 100)), R(-5, 5),
                new Vector2(R(-100, 100), R(-100, 100)), R(-5, 5),
                i % 10 == 0 ? 0f : R(0.01f, 1), i % 11 == 0 ? 0f : 400f, i % 11 == 0 ? 0f : 4000f);

        Vector2 translation = new Vector2(4, -6);
        float omega = 0.4f;
        object[] rigid = { 200f, 1000f, 20f, 100f,
            translation, omega, translation, omega, 1f, 1000f, 10000f };
        Couple.Invoke(null, rigid);
        Check((Vector2)rigid[4] == translation && (Vector2)rigid[6] == translation
            && (float)rigid[5] == omega && (float)rigid[7] == omega,
            "common linear velocity and spin remain unchanged without absolute drag");

        object[] drifting = { 200f, 1000f, 20f, 100f,
            new Vector2(10f, -5f), .05f, Vector2.zero, -.1f, .013451947f, 33.333333f, 333.333333f };
        for (int tick = 0; tick < 2400; tick++) Couple.Invoke(null, drifting);
        Check(((Vector2)drifting[4] - (Vector2)drifting[6]).magnitude < 1e-6f
            && Math.Abs((float)drifting[5] - (float)drifting[7]) < 1e-7f,
            "bounded continuous pulses remove the final linear and spin residual without passive drag");
        Check(((Vector2)drifting[4] - new Vector2(2000f / 220f, -1000f / 220f)).magnitude < .001f,
            "residual cleanup preserves shared drift instead of stopping the target absolutely");
    }
}
