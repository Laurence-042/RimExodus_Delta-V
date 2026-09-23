using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public enum ShipFlightState : byte
    {
        FreeFlight,
        Docked
    }

    /// <summary>
    /// 一艘飞船的宇宙状态（SpaceObject 特化，AGENTS §11）。
    /// 阶段3 新增：玩家阵营标记（POI 距离的参考对象）、主驾驶台引用、固定轨道停靠状态
    /// （FreeFlight / Docked 及其恢复数据，见 ShipFlightTransitionService）。
    /// </summary>
    public class ShipState : SpaceObject
    {
        [Unsaved] internal bool legacyPlayerShip;

        /// <summary>
        /// 主驾驶台（story 规则：第一台自动成为主，仅显式 gizmo 可换主；
        /// 开始自由飞行时的飞船朝向跟随它的朝向）。Scribe_References，可失效。
        /// </summary>
        public Building primaryConsole;

        // ---- 自由飞行 / 已停靠（ShipFlightTransitionService 维护）----

        public ShipFlightState flightState = ShipFlightState.FreeFlight;

        public bool IsDocked => flightState == ShipFlightState.Docked;

        /// <summary>固定轨道中的实际承载图（船降落时球面位置对应的真实轨道 tile 图）。</summary>
        public Map dockedMap;

        /// <summary>本次原版降落选中的整船 Rot4；无驾驶台回航时用于恢复局部布局。</summary>
        public Rot4 dockedRotation = Rot4.North;

        /// <summary>
        /// 口袋图几何中心（即 Ship local 原点）落到停靠图后的连续 Map 坐标。
        /// 它把停靠图中的真实建筑重新接回统一 Universe 坐标，不是另一套姿态状态。
        /// </summary>
        public Vector2 dockedOrigin;

        /// <summary>已停靠船体坐标系相对宿主坐标系的朝向。</summary>
        public float dockedLocalAngle;

        public override Map InteriorMap => IsDocked && dockedMap != null ? dockedMap : map;

        /// <summary>停靠前的宇宙姿态（降落动画起点；确认落点后不再用于恢复位置）。</summary>
        public SpacePose preDockPose;

        /// <summary>飞行口袋图内的引擎根位置；停靠清图后为 Invalid，新图生成时重写。</summary>
        public IntVec3 pocketRoot;

        public ShipState()
        {
        }

        public ShipState(int shipId, string label, Map map, SpacePose pose)
            : base(shipId, label, map, pose)
        {
            preDockPose = pose;
            pocketRoot = IntVec3.Invalid;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                Scribe_Values.Look(ref flightState, "flightState", ShipFlightState.FreeFlight);
            }
            else
            {
                Scribe_Values.Look(ref flightState, "flightState", ShipFlightState.FreeFlight);
                bool legacyDocked = false;
                bool legacyDocking = false;
                Scribe_Values.Look(ref legacyDocked, "docked", false);
                Scribe_Values.Look(ref legacyDocking, "docking", false);
                Scribe_Values.Look(ref legacyPlayerShip, "isPlayerShip", false);
                if (Scribe.mode == LoadSaveMode.LoadingVars && legacyDocked)
                {
                    flightState = ShipFlightState.Docked;
                }
            }
            Scribe_References.Look(ref primaryConsole, "primaryConsole");
            Scribe_References.Look(ref dockedMap, "dockedMap");
            Scribe_Values.Look(ref dockedRotation, "dockedRotation", Rot4.North);
            Scribe_Values.Look(ref dockedOrigin, "dockedOrigin", Vector2.zero);
            Scribe_Values.Look(ref dockedLocalAngle, "dockedLocalAngle", 0f);
            Scribe_Deep.Look(ref preDockPose, "preDockPose");
            Scribe_Values.Look(ref pocketRoot, "pocketRoot", IntVec3.Invalid);
        }
    }
}
