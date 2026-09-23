using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 一局 universe 独占的飞行/停靠转换运行时状态。字段不序列化；每次构造即是完整的空闲
    /// 状态，读档不会继承选择、捕获或战略离场窗口。
    /// </summary>
    internal sealed partial class ShipFlightTransitionService
    {
        private readonly UniverseManager universe;

        internal ShipFlightTransitionService(UniverseManager universe)
        {
            this.universe = universe;
        }

        internal ShipState pendingShip;
        internal bool pendingSelectionWasManual;
        internal ShipState landedShipAwaitingPocketCleanup;
        internal ShipState pendingFreeFlightShip;
        internal Building_GravEngine pendingFreeFlightEngine;
        internal Map pendingFreeFlightSourceMap;
        internal IntVec3 pendingFreeFlightSourceRoot;
        internal Map pendingStrategicSourceMap;
        internal PlanetTile pendingStrategicSourceAddress;
        internal SpacePose? pendingStrategicSourcePose;
        internal Building_GravEngine directCaptureEngine;
        internal Building_GravEngine landingCaptureEngine;
        internal Map landingCaptureMap;
        internal HashSet<IntVec3> landingCaptureCells;
        internal bool preLaunchConfirmationChaining;

        internal void Reset()
        {
            pendingShip = null;
            pendingSelectionWasManual = false;
            landedShipAwaitingPocketCleanup = null;
            pendingFreeFlightShip = null;
            pendingFreeFlightEngine = null;
            pendingFreeFlightSourceMap = null;
            pendingFreeFlightSourceRoot = IntVec3.Invalid;
            pendingStrategicSourceMap = null;
            pendingStrategicSourceAddress = PlanetTile.Invalid;
            pendingStrategicSourcePose = null;
            directCaptureEngine = null;
            landingCaptureEngine = null;
            landingCaptureMap = null;
            landingCaptureCells = null;
            preLaunchConfirmationChaining = false;
        }
    }
}
