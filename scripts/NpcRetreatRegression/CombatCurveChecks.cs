using System;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    static void CheckCombatCurves()
    {
        CheckCombatTactics();
        CombatManeuverCurves defaults = CombatManeuverCurveDefaults.For(null, 30f, 100f);
        Check(defaults.distance.Evaluate(100f) > defaults.distance.Evaluate(160f)
            && defaults.distance.Evaluate(160f) > defaults.distance.Evaluate(240f),
            "production default distance curve rewards re-entering combat");
        Check(defaults.projectileSeconds.Evaluate(0.5f)
            < defaults.projectileSeconds.Evaluate(5f),
            "production weapon-impact curve distinguishes near-term fire");
        Check(defaults.incoming.Evaluate(800f) < defaults.incoming.Evaluate(500f),
            "default incoming curve distinguishes heavy volleys beyond ordinary firepower");
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

        float still = CombatWeaponThreat.HitProbability(5f, 2f, 0f);
        float weak = CombatWeaponThreat.HitProbability(5f, 2f, 2f);
        float moving = CombatWeaponThreat.HitProbability(5f, 2f, 10f);
        Check(still > 0.9f && Mathf.Abs(still - weak) < 0.001f
            && moving < 0.1f,
            "weapon threat falls only when real lateral displacement clears the target width");
        Check(CombatWeaponThreat.ReachFactor(0f, 0f) == 1f
            && CombatWeaponThreat.ReachFactor(0.5f, 1f) > 0f
            && CombatWeaponThreat.ReachFactor(1.5f, 1f) == 0f,
            "weapon risk follows the reachable firing arc");

        var driveReport = new ShipPhysics.MassReport { mass = 100f, inertia = 1000f };
        driveReport.thrusters.Add(new ShipPhysics.Thruster
            { dir = Vector2.right, thrust = 1000f, operational = true });
        var drive = new DriveInfo(driveReport);
        float side = drive.PreviewBraking(-SpaceTransform.RotateInverse(Vector2.up, 0f));
        float turned = drive.PreviewBraking(-SpaceTransform.RotateInverse(
            Vector2.up, Mathf.PI * 0.5f));
        Check(turned > side + 1f,
            "main thrust becomes a real dodge axis after rotating the hull");
        float frontReserve = CombatAttackPose.Acceleration(drive, Vector2.up, 0f);
        float sideReserve = CombatAttackPose.Acceleration(drive, Vector2.up, Mathf.PI * 0.5f);
        Check(sideReserve > frontReserve + 1f,
            "turning the main engines across the firing line increases real lateral capability");
    }
}
