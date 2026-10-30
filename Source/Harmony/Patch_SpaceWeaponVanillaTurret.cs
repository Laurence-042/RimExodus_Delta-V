using HarmonyLib;
using RimWorld;
using Verse;
using UnityEngine;

namespace DeltaV
{
    // RimWorld 1.6 TurretTop.DrawTurret already accepts a visual recoil offset.
    [HarmonyPatch(typeof(TurretTop), nameof(TurretTop.DrawTurret))]
    public static class Patch_TurretTop_SpaceWeaponRecoil
    {
        public static void Prefix(Building_Turret ___parentTurret, ref Vector3 recoilDrawOffset)
        {
            CompSpaceWeapon weapon = ___parentTurret.TryGetComp<CompSpaceWeapon>();
            if (weapon?.SpaceModeEnabled == true) recoilDrawOffset = weapon.VisualRecoilOffset;
        }

        public static void Postfix(TurretTop __instance, Building_Turret ___parentTurret,
            Vector3 drawLoc, Vector3 recoilDrawOffset, float recoilAngleOffset)
        {
            var building = ___parentTurret.def.building;
            Vector3 offset = new Vector3(building.turretTopOffset.x, 0f, building.turretTopOffset.y)
                .RotatedBy(recoilAngleOffset) + recoilDrawOffset;
            float angle = ___parentTurret.CurrentEffectiveVerb?.AimAngleOverride ?? __instance.CurRotation;
            Matrix4x4 matrix = Matrix4x4.TRS(drawLoc + Altitudes.AltIncVect + offset,
                (TurretTop.ArtworkRotation + angle).ToQuat(), new Vector3(building.turretTopDrawSize, 1f, building.turretTopDrawSize));
            for (int part = 0; part < 2; part++)
                if (WeaponHeatVisuals.TryPart(___parentTurret, ___parentTurret.TurretTopMaterial, part, out Mesh mesh, out Material material))
                    Graphics.DrawMesh(mesh, matrix, material, 0);
        }
    }

    [HarmonyPatch(typeof(TurretTop), nameof(TurretTop.TurretTopTick))]
    public static class Patch_TurretTop_SpaceWeaponTraverse
    {
        public static bool Prefix(Building_Turret ___parentTurret)
        {
            CompSpaceWeaponTraverse traverse =
                (___parentTurret as ThingWithComps)?.TryGetComp<CompSpaceWeaponTraverse>();
            if (traverse == null) return true;
            LocalTargetInfo target = ___parentTurret.CurrentTarget;
            if (target.IsValid)
            {
                traverse.AimAtVanilla(CompSpaceWeaponTraverse.MapAngleTo(target, ___parentTurret));
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Building_TurretGun), "BeginBurst")]
    public static class Patch_BuildingTurretGun_SpaceWeaponAlignment
    {
        public static bool Prefix(Building_TurretGun __instance)
        {
            CompSpaceWeaponTraverse traverse = __instance.TryGetComp<CompSpaceWeaponTraverse>();
            if (traverse == null || !__instance.CurrentTarget.IsValid) return true;
            float angle = CompSpaceWeaponTraverse.MapAngleTo(__instance.CurrentTarget, __instance);
            traverse.AimAtVanilla(angle);
            CompSpaceWeapon weapon = __instance.TryGetComp<CompSpaceWeapon>();
            float tolerance = weapon?.fireDeviationDeg ?? traverse.TraverseProps.fireToleranceDeg;
            return SpaceWeaponGeometry.WithinFireDeviation(angle, traverse.CurrentAngle, tolerance);
        }
    }

    /// <summary>原版 ShotReport 仍负责距离、武器、天气、掩体；仅把炮塔射手能力换成炮手。</summary>
    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitFactorFromShooter))]
    public static class Patch_ShotReport_SpaceWeaponCrewAccuracy
    {
        public static bool Prefix(Thing caster, float distance, float? acc, ref float __result)
        {
            if (acc.HasValue || caster is not ThingWithComps comps) return true;
            CompSpaceWeapon weapon = comps.TryGetComp<CompSpaceWeapon>();
            if (weapon?.Mannable == null || !weapon.TryGetSkillLevel(out int skill)) return true;
            float equivalentAccuracy = UnityEngine.Mathf.Lerp(0.80f, 0.99f,
                UnityEngine.Mathf.Clamp01(skill / 20f));
            __result = ShotReport.HitFactorFromShooter(caster, distance, equivalentAccuracy);
            return false;
        }
    }
}
