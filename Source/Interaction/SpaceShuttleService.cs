using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// shuttle comp（太空战架构 §7）：舱体建筑 = 原版 CompTransporter 装货 + 原版
    /// CompLaunchable 发射语义（起飞门禁 = 原版 CanLaunch 结果直取，月池豁免屋顶项
    /// 见 Patch_MoonPool）+ 虚拟推进器 XML 配置（默认 8 台；位置/推力矢量逐台定义）+ 侵入能力
    ///（per-def canBoard）。停靠时是普通建筑；起飞后 = MiniShip（tag=Shuttle 容器）。
    /// 与飞船不得有两套逻辑：飞行/驾驶/自动驾驶/受击/普通停靠全部复用同一实现；
    /// shuttle 特有的只有单建筑域属性、落地冷却与侵入（= 导弹机制）。
    /// </summary>
    public class CompProperties_SpaceShuttle : CompProperties
    {
        /// <summary>具备侵入式对接能力（跳帮特化；普通 shuttle false）。</summary>
        public bool canBoard;

        /// <summary>
        /// 默认虚拟推进器布局。每项的位置/推力矢量都在 shuttle 船体帧中表达；
        /// DefModExtension_BoardingProfile.virtualThrusters 非空时整体覆盖本表。
        /// </summary>
        public List<ShuttleThrusterConfig> virtualThrusters;

        public CompProperties_SpaceShuttle()
        {
            compClass = typeof(CompSpaceShuttle);
        }
    }

    public class CompSpaceShuttle : ThingComp
    {
        public CompProperties_SpaceShuttle ShuttleProps => (CompProperties_SpaceShuttle)props;

        /// <summary>跳帮档位扩展（可空；真继承 def 把档位放 modExtensions，见 DefModExtension_BoardingProfile）。</summary>
        public DefModExtension_BoardingProfile BoardingProfile => parent?.def.GetModExtension<DefModExtension_BoardingProfile>();

        /// <summary>是否具备侵入式对接能力（唯一解析点：modExtension.canBoard ∨ comp.canBoard）。</summary>
        public bool CanBoard => BoardingProfile?.canBoard == true || ShuttleProps.canBoard;

        /// <summary>虚拟推进器布局唯一解析点：modExtension 非空表优先，否则用 comp 默认表。</summary>
        public IReadOnlyList<ShuttleThrusterConfig> ThrusterLayout =>
            BoardingProfile?.virtualThrusters != null && BoardingProfile.virtualThrusters.Count > 0
                ? BoardingProfile.virtualThrusters
                : ShuttleProps.virtualThrusters;

        public CompLaunchable Launchable => parent.TryGetComp<CompLaunchable>();

        /// <summary>所在图是否为宇宙容器图（太空侧语义适用）。</summary>
        public SpaceObject Container => Current.Game?.GetComponent<UniverseManager>()?.FrameOf(parent.Map);

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            base.PostDestroy(mode, previousMap);
            Current.Game?.GetComponent<UniverseManager>()?.Shuttles
                .NotifyHullDestroyed(previousMap);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            bool playerSide = parent.Faction == Faction.OfPlayer || DebugSettings.godMode;
            if (universe == null || !parent.Spawned || !playerSide)
            {
                yield break;
            }
            // 起飞（停靠中）：门禁 = 原版 CanLaunch 结果直取（含装货/超重/冷却）；
            // 月池屋顶豁免见 Patch_MoonPool。
            if (Container != null && Launchable != null)
            {
                CompLaunchable launchable = Launchable;
                var launch = new Command_Action
                {
                    defaultLabel = "DeltaV_ShuttleLaunchGizmo".Translate(),
                    defaultDesc = "DeltaV_ShuttleLaunchGizmoTip".Translate(),
                    icon = TexCommand.Attack,
                    action = delegate { universe.Shuttles.Launch((Building)parent); }
                };
                AcceptanceReport gate = launchable.CanLaunch();
                if (!gate)
                {
                    launch.Disable(gate.Reason);
                }
                yield return launch;
            }
            // 起飞后其余操作（驾驶切换/选择对接/侵入目标指定）统一在 ShipStatusPanel，
            // gizmo 只保留停船状态（普通建筑）必需的起飞入口（2026-09-27 双入口收敛定案）。
        }
    }

    /// <summary>
    /// shuttle 服务（太空战架构 §7/§10，universe.Shuttles 实例服务）：起飞（原版校验
    /// 直取 + 单建筑捕获进口袋）、停靠复用飞船的 Gravship 捕获/剥离/放置链、
    /// 侵入编排（tag 切换的导弹机制）。
    /// 与飞船共用：SpaceObject 驾驶契约、ShipAutopilot（普通对接）、
    /// PilotService（PilotTarget）、SpaceCollision（联合效果）。运行时数据不序列化。
    /// </summary>
    public sealed class SpaceShuttleService
    {
        private readonly UniverseManager universe;

        /// <summary>侵入武装开关（面板；容器 objectId → armed）。</summary>
        private readonly HashSet<int> intrusionArmed = new HashSet<int>();


        public SpaceShuttleService(UniverseManager universe)
        {
            this.universe = universe;
        }

        // ---- 起飞（§7.1/§7.2）----

        /// <summary>
        /// 起飞门禁 = 原版 CompLaunchable.CanLaunch 结果直取（装货/燃料/超重/冷却；
        /// 月池屋顶豁免在 Patch_MoonPool）。通过即把舱体建筑（含 held 内容）移入新
        /// 口袋容器：单建筑搬运 = DeSpawn/Spawn（不搬基架、不进控制域——舱体 held 的
        /// 乘员/货物随 CompTransporter 落盘照常）；位姿/速度 = 宿主刚体上舱体锚点
        ///（RigidPointPose，含自旋切向继承）。冷却写原版 lastLaunchTick（字段镜像
        /// 单点）；起飞与停靠回落共用（§7.4「触发原版降落冷却」同入口）。
        /// </summary>
        public void Launch(Building shuttleBody)
        {
            CompSpaceShuttle comp = shuttleBody?.TryGetComp<CompSpaceShuttle>();
            CompLaunchable launchable = comp?.Launchable;
            Map sourceMap = shuttleBody?.Map;
            SpaceObject host = sourceMap != null ? universe.FrameOf(sourceMap) : null;
            if (comp == null || launchable == null || host == null || sourceMap == null)
            {
                return;
            }
            AcceptanceReport gate = launchable.CanLaunch();
            if (!gate)
            {
                Messages.Message("DeltaV_ShuttleLaunchDenied".Translate(gate.Reason),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 统一起飞核心（§7.2「与船脱离同一套」）：TransferDomainToNewPocket 的舱体
            // 参数化调用——舱内站立的乘员/货物随捕获清单进入新口袋（修复「乘客留在
            // 起飞点」）；不搬基架（宿主甲板/屋顶原样保留）；聚焦交接在核心内完成
            //（与船脱离同一段 SetFocusedWithCamera 代码）。
            SpaceObject container = universe.ShipTransitions.LaunchShuttleToPocket(universe, shuttleBody);
            if (container == null)
            {
                Log.Error($"[DeltaV] Shuttle '{shuttleBody.LabelCap}': transfer-to-pocket failed.");
                return;
            }
            StartLaunchCooldown(launchable);
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"shuttle launched: '{shuttleBody.LabelCap}' -> MiniShip container #{container.objectId} " +
                $"({UniverseManager.MapTag(container.map)})");
            Messages.Message("DeltaV_ShuttleLaunched".Translate(shuttleBody.LabelCap),
                MessageTypeDefOf.PositiveEvent, false);
            // 起飞即驾驶：shuttle 靠起飞按钮进入宇宙，玩家方起飞后直接接管（飞船无需
            // 此机制——本就处于驾驶状态才能出力起飞）。只处理聚焦已随起飞交接进新
            // 口袋图的情况，否则下一帧聚焦自动退出驾驶，进入无意义；dev 敌对发射与
            // NPC shuttle 经派系门排除。其余门禁（零重力/流程互斥/派系）由 Enter
            // 内部 CanEnter 兜底。
            if (shuttleBody.Faction == Faction.OfPlayer && container.map != null
                && Find.CurrentMap == container.map)
            {
                universe.Pilot.Enter(universe, PilotTarget.Of(shuttleBody));
            }
        }

        /// <summary>原版 CompLaunchable.lastLaunchTick 字段镜像（起飞与停靠回落共用的冷却单点）。</summary>
        private static readonly System.Reflection.FieldInfo LastLaunchTickField =
            HarmonyLib.AccessTools.Field(typeof(CompLaunchable), "lastLaunchTick");

        internal static void StartLaunchCooldown(CompLaunchable launchable)
        {
            if (launchable != null && LastLaunchTickField != null)
            {
                LastLaunchTickField.SetValue(launchable, Find.TickManager.TicksGame);
            }
        }

        // ---- 查询 ----

        /// <summary>当前图上的在飞 shuttle 舱体（UpdateFrameState 的接管查询用）。</summary>
        public Building InFlightBodyOn(Map map)
        {
            SpaceObject container = universe.FrameOf(map);
            return container?.baseTag == SpaceObjectTag.Shuttle ? HullOf(container) : null;
        }

        /// <summary>舱体所在容器（图上首个 shuttle comp 的容器归属；无则 null）。</summary>
        public static Building HullOf(SpaceObject container)
        {
            Map map = container?.map;
            if (map == null) return null;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is Building building && building.TryGetComp<CompSpaceShuttle>() != null)
                {
                    return building;
                }
            }
            return null;
        }

        /// <summary>舱体销毁事件：乘员/货物由原版 transporter 处理，容器在帧边界收尾。</summary>
        internal void NotifyHullDestroyed(Map map)
        {
            SpaceObject container = universe.FrameOf(map);
            if (container?.baseTag != SpaceObjectTag.Shuttle) return;
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"shuttle container #{container.objectId}: hull destroyed in-map; MiniShip torn down");
            universe.QueueFrameAction(delegate
            {
                if (!universe.objects.Contains(container)) return;
                universe.Lifecycle.DestroyPocketMap(map);
                universe.UnregisterObject(container);
            });
        }

        /// <summary>外部卸图时，注销已失去地图的 shuttle 容器。</summary>
        internal void NotifyMapRemoved(Map map)
        {
            SpaceObject container = universe.FrameOf(map);
            if (container?.baseTag != SpaceObjectTag.Shuttle) return;
            universe.QueueFrameAction(delegate
            {
                if (universe.objects.Contains(container)) universe.UnregisterObject(container);
            });
        }

        /// <summary>全部在飞 shuttle 容器（面板列表）。</summary>
        public List<SpaceObject> InFlightContainers()
        {
            var result = new List<SpaceObject>();
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj.baseTag == SpaceObjectTag.Shuttle) result.Add(obj);
            }
            return result;
        }

        // ---- 侵入式对接（§7.5 = 导弹接管 + 联合后效 + 共同降落链）----

        public bool IntrusionArmed(SpaceObject container)
        {
            return container != null && intrusionArmed.Contains(container.objectId);
        }

        /// <summary>面板开关（针对在飞 shuttle）：canBoard 才可开。</summary>
        public bool ToggleIntrusionArmed(SpaceObject container)
        {
            if (container == null) return false;
            if (!intrusionArmed.Contains(container.objectId)) intrusionArmed.Add(container.objectId);
            else intrusionArmed.Remove(container.objectId);
            return true;
        }

        /// <summary>侵入指挥：点击敌舰外壳指定目标容器（Targeter 流程内经 ShellPicking 解析）。</summary>
        public void BeginBoardTargeting(PilotTarget shuttle)
        {
            Building body = shuttle.shuttle;
            if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"boarding targeter begin tick={Find.TickManager.TicksGame} hull=#{body?.thingIDNumber} " +
                    $"spawned={body?.Spawned} current={UniverseManager.MapTag(Find.CurrentMap)} " +
                    $"hullMap={UniverseManager.MapTag(body?.Map)}");
            if (body == null || !body.Spawned) return;
            Find.Targeter.BeginTargeting(new TargetingParameters
            {
                canTargetPawns = false,
                canTargetBuildings = false,
                canTargetItems = false,
                canTargetSelf = false,
            }, localTarget =>
            {
                // 目标 = 点击处命中的宇宙容器（ShellPicking 在瞄准会话内解析）。
                Map frameMap = Find.CurrentMap;
                SpaceObject frame = universe.FrameOf(frameMap);
                Vector3 mouse = UI.MouseMapPosition();
                SpaceObject target = frame != null
                    ? ShellPicking.ShellAtFramePoint(universe, frameMap, mouse)
                    : null;
                if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"boarding targeter callback tick={Find.TickManager.TicksGame} " +
                        $"local={localTarget} mouse={UI.MouseMapPosition()} frame=#{frame?.objectId} " +
                        $"picked=#{target?.objectId} tag={target?.Tag} " +
                        $"targetMap={UniverseManager.MapTag(target?.map)}");
                if (target == null || target == frame)
                {
                    Messages.Message("DeltaV_ShuttleBoardNoTarget".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                    return;
                }
                Vector3 hitDirection = SpaceTransform.MapPointToUniverse(frame, frameMap,
                    new Vector2(mouse.x, mouse.z));
                Vector2 hitMapPoint = SpaceTransform.UniverseToMapPoint(target, target.map, hitDirection);
                EngageIntrusion(body, target, true, hitMapPoint);
            }, highlightAction: null, targetValidator: localTarget =>
            {
                // 原版 LocalTargetInfo 仅接受当前 Map 内的格；非聚焦船壳可显示在
                // 11×13 shuttle 口袋图之外。让 Targeter 交付点击，再由 ShellPicking 判壳。
                if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"boarding targeter validate tick={Find.TickManager.TicksGame} " +
                        $"local={localTarget} valid={localTarget.IsValid} " +
                        $"mouseCell={UI.MouseCell()} current={UniverseManager.MapTag(Find.CurrentMap)}");
                return true;
            });
        }

        /// <summary>
        /// 进入侵入：现有艇体交给导弹服务，不创建第二个容器或自动驾驶任务。
        /// </summary>
        public void EngageIntrusion(Building body, SpaceObject target, bool playerInitiated = true,
            Vector2? targetMapPoint = null)
        {
            SpaceObject container = universe.FrameOf(body.Map);
            if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"boarding engage request tick={Find.TickManager.TicksGame} hull=#{body.thingIDNumber} " +
                    $"shuttle=#{container?.objectId} baseTag={container?.baseTag} " +
                    $"target=#{target?.objectId} targetTag={target?.Tag} " +
                    $"targetMap={UniverseManager.MapTag(target?.map)} " +
                    $"ordinaryProjectiles={universe.Projectiles.InFlightCount} player={playerInitiated}");
            if (container == null || container.baseTag != SpaceObjectTag.Shuttle
                || container.projectileRoleOverride || target == null || target == container) return;
            universe.Autopilot.Detach(DriveSubject.Of(PilotTarget.Of(body)), "boarding missile launch");
            if (universe.Pilot.PilotedTarget(universe).ControlKey == body.thingIDNumber)
                universe.Pilot.Exit(universe);
            if (!universe.Projectiles.AdoptShuttle(container, body, target, targetMapPoint)) return;
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"intrusion engaged: shuttle container #{container.objectId} -> '{target.label}' (tag->projectile)");
            Messages.Message("DeltaV_ShuttleBoardEngaged".Translate(target.label),
                MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>退出侵入（打断/目标失效/读档后）：tag 切回 shuttle（目标默认保留可重入）。</summary>
        public void BreakIntrusion(SpaceObject container, bool dropTarget)
        {
            if (container == null) return;
            universe.Projectiles.ReleaseShuttle(container);
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"intrusion broken off: shuttle container #{container.objectId} (tag->shuttle)");
        }

        /// <summary>导弹命中后效：在帧边界以真正的 Gravship 搬运链降落。</summary>
        public void PlaceBoardedShuttle(SpaceObject container, SpaceObject defender,
            Vector3 hitUniverse, Vector2 hitLocal, Vector2 incidentWorld,
            IntVec3 hitCell, bool shieldHit)
        {
            int sourceId = container.objectId, sourceVersion = container.RepresentationVersion;
            int targetId = defender.objectId, targetVersion = defender.RepresentationVersion;
            universe.QueueFrameAction(delegate
            {
                container = universe.ObjectById(sourceId);
                defender = universe.ObjectById(targetId);
                if (container?.RepresentationVersion != sourceVersion || defender?.RepresentationVersion != targetVersion) return;
                Map targetMap = defender?.map;
                Map sourceMap = container?.map;
                Building hull = HullOf(container);
                if (hull == null || sourceMap == null || targetMap == null || targetMap.Disposed
                    || !universe.objects.Contains(defender) || !universe.objects.Contains(container))
                    return;

                // 护盾点在舱壁之外：沿本发实际入射方向找第一格船壳。
                Vector3 ahead = OrbitSphere.Move(hitUniverse, incidentWorld * 0.25f);
                Vector2 localDirection = SpaceTransform.UniverseToMapPoint(defender, targetMap, ahead)
                    - SpaceTransform.UniverseToMapPoint(defender, targetMap, hitUniverse);
                if (localDirection.sqrMagnitude < 1e-8f) return;
                localDirection.Normalize();
                IntVec3 entry = hitCell;
                if (shieldHit)
                {
                    entry = IntVec3.Invalid;
                    for (float distance = 0f; distance <= 64f; distance += 0.25f)
                    {
                        Vector2 probe = hitLocal + localDirection * distance;
                        IntVec3 cell = new IntVec3(Mathf.FloorToInt(probe.x), 0,
                            Mathf.FloorToInt(probe.y));
                        if (!cell.InBounds(targetMap)) continue;
                        if (cell.GetThingList(targetMap).Any(t => t is Building b && b.def.IsWall))
                        {
                            entry = cell;
                            break;
                        }
                    }
                }
                if (!entry.IsValid || !entry.InBounds(targetMap))
                {
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"boarding landing refused: no target hull near {hitLocal} on {UniverseManager.MapTag(targetMap)}");
                    return;
                }

                Rot4 facing = ShipOrientation.Rot4FromAngle(
                    Mathf.Atan2(localDirection.y, localDirection.x));
                IntVec3 lateralStep = facing == Rot4.North || facing == Rot4.South
                    ? IntVec3.East : IntVec3.North;
                for (int depth = 0; depth < 2; depth++)
                for (int lateral = -1; lateral <= 1; lateral++)
                    if (!(entry + facing.FacingCell * depth + lateralStep * lateral).InBounds(targetMap))
                        return;
                // Thing.Position 是原版多格建筑的中心锚，不是脚印左下角。
                IntVec3 desiredCenter = entry - facing.FacingCell * 2;
                CellRect occupied = GenAdj.OccupiedRect(desiredCenter, facing, hull.def.size);
                if (!occupied.Cells.All(c => c.InBounds(targetMap)))
                {
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"boarding landing refused: footprint crosses map edge at {entry}");
                    return;
                }
                // 艇身后四排应位于船外；不能借原版 GenSpawn 的 wipe 语义
                // 无提示地抹掉另一片结构。入口两排由破拆后效显式处理。
                foreach (IntVec3 cell in occupied.Cells)
                {
                    bool inBreach = cell == entry || cell == entry + lateralStep
                        || cell == entry - lateralStep;
                    if (inBreach) continue;
                    if (cell.GetThingList(targetMap).Any(t => t is Building))
                    {
                        DeltaVLog.Message(DeltaVLogModule.Combat,
                            $"boarding landing refused: occupied rear footprint at {cell}; " +
                            $"entry={entry} facing={facing} center={desiredCenter} " +
                            $"hitLocal={hitLocal} incident={localDirection}");
                        return;
                    }
                }

                PilotTarget shuttle = PilotTarget.Of(hull);
                Gravship gravship = universe.ShipTransitions.CaptureDockingTargetForCompletion(
                    shuttle, out HashSet<IntVec3> capturedCells, out bool includeTerrain);
                if (gravship == null) return;
                Rot4 placementRotation = ShipOrientation.PlacementRotation(hull.Rotation, facing);
                gravship.Rotation = placementRotation;
                if (!gravship.ThingPlacements.TryGetValue(hull, out RimWorld.Planet.PositionData.Data placement))
                    return;
                if (placement.rotation != facing) return;
                IntVec3 landingRoot = desiredCenter - placement.local;
                var handoff = Find.CurrentMap == sourceMap
                    ? new ShipFlightTransitionService.DockingFocusHandoff(defender,
                        new Vector2(entry.x + 0.5f, entry.z + 0.5f),
                        Find.CameraDriver.RootSize)
                    : default(ShipFlightTransitionService.DockingFocusHandoff);
                // 破拆在落点确定之后，且只对两排三格的建筑生效；基架不动。
                SpaceImpactEffects.BreachBoardingCorridor(targetMap, entry, facing, hull);
                if (!universe.ShipTransitions.CompleteTransferIntoHost(universe, shuttle,
                    gravship, capturedCells, includeTerrain, targetMap, landingRoot,
                    placementRotation, 0, out Map transferredSource, impactLanding: true))
                {
                    Log.Error("[DeltaV] Boarding breach completed but shared Gravship transfer failed.");
                    return;
                }
                var completion = new DockingCompletion.ShuttleDockingCompletion(
                    universe, hull, targetMap, transferredSource, handoff, impactLanding: true);
                completion.OnTransferedIntoHost();
                universe.QueueFrameAction(completion.Finish);
            });
        }

        /// <summary>
        /// 容器注销时清理侵入运行时键。
        /// </summary>
        internal void ForgetContainer(SpaceObject container)
        {
            if (container != null)
            {
                intrusionArmed.Remove(container.objectId);
            }
        }

        /// <summary>dev ResetObjects / 图销毁清零。</summary>
        public void ResetFlow()
        {
            intrusionArmed.Clear();
        }
    }
}
