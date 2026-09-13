using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 阶段2 推进器表现层。点火状态只读取 ShipPhysics 分配出的逐推进器油门；
    /// 火焰材质、尺寸、偏移与排气 Fleck 参数复用 Odyssey Gravship 推进器 Def。
    /// 表现状态可随时丢弃重建，不反向参与物理或存档。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ThrusterEffects
    {
        private const int FlameRenderQueue = 3201;
        private const float MinVisibleThrottle = 0.001f;

        private static readonly int ShaderPropertyColor2 = Shader.PropertyToID("_Color2");
        private static readonly MaterialPropertyBlock FlameBlock = new MaterialPropertyBlock();
        private static readonly Dictionary<Thing, float> ExhaustEmissionRemainder = new Dictionary<Thing, float>();

        public static void Draw(UniverseManager universe)
        {
            ShipState ship = PilotController.PilotedShip(universe);
            Map current = Find.CurrentMap;
            if (ship?.map == null || current != ship.map || !WorldRendererUtility.DrawingMap)
            {
                ExhaustEmissionRemainder.Clear();
                return;
            }

            ShipPhysics.MassReport report = ShipPhysics.GetReport(ship);
            if (report == null)
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

        private static void DrawFlame(ShipPhysics.Thruster thruster)
        {
            Thing thing = thruster.thing;
            CompGravshipThruster comp = thing.TryGetComp<CompGravshipThruster>();
            CompProperties_GravshipThruster props = comp?.Props;
            if (props == null || props.flameSize <= 0f)
            {
                return;
            }

            float throttle = Mathf.Clamp01(thruster.throttle);
            float fullSize = thing.def.size.x * props.flameSize;
            float visualThrottle = Mathf.Sqrt(throttle);
            float flameSize = fullSize * Mathf.Lerp(0.08f, 1f, visualThrottle);
            Vector3 offset = thing.Rotation.AsQuat * props.flameOffsetsPerDirection[thing.Rotation.AsInt];
            Vector3 position = GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f)
                - thing.Rotation.AsIntVec3.ToVector3() * (thing.def.size.z * 0.5f + flameSize * 0.5f)
                + offset;
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
            GenDraw.DrawQuad(material, position, thing.Rotation.AsQuat, flameSize, FlameBlock);
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
