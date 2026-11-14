using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using RimExodus.Maps;
using Verse;

internal readonly struct PrefabEnvironmentRequirements
{
    internal readonly bool RequireEnvironment, RequireRoofCoverage;
    internal readonly float? ExpectedTemperature, ExpectedVacuum;

    internal PrefabEnvironmentRequirements(bool requireEnvironment = false, bool requireRoofCoverage = false,
        float? expectedTemperature = null, float? expectedVacuum = null)
    {
        RequireEnvironment = requireEnvironment;
        RequireRoofCoverage = requireRoofCoverage;
        ExpectedTemperature = expectedTemperature;
        ExpectedVacuum = expectedVacuum;
    }
}

internal static class PrefabContentValidation
{
    internal static MapPrefabDocument Validate(XmlDocument xml, string label, PrefabEnvironmentRequirements requirements,
        List<string> errors, Action<bool, string> check)
    {
        // Use the production environment reader without bootstrapping the game's content Def databases.
        var environmentXml = (XmlDocument)xml.CloneNode(true);
        foreach (string xpath in new[] { "/MapPrefab/prefab/terrain", "/MapPrefab/foundations", "/MapPrefab/roofs" })
        {
            var node = environmentXml.SelectSingleNode(xpath);
            node?.ParentNode.RemoveChild(node);
        }
        environmentXml.SelectSingleNode("/MapPrefab/thingStates")?.RemoveAll();
        MapPrefabDocument document;
        var harmony = new Harmony("DeltaV.OfflinePrefab.Environment");
        bool profiling = DeepProfiler.enabled;
        harmony.Patch(AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.LogVerbose)),
            prefix: new HarmonyMethod(typeof(PrefabContentValidation), nameof(QuietLog)));
        try { document = MapPrefabDocument.FromXml(environmentXml.OuterXml); }
        catch (Exception error) { throw new InvalidDataException(label + " REF environment validation failed", error); }
        finally { DeepProfiler.enabled = profiling; harmony.UnpatchAll(harmony.Id); }
        check(true, label + " REF environment validation");

        var samples = document.environment;
        if (requirements.RequireEnvironment)
            check(samples.Count > 0, label + " requires environment samples");
        if (requirements.RequireRoofCoverage)
        {
            var covered = xml.SelectNodes("/MapPrefab/roofs/li/pos|/MapPrefab/foundations/li[def='DeltaV_MoonPool']/pos")
                .Cast<XmlNode>().Select(n => IntVec3.FromString(n.InnerText)).ToHashSet();
            check(samples.SelectMany(e => e.cells).ToHashSet().SetEquals(covered),
                label + " environment covers exactly the roof and moonpool cells");
        }
        if (requirements.ExpectedTemperature is float temperature)
            check(samples.All(e => e.temperature == temperature), label + $" sample temperature equals {temperature}");
        if (requirements.ExpectedVacuum is float vacuum)
            check(samples.All(e => e.vacuum == vacuum), label + $" sample vacuum equals {vacuum}");

        var registry = new LoadedObjectDirectory();
        int before = errors.Count;
        foreach (XmlNode node in xml.SelectNodes("/MapPrefab/thingStates/li/state/saveable/descendant-or-self::*[def and id]"))
        {
            // RimWorld also registers the numeric suffix across different ThingDef prefixes.
            var thing = (ThingWithComps)RuntimeHelpers.GetUninitializedObject(typeof(ThingWithComps));
            thing.def = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
            thing.def.defName = node["def"].InnerText;
            thing.thingIDNumber = Thing.IDNumberFromThingID(node["id"].InnerText);
            registry.RegisterLoaded(thing);
        }
        check(errors.Count == before, label + " native root and nested Thing IDs: " + string.Join("\n", errors.Skip(before)));
        return document;
    }

    private static bool QuietLog(ref bool __result) { __result = false; return false; }
}
