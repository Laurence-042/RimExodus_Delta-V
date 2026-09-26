using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 弹体服务（太空战架构 §4.1；取代 BoardingPods 槽位，段7 完成接管）：弹体
    /// 创建/注册（图池借出）、制导（导弹速度转向/直射恒速）、超时自毁、碰撞调度
    /// 对接（SpaceCollision 的主动方来源）、渲染（弹体 def 图形投影 + 激光线段）、
    /// 图池管理与读档孤儿图回收、ResetFlow。
    ///
    /// 实例服务（UniverseManager.Projectiles）：不序列化、读档归零（弹体是瞬态成员，
    /// 写档按 tag 剔除，UniverseManager.ExposeData）；积分与被动阻力豁免走主容器
    /// 循环按 tag 分派（§3.3），本服务只做制导写速度与生命周期。
    ///
    /// 图池纪律（§3.2）：借出 = 注册弹体容器（图归其持有，map 单一持有不变量照常）；
    /// 归还 = 注销容器 + 清内容入池（帧边界）。池内空闲图**无承载容器**——生命周期
    /// 扫描按对象迭代天然不触及，HandleDepartedSourceMap 只处理「有 owner 且空白」
    /// 的图，池图两道闸都不命中，无需豁免清单；读档后残留的弹体小图（写档剔除了
    /// 容器、图随档落盘）由 SweepOrphanProjectileMaps 按内容回收。
    ///
    /// 一切模拟 tick 驱动（不变量 #20：暂停冻结、倍速加速）；销毁/注销/图归池一律
    /// 经 QueueFrameAction 帧边界（禁止在扫掠 foreach 内执行）。
    /// </summary>
    [StaticConstructorOnStartup]
    public sealed class SpaceProjectileService
    {
        private const float Dt = 1f / 60f;

        /// <summary>激光线段视觉存留（tick；分档材质近似渐隐）。</summary>
        private const int BeamLifetimeTicks = 15;

        private readonly UniverseManager universe;

        private sealed class Projectile
        {
            public SpaceObject container;
            public ThingDef def;
            public CompProperties_SpaceProjectile props;
            public Building building;
            public Thing launcher;
            public SpaceObject sourceContainer;
            public SpaceObject target;
            public int spawnedAtTick;
            public bool dead;
            public Vector3 prevPosition;
            public bool laser;
            public Vector3 laserFrom;
            public Vector3 laserTo;
            /// <summary>伤害/穿深倍率（轨道炮 Σ 加速段；随弹体携带到命中结算）。</summary>
            public float damageScale = 1f;
            /// <summary>复用的扫掠查询（每 tick 重填字段，热路径零分配）。</summary>
            public readonly SpaceCollision.SweptBody body = new SpaceCollision.SweptBody();
        }

        private sealed class Beam
        {
            public Vector3 from;
            public Vector3 to;
            public int bornTick;
        }

        private readonly List<Projectile> projectiles = new List<Projectile>();
        private readonly List<Beam> beams = new List<Beam>();
        private readonly List<Projectile> sweepScratch = new List<Projectile>();
        private readonly List<SpaceCollision.SweptBody> bodyScratch = new List<SpaceCollision.SweptBody>();

        /// <summary>图池（footprint 哈希 → 空闲小图）。运行时缓存，不序列化。</summary>
        private readonly Dictionary<int, List<Map>> mapPool = new Dictionary<int, List<Map>>();

        // 激光线段材质（静态渲染缓存，ClearRuntimeCaches 换档销毁；三档透明度近似渐隐）。
        private static Material[] beamMaterials;

        public SpaceProjectileService(UniverseManager universe)
        {
            this.universe = universe;
        }

        /// <summary>在飞弹体数（诊断/图池状态用）。</summary>
        public int InFlightCount => projectiles.Count;

        // ---- 创建 ----

        /// <summary>
        /// 发射一枚弹体。muzzle = 炮口 universe 位置；directionWorld = 炮口处切空间
        /// 单位方向（3D）。初速 = 方向 × 弹速 ⊕ 发射瞬间发射容器速度（弹道继承炮口
        /// 运动；相对速度结算因此对头/追尾天然成立）。导弹传 missileTarget。
        /// damageScale = 轨道炮等的伤害/穿深倍率（随弹体携带到命中结算）。
        /// </summary>
        public bool Launch(ThingDef def, SpaceObject source, Vector3 muzzle, Vector3 directionWorld,
            Faction faction, Thing launcher = null, SpaceObject missileTarget = null, float damageScale = 1f)
        {
            var props = def?.GetCompProperties<CompProperties_SpaceProjectile>();
            if (props == null)
            {
                Log.Error($"[DeltaV] Launch: {def?.defName ?? "null"} has no CompProperties_SpaceProjectile.");
                return false;
            }
            OrbitSphere.Basis(muzzle, out Vector3 east, out Vector3 north);
            var dirTangent = new Vector2(Vector3.Dot(directionWorld, east), Vector3.Dot(directionWorld, north));
            if (dirTangent.sqrMagnitude < 1e-8f) dirTangent = Vector2.right;
            dirTangent.Normalize();
            Vector2 launcherVelocity = source != null
                ? SpaceTransform.TransportVelocity(source.pose, muzzle)
                : Vector2.zero;

            var projectile = new Projectile
            {
                def = def,
                props = props,
                launcher = launcher,
                sourceContainer = source,
                target = missileTarget,
                spawnedAtTick = Find.TickManager.TicksGame,
                laser = props.laserRange > 0f,
            };

            if (projectile.laser)
            {
                // 激光：即时链——无图、不积分；容器只为「同一注册表结算链」存在，
                // 速度取发射瞬间双方速度的攻方位（相对速度语义，§4.3）。
                projectile.laserFrom = muzzle;
                projectile.laserTo = OrbitSphere.Move(muzzle, dirTangent * props.laserRange);
                projectile.container = new SpaceObject(universe.AllocateOwnerId(), null,
                    new SpacePose(muzzle, Mathf.Atan2(dirTangent.y, dirTangent.x)))
                {
                    baseTag = SpaceObjectTag.Projectile,
                    faction = faction,
                };
                projectile.container.pose.velocity = launcherVelocity;
                projectile.prevPosition = muzzle;
            }
            else if (props.hasMap)
            {
                Map map = BorrowMap(def.Size);
                if (map == null) return false;
                projectile.container = new SpaceObject(universe.AllocateOwnerId(), map,
                    new SpacePose(muzzle, Mathf.Atan2(dirTangent.y, dirTangent.x)))
                {
                    baseTag = SpaceObjectTag.Projectile,
                    faction = faction,
                };
                projectile.container.pose.velocity = dirTangent * props.speed + launcherVelocity;
                projectile.building = ThingMaker.MakeThing(def) as Building;
                if (projectile.building != null)
                {
                    GenSpawn.Spawn(projectile.building,
                        new IntVec3((map.Size.x - def.Size.x) / 2, 0, (map.Size.z - def.Size.z) / 2),
                        map, Rot4.North);
                }
                projectile.prevPosition = muzzle;
            }
            else
            {
                projectile.container = new SpaceObject(universe.AllocateOwnerId(), null,
                    new SpacePose(muzzle, Mathf.Atan2(dirTangent.y, dirTangent.x)))
                {
                    baseTag = SpaceObjectTag.Projectile,
                    faction = faction,
                };
                projectile.container.pose.velocity = dirTangent * props.speed + launcherVelocity;
                projectile.prevPosition = muzzle;
            }
            universe.RegisterObject(projectile.container);
            projectile.damageScale = damageScale;
            projectiles.Add(projectile);
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"{def.defName} launched from '{source?.label ?? "nowhere"}' " +
                $"(speed {props.speed:F0}, {(props.hasMap ? "mapped" : "mapless")}" +
                $"{(projectile.laser ? ", laser" : "")})");
            return true;
        }

        // ---- tick（制导/超时；容器积分之前）----

        /// <summary>
        /// 弹体制导与超时（GameComponentTick 内、容器积分之前调用：本 tick 写速度
        /// 方向、积分随即位移，SpaceCollision 在积分后扫掠——先动后撞，§3.3）。
        /// prevPosition 在此记录（扫掠段起点 = 积分前位姿）。
        /// </summary>
        public void Tick()
        {
            if (projectiles.Count > 0)
            {
                int now = Find.TickManager.TicksGame;
                for (int i = projectiles.Count - 1; i >= 0; i--)
                {
                    Projectile p = projectiles[i];
                    if (p.dead) continue;
                    // 有图弹体被图内结算摧毁（PD/毁伤链经 TakeDamage）→ 销毁收尾。
                    if (p.building != null && p.building.Destroyed)
                    {
                        DestroyProjectile(p, "building destroyed in-map");
                        continue;
                    }
                    // 超时自毁（无后效；激光同 tick 结算不超时）。
                    if (!p.laser && now - p.spawnedAtTick > p.props.timeoutTicks)
                    {
                        DestroyProjectile(p, "timeout");
                        continue;
                    }
                    p.prevPosition = p.container.pose.position;
                    if (p.props.guided && !p.laser && TargetAlive(p.target))
                    {
                        SteerTowards(p);
                    }
                }
            }
            // 激光线段视觉寿命（纯表现）。
            for (int i = beams.Count - 1; i >= 0; i--)
            {
                if (Find.TickManager.TicksGame - beams[i].bornTick >= BeamLifetimeTicks) beams.RemoveAt(i);
            }
        }

        /// <summary>纯追踪制导：速度方向每 tick 向目标容器当前位姿转向（转向率受限）。</summary>
        private void SteerTowards(Projectile p)
        {
            Vector2 toTarget = OrbitSphere.Offset(p.container.pose.position, p.target.pose.position);
            if (toTarget.sqrMagnitude < 1e-8f) return;
            Vector2 desired = toTarget.normalized;
            Vector2 velocity = p.container.pose.velocity;
            Vector2 current = velocity.sqrMagnitude > 1e-8f ? velocity.normalized : desired;
            float signed = Mathf.Sign(current.x * desired.y - current.y * desired.x);
            float angle = Mathf.Acos(Mathf.Clamp(Vector2.Dot(current, desired), -1f, 1f));
            float maxTurn = p.props.turnRateDegPerSec * Mathf.Deg2Rad * Dt;
            float turn = Mathf.Min(angle, maxTurn);
            Vector2 newDir = SpaceTransform.Rotate(current, signed * turn);
            p.container.pose.velocity = newDir * p.props.speed;
            p.container.pose.angle = Mathf.Atan2(newDir.y, newDir.x);
        }

        /// <summary>
        /// 导弹目标存活判定：目标仍在 objects 权威列表（销毁即注销；漂移目标照常追踪）。
        /// </summary>
        private bool TargetAlive(SpaceObject target)
        {
            return target != null && universe.objects.Contains(target);
        }

        // ---- 碰撞调度（容器积分之后）----

        /// <summary>
        /// 组装在飞弹体的扫掠查询并结算（GameComponentTick 容器积分之后调用）。
        /// 激光在此完成全射程即时扫掠并随即销毁。
        /// </summary>
        public void ResolveCollisions()
        {
            if (projectiles.Count == 0) return;
            sweepScratch.Clear();
            bodyScratch.Clear();
            foreach (Projectile p in projectiles)
            {
                if (p.dead) continue;
                SpaceCollision.SweptBody body = p.body;
                body.attacker = p.container;
                body.projectileDef = p.def;
                body.attackerBuilding = p.building;
                body.excludeContainer = p.sourceContainer;
                body.launcher = p.launcher;
                body.dead = false;
                body.resolvedHit = false;
                body.sweepRadius = FootprintRadius(p.def);
                body.damageScale = p.damageScale;
                if (p.laser)
                {
                    body.from = p.laserFrom;
                    body.to = p.laserTo;
                }
                else
                {
                    body.from = p.prevPosition;
                    body.to = p.container.pose.position;
                }
                Projectile captured = p;
                body.onSelfDestruct = delegate { DestroyProjectile(captured, "impact"); };
                sweepScratch.Add(p);
                bodyScratch.Add(body);
            }
            // 侵入 shuttle（tag 覆盖为 projectile 的在飞 shuttle，§7.5）：同一碰撞核心
            // 扫掠（脚印 = 舱体脚印；PlaceBody/挡停语义见 SweptBody 回调）。
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj.baseTag != SpaceObjectTag.Shuttle || !obj.projectileRoleOverride || obj.map == null)
                {
                    continue;
                }
                Building hull = null;
                foreach (Thing thing in obj.map.listerThings.AllThings)
                {
                    if (thing is Building b && b.TryGetComp<CompSpaceShuttle>() != null)
                    {
                        hull = b;
                        break;
                    }
                }
                if (hull == null) continue;
                var body = new SpaceCollision.SweptBody
                {
                    attacker = obj,
                    projectileDef = hull.def,
                    attackerBuilding = hull,
                    excludeContainer = null,
                    to = obj.pose.position,
                    from = OrbitSphere.Move(obj.pose.position, -obj.pose.velocity * (1f / 60f)),
                    sweepRadius = FootprintRadius(hull.def),
                    launcher = hull,
                    haltOnShield = true,
                };
                SpaceObject container = obj;
                body.onPlace = delegate (SpaceObject defender, Vector3 hitUniverse, Vector2 hitLocal)
                {
                    universe.Shuttles.PlaceBoardedShuttle(container, defender, hitUniverse, hitLocal);
                };
                bodyScratch.Add(body);
            }
            SpaceCollision.SweepAndResolve(universe, bodyScratch);
            // 激光当 tick 消费完毕：视觉线段（命中点或全射程）+ 销毁。
            foreach (Projectile p in sweepScratch)
            {
                if (!p.laser || p.dead) continue;
                Vector3 end = p.body.resolvedHit ? p.body.resolvedHitUniverse : p.laserTo;
                beams.Add(new Beam { from = p.laserFrom, to = end, bornTick = Find.TickManager.TicksGame });
                DestroyProjectile(p, "laser spent");
            }
        }

        /// <summary>弹体脚印等效半径（1×1 ≈ 0.71、2×1 ≈ 1.12）。</summary>
        internal static float FootprintRadius(ThingDef def)
        {
            return Mathf.Sqrt(def.Size.x * def.Size.x + def.Size.z * def.Size.z) * 0.5f;
        }

        // ---- 销毁与图池 ----

        /// <summary>
        /// 销毁一枚弹体（帧边界）：注销容器、有图清内容归池。dead 立即置位（本 tick
        /// 后续事件跳过），实际收尾经 QueueFrameAction。
        /// </summary>
        private void DestroyProjectile(Projectile p, string reason)
        {
            if (p.dead) return;
            p.dead = true;
            DeltaVLog.Message(DeltaVLogModule.Combat, $"{p.def.defName} #{p.container.objectId} destroyed ({reason})");
            universe.QueueFrameAction(delegate
            {
                projectiles.Remove(p);
                SpaceObject container = p.container;
                if (container == null) return;
                if (p.building != null && !p.building.Destroyed)
                {
                    p.building.Destroy(DestroyMode.Vanish);
                }
                p.building = null;
                if (container.map != null)
                {
                    ReturnMapToPool(container.map);
                }
                if (universe.objects.Contains(container))
                {
                    universe.UnregisterObject(container);
                }
            });
        }

        private Map BorrowMap(IntVec2 footprint)
        {
            int key = PoolKey(footprint.x, footprint.z);
            if (mapPool.TryGetValue(key, out List<Map> idle) && idle.Count > 0)
            {
                Map map = idle[idle.Count - 1];
                idle.RemoveAt(idle.Count - 1);
                return map;
            }
            return ShipPocketMapFactory.Generate(new IntVec3(footprint.x, 1, footprint.z), universe);
        }

        private void ReturnMapToPool(Map map)
        {
            if (map == null || map.Disposed) return;
            // 保险：图上不应再有内容（弹体 building 已毁）；有残留则不入池直接销毁。
            if (map.listerThings.AllThings.Count > 0)
            {
                universe.Lifecycle.DeinitMap(map, false);
                return;
            }
            int key = PoolKey(map.Size.x, map.Size.z);
            if (!mapPool.TryGetValue(key, out List<Map> idle))
            {
                mapPool[key] = idle = new List<Map>();
            }
            idle.Add(map);
        }

        private static int PoolKey(int x, int z) => x * 4096 + z;

        /// <summary>dev：图池状态摘要。</summary>
        public string PoolStatus()
        {
            int pooled = 0;
            foreach (KeyValuePair<int, List<Map>> kv in mapPool) pooled += kv.Value.Count;
            return $"in-flight={projectiles.Count} beams={beams.Count} pooledIdleMaps={pooled}";
        }

        /// <summary>dev：强制击毁全部在飞弹体（帧边界收尾）。</summary>
        public void DestroyAll()
        {
            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                DestroyProjectile(projectiles[i], "dev force-destroy");
            }
        }

        /// <summary>
        /// 读档收尾：写档剔除的是弹体容器，弹体小图随档落盘残留为无容器图——按内容
        /// 回收（清掉弹体 building 后回池）。只动「无承载容器、无人、内容只有弹体
        /// building」的 ShipPocketParent 口袋图，不碰任何真实结构图。
        /// </summary>
        public void SweepOrphanProjectileMaps()
        {
            foreach (Map map in Find.Maps.ToList())
            {
                if (!(map.Parent is ShipPocketParent_DeltaV)) continue;
                if (map == Find.CurrentMap) continue;
                if (universe.SpaceObjectOnMap(map) != null) continue;
                if (map.mapPawns.AllPawnsSpawned.Count > 0) continue;
                bool any = false;
                bool onlyProjectiles = true;
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (thing is Building building
                        && building.def.GetCompProperties<CompProperties_SpaceProjectile>() != null)
                    {
                        any = true;
                        continue;
                    }
                    onlyProjectiles = false;
                    break;
                }
                if (!any || !onlyProjectiles) continue;
                foreach (Thing thing in map.listerThings.AllThings.ToList())
                {
                    if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                }
                ReturnMapToPool(map);
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"orphan projectile map {UniverseManager.MapTag(map)} reclaimed into pool after load");
            }
        }

        /// <summary>
        /// 流程清零（dev ResetObjects）：销毁全部弹体、清空图池（全部帧边界执行）。
        /// </summary>
        public void ResetFlow()
        {
            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                DestroyProjectile(projectiles[i], "reset");
            }
            beams.Clear();
            List<List<Map>> pooledLists = new List<List<Map>>(mapPool.Values);
            universe.QueueFrameAction(delegate
            {
                foreach (List<Map> maps in pooledLists)
                {
                    foreach (Map map in maps)
                    {
                        if (!map.Disposed) universe.Lifecycle.DeinitMap(map, false);
                    }
                }
                mapPool.Clear();
            });
        }

        // ---- 渲染（ShellRenderer 前台层；有图/无图弹体同一路径，图形取自 def）----

        /// <summary>
        /// 弹体与激光线段渲染（与幽灵船/跳帮舱标记同管线，聚焦帧投影）。返回消耗后
        /// 的下一槽位号。弹体朝向按速度方向（UsesVelocityHeading tag 分派）。
        /// </summary>
        internal int TryEmitProjectiles(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
        {
            int slot = baseSlot;
            if (projectiles.Count > 0)
            {
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 10f;
                foreach (Projectile p in projectiles)
                {
                    if (p.dead) continue;
                    Graphic graphic = p.def.graphicData?.Graphic;
                    Material material = graphic?.MatAt(Rot4.North);
                    if (material == null) continue;
                    Vector2 velocity = p.container.pose.velocity;
                    float heading = velocity.sqrMagnitude > 1e-8f
                        ? Mathf.Atan2(velocity.y, velocity.x)
                        : p.container.pose.angle;
                    Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap,
                        p.container.pose.position, altitude);
                    Quaternion rot = SpaceTransform.FocusedMeshRotation(frame, heading);
                    Vector2 drawSize = p.def.graphicData.drawSize;
                    Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, new Vector3(drawSize.x, 1f, drawSize.y));
                    MapOverlayRenderer.CollectSingleQuad(graphic.MeshAt(Rot4.North), matrix, material);
                    ShellLayer.Slot s = layer.At(slot++);
                    s.layer.Emit(s);
                }
            }
            if (beams.Count > 0)
            {
                EnsureBeamMaterials();
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 12f;
                foreach (Beam beam in beams)
                {
                    Vector3 a = SpaceTransform.FocusedDrawPosition(frame, frameMap, beam.from, altitude);
                    Vector3 b = SpaceTransform.FocusedDrawPosition(frame, frameMap, beam.to, altitude);
                    Vector3 delta = b - a;
                    float length = new Vector2(delta.x, delta.z).magnitude;
                    if (length < 0.1f) continue;
                    Vector3 mid = (a + b) * 0.5f;
                    float angle = Mathf.Atan2(delta.z, delta.x);
                    Quaternion rot = SpaceTransform.FocusedMeshRotation(frame, angle);
                    int age = Find.TickManager.TicksGame - beam.bornTick;
                    Material material = beamMaterials[Mathf.Clamp(age * beamMaterials.Length
                        / BeamLifetimeTicks, 0, beamMaterials.Length - 1)];
                    Matrix4x4 matrix = Matrix4x4.TRS(mid, rot, new Vector3(length, 1f, 0.3f));
                    MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10, matrix, material);
                    ShellLayer.Slot s = layer.At(slot++);
                    s.layer.Emit(s);
                }
            }
            return slot;
        }

        private static void EnsureBeamMaterials()
        {
            if (beamMaterials != null) return;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var pixels = new Color32[4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            // 三档透明度近似渐隐（共享材质不逐束调 alpha；表现占位，视觉细节可后调）。
            beamMaterials = new Material[3];
            for (int i = 0; i < beamMaterials.Length; i++)
            {
                float alpha = 0.9f * (1f - (float)i / beamMaterials.Length);
                beamMaterials[i] = new Material(ShaderDatabase.Transparent)
                {
                    name = $"DeltaV_LaserBeam_{i}",
                    mainTexture = texture,
                    color = new Color(1f, 0.35f, 0.3f, alpha),
                    renderQueue = 3200,
                };
            }
        }

        /// <summary>换档清理静态渲染缓存（UniverseManager.ResetRuntimeServices）。</summary>
        public static void ClearRuntimeCaches()
        {
            if (beamMaterials == null) return;
            foreach (Material material in beamMaterials)
            {
                if (material != null)
                {
                    if (material.mainTexture != null) Object.Destroy(material.mainTexture);
                    Object.Destroy(material);
                }
            }
            beamMaterials = null;
        }
    }
}
