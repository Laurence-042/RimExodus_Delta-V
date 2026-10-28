using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using DeltaV;

static class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Main()
    {
        var strategy = new CombatStrategy { name = "multi ship" };
        strategy.maneuverRules.Add(new CombatStrategyRule
        {
            name = "critical", conditions = new List<CombatCriterion>
            {
                new CombatCriterion { metric = CombatMetric.OwnHullFraction,
                    comparison = CombatComparison.Less, value = 0.4f },
                new CombatCriterion { metric = CombatMetric.IncomingProjectileSeconds,
                    comparison = CombatComparison.Less, value = 3f }
            },
            maneuverCurves = new CombatManeuverCurves
            {
                distance = new CombatScoreCurve(true,
                    new CombatScorePoint(30f, -20f),
                    new CombatScorePoint(60f, 15f),
                    new CombatScorePoint(90f, 25f))
            }
        });
        strategy.maneuverRules.Add(new CombatStrategyRule { name = "general" });
        var safe = new CombatStrategyContext(1f, 1f, 0f, 1f, 0f, 1f, 1f, 0f, 40f, 1f, 100f);
        var threatened = new CombatStrategyContext(0.2f, 0.5f, 0f, 0f, 4f, 1f, 1f, 0f, 40f, 1f, 2f);
        Check(strategy.Maneuver(safe).name == "general", "ordered rule fallback");
        Check(strategy.Maneuver(threatened).name == "critical", "combined conditions");
        strategy.maneuverRules[0].matchAny = true;
        Check(strategy.Maneuver(safe).name == "general", "OR rule keeps unmatched conditions false");
        var lowHull = new CombatStrategyContext(0.2f, 1f, 0f, 1f, 0f, 1f, 1f, 0f, 40f, 1f, 100f);
        Check(strategy.Maneuver(lowHull).name == "critical", "OR rule matches one condition");
        strategy.maneuverRules[0].matchAny = false;
        var secondShip = strategy.Copy();
        secondShip.maneuverRules[0].conditions[0].value = 0.1f;
        secondShip.maneuverRules[0].maneuverCurves.distance.points[1].y = 200f;
        Check(strategy.Maneuver(threatened).name == "critical"
            && secondShip.Maneuver(threatened).name == "general", "ship copies are independent");
        Check(strategy.maneuverRules[0].maneuverCurves.distance.points[1].y == 15f,
            "curve point copies are independent");
        var curve = strategy.maneuverRules[0].maneuverCurves.distance;
        Check(Math.Abs(curve.Evaluate(45f) + 2.5f) < 0.001f,
            "piecewise interpolation");
        Check(Math.Abs(curve.Evaluate(120f) - 35f) < 0.001f,
            "right tail extends final segment");
        var clamped = new CombatScoreCurve(false,
            new CombatScorePoint(0f, 0f), new CombatScorePoint(10f, 10f));
        Check(clamped.Evaluate(20f) == 10f, "ordinary curve clamps right tail");
        var serializer = new XmlSerializer(typeof(CombatStrategy));
        using var xml = new StringWriter();
        serializer.Serialize(xml, strategy);
        using var input = new StringReader(xml.ToString());
        var restored = (CombatStrategy)serializer.Deserialize(input);
        restored.Validate();
        Check(restored.Maneuver(threatened).name == "critical", "XML rule roundtrip");
        Check(restored.maneuverRules[0].maneuverCurves.distance.points[1].x == 60f
            && restored.maneuverRules[0].maneuverCurves.distance.extendRight
            && Math.Abs(restored.maneuverRules[0].maneuverCurves.distance.Evaluate(120f) - 35f)
                < 0.001f, "XML curve point and tail roundtrip");
        var allCurves = new CombatManeuverCurves();
        foreach (ManeuverCurveMetric metric in Enum.GetValues(typeof(ManeuverCurveMetric)))
            allCurves.Set(metric, new CombatScoreCurve(false,
                new CombatScorePoint(0f, -1f), new CombatScorePoint(1f, 1f)));
        strategy.maneuverRules[0].maneuverCurves = allCurves;
        using var fullXml = new StringWriter();
        serializer.Serialize(fullXml, strategy);
        using var fullInput = new StringReader(fullXml.ToString());
        var allRestored = (CombatStrategy)serializer.Deserialize(fullInput);
        allRestored.Validate();
        Check(Enum.GetValues(typeof(ManeuverCurveMetric)).Cast<ManeuverCurveMetric>()
            .All(metric => allRestored.maneuverRules[0].maneuverCurves.Get(metric)
                .Evaluate(0.5f) == 0f), "all six XML curves roundtrip");
        restored.maneuverRules[0].maneuverCurves.distance.points[1].x = 30f;
        bool invalidPoints = false;
        try { restored.Validate(); }
        catch (InvalidDataException) { invalidPoints = true; }
        Check(invalidPoints, "duplicate curve x rejected");
        restored.maneuverRules[0].maneuverCurves.distance.points[1].x = float.NaN;
        invalidPoints = false;
        try { restored.Validate(); }
        catch (InvalidDataException) { invalidPoints = true; }
        Check(invalidPoints, "nonfinite curve coordinate rejected");
        restored.maneuverRules[0].maneuverCurves.distance.points[1].x = 60f;
        restored.format++;
        bool rejected = false;
        try { restored.Validate(); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "invalid format rejected");
        Console.WriteLine("PASS 15 combat strategy checks");
    }
}

namespace Verse
{
    public interface IExposable { void ExposeData(); }
    public enum LoadSaveMode { Inactive, PostLoadInit }
    public enum LookMode { Deep }
    public static class Scribe { public static LoadSaveMode mode; }
    public static class Scribe_Values
    {
        public static void Look<T>(ref T value, string label, T defaultValue = default) { }
    }
    public static class Scribe_Collections
    {
        public static void Look<T>(ref List<T> list, string label, LookMode mode) { }
    }
    public static class Scribe_Deep
    {
        public static void Look<T>(ref T value, string label) where T : class { }
    }
    public readonly struct CurvePoint
    {
        public readonly float x, y;
        public CurvePoint(float x, float y) { this.x = x; this.y = y; }
    }
    public sealed class SimpleCurve
    {
        private readonly CurvePoint[] points;
        public SimpleCurve(IEnumerable<CurvePoint> points) =>
            this.points = points.OrderBy(p => p.x).ToArray();
        public float Evaluate(float x)
        {
            if (x <= points[0].x) return points[0].y;
            for (int i = 1; i < points.Length; i++)
                if (x <= points[i].x)
                    return points[i - 1].y + (points[i].y - points[i - 1].y)
                        * (x - points[i - 1].x) / (points[i].x - points[i - 1].x);
            return points[points.Length - 1].y;
        }
    }
}

namespace UnityEngine
{
    public static class Mathf { public static float Abs(float value) => Math.Abs(value); }
}
