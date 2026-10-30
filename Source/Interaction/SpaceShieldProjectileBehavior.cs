using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Projectile Def owns fragment composition and pulse energy; shield owns velocity change.</summary>
    public static class SpaceShieldProjectileBehavior
    {
        public static bool IsKinetic(CompProperties_SpaceProjectile props) =>
            props != null && !props.guided && props.laserRange <= 0f;

        public static bool IsLaser(CompProperties_SpaceProjectile props) =>
            props != null && props.laserRange > 0f;

        public static void SpawnFragments(SpaceProjectileService service, ThingDef sourceDef,
            SpaceObject source, SpaceObject defender, Thing launcher, Vector3 hit,
            Vector2 direction, float signedSpeed, float mass, float armorPenetrationScale)
        {
            var props = sourceDef.GetCompProperties<CompProperties_SpaceProjectile>();
            if (!IsKinetic(props)) return;
            ThingDef fragmentDef = props.shieldFragmentDef
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Projectile_ShieldFragment");
            if (fragmentDef == null) return;
            int count = Mathf.Clamp(props.shieldFragmentCount, 1, 12);
            for (int i = 0; i < count; i++)
            {
                float span = count == 1 ? 0f : (2f * i / (count - 1) - 1f)
                    * props.shieldFragmentScatterDeg;
                Vector2 fragmentDirection = SpaceTransform.Rotate(direction, span * Mathf.Deg2Rad);
                service.LaunchShieldFragment(fragmentDef, source, defender, launcher,
                    hit, fragmentDirection, signedSpeed, mass / count, armorPenetrationScale);
            }
        }
    }
}
