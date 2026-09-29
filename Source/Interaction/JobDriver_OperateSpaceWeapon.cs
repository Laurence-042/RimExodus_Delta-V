using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace DeltaV
{
    /// <summary>任意 ThingWithComps 舰载武器的固定炮手位操作 Job。</summary>
    public sealed class JobDriver_OperateSpaceWeapon : JobDriver
    {
        // targetA 会被 Pawn_JobTracker.DrawLinesBetweenTargets 当成队列目的地绘线。
        // 固定岗位与工作台同类：武器只作为内部工作对象放在 targetB，移动目的地由
        // Driver 直接取 Crew.GunnerCell，不向选择覆盖层暴露目标线。
        private ThingWithComps Weapon => job.targetB.Thing as ThingWithComps;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.B);
            CompSpaceWeaponCrew crew = Weapon?.TryGetComp<CompSpaceWeaponCrew>();
            if (crew == null) yield break;
            yield return Toils_Goto.GotoCell(crew.GunnerCell, PathEndMode.OnCell);
            Toil operate = ToilMaker.MakeToil("OperateSpaceWeapon");
            operate.tickAction = delegate
            {
                ThingWithComps weapon = Weapon;
                CompSpaceWeaponCrew current = weapon?.TryGetComp<CompSpaceWeaponCrew>();
                if (current == null || pawn.Position != current.GunnerCell)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                current.ManForATick(pawn);
                pawn.rotationTracker.FaceCell(weapon.Position);
            };
            operate.handlingFacing = true;
            operate.defaultCompleteMode = ToilCompleteMode.Never;
            yield return operate;
        }
    }
}
