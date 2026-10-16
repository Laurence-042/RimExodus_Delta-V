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

        /// <summary>
        /// 选点/进场流程中的 shuttle 舱体（BeginDockingShuttle 置位；与 pendingShip
        /// 互斥——同一时刻最多一个对接流程，§7.4「降落逻辑只有一套」的 shuttle 变体）。
        /// </summary>
        internal Building pendingShuttleBody;

        /// <summary>本流程创建的选点 marker（取消时直接销毁；shuttle 预览无主台，不能靠 PilotConsole 识别）。</summary>
        internal GravshipLandingMarker pendingSelectionMarker;

        /// <summary>发起对接时是否处于手操驾驶（取消选点时恢复驾驶位）。</summary>
        internal bool pendingSelectionWasManual;

        /// <summary>已放入目标图、等待收尾（源口袋图删除）的对接完成窗口。</summary>
        internal Ship landedShipAwaitingCleanup;

        // ---- 战略发射（原版管线适配）----

        internal Map pendingStrategicSourceMap;
        internal PlanetTile pendingStrategicSourceAddress;
        internal SpacePose? pendingStrategicSourcePose;

        /// <summary>战略发射源 = 发射船的自有口袋容器（发射后按离场标准流程销毁）。</summary>
        private int pendingStrategicOwnPocketId;
        internal SpaceObject pendingStrategicOwnPocket { get => Current.Game?.GetComponent<UniverseManager>()?.ObjectById(pendingStrategicOwnPocketId); set => pendingStrategicOwnPocketId = value?.objectId ?? 0; }

        internal bool preLaunchConfirmationChaining;

        // ---- 出力前分离（§8.2：流程状态，不存档）----

        /// <summary>
        /// 等待结果的分离请求（确认窗已打开或已排队到帧上下文）：同一条船只挂一个待决
        /// 分离；确认/取消/执行时移除。不设「结构版本抑制」——旧机制取消后按
        /// GlobalStructureVersion 记忆，玩家不改结构就永远无法再出力且无任何反馈
        /// （2026-09-24 用户报障），已随统一分离入口删除：取消 = 撤销自动驾驶任务 +
        /// 退出驾驶并提示，重试 = 重新进入驾驶再按推进键。
        /// </summary>
        internal readonly HashSet<int> pendingSeparationOwnerIds = new HashSet<int>();

        internal void Reset()
        {
            pendingShip = Ship.Invalid;
            pendingShuttleBody = null;
            pendingSelectionMarker = null;
            pendingSelectionWasManual = false;
            landedShipAwaitingCleanup = Ship.Invalid;
            pendingLeftoverThingIds = null;
            pendingStrategicSourceMap = null;
            pendingStrategicSourceAddress = PlanetTile.Invalid;
            pendingStrategicSourcePose = null;
            pendingStrategicOwnPocket = null;
            preLaunchConfirmationChaining = false;
            pendingSeparationOwnerIds.Clear();
            landingCaptureEngine = null;
            landingCaptureMap = null;
            landingCaptureCells = null;
        }
    }
}
