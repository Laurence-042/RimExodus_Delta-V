using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>对接口与通道共用的气密标记；原版门提供独立 Portal 和寻路边界。</summary>
    public abstract class Building_SealedDockingDoor : Building_Door
    {
        public override bool IsAirtight => true;
        public override bool ExchangeVacuum => false;
    }

    /// <summary>可收回甲板上的常开通行单元。port 是清理归属，不是船间连接网络。</summary>
    public class Building_DockingChannel : Building_SealedDockingDoor
    {
        internal Building_DockingPort port;

        // 部分 prefab 不包含端口时，外部引用会被清空；孤立单元不能继续提供气密。
        public override bool IsAirtight => port != null && port.Spawned && port.Map == Map;
        public override bool ExchangeVacuum => !IsAirtight;
        protected override bool AlwaysOpen => true;
        protected override bool CheckFaction => false;
        public override bool FreePassage => true;
        public override bool PawnCanOpen(Pawn pawn) => true;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            openInt = true;
            holdOpenInt = true;
            base.SpawnSetup(map, respawningAfterLoad);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref port, "dockingPort");
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            Graphic.Draw(drawLoc, Rotation, this);
        }

        public override IEnumerable<Gizmo> GetGizmos() { yield break; }
    }
}
