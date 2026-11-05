using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Projectile Def owns fragment composition; paid shield impulse changes velocity once.</summary>
    public static class SpaceShieldProjectileBehavior
    {
        public static bool IsKinetic(CompProperties_SpaceProjectile props) =>
            props != null && !props.guided && props.laserRange <= 0f;

        public static bool IsLaser(CompProperties_SpaceProjectile props) =>
            props != null && props.laserRange > 0f;

        public static void SpawnFragments(SpaceProjectileService service, ThingDef sourceDef,
            SpaceObject source, SpaceObject defender, Thing launcher, Vector3 hit,
            Vector2 relativeVelocity, Vector2 outwardNormal, float impulse, float mass,
            float armorPenetrationScale, bool reflectedByShield = false, float damageScale = 1f,
            int fragmentCount = -1, int remainingLifetimeTicks = -1)
        {
            var props = sourceDef.GetCompProperties<CompProperties_SpaceProjectile>();
            if (!IsKinetic(props)) return;
            int count = fragmentCount < 0
                ? SpaceKinetics.ShieldFragmentCount(mass, relativeVelocity, outwardNormal, impulse, props) : fragmentCount;
            if (count <= 0) return;
            ThingDef fragmentDef = (count == 1 ? props.shieldTumbleDef : props.shieldFragmentDef)
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Projectile_ShieldFragment");
            if (fragmentDef == null) return;
            Vector2 outgoing = SpaceKinetics.ApplyShieldImpulse(mass, relativeVelocity, outwardNormal, impulse);
            for (int i = 0; i < count; i++)
            {
                float span = count == 1 ? 0f : (2f * i / (count - 1) - 1f)
                    * props.shieldFragmentScatterDeg;
                Vector2 fragmentVelocity = SpaceTransform.Rotate(outgoing, span * Mathf.Deg2Rad);
                service.LaunchShieldFragment(fragmentDef, source, defender, launcher,
                    hit, fragmentVelocity, outwardNormal, mass / count, armorPenetrationScale, reflectedByShield, damageScale, remainingLifetimeTicks);
            }
        }
    }
}
