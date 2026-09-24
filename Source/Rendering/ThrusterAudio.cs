using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DeltaV
{
    /// <summary>
    /// 推进器工作音表现层：与 ThrusterEffects（火焰）同源同判据——只读分配器真实油门
    /// （ShipPhysics.GetReport + throttlesWrittenAt 0.25s 新鲜期），火焰在、声音在。
    /// 两条路径与火焰对称：聚焦图逐推进器空间化循环（SoundInfo.InMap(建筑)：声源每帧
    /// 跟随建筑、distRange 随摄像机距离自动衰减，音量 = √油门）；非聚焦船壳整船
    /// OnCamera 循环（跨图 InMap 声源会被引擎 context 强制静音，音量 = 总油门 ×
    /// 与摄像机距离的线性衰减，距离复用船壳绘制的 FocusedDrawPosition 换算）。
    /// 纯表现缓存：不写物理、不进存档，读档/重置经 Reset 自愈；被休眠服务等外部
    /// End 掉的 sustainer 视作不存在，按新鲜油门自然重建。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ThrusterAudio
    {
        /// <summary>油门新鲜期（秒）：与 ThrusterEffects 同值——火焰在、声音在。</summary>
        private const float ThrottleFreshSeconds = 0.25f;

        /// <summary>可闻油门下限：与火焰 MinVisibleThrottle 同值（表现同步，不另设第二档）。</summary>
        private const float MinAudibleThrottle = 0.001f;

        /// <summary>船壳音满音量距离（格）：更近不再放大。</summary>
        private const float ShellHearNear = 30f;

        /// <summary>船壳音归零距离（格）：更远不发声（宇宙视野拉远时随镜头远离自然静默）。</summary>
        private const float ShellHearFar = 120f;

        /// <summary>船壳音满油门台数：Σ√油门 达到该值（约 3 台满油门）即满音量。</summary>
        private const float ShellFullThrottleCount = 3f;

        private static SoundDef loopMapDef;
        private static SoundDef loopShellDef;

        private static readonly Dictionary<Thing, Sustainer> MapLoops = new Dictionary<Thing, Sustainer>();
        private static readonly Dictionary<int, Sustainer> ShellLoops = new Dictionary<int, Sustainer>();
        private static readonly HashSet<Thing> ActiveMapThrusters = new HashSet<Thing>();
        private static readonly HashSet<int> ActiveShellShips = new HashSet<int>();
        private static readonly List<Thing> MapLoopRemovals = new List<Thing>();
        private static readonly List<int> ShellLoopRemovals = new List<int>();

        static ThrusterAudio()
        {
            // 两个 def 不可混用（见 Defs/SoundDefs/DeltaV_ThrusterSounds.xml 注释）：
            // Map def 只能 InMap 生成、Shell def 只能 OnCamera 生成。
            loopMapDef = DefDatabase<SoundDef>.GetNamed("DeltaV_ThrusterLoopMap");
            loopShellDef = DefDatabase<SoundDef>.GetNamed("DeltaV_ThrusterLoopShell");
        }

        /// <summary>每帧入口（UniverseManager.GameComponentUpdate，紧随 ThrusterEffects.Draw）。</summary>
        public static void Update(UniverseManager universe)
        {
            // 门控与 ShellRenderer.DrawNonFocusedShells 一致：非 Delta-V 参考系（含地面
            // 图，兼容 RimExodus）或世界视图一律无声。
            Map current = Find.CurrentMap;
            SpaceObject frame = current == null ? null : universe.FrameOf(current);
            if (frame == null || !WorldRendererUtility.DrawingMap)
            {
                EndAll();
                return;
            }
            UpdateFocusedLoops(current);
            UpdateShellLoops(universe, frame, current);
        }

        /// <summary>
        /// 聚焦图路径（守卫与 ThrusterEffects.Draw 逐条一致）：聚焦容器自身地图内，
        /// 每台点火推进器一个空间化循环，声源跟随建筑、镜头衰减交给 distRange。
        /// </summary>
        private static void UpdateFocusedLoops(Map current)
        {
            ShipPhysics.MassReport report = ShipPhysics.GetReport(current);
            bool fresh = report != null && Time.time - report.throttlesWrittenAt <= ThrottleFreshSeconds;

            ActiveMapThrusters.Clear();
            if (fresh)
            {
                for (int i = 0; i < report.thrusters.Count; i++)
                {
                    ShipPhysics.Thruster thruster = report.thrusters[i];
                    if (thruster.throttle <= MinAudibleThrottle || thruster.thing == null || !thruster.thing.Spawned)
                    {
                        continue;
                    }
                    ActiveMapThrusters.Add(thruster.thing);
                    float volume = Mathf.Sqrt(Mathf.Clamp01(thruster.throttle));
                    if (!MapLoops.TryGetValue(thruster.thing, out Sustainer loop) || loop == null || loop.Ended)
                    {
                        loop = loopMapDef.TrySpawnSustainer(
                            SoundInfo.InMap(new TargetInfo(thruster.thing), MaintenanceType.PerFrame));
                        MapLoops[thruster.thing] = loop;
                        if (loop != null && DeltaVLog.Enabled(DeltaVLogModule.Render))
                        {
                            DeltaVLog.Message(DeltaVLogModule.Render,
                                $"thruster audio map+ {thruster.thing.def.defName}#{thruster.thing.thingIDNumber} vol={volume:F2}");
                        }
                    }
                    if (loop != null)
                    {
                        loop.Maintain();
                        loop.info.volumeFactor = volume;
                    }
                }
            }

            // 本帧未点火的推进器熄声（淡出交给 sustainFadeoutTime）。
            MapLoopRemovals.Clear();
            foreach (KeyValuePair<Thing, Sustainer> kv in MapLoops)
            {
                if (ActiveMapThrusters.Contains(kv.Key))
                {
                    continue;
                }
                if (kv.Value != null && !kv.Value.Ended)
                {
                    kv.Value.End();
                    if (DeltaVLog.Enabled(DeltaVLogModule.Render))
                    {
                        DeltaVLog.Message(DeltaVLogModule.Render,
                            $"thruster audio map- {kv.Key.def.defName}#{kv.Key.thingIDNumber}");
                    }
                }
                MapLoopRemovals.Add(kv.Key);
            }
            for (int i = 0; i < MapLoopRemovals.Count; i++)
            {
                MapLoops.Remove(MapLoopRemovals[i]);
            }
        }

        /// <summary>
        /// 船壳路径（口径与 ShellRenderer.DrawObjectShell 的火焰条件一致：非聚焦容器、
        /// 源图有效；进场悬停船的容器包含——降落进场时船壳火焰可见、声音同在）。
        /// 每容器一个 OnCamera 循环，音量 = 总油门 × 与摄像机距离的线性衰减。
        /// </summary>
        private static void UpdateShellLoops(UniverseManager universe, SpaceObject frame, Map frameMap)
        {
            ActiveShellShips.Clear();
            Vector3 cameraPos = Find.Camera.transform.position;
            List<SpaceObject> objects = universe.objects;
            for (int i = 0; i < objects.Count; i++)
            {
                SpaceObject obj = objects[i];
                if (obj == frame)
                {
                    continue;
                }
                Map map = obj.map;
                if (map == null || map.Disposed || universe.Generation?.IsGenerating(map) == true)
                {
                    continue;
                }
                ShipPhysics.MassReport report = ShipPhysics.GetReport(map);
                if (report == null || Time.time - report.throttlesWrittenAt > ThrottleFreshSeconds)
                {
                    continue;
                }
                float thrust = 0f;
                for (int t = 0; t < report.thrusters.Count; t++)
                {
                    ShipPhysics.Thruster thruster = report.thrusters[t];
                    if (thruster.throttle > MinAudibleThrottle && thruster.thing != null && thruster.thing.Spawned)
                    {
                        thrust += Mathf.Sqrt(Mathf.Clamp01(thruster.throttle));
                    }
                }
                if (thrust <= 0f)
                {
                    continue;
                }
                // 位置复用船壳绘制同款换算（与所见一致）；距离含镜头高度——宇宙视野
                // 拉远时随摄像机远离自然衰减。
                Vector3 drawPos = SpaceTransform.FocusedDrawPosition(frame, frameMap, obj.pose.position, 0f);
                float distance = Vector3.Distance(cameraPos, drawPos);
                if (distance >= ShellHearFar)
                {
                    continue;
                }
                float falloff = distance <= ShellHearNear
                    ? 1f
                    : 1f - (distance - ShellHearNear) / (ShellHearFar - ShellHearNear);
                float volume = Mathf.Clamp01(thrust / ShellFullThrottleCount) * falloff;
                if (volume <= MinAudibleThrottle)
                {
                    continue;
                }
                ActiveShellShips.Add(obj.objectId);
                if (!ShellLoops.TryGetValue(obj.objectId, out Sustainer loop) || loop == null || loop.Ended)
                {
                    loop = loopShellDef.TrySpawnSustainer(SoundInfo.OnCamera(MaintenanceType.PerFrame));
                    ShellLoops[obj.objectId] = loop;
                    if (loop != null && DeltaVLog.Enabled(DeltaVLogModule.Render))
                    {
                        DeltaVLog.Message(DeltaVLogModule.Render,
                            $"thruster audio shell+ obj{obj.objectId} d={distance:F0} vol={volume:F2}");
                    }
                }
                if (loop != null)
                {
                    loop.Maintain();
                    loop.info.volumeFactor = volume;
                }
            }

            ShellLoopRemovals.Clear();
            foreach (KeyValuePair<int, Sustainer> kv in ShellLoops)
            {
                if (ActiveShellShips.Contains(kv.Key))
                {
                    continue;
                }
                if (kv.Value != null && !kv.Value.Ended)
                {
                    kv.Value.End();
                    if (DeltaVLog.Enabled(DeltaVLogModule.Render))
                    {
                        DeltaVLog.Message(DeltaVLogModule.Render, $"thruster audio shell- obj{kv.Key}");
                    }
                }
                ShellLoopRemovals.Add(kv.Key);
            }
            for (int i = 0; i < ShellLoopRemovals.Count; i++)
            {
                ShellLoops.Remove(ShellLoopRemovals[i]);
            }
        }

        /// <summary>读档 / 场景重置自愈（挂 UniverseManager 清理链，与 ThrusterEffects 同位）。</summary>
        public static void Reset()
        {
            EndAll();
        }

        private static void EndAll()
        {
            foreach (KeyValuePair<Thing, Sustainer> kv in MapLoops)
            {
                if (kv.Value != null && !kv.Value.Ended)
                {
                    kv.Value.End();
                }
            }
            MapLoops.Clear();
            foreach (KeyValuePair<int, Sustainer> kv in ShellLoops)
            {
                if (kv.Value != null && !kv.Value.Ended)
                {
                    kv.Value.End();
                }
            }
            ShellLoops.Clear();
            ActiveMapThrusters.Clear();
            ActiveShellShips.Clear();
        }
    }
}
