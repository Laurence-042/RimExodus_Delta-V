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

        public override void ExposeData()
        {
            base.ExposeData();
            // JobDriver 恢复当前 toil 时不会再调用 initAction；重建运行时炮手登记。
            if (Scribe.mode == LoadSaveMode.PostLoadInit && CurToilIndex == 1)
                Weapon?.TryGetComp<CompSpaceWeaponCrew>()?.BeginOperating(pawn);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.B);
            CompSpaceWeaponCrew crew = Weapon?.TryGetComp<CompSpaceWeaponCrew>();
            if (crew == null) yield break;
            Map assignedMap = Weapon.Map;
            AddFinishAction(_ =>
            {
                crew.EndOperating(pawn);
                ShipControlService.NotifyGunnerChange(assignedMap);
            });
            yield return Toils_Goto.GotoCell(crew.GunnerCell, PathEndMode.OnCell);
            Toil operate = ToilMaker.MakeToil("OperateSpaceWeapon");
            operate.initAction = delegate
            {
                if (Weapon == null || pawn.Position != crew.GunnerCell)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                crew.BeginOperating(pawn);
                pawn.rotationTracker.FaceCell(Weapon.Position);
            };
            operate.handlingFacing = true;
            operate.defaultCompleteMode = ToilCompleteMode.Never;
            yield return operate;
        }
    }
}
