using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DeltaV
{
    public sealed class Projectile_CuttingBomb : Projectile_Explosive
    {
        internal const int CutLength = 5;
        private bool cutFoundation;

        internal static bool CanCutOn(Map map) =>
            Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) != null;

        internal static IEnumerable<IntVec3> CutCells(IntVec3 source, IntVec3 center)
        {
            IntVec3 throwDirection = center - source;
            int longestAxis = Mathf.Max(Mathf.Abs(throwDirection.x), Mathf.Abs(throwDirection.z));
            if (longestAxis == 0) yield break;

            // 将垂线栅格化为五格，主轴每次前进一格，斜投时也不会重复或跳格。
            for (int i = -CutLength / 2; i <= CutLength / 2; i++)
            {
                float offset = (float)i / longestAxis;
                yield return center + new IntVec3(Mathf.RoundToInt(-throwDirection.z * offset), 0,
                    Mathf.RoundToInt(throwDirection.x * offset));
            }
        }

        public override void Launch(Thing launcher, Vector3 origin, LocalTargetInfo usedTarget,
            LocalTargetInfo intendedTarget, ProjectileHitFlags hitFlags, bool preventFriendlyFire = false,
            Thing equipment = null, ThingDef targetCoverDef = null)
        {
            cutFoundation = equipment?.TryGetComp<CompCuttingBombPack>()?.CutFoundation == true;
            // 使用瞄准预览的发射格，飞抵指定格后由原版引信计时。
            base.Launch(launcher, launcher.Position.ToVector3Shifted(), usedTarget, intendedTarget,
                ProjectileHitFlags.IntendedTarget, preventFriendlyFire, equipment, targetCoverDef);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref cutFoundation, "cutFoundation");
        }

        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            if (blockedByShield)
            {
                Destroy();
                return;
            }

            base.Impact(hitThing, blockedByShield);
        }

        protected override void Explode()
        {
            Map map = Map;
            IntVec3 center = Position;
            Thing thrower = Launcher;
            Destroy();
            if (!CanCutOn(map)) return;

            def.projectile.soundExplode?.PlayOneShot(new TargetInfo(center, map));
            foreach (IntVec3 cell in CutCells(origin.ToIntVec3(), center))
            {
                if (!cell.InBounds(map)) continue;
                if (cutFoundation && SubstructureDamageUtility.TryDestroyHitFoundation(map, cell,
                    scatterNearbyRoofs: false, instigator: thrower))
                    FleckMaker.ThrowDustPuff(cell, map, 1f);
                else
                    BreachWalls(map, cell, thrower);
            }
        }

        private void BreachWalls(Map map, IntVec3 cell, Thing thrower)
        {
            foreach (Thing thing in cell.GetThingList(map).ToArray())
            {
                if (thing is not Building wall || !wall.Spawned || wall.Map != map || !wall.def.destroyable
                    || !wall.def.building.canBeDamagedByAttacks || wall.def.building.isNaturalRock
                    || (!wall.def.IsWall && !wall.def.IsDoor)) continue;

                wall.TakeDamage(new DamageInfo(DamageDefOf.Cut, wall.HitPoints, instigator: thrower, weapon: EquipmentDef));
                if (wall.Destroyed) FleckMaker.ThrowDustPuff(cell, map, 1f);
            }
        }
    }
}
