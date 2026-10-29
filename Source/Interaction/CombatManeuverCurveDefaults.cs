using UnityEngine;

namespace DeltaV
{
    internal static class CombatManeuverCurveDefaults
    {
        internal static CombatManeuverCurves For(ShipBase ship, float clearance,
            float fallbackDistance)
        {
            float preferred = fallbackDistance;
            preferred = Mathf.Max(clearance + 20f, preferred);
            float middle = clearance + (preferred - clearance) * 0.5f;
            float far = preferred + Mathf.Max(12f, preferred * 0.2f);
            return new CombatManeuverCurves
            {
                distance = new CombatScoreCurve(true,
                    new CombatScorePoint(0f, -300f),
                    new CombatScorePoint(clearance, -150f),
                    new CombatScorePoint(middle, 80f),
                    new CombatScorePoint(preferred, 100f),
                    new CombatScorePoint(far, -200f),
                    new CombatScorePoint(Mathf.Max(far + 1f, 2f * preferred), -400f)),
                outgoing = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, 0f), new CombatScorePoint(10f, 20f),
                    new CombatScorePoint(50f, 50f), new CombatScorePoint(500f, 100f)),
                incoming = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, 0f), new CombatScorePoint(10f, -20f),
                    new CombatScorePoint(50f, -50f), new CombatScorePoint(500f, -100f),
                    new CombatScorePoint(1500f, -300f)),
                projectileSeconds = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, -80f), new CombatScorePoint(1f, -60f),
                    new CombatScorePoint(3f, -20f), new CombatScorePoint(5f, 0f),
                    new CombatScorePoint(8f, 0f)),
                hullOverlap = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, 0f), new CombatScorePoint(0.5f, -150f),
                    new CombatScorePoint(1f, -300f)),
                alignmentError = new CombatScoreCurve(false,
                    new CombatScorePoint(0f, 20f), new CombatScorePoint(10f, 15f),
                    new CombatScorePoint(90f, 0f), new CombatScorePoint(180f, -10f))
            };
        }
    }
}
