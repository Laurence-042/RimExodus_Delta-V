using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal static class WeaponHeatVisuals
    {
        internal static int BarrelHeatLevel(CompWeaponTuning heat) =>
            heat?.Settings.chemical == true ? Mathf.Clamp(Mathf.RoundToInt(32f * heat.HeatFraction), 0, 32) : 0;

        internal static int BodyHeatLevel(CompWeaponTuning heat) =>
            heat?.Settings.chemical == true && heat.overheated ? 32 : 0;

        internal static Color HeatColor(int level) =>
            Color.Lerp(Color.white, new Color(1f, .16f, .025f), level / 32f);

        // The top artwork faces right. Offsets are fractions of its rotating square draw size.
        internal static int SmokePortCount(CompWeaponTuning heat) =>
            heat?.Settings.chemical == true ? heat.Settings.lockEveryShot ? 1 : 2 : 0;

        internal static void SmokePort(Building_Turret turret, float shellTopAngle, bool cannon,
            int index, out Vector2 point, out Vector2 direction)
        {
            float side = index == 0 ? 1f : -1f;
            Vector3 offset = cannon ? new Vector3(-.06f, 0f, .12f)
                : new Vector3(-.03f, 0f, .16f * side);
            Vector3 outward = cannon ? Vector3.forward : new Vector3(0f, 0f, side);
            float angle = TurretTop.ArtworkRotation + shellTopAngle;
            offset = (offset * turret.def.building.turretTopDrawSize).RotatedBy(angle);
            outward = outward.RotatedBy(angle);
            Vector3 center = turret.DrawPos +
                ((turret as ThingWithComps)?.TryGetComp<CompSpaceWeapon>()?.VisualRecoilOffset
                    ?? Vector3.zero);
            point = new Vector2(center.x + offset.x, center.z + offset.z);
            direction = new Vector2(outward.x, outward.z);
        }

        internal static void EmitGroundCoolingSmoke(Building_SpaceTurretGun turret) =>
            EmitCoolingSmoke(turret, turret.ShellTopAngle,
                turret.TryGetComp<CompWeaponTuning>()?.Settings.lockEveryShot ?? false);

        /// <summary>
        /// Vents smoke from the rotating turret top while a weapon is cooling down. Adapted
        /// turrets share the heavy-cannon single top port; their cooldown fact is the persisted
        /// fire-control cooldown, not any source-side field.
        /// </summary>
        internal static void EmitCoolingSmoke(Building_Turret turret, float shellTopAngle,
            bool cannon)
        {
            if (!turret.Spawned) return;
            int count = cannon ? 1 : 2;
            for (int i = 0; i < count; i++)
            {
                SmokePort(turret, shellTopAngle, cannon, i, out Vector2 point, out Vector2 direction);
                Vector3 position = new Vector3(point.x, turret.DrawPos.y, point.y);
                if (!position.ShouldSpawnMotesAt(turret.Map)) continue;
                FleckCreationData smoke = FleckMaker.GetDataStatic(position, turret.Map, FleckDefOf.Smoke, .7f);
                smoke.velocityAngle = new Vector3(direction.x, 0f, direction.y).AngleFlat();
                smoke.velocitySpeed = .6f;
                turret.Map.flecks.CreateFleck(smoke);
            }
        }
    }
}
