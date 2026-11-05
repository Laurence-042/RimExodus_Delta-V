using System;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    static void CheckShieldNormalEnergy()
    {
        Check(Near(SpaceKinetics.ShieldCost(1, -60, 360, 2), 42)
            && SpaceKinetics.ShieldCost(1, -60, 0, 2) == 0, "reflection pays positive work; zero impulse has no charge");
        foreach (float mass in new[] { 1f, 2f, 20f, 48f, 96f, 144f })
        foreach (float impulse in new[] { 0f, 36f, 180f, 360f })
        {
            var incoming = new Vector2(17, -60);
            Vector2 outgoing = SpaceKinetics.ApplyShieldImpulse(mass, incoming, Vector2.up, impulse);
            Check(Near(outgoing.x, 17) && Near((outgoing.y + 60) * mass, impulse), "normal momentum changes by paid impulse; tangent preserved");
            foreach (float angle in new[] { -.7f, 1.1f, 2.6f })
            {
                Vector2 rotated = SpaceKinetics.ApplyShieldImpulse(mass, SpaceTransform.Rotate(incoming, angle),
                    SpaceTransform.Rotate(Vector2.up, angle), impulse);
                Check((rotated - SpaceTransform.Rotate(outgoing, angle)).magnitude < .001f, "impulse response is invariant under shared rotation");
            }
            foreach (float available in new[] { 0f, 1f, 2f, 3f, 10f, 100f })
            {
                float paid = SpaceKinetics.FundedShieldImpulse(mass, -60, impulse, 2, available);
                Check(paid <= impulse && SpaceKinetics.ShieldCost(mass, -60, paid, 2) <= available + .00001f,
                    "partially funded pulse never borrows energy or exceeds selected impulse");
            }
        }
        var normal = new CompProperties_SpaceProjectile { shieldFragmentCount = 3 };
        var rod = new CompProperties_SpaceProjectile { shieldFragmentCount = 4, shieldBreakDeltaV = .5f };
        Check(!SpaceKinetics.Shatters(48, 0, normal) && !SpaceKinetics.Shatters(48, 180, normal)
            && SpaceKinetics.Shatters(48, 216, normal) && !SpaceKinetics.Shatters(96, 360, normal), "ordinary shell fracture threshold distinguishes intact heavy rounds");
        Check(SpaceKinetics.Shatters(60, 36, rod), "ten percent impulse can fracture the heaviest current long rod");
        normal.shieldFragmentCount = 1;
        Check(!SpaceKinetics.Shatters(1, 360, normal), "existing fragments do not fragment and multiply conversion loss again");
        Check(SpaceKinetics.FundedShieldImpulse(1, 60, 360, 2, 100) == 0, "outgoing shots receive no pulse");
        Check(SpaceKinetics.ApplyShieldImpulse(1, new Vector2(0, -60), Vector2.up, 60).sqrMagnitude == 0,
            "exact stopping impulse creates no artificial velocity");
    }
}
