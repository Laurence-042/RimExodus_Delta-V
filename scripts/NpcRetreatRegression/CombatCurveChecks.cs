using System;
using DeltaV;

internal static partial class Program
{
    static void CheckCombatCurves()
    {
        CombatManeuverCurves defaults = CombatManeuverCurveDefaults.For(null, 30f, 100f);
        Check(defaults.distance.Evaluate(100f) > defaults.distance.Evaluate(160f)
            && defaults.distance.Evaluate(160f) > defaults.distance.Evaluate(240f),
            "production default distance curve rewards re-entering combat");
        Check(defaults.projectileSeconds.Evaluate(0.5f)
            < defaults.projectileSeconds.Evaluate(5f),
            "production projectile curve permits a temporary dodge");
        var fleeing = new CombatScoreCurve(true,
            new CombatScorePoint(50f, -100f), new CombatScorePoint(100f, 100f));
        Check(fleeing.Evaluate(150f) > fleeing.Evaluate(100f),
            "custom distance curve can explicitly prefer continued separation");
        var copy = fleeing.Copy();
        copy.points[1].y = -100f;
        Check(fleeing.points[1].y == 100f,
            "production curve copies keep each ship's points independent");
        var comp = new CompShipCombatAI();
        CombatStrategy original = comp.strategy;
        var invalid = new CombatStrategy();
        invalid.maneuverRules.Add(new CombatStrategyRule
        {
            maneuverCurves = new CombatManeuverCurves
            {
                distance = new CombatScoreCurve(false,
                    new CombatScorePoint(50f, 0f), new CombatScorePoint(50f, 10f))
            }
        });
        bool rejected = false;
        try { comp.Replace(invalid); }
        catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected && ReferenceEquals(comp.strategy, original),
            "invalid imported curve leaves selected console strategy intact");
    }
}
