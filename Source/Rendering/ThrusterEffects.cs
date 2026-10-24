using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 阶段2 推进器表现层。点火状态只读取 ShipPhysics 分配器写出的**真实**逐推进器
    /// 油门（唯一写入者 Allocate，驾驶/自动驾驶/未来舰船 AI 共用；配 0.25s 新鲜期，
    /// 防止力源停止后的幽灵火焰）；火焰材质、尺寸、偏移与排气喷口参数复用
    /// Odyssey Gravship 推进器 Def。两条读取路径：聚焦帧船的真实地图位姿（Draw），
    /// 非聚焦船壳的显示矩阵换算（CollectShellFlames，经 ShellRenderer 调用）。
    /// 表现状态可随时丢弃重建，不反向参与物理或存档。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ThrusterEffects
    {
        private const int FlameRenderQueue = 3201;
        private const float MinVisibleThrottle = 0.001f;

        /// <summary>油门新鲜期（秒）：超过则视为陈旧不画（时间戳由 Allocate 盖章；
        /// 暂停时 Time.time 冻结、火焰保持，与旧地图路径行为一致）。</summary>
        private const float ThrottleFreshSeconds = 0.25f;

        private static readonly int ShaderPropertyColor2 = Shader.PropertyToID("_Color2");
        private static readonly MaterialPropertyBlock FlameBlock = new MaterialPropertyBlock();

        public static void Draw(UniverseManager universe)
        {
            // 聚焦容器（驾驶船的刚体；未来聚焦敌舰、其 AI 出力时同样在地图内显示
            // 真实火焰，与船壳路径对称）。
            Map current = Find.CurrentMap;
            SpaceObject frame = universe.FrameOf(current);
            if (frame == null || frame.baseTag == SpaceObjectTag.Projectile || !WorldRendererUtility.DrawingMap)
            {
                return;
            }

            DriveInfo report = Current.Game?.GetComponent<UniverseManager>()?.FrameOf(current)?.MotionInfo;
            if (report == null || Time.time - report.throttlesWrittenAt > ThrottleFreshSeconds)
            {
                return;
            }

            for (int i = 0; i < report.thrusters.Count; i++)
            {
                ThrusterInfo thruster = report.thrusters[i];
                if (thruster.throttle <= MinVisibleThrottle)
                {
                    continue;
                }
                if (thruster.thing == null)
                {
                    DrawVirtualFlame(thruster, current);
                    continue;
                }
                if (!thruster.thing.Spawned) continue;
                DrawFlame(thruster);
            }
        }

        /// <summary>按推进器当前地图位置/朝向画火焰（驾驶模式常规路径）。</summary>
        private static void DrawFlame(ThrusterInfo thruster)
        {
            Thing thing = thruster.thing;
            DrawFlame(thing, thruster.throttle, thing.Rotation.AsQuat,
                GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f));
        }

        private static CompProperties_GravshipThruster VirtualFlameProps =>
            DeltaVDefOf.LargeThruster?.GetCompProperties<CompProperties_GravshipThruster>();

        /// <summary>虚拟推进器沿 XML 力矢量反向喷焰；位置与物理作用点完全共用。</summary>
        private static void DrawVirtualFlame(ThrusterInfo thruster, Map map)
        {
            Vector2 origin = SpaceTransform.ShipLocalOrigin(map);
            Vector3 center = new Vector3(origin.x + thruster.localPos.x, 0f,
                origin.y + thruster.localPos.y);
            Quaternion rotation = Quaternion.LookRotation(
                new Vector3(thruster.dir.x, 0f, thruster.dir.y), Vector3.up);
            if (!TryBuildVirtualFlame(thruster, rotation, center, out FlameDraw flame)) return;
            ApplyFlameBlock(FlameBlock, flame);
            GenDraw.DrawQuad(flame.material, flame.position, flame.rotation, flame.flameSize, FlameBlock);
        }

        /// <summary>一次火焰的全部绘制数据（聚焦图立即绘制与船壳缓冲命令共用计算）。</summary>
        private struct FlameDraw
        {
            public Vector3 position;
            public Quaternion rotation;
            public float flameSize;
            public Material material;
            public CompProperties_GravshipThruster props;
            public Color color;
        }

        private static bool TryBuildFlame(Thing thing, float throttle, Quaternion rotation, Vector3 thrusterCenter,
            out FlameDraw flame)
        {
            flame = default(FlameDraw);
            CompGravshipThruster comp = thing.TryGetComp<CompGravshipThruster>();
            CompProperties_GravshipThruster props = comp?.Props;
            if (props == null || props.flameSize <= 0f)
            {
                return false;
            }

            throttle = Mathf.Clamp01(throttle);
            float fullSize = thing.def.size.x * props.flameSize;
            float visualThrottle = Mathf.Sqrt(throttle);
            float flameSize = fullSize * Mathf.Lerp(0.08f, 1f, visualThrottle);
            Vector3 facing = rotation * Vector3.forward;
            Vector3 offset = rotation * props.flameOffsetsPerDirection[thing.Rotation.AsInt];
            Vector3 position = thrusterCenter - facing * (thing.def.size.z * 0.5f + flameSize * 0.5f) + offset;
            position = position.SetToAltitude(AltitudeLayer.Skyfaller).WithYOffset(0.07317074f);
            MaterialRequest request = new MaterialRequest(props.FlameShaderType.Shader)
            {
                renderQueue = FlameRenderQueue
            };
            float flicker = Mathf.Lerp(0.75f, 1f, Mathf.PerlinNoise(thing.thingIDNumber, Time.time * 10f));
            Color color = Color.white * Mathf.Lerp(0.2f, 1f, visualThrottle) * flicker;
            color.a = Mathf.Lerp(0.12f, 1f, visualThrottle);
            flame = new FlameDraw
            {
                position = position,
                rotation = rotation,
                flameSize = flameSize,
                material = MaterialPool.MatFrom(request),
                props = props,
                color = color
            };
            return true;
        }

        private static bool TryBuildVirtualFlame(ThrusterInfo thruster, Quaternion rotation,
            Vector3 thrusterCenter, out FlameDraw flame)
        {
            flame = default(FlameDraw);
            CompProperties_GravshipThruster props = VirtualFlameProps;
            if (props == null || thruster.thrust <= 0f) return false;
            float throttle = Mathf.Clamp01(thruster.throttle);
            float visualThrottle = Mathf.Sqrt(throttle);
            // 以推力矢量长度同时区分主喷口与姿控喷口；不另设脱离物理事实的视觉布局。
            float fullSize = Mathf.Lerp(0.35f, 1.35f, Mathf.Clamp01(thruster.thrust / 280f));
            float flameSize = fullSize * Mathf.Lerp(0.08f, 1f, visualThrottle);
            Vector3 facing = rotation * Vector3.forward;
            Vector3 position = thrusterCenter - facing * flameSize * 0.5f;
            position = position.SetToAltitude(AltitudeLayer.Skyfaller).WithYOffset(0.07317074f);
            MaterialRequest request = new MaterialRequest(props.FlameShaderType.Shader)
            {
                renderQueue = FlameRenderQueue
            };
            float seed = thruster.localPos.x * 17.13f + thruster.localPos.y * 31.71f;
            float flicker = Mathf.Lerp(0.75f, 1f, Mathf.PerlinNoise(seed, Time.time * 10f));
            Color color = Color.white * Mathf.Lerp(0.2f, 1f, visualThrottle) * flicker;
            color.a = Mathf.Lerp(0.12f, 1f, visualThrottle);
            flame = new FlameDraw
            {
                position = position,
                rotation = rotation,
                flameSize = flameSize,
                material = MaterialPool.MatFrom(request),
                props = props,
                color = color
            };
            return true;
        }

        private static void ApplyFlameBlock(MaterialPropertyBlock block, FlameDraw flame)
        {
            block.Clear();
            for (int i = 0; i < flame.props.flameShaderParameters.Count; i++)
            {
                flame.props.flameShaderParameters[i].Apply(block);
            }
            // Def 参数先应用，油门颜色后写入，避免未来 Def 增加 _Color2 时覆盖动态出力。
            block.SetColor(ShaderPropertyColor2, flame.color);
        }

        /// <summary>
        /// 立即绘制入口（聚焦图内真实火焰）：指定世界位置/朝向画一次火焰；
        /// 位置/朝向由调用方换算，不读 Thing 的真实地图位姿。
        /// </summary>
        private static void DrawFlame(Thing thing, float throttle, Quaternion rotation, Vector3 thrusterCenter)
        {
            if (!TryBuildFlame(thing, throttle, rotation, thrusterCenter, out FlameDraw flame))
            {
                return;
            }
            ApplyFlameBlock(FlameBlock, flame);
            GenDraw.DrawQuad(flame.material, flame.position, flame.rotation, flame.flameSize, FlameBlock);
        }

        // 池化火焰命令块：CommandBuffer 对 MaterialPropertyBlock 内容的读取时序不保证
        // 记录时快照，每条火焰命令必须持有**独立**块（共享块会被同帧后收集的火焰覆盖）。
        // 游标按帧重置——一帧内多壳连续收集不互踩；收集(Update)与执行(其后渲染)同帧
        // 完成、池块只在下一帧收集时重写，跨帧安全。
        private static readonly List<MaterialPropertyBlock> FlameBlockPool = new List<MaterialPropertyBlock>();
        private static int flameBlockCursor;
        private static int flameBlockFrame = -1;

        /// <summary>
        /// 非聚焦船壳路径：按外壳显示矩阵把真实油门火焰收集为缓冲命令（与壳内容同表、
        /// 经 renderQueue 3201 排在本壳地形之上；随本壳进背景或前台缓冲）。矩阵/旋转
        /// 来自 ShellRenderer.DrawObjectShell（船局部 → 聚焦帧）；推进器本体仍在源
        /// Map 上，只读它的 Def 参数与朝向，不读其真实地图位姿。mesh/matrix 照抄
        /// GenDraw.DrawQuad（反编译核实：plane10 + TRS(pos, rot, (size,1,size))）。
        /// 脱离排气由宇宙表现管线统一绘制，不在船壳层重复发射。
        /// </summary>
        public static void CollectShellFlames(SpaceObject container, Map map, Matrix4x4 matrix, Quaternion rotation)
        {
            // 普通弹体另有尾焰；侵入 shuttle 与正常驾驶均读取真实分配油门。
            if (container.baseTag == SpaceObjectTag.Projectile) return;
            DriveInfo report = Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map)?.MotionInfo;
            if (report == null || Time.time - report.throttlesWrittenAt > ThrottleFreshSeconds)
            {
                return;
            }
            if (flameBlockFrame != Time.frameCount)
            {
                flameBlockFrame = Time.frameCount;
                flameBlockCursor = 0;
            }
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                ThrusterInfo thruster = report.thrusters[i];
                if (thruster.throttle <= MinVisibleThrottle)
                {
                    continue;
                }
                if (thruster.thing == null)
                {
                    Vector2 origin = SpaceTransform.ShipLocalOrigin(map);
                    Vector3 virtualCenter = new Vector3(origin.x + thruster.localPos.x, 0f,
                        origin.y + thruster.localPos.y);
                    Quaternion localRotation = Quaternion.LookRotation(
                        new Vector3(thruster.dir.x, 0f, thruster.dir.y), Vector3.up);
                    if (TryBuildVirtualFlame(thruster, rotation * localRotation,
                        matrix.MultiplyPoint3x4(virtualCenter), out FlameDraw virtualFlame))
                    {
                        AddFlameCommand(virtualFlame);
                    }
                    continue;
                }
                if (!thruster.thing.Spawned) continue;
                Thing thing = thruster.thing;
                Vector3 center = GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f);
                if (!TryBuildFlame(thing, thruster.throttle, rotation * thing.Rotation.AsQuat,
                    matrix.MultiplyPoint3x4(center), out FlameDraw flame))
                {
                    continue;
                }
                AddFlameCommand(flame);
            }
        }

        private static void AddFlameCommand(FlameDraw flame)
        {
            while (FlameBlockPool.Count <= flameBlockCursor)
            {
                FlameBlockPool.Add(new MaterialPropertyBlock());
            }
            MaterialPropertyBlock block = FlameBlockPool[flameBlockCursor++];
            ApplyFlameBlock(block, flame);
            MapOverlayRenderer.AddFlameCommand(MeshPool.plane10,
                Matrix4x4.TRS(flame.position, flame.rotation,
                    new Vector3(flame.flameSize, 1f, flame.flameSize)), flame.material, block);
        }

        // 喷口由同一火焰几何推导，宇宙排气不再进入随船 Map 的 Fleck 系统。
        internal static bool TryGetExhaustPort(ThrusterInfo thruster, Map map,
            out Vector2 point, out float size)
        {
            point = default;
            size = 0f;
            FlameDraw flame;
            Vector3 facing;
            if (thruster.thing != null)
            {
                Thing thing = thruster.thing;
                if (!thing.Spawned) return false;
                Quaternion rotation = thing.Rotation.AsQuat;
                if (!TryBuildFlame(thing, thruster.throttle, rotation,
                    GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f), out flame)) return false;
                facing = rotation * Vector3.forward;
            }
            else
            {
                Vector2 origin = SpaceTransform.ShipLocalToMap(map, thruster.localPos);
                facing = new Vector3(thruster.dir.x, 0f, thruster.dir.y);
                if (!TryBuildVirtualFlame(thruster, Quaternion.LookRotation(facing, Vector3.up),
                    new Vector3(origin.x, 0f, origin.y), out flame)) return false;
            }
            Vector3 nozzle = flame.position + facing * flame.flameSize * 0.5f;
            point = new Vector2(nozzle.x, nozzle.z);
            size = flame.flameSize;
            return true;
        }

        public static void ClearRuntimeCaches()
        {
            FlameBlockPool.Clear();
            flameBlockCursor = 0;
            flameBlockFrame = -1;
        }
    }
}
