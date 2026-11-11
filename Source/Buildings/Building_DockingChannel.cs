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

    public abstract class Building_OpenVacuumPortal : Building_SealedDockingDoor
    {
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

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            Graphic.Draw(drawLoc, Rotation, this);
        }

    }

    public sealed class Building_VacuumBarrier : Building_OpenVacuumPortal
    {
        private Graphic_Linked linkedGraphic;

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            // Blueprint graphics inherit the Def's link type. Link only the finished barrier.
            Graphic graphic = Graphic;
            if (linkedGraphic?.SubGraphic != graphic)
                linkedGraphic = new Graphic_Linked(graphic);
            // The atlas already encodes map-relative directions.
            var matrix = Matrix4x4.TRS(drawLoc, Quaternion.identity, Vector3.one);
            Graphics.DrawMesh(MeshPool.plane10, matrix, linkedGraphic.MatSingleFor(this), 0);
        }
    }

    /// <summary>可收回甲板上的常开通行单元。port 是清理归属，不是船间连接网络。</summary>
    public class Building_DockingChannel : Building_OpenVacuumPortal
    {
        internal Building_DockingPort port;

        public override bool IsAirtight => port != null && port.Spawned && port.Map == Map;
        public override bool ExchangeVacuum => !IsAirtight;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref port, "dockingPort");
        }

        public override IEnumerable<Gizmo> GetGizmos() { yield break; }
    }
}
