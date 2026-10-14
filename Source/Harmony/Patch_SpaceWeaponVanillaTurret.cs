using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
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
