using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 一局 universe 独占的模式转换运行时状态。字段不序列化；每次构造即是完整的空闲
    /// 状态，读档不会继承选择、捕获或战略离场窗口。
    /// </summary>
    internal sealed partial class ShipModeService
    {
        private readonly UniverseManager universe;

        internal ShipModeService(UniverseManager universe)
        {
            this.universe = universe;
        }

        internal ShipState pendingShip;
        internal ShipState landedShipAwaitingPocketCleanup;
        internal ShipState pendingMobileShip;
        internal Building_GravEngine pendingMobileEngine;
        internal Map pendingMobileSourceMap;
        internal IntVec3 pendingMobileSourceRoot;
        internal Vector2 pendingMobileOldOrigin;
        internal float pendingMobileHeading;
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
            landedShipAwaitingPocketCleanup = null;
            pendingMobileShip = null;
            pendingMobileEngine = null;
            pendingMobileSourceMap = null;
            pendingMobileSourceRoot = IntVec3.Invalid;
            pendingMobileOldOrigin = Vector2.zero;
            pendingMobileHeading = 0f;
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
