using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 一局 universe 独占的飞行/停靠转换与结构拆分运行时状态。字段不序列化；每次构造
    /// 即是完整的空闲状态，读档不会继承选择、捕获、战略离场或待确认窗口（§12：未执行
    /// 的分离确认不保存、不视为已获授权）。
    /// </summary>
    internal sealed partial class ShipFlightTransitionService
    {
        private readonly UniverseManager universe;

        internal ShipFlightTransitionService(UniverseManager universe)
        {
            this.universe = universe;
        }

        // ---- 战术对接（移动 → 固定，合图）----

        /// <summary>选点/进场流程中的战术降落船（BeginDocking 置位，完成/取消清除）。</summary>
        internal Ship pendingShip;

        /// <summary>发起对接时是否处于手操驾驶（取消选点时恢复驾驶位）。</summary>
        internal bool pendingSelectionWasManual;

        /// <summary>已放入目标图、等待收尾（源口袋图删除）的对接完成窗口。</summary>
        internal Ship landedShipAwaitingCleanup;

        /// <summary>完成时刻捕获的 Gravship（WriteDockedSegment 使用；与移除同一调用栈）。</summary>
        internal Gravship pendingCapture;

        // ---- 战略发射（原版管线适配）----

        internal Map pendingStrategicSourceMap;
        internal PlanetTile pendingStrategicSourceAddress;
        internal SpacePose? pendingStrategicSourcePose;

        /// <summary>战略发射源 = 发射船的自有口袋容器（发射后按离场标准流程销毁）。</summary>
        internal SpaceObject pendingStrategicOwnPocket;

        internal bool preLaunchConfirmationChaining;

        // ---- 出力前拆分确认（§8.2：流程状态，不存档）----

        /// <summary>等待玩家确认的拆分请求（同一次分离只弹一个汇总窗口）。</summary>
        internal int pendingSplitOwnerId = -1;

        /// <summary>该次请求是否由自动驾驶触发（取消时撤销对应任务，§8.2 步骤 5）。</summary>
        internal bool pendingSplitFromAutopilot;

        /// <summary>玩家取消后的抑制记录：结构版本不变期间不再重复弹窗。</summary>
        internal int splitPromptSuppressedOwnerId = -1;
        internal int splitPromptSuppressedVersion = -1;

        internal void Reset()
        {
            pendingShip = Ship.Invalid;
            pendingSelectionWasManual = false;
            landedShipAwaitingCleanup = Ship.Invalid;
            pendingCapture = null;
            pendingDockingSourceMap = null;
            pendingLeftoverThingIds = null;
            pendingStrategicSourceMap = null;
            pendingStrategicSourceAddress = PlanetTile.Invalid;
            pendingStrategicSourcePose = null;
            pendingStrategicOwnPocket = null;
            preLaunchConfirmationChaining = false;
            pendingSplitOwnerId = -1;
            pendingSplitFromAutopilot = false;
            splitPromptSuppressedOwnerId = -1;
            splitPromptSuppressedVersion = -1;
            landingCaptureEngine = null;
            landingCaptureMap = null;
            landingCaptureCells = null;
        }
    }
}
