using System;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    static void CheckShieldNormalEnergy()
    {
        Check(Near(SpaceKinetics.ShieldCost(SpaceKinetics.ShieldNormalEnergy(100), 2), 18)
            && Near(SpaceKinetics.ShieldCost(0, 2), 2), "pulse cost buys only the selected normal budget plus fragmentation");
        foreach (float mass in new[] { 1f, 24f, 48f })
        foreach (float ratio in new[] { 0f, .5f, 1f, 2f })
        {
            var incoming = new Vector2(17, -60);
            float normalEnergy = SpaceKinetics.Energy(mass, 60);
            Vector2 outgoing = SpaceKinetics.ApplyShieldNormalEnergy(mass, incoming, Vector2.up, normalEnergy * ratio);
            float expectedNormal = ratio < 1 ? -60 * (float)Math.Sqrt(1 - ratio) : 60 * (float)Math.Sqrt(ratio - 1);
            Check(Near(outgoing.x, 17) && Near(outgoing.y, expectedNormal),
                $"normal budget slows, stops or reflects without changing tangent: mass={mass}, ratio={ratio}");
            Vector2 fastTangent = SpaceKinetics.ApplyShieldNormalEnergy(mass, new Vector2(1700, -60), Vector2.up, normalEnergy * ratio);
            Check(Near(fastTangent.x, 1700) && Near(fastTangent.y, outgoing.y), "tangential energy cannot consume normal budget");
            Vector2 fragment = SpaceKinetics.ApplyShieldNormalEnergy(mass / 3, incoming, Vector2.up, normalEnergy * ratio / 3);
            Check((fragment - outgoing).magnitude < .001f, "mass and paid energy split preserve group response");
            foreach (float angle in new[] { -.7f, 1.1f, 2.6f })
            {
                Vector2 rotated = SpaceKinetics.ApplyShieldNormalEnergy(mass,
                    SpaceTransform.Rotate(incoming, angle), SpaceTransform.Rotate(Vector2.up, angle), normalEnergy * ratio);
                // At the exact stopping boundary float projection error is amplified by sqrt.
                Check((rotated - SpaceTransform.Rotate(outgoing, angle)).magnitude < .05f,
                    "normal response is invariant under a shared rotation");
            }
        }
        Vector2 slow = SpaceKinetics.ApplyShieldNormalEnergy(1, new Vector2(0, -60), Vector2.up, 28800);
        Vector2 heavy = SpaceKinetics.ApplyShieldNormalEnergy(48, new Vector2(0, -60), Vector2.up, 28800);
        Check(slow.y > 0 && heavy.y < 0, "same fully funded shield reflects a light round but only slows a heavy one");
        Vector2 first = SpaceKinetics.ApplyShieldNormalEnergy(1, new Vector2(0, -60), Vector2.up, 3000);
        Vector2 second = SpaceKinetics.ApplyShieldNormalEnergy(1, first, -Vector2.up, 3000);
        Check((second - new Vector2(0, -60)).magnitude < .001f, "opposed shields may keep bouncing without extra attenuation");
        Check(SpaceKinetics.ApplyShieldNormalEnergy(1, new Vector2(0, -60), Vector2.up, 1800).sqrMagnitude == 0,
            "exact head-on stopping budget leaves no artificial motion");
    }
}
