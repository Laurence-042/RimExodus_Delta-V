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
    /// 防止力源停止后的幽灵火焰）；火焰材质、尺寸、偏移与排气 Fleck 参数复用
    /// Odyssey Gravship 推进器 Def。两条读取路径：聚焦帧船的真实地图位姿（Draw），
    /// 非聚焦船壳的显示矩阵换算（DrawShellFlames，经 ShellRenderer 调用）。
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
        private static readonly Dictionary<Thing, float> ExhaustEmissionRemainder = new Dictionary<Thing, float>();

        public static void Draw(UniverseManager universe)
        {
            // 聚焦帧飞船（驾驶船是其子集；未来聚焦敌舰、其 AI 出力时同样在
            // 地图内显示真实火焰，与船壳路径对称）。
            ShipState ship = universe.FrameOf(Find.CurrentMap) as ShipState;
            Map current = Find.CurrentMap;
            if (ship?.map == null || current != ship.map || !WorldRendererUtility.DrawingMap)
            {
                ExhaustEmissionRemainder.Clear();
                return;
            }

            ShipPhysics.MassReport report = ShipPhysics.GetReport(ship);
            if (report == null || Time.time - report.throttlesWrittenAt > ThrottleFreshSeconds)
            {
                return;
            }

            for (int i = 0; i < report.thrusters.Count; i++)
            {
                ShipPhysics.Thruster thruster = report.thrusters[i];
                if (thruster.throttle <= MinVisibleThrottle || thruster.thing == null || !thruster.thing.Spawned)
                {
                    if (thruster.thing != null)
                    {
                        ExhaustEmissionRemainder.Remove(thruster.thing);
                    }
                    continue;
                }
                DrawFlame(thruster);
                EmitExhaust(thruster, current);
            }
        }

        /// <summary>按推进器当前地图位置/朝向画火焰（驾驶模式常规路径）。</summary>
        private static void DrawFlame(ShipPhysics.Thruster thruster)
        {
            Thing thing = thruster.thing;
            DrawFlame(thing, thruster.throttle, thing.Rotation.AsQuat,
                GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f));
        }

        /// <summary>
        /// 指定世界位置/朝向画火焰（降落动画等表现层复用同一套材质与参数；
        /// 位置/朝向由调用方经动画矩阵换算，不读 Thing 的真实地图位姿）。
        /// </summary>
        public static void DrawFlame(Thing thing, float throttle, Quaternion rotation, Vector3 thrusterCenter)
        {
            CompGravshipThruster comp = thing.TryGetComp<CompGravshipThruster>();
            CompProperties_GravshipThruster props = comp?.Props;
            if (props == null || props.flameSize <= 0f)
            {
                return;
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
            Material material = MaterialPool.MatFrom(request);
            float flicker = Mathf.Lerp(0.75f, 1f, Mathf.PerlinNoise(thing.thingIDNumber, Time.time * 10f));
            Color color = Color.white * Mathf.Lerp(0.2f, 1f, visualThrottle) * flicker;
            color.a = Mathf.Lerp(0.12f, 1f, visualThrottle);

            FlameBlock.Clear();
            for (int i = 0; i < props.flameShaderParameters.Count; i++)
            {
                props.flameShaderParameters[i].Apply(FlameBlock);
            }
            // Def 参数先应用，油门颜色后写入，避免未来 Def 增加 _Color2 时覆盖动态出力。
            FlameBlock.SetColor(ShaderPropertyColor2, color);
            GenDraw.DrawQuad(material, position, rotation, flameSize, FlameBlock);
        }

        /// <summary>
        /// 非聚焦船壳路径：按外壳显示矩阵画真实油门火焰。矩阵/旋转来自
        /// ShellRenderer.DrawObjectShell（船局部 → 聚焦帧）；推进器本体仍在源 Map 上，
        /// 只读它的 Def 参数与朝向，不读其真实地图位姿。高度沿用 DrawFlame 内的
        /// Skyfaller 绝对海拔，与聚焦地图火焰同约定（船壳画在聚焦帧世界坐标里，
        /// 遮挡关系天然一致）。排气 Fleck 属船自身 Map 的 flecks 体系，画不进
        /// 聚焦帧，不在船壳层发射。
        /// </summary>
        public static void DrawShellFlames(ShipState ship, Matrix4x4 matrix, Quaternion rotation)
        {
            ShipPhysics.MassReport report = ShipPhysics.GetReport(ship);
            if (report == null || Time.time - report.throttlesWrittenAt > ThrottleFreshSeconds)
            {
                return;
            }
            for (int i = 0; i < report.thrusters.Count; i++)
            {
                ShipPhysics.Thruster thruster = report.thrusters[i];
                if (thruster.throttle <= MinVisibleThrottle || thruster.thing == null || !thruster.thing.Spawned)
                {
                    continue;
                }
                Thing thing = thruster.thing;
                Vector3 center = GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f);
                DrawFlame(thing, thruster.throttle, rotation * thing.Rotation.AsQuat,
                    matrix.MultiplyPoint3x4(center));
            }
        }

        private static void EmitExhaust(ShipPhysics.Thruster thruster, Map map)
        {
            if (Find.TickManager.Paused)
            {
                return;
            }
            Thing thing = thruster.thing;
            CompProperties_GravshipThruster props = thing.TryGetComp<CompGravshipThruster>()?.Props;
            CompProperties_GravshipThruster.ExhaustSettings settings = props?.exhaustSettings;
            if (settings == null || !settings.enabled || settings.ExhaustFleckDef == null)
            {
                return;
            }

            float throttle = Mathf.Clamp01(thruster.throttle);
            float visualThrottle = Mathf.Sqrt(throttle);
            float queued = ExhaustEmissionRemainder.TryGetValue(thing, out float remainder) ? remainder : 0f;
            queued += Time.deltaTime * settings.emissionsPerSecond * throttle;
            int count = Mathf.FloorToInt(queued);
            ExhaustEmissionRemainder[thing] = queued - count;
            if (count <= 0)
            {
                return;
            }

            float fullSize = thing.def.size.x * props.flameSize;
            float flameSize = fullSize * Mathf.Lerp(0.08f, 1f, visualThrottle);
            Vector3 flameOffset = thing.Rotation.AsQuat * props.flameOffsetsPerDirection[thing.Rotation.AsInt];
            Vector3 position = GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f)
                - thing.Rotation.AsIntVec3.ToVector3() * (thing.def.size.z * 0.5f + flameSize * 0.5f)
                + flameOffset;
            Quaternion rotation = thing.Rotation.AsQuat;
            for (int i = 0; i < count; i++)
            {
                Vector3 radial = Random.insideUnitSphere.WithY(0f);
                if (radial.sqrMagnitude > 1e-6f)
                {
                    radial.Normalize();
                }
                map.flecks.CreateFleck(new FleckCreationData
                {
                    def = settings.ExhaustFleckDef,
                    spawnPosition = position + rotation * settings.spawnOffset
                        + radial * settings.spawnRadiusRange.RandomInRange,
                    scale = settings.scaleRange.RandomInRange * Mathf.Lerp(0.35f, 1f, visualThrottle),
                    velocity = rotation * Quaternion.Euler(0f, settings.velocityRotationRange.RandomInRange, 0f)
                        * (settings.velocity * settings.velocityMultiplierRange.RandomInRange
                            * Mathf.Lerp(0.35f, 1f, visualThrottle)),
                    rotationRate = settings.rotationOverTimeRange.RandomInRange,
                    ageTicksOverride = -1
                });
            }
        }

        public static void ClearRuntimeCaches()
        {
            ExhaustEmissionRemainder.Clear();
        }
    }
}
