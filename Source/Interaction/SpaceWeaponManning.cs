using System.Collections.Generic;
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
            // Delta-V 顶置炮塔的炮手格是可选岗位：允许布局和 prefab 在这里放置建筑，
            // 是否还能开火仍由真实炮手／自瞄火控判定。蓝图、框架和其他设施继续受原版保护。
            return def.HasSingleOrMultipleInteractionCells && thing is not Building_SpaceTurretGun;
        }

        internal static int CountTurretsBlockedBy(BuildableDef entDef, IntVec3 center, Rot4 rotation,
            Map map, Thing thingToIgnore = null)
        {
            if (entDef == null || map == null) return 0;
            CellRect occupied = GenAdj.OccupiedRect(center, rotation, entDef.Size);
            var blocked = new HashSet<Building_SpaceTurretGun>();
            foreach (IntVec3 adjacent in GenAdj.CellsAdjacentCardinal(center, rotation, entDef.Size))
            {
                if (!adjacent.InBounds(map)) continue;
                List<Thing> things = adjacent.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] == thingToIgnore || things[i] is not Building_SpaceTurretGun turret
                        || !turret.def.HasSingleOrMultipleInteractionCells
                        || entDef.passability == Traversability.Standable && entDef != turret.def) continue;
                    foreach (IntVec3 interactionCell in ThingUtility.InteractionCellsWhenAt(
                        turret.def, turret.Position, turret.Rotation, map))
                    {
                        if (!occupied.Contains(interactionCell)) continue;
                        blocked.Add(turret);
                        break;
                    }
                }
            }
            return blocked.Count;
        }

        // 只过滤绘制读取，绝不修改 Job.targetA；后续排队命令仍由原版连线。
        internal static bool HasSelectionLine(Job job)
        {
            return WeaponFor(job) == null && job.targetA.IsValid;
        }
    }
}
