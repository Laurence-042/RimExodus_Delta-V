using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 跳帮舱飞行（2026-09-21 阶段4）：载人制导飞行器——像导弹一样**追踪**目标船
    /// （纯追踪制导，无需速度匹配对接），命中即在命中点自动降落：撞穿外壳开孔 →
    /// 密封舱落位堵孔 → 乘员在目标图落点旁生成（乘员经 ThingOwner 容器搬运送达，
    /// 非自由 Pawn 跨图转移）。此后一切移动/战斗/追击都是目标图上的原版同图行为。
    ///
    /// 边界与纪律：
    /// - 自包含实现，**不是**阶段5 的通用 SpaceProjectile 系统（AGENTS §13 战斗纪律
    ///   不提前铺开；届时按正式设计重构，本类只保留「载人投放」职责）；
    /// - 只在轨道层持有位置（球面方向 + 追踪速度），显示层只投影不模拟；
    /// - 飞行期禁存档（Patch_SavingGate；运行时数据不序列化）；
    /// - 目标限 ShipState（有引擎连通域可估壳半径；POI 走整机对接）；
    /// - 单程：撤离 = 整机对接或占领目标。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class BoardingPodFlight
    {
        private class Flight : IThingHolder
        {
            public ShipState sourceShip;
            public ShipState target;
            public float hitRadius;
            public Vector3 position;
            public float speed;
            public int age;
            public ThingOwner<Thing> cargo;

            public IThingHolder ParentHolder => null;

            public void GetChildHolders(List<IThingHolder> outChildren)
            {
            }

            public ThingOwner GetDirectlyHeldThings()
            {
                return cargo;
            }
        }

        /// <summary>巡航速度（格/游戏秒）与超时（tick）：战术距离内数秒到达。
        /// tick 驱动 = 游戏时间语义：暂停冻结、倍速加速（AGENTS §18 #20）。</summary>
        private const float CruiseSpeed = 45f;

        private const int MaxFlightTicks = 3600;

        private static readonly List<Flight> flights = new List<Flight>();

        private static Texture2D markerTexture;
        private static Material markerMaterial;

        public static bool AnyInFlight => flights.Count > 0;

        /// <summary>
        /// 发射：乘员 DeSpawn 装入舱内容器，从源船位姿出发追踪目标。命中半径按目标
        /// 引擎相连基架域到 pose 锚（口袋图几何中心）的最远格估等效壳半径。
        /// </summary>
        public static void Launch(ShipState source, ShipState target, List<Pawn> pawns)
        {
            if (source?.map == null || target?.map == null || pawns.NullOrEmpty()) return;
            var flight = new Flight
            {
                sourceShip = source,
                target = target,
                position = source.pose.position,
                speed = CruiseSpeed,
                hitRadius = EstimateShellRadius(target),
            };
            flight.cargo = new ThingOwner<Thing>(flight);
            foreach (Pawn pawn in pawns.ToList())
            {
                if (pawn == null || pawn.Destroyed) continue;
                if (pawn.Spawned)
                {
                    pawn.DeSpawn(DestroyMode.Vanish);
                }
                flight.cargo.TryAdd(pawn, canMergeWithExistingStacks: false);
            }
            flights.Add(flight);
            Messages.Message("DeltaV_PodLaunched".Translate(pawns.Count, target.label),
                MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"boarding pod launched: {pawns.Count} pawns, '{source.label}' -> '{target.label}' (hit radius {flight.hitRadius:F0})");
        }

        /// <summary>UniverseManager.GameComponentTick 每 tick 驱动（游戏时间语义：
        /// 暂停冻结、倍速加速；绝不使用帧时钟）。</summary>
        public static void Tick()
        {
            if (flights.Count == 0) return;
            const float dt = 1f / 60f;
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                Flight flight = flights[i];
                flight.age += 1;
                if (flight.age > MaxFlightTicks
                    || flight.target.map == null || flight.target.map.Disposed)
                {
                    AbortFlight(flight, flight.target.map == null ? "target lost" : "timeout");
                    flights.RemoveAt(i);
                    continue;
                }
                // 纯追踪制导：速度方向始终指向目标当前位置（球面切空间，OrbitSphere 唯一入口）。
                Vector2 toTarget = OrbitSphere.Offset(flight.position, flight.target.pose.position);
                if (toTarget.sqrMagnitude < 1e-8f) toTarget = Vector2.right;
                flight.position = OrbitSphere.Move(flight.position, toTarget.normalized * flight.speed * dt);
                float distance = OrbitSphere.ArcCells(flight.position, flight.target.pose.position);
                if (distance <= flight.hitRadius)
                {
                    Impact(flight);
                    flights.RemoveAt(i);
                }
            }
        }

        /// <summary>目标壳等效半径：引擎相连基架域各格到 pose 锚（口袋图几何中心）的最远距离 + 2。</summary>
        private static float EstimateShellRadius(ShipState target)
        {
            Map map = target.map;
            Building_GravEngine engine = DockController.FindEngineForShip(map, target.primaryConsole);
            HashSet<IntVec3> cells = engine?.AllConnectedSubstructure;
            if (cells == null || cells.Count == 0) return map.Size.x * 0.5f;
            Vector2 center = SpaceTransform.ShipLocalOrigin(map);
            float radius = 0f;
            foreach (IntVec3 cell in cells)
            {
                float d = Vector2.SqrMagnitude(new Vector2(cell.x + 0.5f - center.x, cell.z + 0.5f - center.y));
                if (d > radius) radius = d;
            }
            return Mathf.Sqrt(radius) + 2f;
        }

        /// <summary>命中即降落：命中点 → 目标图格子 → 沿入射方向找首个外壳结构格开孔 → 密封舱落位 → 乘员投放。</summary>
        private static void Impact(Flight flight)
        {
            Map map = flight.target.map;
            Vector2 hitPoint = SpaceTransform.UniverseToMapPoint(flight.target, flight.position);
            IntVec3 breach = FindBreachCell(map, hitPoint);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"boarding pod impact on '{flight.target.label}': breach cell {breach}");

            // 撞穿开孔：摧毁破口格上的建筑（外壳墙体），地形/地基保留。
            foreach (Thing thing in breach.GetThingList(map).ToList())
            {
                if (thing is Building building && !building.Destroyed)
                {
                    building.Destroy(DestroyMode.Vanish);
                }
            }
            // 破口封顶（外壳格通常有顶；防御性补齐，保证密封舱自成不暴露真空的边界）。
            if (map.roofGrid.RoofAt(breach) == null)
            {
                map.roofGrid.SetRoof(breach, RoofDefOf.RoofConstructed);
            }
            // 密封舱落位堵孔（Building_Door 式常闭密封，复用对接口气闸语义）。
            Building pod = ThingMaker.MakeThing(DeltaVDefOf.DeltaV_BoardingPodLanded) as Building;
            if (pod != null)
            {
                GenSpawn.Spawn(pod, breach, map, Rot4.North);
            }
            // 乘员在破口旁落位（原版同图生成；后续战斗全走原版机制）。
            foreach (Thing thing in flight.cargo.InnerListForReading.ToList())
            {
                if (!(thing is Pawn pawn) || pawn.Destroyed) continue;
                flight.cargo.Remove(pawn);
                IntVec3 cell = CellFinder.RandomClosewalkCellNear(breach, map, 4, cell =>
                    cell.Standable(map) && cell != breach);
                if (!cell.IsValid) cell = breach;
                GenSpawn.Spawn(pawn, cell, map, Rot4.Random);
            }
            FleckMaker.Static(breach, map, FleckDefOf.ExplosionFlash, 8f);
            Messages.Message("DeltaV_PodImpact".Translate(flight.target.label), MessageTypeDefOf.PositiveEvent, false);
            PlanetGlobeRenderer.InvalidateSilhouette(map);
        }

        /// <summary>
        /// 破口格：从命中点向目标中心逐格采样，首个有可见结构外观的格子（船壳外沿）；
        /// 全程无结构（图比估算半径空）则退化为命中格。
        /// </summary>
        private static IntVec3 FindBreachCell(Map map, Vector2 hitPoint)
        {
            Vector2 center = SpaceTransform.ShipLocalOrigin(map);
            Vector2 dir = center - hitPoint;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.right;
            for (int i = 0; i < 200; i++)
            {
                Vector2 probe = hitPoint + dir * i;
                var cell = new IntVec3(Mathf.FloorToInt(probe.x), 0, Mathf.FloorToInt(probe.y));
                if (!cell.InBounds(map)) continue;
                if (MapOverlayRenderer.ContainsVisibleContent(map, cell))
                {
                    return cell;
                }
            }
            var fallback = new IntVec3(Mathf.FloorToInt(hitPoint.x), 0, Mathf.FloorToInt(hitPoint.y));
            return fallback.ClampInsideMap(map);
        }

        /// <summary>飞行失败（超时/目标失效）：舱毁人亡——目标在飞行中被摧毁属极端边界；
        /// 消息明示。乘员随容器销毁。</summary>
        private static void AbortFlight(Flight flight, string reason)
        {
            foreach (Thing thing in flight.cargo.InnerListForReading.ToList())
            {
                if (thing is Pawn pawn && !pawn.Destroyed)
                {
                    pawn.Destroy();
                }
            }
            flight.cargo.Clear();
            Log.Warning($"[DeltaV] Boarding pod flight aborted ({reason}); crew lost.");
            Messages.Message("DeltaV_PodLost".Translate(), MessageTypeDefOf.NegativeEvent, false);
        }

        /// <summary>
        /// 飞行标记渲染（ShellRenderer 前台层，与幽灵船同管线）：聚焦帧中的小四边形，
        /// 位置 = 轨道层飞行位置在聚焦帧的投影。返回消耗后的下一槽位号。
        /// </summary>
        internal static int TryEmitMarkers(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
        {
            if (flights.Count == 0) return baseSlot;
            EnsureMarkerMaterial();
            int slot = baseSlot;
            foreach (Flight flight in flights)
            {
                Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap, flight.position,
                    AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 10f);
                Quaternion rot = SpaceTransform.FocusedMeshRotation(frame, 0f);
                Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, new Vector3(2f, 1f, 2f));
                MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10, matrix, markerMaterial);
                ShellLayer.Slot s = layer.At(slot++);
                s.layer.Emit(s);
            }
            return slot;
        }

        private static void EnsureMarkerMaterial()
        {
            if (markerMaterial != null) return;
            markerTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var pixels = new Color32[4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            markerTexture.SetPixels32(pixels);
            markerTexture.Apply(false, true);
            markerMaterial = new Material(ShaderDatabase.Transparent)
            {
                name = "DeltaV_BoardingPodMarker",
                mainTexture = markerTexture,
                color = new Color(1f, 0.55f, 0.15f, 0.85f),
                renderQueue = 3000
            };
        }

        /// <summary>读档防御：飞行期禁存档，正常无状态；异常存档丢弃（防御性不杀乘员——
        /// 直接按落空处理会把人留在无图容器，销毁反而产生幽灵尸体，这里静默清空）。</summary>
        public static void ResetOnLoad()
        {
            foreach (Flight flight in flights)
            {
                flight.cargo.ClearAndDestroyContents(DestroyMode.Vanish);
            }
            flights.Clear();
        }

        public static void ClearRuntimeCaches()
        {
            if (markerMaterial != null)
            {
                Object.Destroy(markerMaterial);
                markerMaterial = null;
            }
            if (markerTexture != null)
            {
                Object.Destroy(markerTexture);
                markerTexture = null;
            }
        }
    }
}
