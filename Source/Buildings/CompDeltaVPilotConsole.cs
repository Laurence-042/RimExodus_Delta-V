using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 驾驶台（阶段3）：正式进入/退出驾驶的入口 + 主驾驶台规则。
    ///
    /// 主驾驶台规则（story.md「固定轨道/建造模式」）：第一台自动成为主驾驶台，
    /// 第二台不会取代主台，除非玩家显式用 gizmo 设置；开始自由飞行时飞船朝向跟随
    /// 主驾驶台的朝向（ShipFlightTransitionService 消费）。主台被拆除时转交给船上最老的一台。
    ///
    /// 本 Comp 由 XML patch 追加到原版 PilotConsole，与原版 CompPilotConsole 共存：原版
    /// Gravship 继续按 ThingDefOf.PilotConsole 识别设施与发射方向。本 Comp 在共享地图上
    /// 以被选中的驾驶台确定具体 Gravship；进入独占口袋图后再提供驾驶切换等 Delta-V gizmo。
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

        private ShipState Ship
        {
            get
            {
                UniverseManager universe = Universe;
                Map map = parent.Map;
                if (universe == null || map == null)
                {
                    return null;
                }

                // 驾驶台身份是第一裁决源：共享图上可能同时有一个仍持有该 Map 的移动
                // ShipState 和若干停靠船，不能先用 ShipOnMap 把所有驾驶台都归给宿主。
                ShipState identified = universe.ShipById(shipId);
                if (identified != null && ShipStructureResolver.Contains(identified, parent))
                {
                    return identified;
                }

                // 新建驾驶台尚未领取 shipId 时按船体域解析；停靠船优先，保证玩家把残骸
                // 基架改归本船并接通后，新域上的驾驶台仍得到脱离停靠入口。
                ShipState docked = universe.Ships.FirstOrDefault(s => s.IsDocked
                    && s.dockedMap == map && ShipStructureResolver.Contains(s, parent));
                if (docked != null)
                {
                    return docked;
                }

                ShipState mapOwner = universe.ShipOnMap(map);
                return mapOwner != null && ShipStructureResolver.Contains(mapOwner, parent)
                    ? mapOwner
                    : null;
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!respawningAfterLoad)
            {
                ShipState ship = Ship;
                if (ship != null)
                {
                    EnsurePrimaryConsole(ship);
                }
            }
            RestampShipDomain();
        }

        /// <summary>
        /// 场景盖章发生在 ShipState 注册之前，Comp.PostSpawnSetup 当时无法认领主台；注册后
        /// 由场景入口补一次。普通游戏中后建造的驾驶台仍由 PostSpawnSetup 自动认领。
        /// </summary>
        public static Building EnsurePrimaryConsole(ShipState ship)
        {
            if (ship == null)
            {
                return null;
            }
            Building current = ship.primaryConsole;
            if (current != null && !current.Destroyed && current.Spawned)
            {
                return current;
            }
            ship.primaryConsole = OldestConsoleOn(ship);
            if (ship.primaryConsole != null)
            {
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"{ship.label}: primary vanilla pilot console auto-assigned ({ship.primaryConsole.Position})");
            }
            return ship.primaryConsole;
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            // 驾驶台可能在口袋图（飞行中）也可能在宿主图（停靠中），按所在图双路径找船。
            UniverseManager universe = Universe;
            ShipState ship = universe?.ShipOnMap(previousMap);
            if (ship == null && universe != null)
            {
                ship = universe.Ships.FirstOrDefault(s => s.IsDocked && s.dockedMap == previousMap
                    && s.primaryConsole == parent);
            }
            if (ship != null && ship.primaryConsole == parent)
            {
                ship.primaryConsole = OldestConsoleOn(ship);
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"{ship.label}: primary console destroyed; {(ship.primaryConsole == null ? "none left" : "transferred to oldest remaining")}");
            }
            base.PostDestroy(mode, previousMap);
        }

        private static Building OldestConsoleOn(ShipState ship)
        {
            // 停靠中的船上其余驾驶台与被拆台同图（宿主图），从宿主图找；飞行中从口袋图找。
            Map consoleMap = ship.InteriorMap;
            if (consoleMap == null)
            {
                return null;
            }
            Building oldest = null;
            foreach (Thing thing in consoleMap.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
            {
                if (thing.TryGetComp<CompDeltaVPilotConsole>() != null
                    && (!ship.IsDocked || ShipStructureResolver.Contains(ship, thing))
                    && (oldest == null || thing.thingIDNumber < oldest.thingIDNumber))
                {
                    oldest = thing as Building;
                }
            }
            return oldest;
        }

        // ---- 船份身份（2026-09-20 设计：驾驶台 = 身份锚点）----

        /// <summary>本结构的持久身份 ID：一经领取永不改变、随 comp 存档。旧档的
        /// 一次性停泊字段（parkedShipId）在 PostExposeData 迁移读入。</summary>
        public int shipId = -1;

        /// <summary>
        /// 解析本结构的身份 ID（确定性顺序）：已有 shipId → 本结构已注册 ShipState →
        /// 本格归属域 owner（无注册对象持有时认领：副台认领主台、战略降落后域内恢复；
        /// 被 POI 持有则不认领——wreck 甲板上的新台不冒领 wreck 身份）→ 统一计数器
        /// 新领（UniverseManager.AllocateOwnerId，永不回号）。领取/认领时同步注册船名。
        /// 未 Spawned 的 comp（随 Gravship 飞行中）也可调用——跳过域认领，直接领号。
        /// </summary>
        public int ShipIdentityId(UniverseManager universe)
        {
            if (shipId <= 0)
            {
                ShipState ship = Ship;
                if (ship != null)
                {
                    shipId = ship.objectId;
                }
                else if (parent.Spawned)
                {
                    SubstructureOwnership ownership = SubstructureOwnership.TryGet(parent.Map);
                    int domainOwner = ownership?.OwnerAt(parent.Position) ?? SubstructureOwnership.Unassigned;
                    if (domainOwner != SubstructureOwnership.Unassigned
                        && universe.ObjectById(domainOwner) == null)
                    {
                        shipId = domainOwner;
                    }
                }
                if (shipId <= 0)
                {
                    shipId = universe != null ? universe.AllocateOwnerId() : -1;
                }
            }
            NoteName(universe);
            return shipId;
        }

        /// <summary>把本结构当前船名（引擎名）注册到 UniverseManager（归属域名/显示用）。</summary>
        private void NoteName(UniverseManager universe)
        {
            string label = EngineLabel();
            if (shipId > 0 && !label.NullOrEmpty())
            {
                universe?.NoteShipName(shipId, label);
            }
        }

        private string EngineLabel()
        {
            return parent.TryGetComp<CompPilotConsole>()?.engine?.RenamableLabel;
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
        /// 异主域（独立结构 / 残骸）不可达，不会被吞并；漏盖章的降落自愈。放置时序
        /// 已核实：原版 PlaceGravshipInMap 先 SpawnFoundations 后 SpawnNonPawnThings，
        /// 重生时地基已盖章，副台经「本格域 owner」认领主台 ID。
        /// 承载图：真实轨道图（IsZeroGravity）或飞船自有口袋图（无 tile，IsZeroGravity
        /// 不覆盖；2026-09-20 教训——口袋图被守卫拦掉导致身份从未认领、战略飞行后
        /// 落点新领裸 ID）。wreck 等 POI 口袋图不属于任何 ShipState，照旧不触发。
        /// </summary>
        private void RestampShipDomain()
        {
            UniverseManager universe = Universe;
            Map map = parent.Map;
            if (universe == null || map == null
                || (!FreeFlightBootstrap.IsZeroGravity(map) && !(universe.SpaceObjectOnMap(map) is ShipState))
                || universe.FrameOf(map) == null)
            {
                return;
            }
            int id = ShipIdentityId(universe);
            if (id <= 0)
            {
                return;
            }
            // 设施链接在引擎 Spawn 时才建立（飞行中更被原版显式清空，见
            // CompGravshipFacility），此刻 NoteName 可能拿不到引擎名；放置批次
            // 完成后补注册一次，保证 shipNames 最终有名而非「独立结构 #N」。
            LongEventHandler.ExecuteWhenFinished(delegate { NoteName(Universe); });
            SubstructureOwnership.For(map).ClaimConnectedDomain(parent.Position, id);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref shipId, "shipId", -1);
            // 旧档一次性停泊字段：仅迁移读入（停泊语义已删除，身份永久驻留本 comp）。
            int parkedLegacy = -1;
            Scribe_Values.Look(ref parkedLegacy, "parkedShipId", -1);
            if (Scribe.mode == LoadSaveMode.LoadingVars && shipId <= 0 && parkedLegacy > 0)
            {
                shipId = parkedLegacy;
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            ShipState ship = Ship;
            UniverseManager universe = Universe;
            if (ship == null)
            {
                // 所有玩家原版驾驶台都提供自由飞行入口：地面图灰化并解释重力限制；
                // 未登记的 Orbit Gravship 则由 FreeFlightBootstrap 接入 Universe。
                if (parent.Spawned && parent.Faction == Faction.OfPlayer)
                {
                    Command_Action action = NewAction("DeltaV_BeginFreeFlightButton".Translate(), TexCommand.Draft,
                        () => FreeFlightBootstrap.TryEnter(universe, parent as Building));
                    if (!FreeFlightBootstrap.IsZeroGravity(parent.Map))
                    {
                        action.Disable("DeltaV_PilotGravityRestricted".Translate());
                    }
                    yield return action;
                }
                yield break;
            }
            if (ship.IsDocked)
            {
                // 固定图可能同时有多艘逆重飞船，必须由玩家点中这艘船的驾驶台来选择目标。
                // 控制权门禁（阶段4）：限玩家方（dev 放行）——敌舰驾驶台不是玩家入口。
                if (universe.Pilot.CanPlayerControl(ship))
                {
                    yield return NewAction("DeltaV_BeginFreeFlightButton".Translate(), TexCommand.Draft,
                        () => universe.ShipTransitions.BeginFreeFlight(universe, ship));
                }
            }
            else
            {
                if (universe.Pilot.CanPlayerControl(ship))
                {
                    ShipControlState control = universe.Pilot.GetControlState(ship);
                    bool planUnderManualControl = control == ShipControlState.Manual
                        && universe.Approach.ActivePlan?.ship == ship;
                    string label = planUnderManualControl
                        ? "DeltaV_ControlSwitchAutopilot".Translate()
                        : universe.Pilot.IsPlayerInputArmed(ship)
                            ? "DeltaV_PilotButtonExit".Translate()
                            : "DeltaV_PilotButtonEnter".Translate();
                    yield return NewAction(label, TexCommand.Draft,
                        () => universe.Pilot.ToggleControl(universe));
                }
            }

            if (ship.primaryConsole != parent)
            {
                yield return NewAction("DeltaV_ConsoleGizmoMakePrimary".Translate(), TexCommand.Install,
                    delegate
                    {
                        ship.primaryConsole = parent as Building;
                        Messages.Message("DeltaV_ConsolePrimarySet".Translate(parent.LabelCap), MessageTypeDefOf.NeutralEvent, false);
                    },
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
}
