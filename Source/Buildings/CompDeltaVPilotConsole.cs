using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 驾驶台（解耦架构 §3）：飞船身份与认领的锚点。
    ///
    /// 认领体系（主/备用，§3.2 已定）：
    /// - 每台驾驶台持久持有结构归属 ID（shipId）与 isMain 认领标志；
    /// - 无主台归属上新建台 → 自动认领成主台（形成飞船）；未占有域上建台 → 领新
    ///   结构 ID 并只认领脚下连通片；
    /// - 已有主台的归属上新建台 → 默认备用，不抢占；
    /// - 「设为主驾驶台」显式认领（原主台降为备用，新主台朝向定义船头）；
    /// - 「设为备用驾驶台」主动放弃认领（不自动选回，保证按钮真正解除控制）；
    /// - 主台被摧毁 → 同归属内同方向备用台按稳定 Thing ID 继任；无同向则结构成为
    ///   残骸，玩家可手动认领恢复为船。搬运造成的暂时 DeSpawn 不算损毁
    ///   （PostDestroy 不在 DeSpawn 路径上触发）。
    ///
    /// 本 Comp 由 XML patch 追加到原版 PilotConsole，与原版 CompPilotConsole 共存：原版
    /// Gravship 继续按 ThingDefOf.PilotConsole 识别设施与发射方向（战略发射仍需引擎）。
    /// </summary>
    public class CompProperties_DeltaVPilotConsole : CompProperties
    {
        public CompProperties_DeltaVPilotConsole()
        {
            compClass = typeof(CompDeltaVPilotConsole);
        }
    }

    public class CompDeltaVPilotConsole : ThingComp
    {
        private UniverseManager Universe => Current.Game?.GetComponent<UniverseManager>();

        // ---- 身份与认领（持久事实）----

        /// <summary>
        /// 本结构的持久身份 ID = 结构归属 ID（解耦架构 §2：与对象 ID 共用计数器但语义
        /// 分离，不得反查对象）。一经领取永不改变、随 comp 存档。
        /// </summary>
        public int shipId = -1;

        /// <summary>是否为该归属的主驾驶台（认领事实；一归属至多一主台）。</summary>
        public bool isMain;

        public Ship Ship => isMain && shipId > 0 && parent.Spawned
            ? new Ship(shipId, parent.Map, this)
            : Ship.Invalid;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            ClaimOnSpawn();
            RestampShipDomain();
        }

        /// <summary>
        /// 新建驾驶台的认领落位（§3.2 表格前三行）。仅对尚无身份的新台生效——搬运
        /// 重放/读档重生的台带原身份与认领，不得被重置。仅 Delta-V 参考系图参与
        /// （地表图没有归属组件，驾驶台保持原版语义、提供灰化的自由飞行入口）。
        /// </summary>
        private void ClaimOnSpawn()
        {
            UniverseManager universe = Universe;
            Map map = parent.Map;
            if (universe == null || map == null || universe.FrameOf(map) == null || shipId > 0)
            {
                return;
            }
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            int domainOwner = ownership?.OwnerAt(parent.Position) ?? SubstructureOwnership.Unassigned;
            if (domainOwner > 0)
            {
                // 已有归属（残骸/已有船）：无主台则认领成船，有主台默认备用（不抢占）。
                shipId = domainOwner;
                isMain = !ShipRegistry.OwnershipHasMainConsole(map, domainOwner);
            }
            else
            {
                // 未占有域：领取新结构 ID，只认领脚下连通未占有片（§3.2 方案建议）。
                shipId = universe.AllocateOwnerId();
                isMain = true;
                ownership?.ClaimConnectedDomain(parent.Position, shipId);
                universe.NoteStructureName(shipId, "DeltaV_ShipDefaultName".Translate(shipId));
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"pilot console spawned on structure #{shipId}; main={isMain} ({parent.Position})");
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            // 主台损毁 → 同归属同方向备用台按稳定 Thing ID 继任（§3.2）；无同向则结构
            // 成为残骸（可手动认领恢复）。原版搬运走 DeSpawn（WillReplace），不进本路径。
            UniverseManager universe = Universe;
            if (universe != null && isMain && shipId > 0)
            {
                isMain = false;
                CompDeltaVPilotConsole successor = previousMap != null
                    ? FindSuccessor(previousMap, shipId, parent.Rotation)
                    : null;
                if (successor != null)
                {
                    successor.isMain = true;
                    DeltaVLog.Message(DeltaVLogModule.Core,
                        $"structure #{shipId}: main console destroyed; succeeded by same-facing console {successor.parent.thingIDNumber}");
                }
                else
                {
                    DeltaVLog.Message(DeltaVLogModule.Core,
                        $"structure #{shipId}: main console destroyed with no same-facing backup; structure is now a wreck (claim a console to restore)");
                }
            }
            base.PostDestroy(mode, previousMap);
        }

        /// <summary>同归属、同方向、非主台的候选中 Thing ID 最小者（确定性，不受遍历顺序影响）。</summary>
        private static CompDeltaVPilotConsole FindSuccessor(Map map, int ownerId, Rot4 direction)
        {
            CompDeltaVPilotConsole best = null;
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
            {
                CompDeltaVPilotConsole console = thing.TryGetComp<CompDeltaVPilotConsole>();
                if (console == null || console.shipId != ownerId || console.isMain) continue;
                if (thing.Rotation != direction) continue;
                if (best == null || thing.thingIDNumber < best.parent.thingIDNumber)
                {
                    best = console;
                }
            }
            return best;
        }

        // ---- 主/备用切换（显式认领，§3.2）----

        /// <summary>本台设为主驾驶台：认领脚下归属，原主台（若在）降为备用；朝向定义新船头。</summary>
        public void MakeMainConsole()
        {
            UniverseManager universe = Universe;
            Map map = parent.Map;
            if (universe == null || map == null || !parent.Spawned) return;
            SubstructureOwnership ownership = SubstructureOwnership.For(map);
            int target = shipId > 0 ? shipId : ownership.OwnerAt(parent.Position);
            if (target <= 0)
            {
                // 未占有域上直接认领：领新 ID 并认领脚下连通片。
                target = universe.AllocateOwnerId();
                shipId = target;
                ownership.ClaimConnectedDomain(parent.Position, target);
                universe.NoteStructureName(target, "DeltaV_ShipDefaultName".Translate(target));
            }
            else
            {
                shipId = target;
            }
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
            {
                CompDeltaVPilotConsole console = thing.TryGetComp<CompDeltaVPilotConsole>();
                if (console != null && console != this && console.shipId == target && console.isMain)
                {
                    console.isMain = false;
                }
            }
            isMain = true;
            Messages.Message("DeltaV_ConsolePrimarySet".Translate(parent.LabelCap),
                MessageTypeDefOf.NeutralEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"structure #{target}: console {parent.thingIDNumber} claimed as main (bow redefined)");
        }

        /// <summary>主台主动降为备用（不自动选回自己或另一台，保证按钮真正解除控制）。</summary>
        public void MakeBackupConsole()
        {
            if (!isMain) return;
            isMain = false;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"structure #{shipId}: main console demoted to backup by player");
        }

        // ---- 身份解析 ----

        /// <summary>
        /// 解析本结构的身份 ID：已有 shipId 直接返回；未 Spawned（随 Gravship 飞行中）或
        /// 无可认领域时经统一计数器新领并注册默认名。战略到达的落点盖章等管线使用。
        /// </summary>
        public int ShipIdentityId(UniverseManager universe)
        {
            if (shipId <= 0)
            {
                shipId = universe != null ? universe.AllocateOwnerId() : -1;
                universe?.NoteStructureName(shipId, "DeltaV_ShipDefaultName".Translate(shipId));
            }
            return shipId;
        }

        /// <summary>随船找身份驾驶台：主台（vanilla PilotConsole 链接）优先，兜底遍历
        /// Gravship 携带的 Thing。返回的 comp 可在未 Spawned 状态下解析/领取身份。</summary>
        public static CompDeltaVPilotConsole IdentityConsoleOf(Gravship gravship)
        {
            CompDeltaVPilotConsole primary = gravship?.PilotConsole?.TryGetComp<CompDeltaVPilotConsole>();
            if (primary != null)
            {
                return primary;
            }
            if (gravship != null)
            {
                foreach (Thing thing in gravship.Things)
                {
                    CompDeltaVPilotConsole comp = thing.TryGetComp<CompDeltaVPilotConsole>();
                    if (comp != null)
                    {
                        return comp;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 驾驶台重生（降落放置 / 读档 / 模式切换后的重新 Spawn）即重盖章：从本台
        /// BFS 相连基架（谓词：基架 ∧ owner ∈ {本船 ID, 未占有}）全部盖为本船 ID。
        /// 异主域（独立结构 / 残骸）不可达，不会被吞并；漏盖章的降落自愈。
        /// </summary>
        private void RestampShipDomain()
        {
            UniverseManager universe = Universe;
            Map map = parent.Map;
            if (universe == null || map == null || universe.FrameOf(map) == null)
            {
                return;
            }
            int id = ShipIdentityId(universe);
            if (id <= 0)
            {
                return;
            }
            SubstructureOwnership.For(map).ClaimConnectedDomain(parent.Position, id);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref shipId, "shipId", -1);
            Scribe_Values.Look(ref isMain, "isMain", false);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            UniverseManager universe = Universe;
            if (universe == null || !parent.Spawned)
            {
                yield break;
            }
            bool playerSide = parent.Faction == Faction.OfPlayer || DebugSettings.godMode;

            // 驾驶入口只服务「有宇宙容器且零重力」的图（地表/重力图只能逆重战略发射）。
            bool spaceMap = parent.Map != null && universe.FrameOf(parent.Map) != null
                && FreeFlightBootstrap.IsZeroGravity(universe, parent.Map);

            Ship ship = Ship;
            if (ship.IsValid)
            {
                // 驾驶切换（停靠/自由飞行统一入口，2026-09-24 统一分离定案）：已停靠的
                // 船同样可进入驾驶——首次推进器出力即触发与其余部分的分离，「脱离
                // 停靠」不再是独立命令。
                if (playerSide && spaceMap)
                {
                    ShipControlState control = universe.Pilot.GetControlState(ship);
                    bool planUnderManualControl = control == ShipControlState.Manual
                        && universe.Approach.ActivePlan?.ship.ownerId == ship.ownerId;
                    string label = planUnderManualControl
                        ? "DeltaV_ControlSwitchAutopilot".Translate()
                        : universe.Pilot.IsPlayerInputArmed(ship)
                            ? "DeltaV_PilotButtonExit".Translate()
                            : "DeltaV_PilotButtonEnter".Translate();
                    yield return NewAction(label, TexCommand.Draft,
                        () => universe.Pilot.ToggleControl(universe));
                }

                // 主台专属：改名与主动降级。
                yield return NewAction("DeltaV_ConsoleGizmoRename".Translate(), TexButton.Rename,
                    () => Find.WindowStack.Add(new Dialog_RenameStructure(
                        universe.LabelForOwner(ship.ownerId),
                        name => universe.RenameStructure(ship.ownerId, name))));
                yield return NewAction("DeltaV_ConsoleGizmoMakeBackup".Translate(),
                    // 1.6 无 UI/Commands/Cancel；原版取消图标在 Designators 下（TexCommand:41）。
                    ContentFinder<Texture2D>.Get("UI/Designators/Cancel"),
                    () => MakeBackupConsole(),
                    "DeltaV_ConsoleGizmoBackupTip".Translate());
            }
            else if (parent.Faction == Faction.OfPlayer && spaceMap)
            {
                // 未形成船的驾驶台（口袋/POI 图未认领）：认领主台并直接进入驾驶；
                // 首次出力触发分离。重力/地表图无此入口（只能逆重战略发射）。
                yield return NewAction("DeltaV_PilotButtonEnter".Translate(), TexCommand.Draft,
                    () =>
                    {
                        if (!isMain)
                        {
                            MakeMainConsole();
                        }
                        Ship claimed = Ship;
                        if (claimed.IsValid)
                        {
                            universe.Pilot.Enter(universe, claimed);
                        }
                        else
                        {
                            Messages.Message("DeltaV_PilotNeedShipFocus".Translate(),
                                MessageTypeDefOf.RejectInput, false);
                        }
                    });
            }

            if (!isMain)
            {
                yield return NewAction("DeltaV_ConsoleGizmoMakePrimary".Translate(), TexCommand.Install,
                    () => MakeMainConsole(),
                    "DeltaV_ConsoleGizmoPrimaryTip".Translate());
            }
        }

        private static Command_Action NewAction(string label, Texture2D icon, System.Action action, string tip = null)
        {
            return new Command_Action
            {
                defaultLabel = label,
                defaultDesc = tip ?? label,
                icon = icon,
                action = action,
                hotKey = null
            };
        }
    }

    /// <summary>
    /// 结构改名窗（原版 Dialog_Rename 是抽象泛型类，无法直接用于裸字符串；此处按其
    /// 交互形态自实现：文本框 + 回车/确认提交，forcePause）。改名唯一出口 =
    /// UniverseManager.RenameStructure（归属名 = 船名，同步本船引擎 RenamableLabel）。
    /// </summary>
    internal sealed class Dialog_RenameStructure : Window
    {
        private string name;
        private readonly Action<string> onConfirm;

        public override Vector2 InitialSize => new Vector2(420f, 150f);

        public Dialog_RenameStructure(string initial, Action<string> onConfirm)
        {
            name = initial ?? "";
            this.onConfirm = onConfirm;
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "DeltaV_RenameTitle".Translate());
            Text.Font = GameFont.Small;
            string renamed = Widgets.TextField(new Rect(0f, 40f, inRect.width, 32f), name);
            if (renamed.Length <= 28)
            {
                name = renamed;
            }
            bool committed = Widgets.ButtonText(
                new Rect(inRect.width / 2f - 60f, inRect.height - 36f, 120f, 32f), "OK".Translate());
            if (committed && Event.current.type == EventType.Used)
            {
                // 按钮消费了点击事件时回车不会同时触发；避免双重提交。
                committed = true;
            }
            if (committed
                || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
            {
                onConfirm(name.Trim());
                Close();
            }
        }
    }
}
