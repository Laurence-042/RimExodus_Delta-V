using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static readonly Dictionary<string, DefModExtension_ThrusterPower> configuredThrust = new();

    static void InitializeThrusterDefs(string prefabRoot)
    {
        var binding = AccessTools.Field(typeof(DefOfHelper), "bindingNow");
        bool before = (bool)binding.GetValue(null); binding.SetValue(null, true);
        try
        {
            StatDefOf.GravshipRange ??= new StatDef { defName = "GravshipRange" };
            var xml = new XmlDocument();
            xml.Load(Path.GetFullPath(Path.Combine(prefabRoot, "../../1.6/Patches/DeltaV_ThrusterPower.xml")));
            configuredThrust.Clear();
            foreach (XmlNode operation in xml.SelectNodes("/Patch/Operation[@Class='PatchOperationAddModExtension']"))
            {
                string name = operation["xpath"].InnerText.Split('"')[1];
                var extension = DirectXmlToObject.ObjectFromXml<DefModExtension_ThrusterPower>(operation["value"]["li"], false);
                configuredThrust.Add(name, extension);
            }
            ThingDefOf.SmallThruster = NativeThrusterDef("SmallThruster");
            ThingDefOf.LargeThruster = NativeThrusterDef("LargeThruster");
        }
        finally { binding.SetValue(null, before); }
    }

    static ThingDef NativeThrusterDef(string name)
    {
        var def = Raw<ThingDef>(); def.defName = name;
        ApplyNativeThrust(def);
        return def;
    }

    static void ApplyNativeThrust(ThingDef def)
    {
        string range = SourceChain(def.defName).Select(n => n.SelectSingleNode(
            "comps/li[@Class='CompProperties_GravshipThruster']/statOffsets/GravshipRange")?.InnerText)
            .FirstOrDefault(v => v != null);
        def.comps ??= new List<CompProperties>();
        def.comps.Add(new CompProperties_GravshipThruster { statOffsets = new List<StatModifier>
            { new StatModifier { stat = StatDefOf.GravshipRange,
                value = range == null ? 0 : float.Parse(range, CultureInfo.InvariantCulture) } } });
        if (configuredThrust.TryGetValue(def.defName, out var extension))
            (def.modExtensions ??= new List<DefModExtension>()).Add(extension);
    }

    sealed class DerivedThrusterProperties : CompProperties_GravshipThruster { }

    static void ThrusterThrustChecks(Game game, Harmony harmony, string prefabRoot)
    {
        var binding = AccessTools.Field(typeof(DefOfHelper), "bindingNow");
        bool before = (bool)binding.GetValue(null); binding.SetValue(null, true);
        var maps = game.Maps.ToArray();
        try
        {
            Check(ThrusterPower.BaseThrust(ThingDefOf.SmallThruster) == 800
                && ThrusterPower.BaseThrust(ThingDefOf.LargeThruster) == 2000,
                "native XML supplies explicit small=800 and large=2000 thrust");
            ThingDef Def(float range, float? force = null)
            {
                var def = PilotFixtureDef("thrustFixture");
                def.comps.Add(new DerivedThrusterProperties { statOffsets = new List<StatModifier>
                    { new StatModifier { stat = StatDefOf.GravshipRange, value = range } } });
                if (force.HasValue) def.modExtensions = new List<DefModExtension>
                    { new DefModExtension_ThrusterPower { thrust = force } };
                return def;
            }
            ThrusterDisplayChecks(harmony, prefabRoot, Def);
            var cases = new[] { (0f,0f),(1f,80f),(6f,480f),(9.999f,799.92f),(10f,800f),
                (10.001f,800.2f),(12f,1200f),(16f,2000f),(20f,2800f),(-5f,-400f) };
            foreach (var (range, expected) in cases)
                Check(Math.Abs(ThrusterPower.BaseThrust(Def(range))-expected) < .002f,
                    "piecewise range calibration " + range.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("THRUST_CALIBRATION " + JsonSerializer.Serialize(cases.Select(c => new
                { range = c.Item1, thrust = ThrusterPower.BaseThrust(Def(c.Item1)) })));
            foreach (float force in new[] { -800f, 0f, 123.5f })
                Check(ThrusterPower.BaseThrust(Def(100, force)) == force,
                    "explicit signed/zero/fractional thrust overrides range " + force);
            var omitted = new XmlDocument(); omitted.LoadXml("<li />");
            Check(DirectXmlToObject.ObjectFromXml<DefModExtension_ThrusterPower>(omitted.DocumentElement, false).thrust == null,
                "native XML preserves omitted nullable thrust");
            foreach (float force in new[] { -800f, 0f })
            {
                omitted.LoadXml("<li><thrust>" + force.ToString(CultureInfo.InvariantCulture) + "</thrust></li>");
                var extension = DirectXmlToObject.ObjectFromXml<DefModExtension_ThrusterPower>(omitted.DocumentElement, false);
                Check(extension.thrust == force && !extension.ConfigErrors().Any(),
                    "native XML retains signed and zero values without config errors");
            }
            var missing = PilotFixtureDef("missingThrustRange");
            missing.comps.Add(new CompProperties_GravshipThruster());
            Check(ThrusterPower.BaseThrust(missing) == 0, "missing range supplies zero default thrust");
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                Check(ThrusterPower.BaseThrust(Def(10,invalid)) == 0
                    && new DefModExtension_ThrusterPower { thrust = invalid }.ConfigErrors().Any(),
                    "nonfinite explicit thrust is rejected before entering physics");
            float oldSmall = configuredThrust["SmallThruster"].thrust.Value;
            float oldLarge = configuredThrust["LargeThruster"].thrust.Value;
            try
            {
                configuredThrust["SmallThruster"].thrust = 1000;
                configuredThrust["LargeThruster"].thrust = 2600;
                Check(ThrusterPower.BaseThrust(Def(5)) == 500
                    && ThrusterPower.BaseThrust(Def(13)) == 1800,
                    "both segment slopes follow final Def calibration values");
            }
            finally { configuredThrust["SmallThruster"].thrust = oldSmall; configuredThrust["LargeThruster"].thrust = oldLarge; }

            var map = Raw<Map>(); map.info = new MapInfo { Size = new IntVec3(8,1,8) };
            game.Maps.Clear(); game.Maps.Add(map);
            var body = Raw<Building>(); body.def = Def(16,-800);
            Set(body,"mapIndexOrState",(sbyte)0); Set(body,"positionInt",new IntVec3(3,0,3));
            var limit = new CompThrusterPowerLimit { parent=body, powerPercent=50 };
            Set(body,"comps",new List<ThingComp> { limit });
            foreach (var rotation in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                body.Rotation = rotation;
                var thruster = ShipPhysics.BuildInstalledThruster(body);
                Check(thruster.operational && thruster.thrust == 400
                    && thruster.dir == -ShipPhysics.ThrustDir(rotation),
                    "negative thrust reverses actual direction and retains power limit " + rotation);
                var report = new ShipPhysics.MassReport { mass=100, inertia=100, comLocal=thruster.localPos };
                report.thrusters.Add(thruster);
                ShipPhysics.PreviewAllocation(report,new ShipPhysics.Command { fx=thruster.dir.x,fz=thruster.dir.y },
                    out var force,out float torque);
                Check(Vector2.Dot(force,thruster.dir) > 399 && Math.Abs(torque) < .01f
                    && new DeltaV.DriveInfo(report).AvailableThrust == 400,
                    "production allocator and available force use the normalized signed configuration");
                report.comLocal = thruster.localPos - Vector2.right*2;
                var command = new ShipPhysics.Command { fx=thruster.dir.x,fz=thruster.dir.y,
                    torque=Math.Sign(thruster.dir.y) };
                ShipPhysics.PreviewAllocation(report,command,out force,out torque);
                Check(Math.Abs(torque - 2*force.y) < .01f,
                    "negative configured thrust produces torque through the same force geometry");
            }
            limit.powerPercent=0;
            Check(!ShipPhysics.BuildInstalledThruster(body).operational, "zero power disables a negative thruster");
            body.def=Def(16,0); limit.powerPercent=100;
            Check(!ShipPhysics.BuildInstalledThruster(body).operational, "explicit zero force cannot drive");
            body.def=Def(6); limit.powerPercent=25;
            Check(ShipPhysics.BuildInstalledThruster(body).thrust == 120,
                "default calibrated thrust is multiplied by the user power limit exactly once");
        }
        finally { game.Maps.Clear(); game.Maps.AddRange(maps); binding.SetValue(null,before); }
    }

    static StatDrawEntry originalDisplayEntry;
    static bool FixtureOriginalStats(ref IEnumerable<StatDrawEntry> __result)
    { __result = new[] { originalDisplayEntry }; return false; }

    static void ThrusterDisplayChecks(Harmony harmony, string prefabRoot, Func<float, float?, ThingDef> makeDef)
    {
        var oldLanguage = LanguageDatabase.activeLanguage;
        var xml = new XmlDocument();
        xml.Load(Path.GetFullPath(Path.Combine(prefabRoot, "../../1.6/Defs/SpaceCombat/DeltaV_StatCategories.xml")));
        var category = DirectXmlToObject.ObjectFromXml<StatCategoryDef>(
            xml.SelectSingleNode("/Defs/StatCategoryDef[defName='DeltaV_General']"), false);
        DefDatabase<StatCategoryDef>.Add(category);
        originalDisplayEntry = new StatDrawEntry(category, "Original stat", "original", "Original explanation", 1);
        var target = AccessTools.Method(typeof(ThingDef), nameof(ThingDef.SpecialDisplayStats), new[] { typeof(StatRequest) });
        var prefix = AccessTools.Method(typeof(Program), nameof(FixtureOriginalStats));
        var postfix = AccessTools.Method(typeof(Patch_ThrusterStats), nameof(Patch_ThrusterStats.Postfix));
        harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
        try
        {
            foreach (string language in new[] { "English", "ChineseSimplified" })
            {
                LanguageDatabase.activeLanguage = StatsFixtureLanguage(prefabRoot, language);
                foreach (var def in new[] { ThingDefOf.SmallThruster, ThingDefOf.LargeThruster,
                    makeDef(6, null), makeDef(16, -800), makeDef(16, 0), makeDef(16, 123.5f),
                    makeDef(16, -.001f), makeDef(16, .001f) })
                {
                    var entries = def.SpecialDisplayStats(StatRequest.For(def, null)).ToArray();
                    Check(entries.Length == 2 && ReferenceEquals(entries[0], originalDisplayEntry),
                        "native stats Harmony hook preserves existing entries " + language);
                    Check(entries[1].category == category && entries[1].stat == null
                        && entries[1].LabelCap == "DeltaV_StatBaseThrust".Translate().ToString().CapitalizeFirst()
                        && entries[1].ValueString == ThrusterPower.BaseThrust(def).ToString("G"),
                        "native info card displays signed/configured/fallback subclass thrust " + language);
                }
                var body = Raw<Building>(); body.def = makeDef(16, -800);
                var limit = new CompThrusterPowerLimit { parent = body, powerPercent = 25 };
                Set(body, "comps", new List<ThingComp> { limit });
                Check(body.def.SpecialDisplayStats(StatRequest.For(body)).Last().ValueString == "-800",
                    "instance info card reports base thrust independently of current power limit " + language);
                var ordinary = PilotFixtureDef("ordinaryBuilding");
                ordinary.modExtensions = new List<DefModExtension> { new DefModExtension_ThrusterPower { thrust = 100 } };
                Check(ordinary.SpecialDisplayStats(StatRequest.For(ordinary, null)).SequenceEqual(new[] { originalDisplayEntry }),
                    "non-thruster buildings do not acquire a thrust entry " + language);
            }
        }
        finally
        {
            harmony.Unpatch(target, prefix); harmony.Unpatch(target, postfix);
            AccessTools.Method(typeof(DefDatabase<StatCategoryDef>), "Remove").Invoke(null, new object[] { category });
            LanguageDatabase.activeLanguage = oldLanguage;
            originalDisplayEntry = null;
        }
    }
}
