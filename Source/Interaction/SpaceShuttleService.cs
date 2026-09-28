using System.Collections.Generic;
using System.Linq;
using RimWorld;
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
    /// 与飞船共用：ShipPhysics（虚拟推进器注入）、ShipAutopilot（TrackTo/Pursue）、
    /// PilotService（PilotTarget）、SpaceCollision（联合效果）。运行时数据不序列化。
    /// </summary>
    public sealed class SpaceShuttleService
    {
        private readonly UniverseManager universe;

        /// <summary>侵入武装开关（面板；容器 objectId → armed）。</summary>
        private readonly HashSet<int> intrusionArmed = new HashSet<int>();

        /// <summary>侵入计划（目标容器；读档丢弃——运行时数据）。</summary>
        private readonly Dictionary<int, SpaceObject> intrusionTargets = new Dictionary<int, SpaceObject>();

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
            InjectVirtualThrusters(comp, container.map, shuttleBody);
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

        /// <summary>
        /// 虚拟推进器注入（§7.3）：逐项读取 XML 的「船体帧位置 + 推力矢量」，再按
        /// 舱体当前 Rot4 一次性转进稳定 Map 局部系。默认 PassengerShuttle 表是 8 台，
        /// 但数量、位置、方向和大小都可由 def/跳帮档位完整替换；此处不生成第二套布局。
        /// </summary>
        private static void InjectVirtualThrusters(CompSpaceShuttle comp, Map pocket, Building hull)
        {
            Vector2 origin = SpaceTransform.ShipLocalOrigin(pocket);
            Vector2 hullCenter = SpaceTransform.BoundsCenterOfCells(hull.OccupiedRect().Cells);
            float frameAngle = ShipOrientation.AngleFromRot4(hull.Rotation);
            IReadOnlyList<ShuttleThrusterConfig> layout = comp.ThrusterLayout;
            var thrusters = new List<ShipPhysics.Thruster>(layout?.Count ?? 0);
            if (layout != null)
            {
                for (int i = 0; i < layout.Count; i++)
                {
                    ShuttleThrusterConfig configured = layout[i];
                    if (configured == null || configured.force.sqrMagnitude <= 1e-6f) continue;
                    Vector2 mapForce = SpaceTransform.Rotate(configured.force, frameAngle);
                    float thrust = mapForce.magnitude;
                    thrusters.Add(new ShipPhysics.Thruster
                    {
                        thing = null,
                        localPos = hullCenter - origin
                            + SpaceTransform.Rotate(configured.position, frameAngle),
                        dir = mapForce / thrust,
                        operational = true,
                        thrust = thrust,
                    });
                }
            }
            ShipPhysics.SetVirtualThrusters(pocket, thrusters.ToArray());
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

        /// <summary>图上的在飞 shuttle 舱体（UpdateFrameState 的接管扫描用）。</summary>
        public List<Building> InFlightBodiesOn(Map map)
        {
            var result = new List<Building>();
            if (map == null) return result;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj.baseTag != SpaceObjectTag.Shuttle || obj.map != map) continue;
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (thing is Building building && building.TryGetComp<CompSpaceShuttle>() != null)
                    {
                        result.Add(building);
                    }
                }
            }
            return result;
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

        /// <summary>
        /// 每 tick 巡护（GameComponentTick 内、Projectiles 之前）：舱体被图内结算摧毁
        ///（PD/毁伤链 TakeDamage，§3.5「与飞船通用的受击链即此」）→ 容器收尾
        ///（乘员/货物按原版 transporter 摧毁语义，帧边界销图注销）。
        /// </summary>
        public void Tick()
        {
            List<SpaceObject> containers = InFlightContainers();
            foreach (SpaceObject container in containers)
            {
                Building hull = HullOf(container);
                if (hull != null && !hull.Destroyed)
                {
                    // 虚拟推进器是由舱体 def 可重建的运行时缓存，不序列化。换档清理后
                    // 首 tick 在唯一注入入口重建，保证在飞 shuttle 存读档后仍可驾驶。
                    if (!ShipPhysics.HasVirtualThrusters(container.map))
                    {
                        CompSpaceShuttle comp = hull.TryGetComp<CompSpaceShuttle>();
                        if (comp != null) InjectVirtualThrusters(comp, container.map, hull);
                    }
                    continue;
                }
                SpaceObject captured = container;
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"shuttle container #{captured.objectId}: hull destroyed in-map; MiniShip torn down");
                universe.QueueFrameAction(delegate
                {
                    if (captured.map != null)
                    {
                        ShipPhysics.ClearVirtualThrusters(captured.map);
                        universe.Lifecycle.DestroyPocketMap(captured.map);
                    }
                    intrusionArmed.Remove(captured.objectId);
                    intrusionTargets.Remove(captured.objectId);
                    if (universe.objects.Contains(captured))
                    {
                        universe.UnregisterObject(captured);
                    }
                });
            }
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

        // ---- 侵入式对接（§7.5 = 导弹机制：tag 切换 + 满推追踪 + 联合后效放置）----

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
                SpaceObject target = frame != null
                    ? ShellPicking.ShellAtFramePoint(universe, frameMap, UI.MouseMapPosition())
                    : null;
                if (target == null || target == frame)
                {
                    Messages.Message("DeltaV_ShuttleBoardNoTarget".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                    return;
                }
                EngageIntrusion(body, target);
            });
        }

        /// <summary>
        /// 进入侵入：容器 tag 由 shuttle 切换为 projectile（获得弹体角色：碰撞主动参与
        /// /阻力豁免/可拦截；projectileRoleOverride，§3.1），自动驾驶切满推追踪
        ///（Pursue，无刹车包络）。打断接管 = 驾驶键 Enter 内 Detach 后由
        /// BreakIntrusion 在 UpdateFrameState 兜底（目标保留可重入）。
        /// </summary>
        public void EngageIntrusion(Building body, SpaceObject target)
        {
            SpaceObject container = universe.FrameOf(body.Map);
            if (container == null || container.baseTag != SpaceObjectTag.Shuttle) return;
            container.projectileRoleOverride = true;
            intrusionTargets[container.objectId] = target;
            universe.Pilot.Exit(universe);
            universe.Autopilot.Pursue(body, target, delegate
            {
                // 目标失效：退出侵入（tag 切回），目标丢弃。
                var captured = container;
                BreakIntrusion(captured, dropTarget: true);
            });
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"intrusion engaged: shuttle container #{container.objectId} -> '{target.label}' (tag->projectile)");
            Messages.Message("DeltaV_ShuttleBoardEngaged".Translate(target.label),
                MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>退出侵入（打断/目标失效/读档后）：tag 切回 shuttle（目标默认保留可重入）。</summary>
        public void BreakIntrusion(SpaceObject container, bool dropTarget)
        {
            if (container == null) return;
            container.projectileRoleOverride = false;
            if (dropTarget) intrusionTargets.Remove(container.objectId);
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"intrusion broken off: shuttle container #{container.objectId} (tag->shuttle)");
        }

        /// <summary>
        /// 侵入命中结算（联合后效的放置段，§7.5）：舱体 building 放置在撞击位置**靠船体
        /// 内侧一些**（沿内法线偏移；脚印冲突建筑清障、沿入射回退首个可放位置），
        /// 容器销毁、乘员/货物仍 held 由原版一键卸货出击。帧边界执行。
        /// </summary>
        public void PlaceBoardedShuttle(SpaceObject container, SpaceObject defender,
            Vector3 hitUniverse, Vector2 hitLocal)
        {
            universe.QueueFrameAction(delegate
            {
                Building hull = null;
                if (container?.map != null)
                {
                    foreach (Thing thing in container.map.listerThings.AllThings)
                    {
                        if (thing is Building building && building.TryGetComp<CompSpaceShuttle>() != null)
                        {
                            hull = building;
                            break;
                        }
                    }
                }
                Map targetMap = defender?.map;
                if (hull == null || targetMap == null || targetMap.Disposed)
                {
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        "boarded shuttle placement failed: hull or target map lost");
                    return;
                }
                // 内法线偏移：命中点向宿主中心回退（沿入射反方向）一格起步，找首个可放位置。
                Vector2 center = SpaceTransform.ShipLocalOrigin(targetMap);
                Vector2 inward = center - hitLocal;
                Vector2 dir = inward.sqrMagnitude > 1e-6f ? inward.normalized : Vector2.zero;
                IntVec3 placed = IntVec3.Invalid;
                for (int step = 0; step <= 8; step++)
                {
                    Vector2 probe = hitLocal + dir * step;
                    IntVec3 root = new IntVec3(Mathf.FloorToInt(probe.x), 0, Mathf.FloorToInt(probe.y))
                        .ClampInsideMap(targetMap);
                    root = new IntVec3(
                        Mathf.Clamp(root.x, 0, targetMap.Size.x - hull.def.Size.x), 0,
                        Mathf.Clamp(root.z, 0, targetMap.Size.z - hull.def.Size.z));
                    // 清障：脚印冲突建筑摧毁（Vanish），地形保留。
                    bool blocked = false;
                    foreach (IntVec3 cell in hull.OccupiedRect().MovedBy(root))
                    {
                        foreach (Thing thing in cell.GetThingList(targetMap).ToList())
                        {
                            if (thing is Building conflict && !conflict.Destroyed && conflict != hull)
                            {
                                conflict.Destroy(DestroyMode.Vanish);
                            }
                        }
                        if (!cell.Standable(targetMap) && cell.GetDoor(targetMap) == null) blocked = true;
                    }
                    if (!blocked)
                    {
                        placed = root;
                        break;
                    }
                }
                Map pocket = container.map;
                hull.DeSpawn(DestroyMode.WillReplace);
                if (placed.IsValid)
                {
                    GenSpawn.Spawn(hull, placed, targetMap, hull.Rotation);
                    CompLaunchable launchable = hull.TryGetComp<CompLaunchable>();
                    StartLaunchCooldown(launchable); // 降落后处理：回落建筑 + 原版冷却（§7.4 唯一差异分支）
                }
                else
                {
                    hull.Destroy(DestroyMode.Vanish); // 无处安放：舱毁（乘员随 transporter 语义）
                }
                ClearShuttlePocketRuntime(pocket, container);
                universe.Lifecycle.DestroyPocketMap(pocket);
                if (universe.objects.Contains(container))
                {
                    universe.UnregisterObject(container);
                }
                PlanetGlobeRenderer.InvalidateSilhouette(targetMap);
                SubstructureOwnership.BumpStructureVersion();
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"boarded shuttle placed inside '{defender.label}' at {placed}; container #{container.objectId} destroyed");
            });
        }

        /// <summary>
        /// shuttle 源口袋的运行时键清理（图销毁前，FinishDocking 公共收尾调用）：
        /// 虚拟推进器表 + 侵入武装/目标键。图的销毁与容器注销走公共
        /// ShipFlightTransitionService.FinishDocking，不在此另实现。
        /// </summary>
        internal void ClearShuttlePocketRuntime(Map pocketMap, SpaceObject container)
        {
            if (pocketMap != null && !pocketMap.Disposed)
            {
                ShipPhysics.ClearVirtualThrusters(pocketMap);
            }
            if (container != null)
            {
                intrusionArmed.Remove(container.objectId);
                intrusionTargets.Remove(container.objectId);
            }
        }

        /// <summary>dev ResetObjects / 图销毁清零。</summary>
        public void ResetFlow()
        {
            intrusionArmed.Clear();
            intrusionTargets.Clear();
        }
    }
}
