using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 一艘飞船的宇宙状态（SpaceObject 特化，AGENTS §11）。
    /// 阶段3 新增：玩家阵营标记（POI 距离的参考对象）、主驾驶台引用、固定轨道停靠状态
    /// （docked 及其恢复数据，见 DockController）。
    /// </summary>
    public class ShipState : SpaceObject
    {
        /// <summary>玩家阵营船：动态锚候选、POI 生命周期距离参考。存档事实。</summary>
        public bool isPlayerShip;

        /// <summary>
        /// 主驾驶台（story 规则：第一台自动成为主，仅显式 gizmo 可换主；
        /// 返回飞行时的飞船朝向跟随它的朝向）。Scribe_References，可失效。
        /// </summary>
        public Building primaryConsole;

        // ---- 固定轨道 / 建造模式（阶段3，DockController 维护）----

        /// <summary>停靠中：船体已作为真实建筑落到宿主图，口袋图保留为空图。</summary>
        public bool docked;

        /// <summary>原版降落选点已经开始、但动画尚未确认完成；此时船体在 Gravship 单例中。</summary>
        public bool docking;

        /// <summary>固定轨道中的实际承载图（宿主图或已加载 POI 图）。</summary>
        public Map dockedMap;

        /// <summary>本次原版降落选中的整船 Rot4；无驾驶台回航时用于恢复局部布局。</summary>
        public Rot4 dockedRotation = Rot4.North;

        /// <summary>停靠前的宇宙姿态（位置恢复用；朝向恢复优先用主驾驶台朝向）。</summary>
        public SpacePose preDockPose;

        /// <summary>停靠前引擎在口袋图内的位置（起飞时按它原样放回）。</summary>
        public IntVec3 pocketRoot;

        public ShipState()
        {
        }

        public ShipState(int shipId, string label, Map map, SpacePose pose, bool isPlayerShip = false)
            : base(shipId, label, map, pose)
        {
            this.isPlayerShip = isPlayerShip;
            preDockPose = pose;
            pocketRoot = IntVec3.Invalid;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref isPlayerShip, "isPlayerShip", false);
            Scribe_References.Look(ref primaryConsole, "primaryConsole");
            Scribe_Values.Look(ref docked, "docked", false);
            Scribe_Values.Look(ref docking, "docking", false);
            Scribe_References.Look(ref dockedMap, "dockedMap");
            Scribe_Values.Look(ref dockedRotation, "dockedRotation", Rot4.North);
            Scribe_Deep.Look(ref preDockPose, "preDockPose");
            Scribe_Values.Look(ref pocketRoot, "pocketRoot", IntVec3.Invalid);
        }
    }
}
