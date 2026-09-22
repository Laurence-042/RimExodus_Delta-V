using System;
using System.Collections.Generic;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 可独立抽取的地图软休眠核心。这里只管理 Map 的模拟状态，不理解宇宙距离、POI 或飞船。
    /// 休眠状态是运行时缓存，不序列化；读档后由上层策略重新收敛。
    /// </summary>
    internal sealed class MapDormancyService
    {
        private readonly HashSet<Map> dormantMaps = new HashSet<Map>();

        public bool IsDormant(Map map)
        {
            return map != null && !map.Disposed && dormantMaps.Contains(map);
        }

        public void Sleep(Map map, string reason)
        {
            if (map == null || map.Disposed) return;
            bool newlyDormant = dormantMaps.Add(map);

            // MapPre/PostTick 并不覆盖全局 Thing tick 表；两条腿必须同时停。
            TickManager tickManager = Find.TickManager;
            tickManager?.RemoveAllFromMap(map);
            if (!newlyDormant) return;
            map.weatherManager?.EndAllSustainers();
            Find.SoundRoot?.sustainerManager?.EndAllInMap(map);
            Find.ColonistBar?.MarkColonistsDirty();
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{map.Parent?.Label ?? map.ToString()}: dormant ({reason})");
        }

        public void Wake(Map map, string reason)
        {
            if (map == null || map.Disposed || !dormantMaps.Remove(map)) return;

            // 对齐读档 respawn 的恢复语义。RemoveAllFromMap 已把旧条目与注册缓冲一起摘净，
            // 因此这里只会恢复一次，不会双注册。
            TickManager tickManager = Find.TickManager;
            tickManager?.RemoveAllFromMap(map);
            ThingOwner things = map.spawnedThings;
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing != null && !thing.Destroyed && thing.Spawned)
                {
                    tickManager?.RegisterAllTickabilityFor(thing);
                }
            }
            Find.ColonistBar?.MarkColonistsDirty();
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{map.Parent?.Label ?? map.ToString()}: woken ({reason})");
        }

        /// <summary>图即将删除时只忘记运行时状态，绝不尝试恢复。</summary>
        public void Forget(Map map)
        {
            if (map != null) dormantMaps.Remove(map);
        }

        public void Reset()
        {
            foreach (Map map in new List<Map>(dormantMaps))
                Wake(map, "dormancy service reset");
            dormantMaps.Clear();
        }
    }
}
