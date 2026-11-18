using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static LoadedLanguage StatsFixtureLanguage(string prefabRoot, string folder)
    {
        var language = Raw<LoadedLanguage>(); Set(language, "dataIsLoaded", true);
        Set(language, "workerInt", new LanguageWorker_English());
        language.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>();
        string directory = Path.GetFullPath(Path.Combine(prefabRoot, "../../1.6/Languages", folder, "Keyed"));
        foreach (string file in Directory.EnumerateFiles(directory, "*.xml"))
        {
            var xml = new XmlDocument(); xml.Load(file);
            foreach (XmlNode node in xml.DocumentElement.ChildNodes)
                if (node.NodeType == XmlNodeType.Element)
                    language.keyedReplacements[node.Name] = new LoadedLanguage.KeyedReplacement
                        { key = node.Name, value = node.InnerText.Replace("\\n", "\n") };
        }
        return language;
    }

    static void ShieldSettingChecks(string prefabRoot)
    {
        var oldLanguage = LanguageDatabase.activeLanguage;
        var oldCulture = CultureInfo.CurrentCulture;
        var references = new List<ThingDef>();
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            foreach (string name in new[] { "DeltaV_Projectile_Rapid", "DeltaV_Projectile_Laser", "DeltaV_Projectile_LaserHeavy" })
            {
                var properties = new CompProperties_SpaceProjectile();
                foreach (string field in new[] { "mass", "speed", "shieldFragmentCost", "shieldLaserTurnEnergy" })
                {
                    string value = SourceChain(name).Select(n => n.SelectSingleNode(
                        "comps/li[@Class='DeltaV.CompProperties_SpaceProjectile']/" + field)?.InnerText).FirstOrDefault(v => v != null);
                    if (value != null) AccessTools.Field(typeof(CompProperties_SpaceProjectile), field)
                        .SetValue(properties, float.Parse(value, CultureInfo.InvariantCulture));
                }
                var def = PilotFixtureDef(name); def.comps.Add(properties);
                DefDatabase<ThingDef>.Add(def); references.Add(def);
            }
            var shield = new CompSpaceShield { props = new CompProperties_SpaceShield
                { regularCapacity = 0 }, mode = SpaceShieldMode.Kinetic, projection = 12, length = 18 };
            foreach (string language in new[] { "English", "ChineseSimplified" })
            {
                LanguageDatabase.activeLanguage = StatsFixtureLanguage(prefabRoot, language);
                foreach (var sample in new[] { (0, "0 W·d"), (5, "2.8 W·d"), (20, "5.2 W·d"), (100, "42 W·d") })
                {
                    shield.kineticStrengthPercent = sample.Item1;
                    string description = SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldKineticStrength");
                    Check(description.Contains(sample.Item2) && !description.Contains("{0}"),
                        "kinetic strength shows fully funded per-hit cost from real projectile data " + language + " " + sample.Item1);
                }
                shield.mode = SpaceShieldMode.Electromagnetic;
                foreach (int strength in new[] { 0, 1, 50, 100 })
                {
                    shield.electromagneticStrengthPercent = strength;
                    string description = SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldElectromagneticStrength");
                    float angle = SpaceShieldRefraction.Calculate(new Vector2(.5f, -.8660254f), Vector2.up, strength).addedDeg;
                    foreach (float rate in new[] { .2f, .4f })
                    {
                        string oblique = (rate * angle / 180).ToString("0.####") + " W·d";
                        string normal = (rate * strength / 100).ToString("0.####") + " W·d";
                        Check(description.Contains(oblique) && description.Contains(normal),
                            "laser examples match production turn law at normal and oblique incidence " + language + " " + strength);
                    }
                    Check(!description.Contains("{0}") && (strength != 1 || description.Contains("0.002 W·d")),
                        "low positive laser cost remains visible instead of rounding to zero " + language);
                }
                var round = references[0].GetCompProperties<CompProperties_SpaceProjectile>();
                round.shieldFragmentCost = 5;
                shield.kineticStrengthPercent = 100;
                Check(SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldKineticStrength").Contains("45 W·d"),
                    "kinetic energy example reads the current projectile trigger cost " + language);
                round.shieldFragmentCost = 2;
                var laser = references[1].GetCompProperties<CompProperties_SpaceProjectile>();
                laser.shieldLaserTurnEnergy = .6f;
                Check(SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldElectromagneticStrength").Contains("0.6 W·d"),
                    "laser energy example reads the current projectile turn cost " + language);
                laser.shieldLaserTurnEnergy = .2f;
                shield.projection = 12; shield.length = 18;
                Check(shield.WantedPowerWatts == 41, "default line geometry requests 41 W");
                string length = SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldLength");
                string projection = SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldProjection");
                Check(length.Contains("1.5 W") && length.Contains("41 W")
                    && projection.Contains("1 W") && projection.Contains("41 W"),
                    "length and projection show their distinct configured power increments " + language);
                shield.length++;
                Check(shield.WantedPowerWatts == 42.5f
                    && SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldLength").Contains("42.5 W"),
                    "live geometry cost retains fractional watts " + language);
                shield.projection++;
                Check(shield.WantedPowerWatts == 43.5f, "one projection cell adds its configured watt");
                shield.ShieldProps.spaceLengthPowerWatts = 2.25f;
                Check(SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldLength").Contains("2.25 W"),
                    "length explanation follows customized generator properties " + language);
                shield.ShieldProps.spaceLengthPowerWatts = 1.5f;
                foreach (string key in new[] { "DeltaV_ShieldRadius", "DeltaV_ShieldProjection", "DeltaV_ShieldLength", "DeltaV_ShieldAngle" })
                {
                    string description = SpaceShieldReference.SettingDescription(shield, key);
                    Check(description.StartsWith((key + "Tip").Translate(
                        key == "DeltaV_ShieldLength" ? "1.5 W" : "1 W").ToString())
                        && !description.Contains("{0}"), "each geometry setting uses its own translated explanation " + language + " " + key);
                }
                string angleTip = SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldAngle");
                float watts = shield.WantedPowerWatts; shield.angleDeg = 75;
                Check(angleTip == SpaceShieldReference.SettingDescription(shield, "DeltaV_ShieldAngle")
                    && shield.WantedPowerWatts == watts, "rotating projection does not add maintenance power");
                shield.mode = SpaceShieldMode.Guard;
                watts = shield.WantedPowerWatts; shield.groundRadius++;
                Check(shield.WantedPowerWatts == watts, "guard radius does not add maintenance power");
                shield.mode = SpaceShieldMode.Kinetic;
            }
        }
        finally
        {
            foreach (var def in references)
                AccessTools.Method(typeof(DefDatabase<ThingDef>), "Remove").Invoke(null, new object[] { def });
            LanguageDatabase.activeLanguage = oldLanguage;
            CultureInfo.CurrentCulture = oldCulture;
        }
    }
}
