using RimWorld;
using Verse;
using Verse.AI;

namespace DeltaV
{
    /// <summary>原版操作链的无状态适配；操作人、交互格和岗位生命周期仍由原版持有。</summary>
    internal static class SpaceWeaponManning
    {
        internal static CompSpaceWeapon WeaponFor(Job job)
        {
            return job?.def == JobDefOf.ManTurret
                ? job.targetA.Thing?.TryGetComp<CompSpaceWeapon>() : null;
        }

        // 仅替换炮塔 Tick 的开火门禁；不能让 MannedNow 假报有人而 ManningPawn 仍为空。
        internal static bool CanFire(CompMannable mannable)
        {
            CompSpaceWeapon weapon = mannable.parent.TryGetComp<CompSpaceWeapon>();
            if (weapon == null) return mannable.MannedNow;
            return weapon.Operational
                && weapon.OwnerShip.EffectiveFireControlMode != ShipFireControlMode.Disabled;
        }

        internal static void DrawInteractionCells(ThingDef def, IntVec3 center, Rot4 rotation, Thing thing)
        {
            CompSpaceWeapon weapon = thing.TryGetComp<CompSpaceWeapon>();
            if (weapon?.Mannable != null && weapon.AutoAimEffective) return;
            GenDraw.DrawInteractionCells(def, center, rotation);
        }

        // 在原版建造循环内判断当前实例；交互格枚举和其他建筑的保护仍由原版负责。
        internal static bool HasProtectedInteractionCells(ThingDef def, Thing thing)
        {
            CompSpaceWeapon weapon = thing.TryGetComp<CompSpaceWeapon>();
            return def.HasSingleOrMultipleInteractionCells
                && !(weapon?.Mannable != null && weapon.AutoAimEffective);
        }

        // 只过滤绘制读取，绝不修改 Job.targetA；后续排队命令仍由原版连线。
        internal static bool HasSelectionLine(Job job)
        {
            return WeaponFor(job) == null && job.targetA.IsValid;
        }
    }
}
