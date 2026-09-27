using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 居住区预制（地图工具包的"预制序列化"能力，2026-09-27）：把一张图的 Home area
    ///（居住区语义，同 RimExodus 口径）内的非太空结构与物品导出为可复用的 JSON 预制
    /// 文件，并可回放到新图。当前消费者 = 双船测试场景（替换 ShipStructureStamp 的
    /// 写死布局表）；将来通用地图工具包的其他能力沿用同一入口，不再另建格式。
    ///
    /// 与 PoiSnapshot 的边界：PoiSnapshot 是**存档事实**（全图、Scribe 深序列化、玩家
    /// 改动恢复）；HomePrefab 是 **mod 内容资产**（区域捕获、JSON 文件、设计期可迭代）。
    /// 两者共享 MapContentSpawner 回放内核，数据类分开。
    ///
    /// 「太空结构」排除判据（导出侧）：推进器（物理表归 XML，由 stamp 放置）、shuttle
    ///（载具非布局）、以及 Wall/Door 四邻含无基架格者（船体外壳环——外环墙属于 hull
    /// stamp；内隔墙保留）。基架地形不导出（hull stamp 铺设）。
    /// </summary>
    public static class HomePrefab
    {
        private const int FormatVersion = 1;

        [System.Serializable]
        private class ThingRecord
        {
            public int x;            // 相对捕获锚点（Home 包围盒 min）
            public int z;
            public string def = "";
            public string stuff = ""; // 空 = 无材质
            public int rot;
            public int hp;           // ≤0 = 默认满血
            public int stack;        // ≤1 = 单件（仅物品用）
            public int quality = -1; // QualityCategory；-1 = 无 CompQuality
        }

        [System.Serializable]
        private class PrefabData
        {
            public int version = FormatVersion;
            public int sizeX;
            public int sizeZ;
            public List<ThingRecord> buildings = new List<ThingRecord>();
            public List<ThingRecord> items = new List<ThingRecord>();
        }

        // ---- 导出 ----

        /// <summary>
        /// 把当前聚焦图的 Home area 导出为预制文件（调试动作入口）。返回导出的
        /// 建筑+物品计数；无 Home area 或无内容时报错返回 -1。
        /// </summary>
        public static int ExportHomeArea(string name)
        {
            Map map = Find.CurrentMap;
            if (map == null) return -1;
            Area home = map.areaManager?.Home;
            if (home == null || !home.ActiveCells.Any())
            {
                Log.Error("[DeltaV] HomePrefab: current map has no home area cells - paint the "
                    + "habitat area before exporting.");
                return -1;
            }
            var cells = new List<IntVec3>(home.ActiveCells);
            var data = Capture(map, cells);
            if (data.buildings.Count + data.items.Count == 0)
            {
                Log.Error("[DeltaV] HomePrefab: nothing capturable in home area (only space "
                    + "structure found).");
                return -1;
            }
            string path = PrefabPath(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(data, true));
            int count = data.buildings.Count + data.items.Count;
            Log.Message($"[DeltaV] HomePrefab exported {count} thing(s) "
                + $"({data.buildings.Count} buildings, {data.items.Count} items, "
                + $"{data.sizeX}x{data.sizeZ}) to {path}");
            return count;
        }

        private static PrefabData Capture(Map map, List<IntVec3> cells)
        {
            var data = new PrefabData();
            var homeSet = new HashSet<IntVec3>(cells);
            IntVec3 anchor = cells[0];
            foreach (IntVec3 cell in cells)
            {
                anchor.x = Mathf.Min(anchor.x, cell.x);
                anchor.z = Mathf.Min(anchor.z, cell.z);
            }
            data.sizeX = cells.Max(c => c.x) - anchor.x + 1;
            data.sizeZ = cells.Max(c => c.z) - anchor.z + 1;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (!thing.Spawned || !homeSet.Contains(thing.Position)) continue;
                bool isBuilding = thing is Building;
                if (!isBuilding && thing.def.category != ThingCategory.Item) continue;
                if (ThrusterPower.IsThruster(thing)) continue;             // 推进器：物理表归 XML
                if (thing.TryGetComp<CompSpaceShuttle>() != null) continue; // shuttle：载具非布局
                if (isBuilding && IsHullSkin(thing, map)) continue;         // 外环墙/门：hull stamp 语义
                var record = new ThingRecord
                {
                    x = thing.Position.x - anchor.x,
                    z = thing.Position.z - anchor.z,
                    def = thing.def.defName,
                    stuff = thing.Stuff?.defName ?? "",
                    rot = thing.Rotation.AsInt,
                    hp = thing.HitPoints,
                    stack = thing.stackCount,
                    quality = thing.TryGetComp<CompQuality>() is { } quality ? (int)quality.Quality : -1,
                };
                (isBuilding ? data.buildings : data.items).Add(record);
            }
            return data;
        }

        /// <summary>船体外壳件：Wall/Door 且四邻存在无基架格（外环朝真空；内隔墙四邻皆基架，保留）。</summary>
        private static bool IsHullSkin(Thing thing, Map map)
        {
            if (!thing.def.IsWall && !thing.def.IsDoor) return false;
            foreach (IntVec3 near in CardinalOffsets)
            {
                IntVec3 neighbor = thing.Position + near;
                if (!neighbor.InBounds(map)
                    || map.terrainGrid.FoundationAt(neighbor)?.IsSubstructure != true)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly IntVec3[] CardinalOffsets =
        {
            new IntVec3(1, 0, 0), new IntVec3(-1, 0, 0), new IntVec3(0, 0, 1), new IntVec3(0, 0, -1),
        };

        // ---- 文件 ----

        /// <summary>预制文件是否存在（测试场景决定走预制还是写死布局）。</summary>
        public static bool Exists(string name)
        {
            return File.Exists(PrefabPath(name));
        }

        private static string PrefabPath(string name)
        {
            string rootDir = LoadedModManager.GetMod<DeltaVMod>().Content.RootDir;
            return Path.Combine(rootDir, "Prefabs", name + ".json");
        }

        // ---- 回放 ----

        /// <summary>
        /// 回放预制到 target 矩形（先建筑后物品，MapContentSpawner 统一入口；
        /// WipeMode.Vanish 让位同格冲突）。返回放置计数。
        /// </summary>
        public static int SpawnInto(string name, Map map, CellRect target, Faction faction)
        {
            PrefabData data = Read(name);
            if (data == null) return 0;
            IntVec2 offset = new IntVec2(
                target.minX + Mathf.Max(0, (target.Width - data.sizeX) / 2),
                target.minZ + Mathf.Max(0, (target.Height - data.sizeZ) / 2));
            int spawned = SpawnRecords(map, data.buildings, offset, faction)
                + SpawnRecords(map, data.items, offset, faction);
            if (spawned < data.buildings.Count + data.items.Count)
            {
                Log.Error($"[DeltaV] HomePrefab '{name}': only {spawned}/"
                    + $"{data.buildings.Count + data.items.Count} entries spawned "
                    + $"(missing defs or out of map bounds on {UniverseManager.MapTag(map)}).");
            }
            return spawned;
        }

        private static int SpawnRecords(Map map, List<ThingRecord> records, IntVec2 offset, Faction faction)
        {
            int spawned = 0;
            foreach (ThingRecord record in records)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(record.def);
                if (def == null) continue;
                ThingDef stuff = string.IsNullOrEmpty(record.stuff)
                    ? null
                    : DefDatabase<ThingDef>.GetNamedSilentFail(record.stuff);
                var cell = new IntVec3(offset.x + record.x, 0, offset.z + record.z);
                if (!cell.InBounds(map)) continue;
                Thing thing = MapContentSpawner.SpawnRecord(map, def, stuff, cell,
                    new Rot4(record.rot), record.hp, record.stack, faction,
                    record.quality >= 0 ? (QualityCategory)record.quality : (QualityCategory?)null);
                if (thing != null) spawned++;
            }
            return spawned;
        }

        private static PrefabData Read(string name)
        {
            string path = PrefabPath(name);
            if (!File.Exists(path))
            {
                Log.Error($"[DeltaV] HomePrefab: file not found {path}");
                return null;
            }
            PrefabData data;
            try
            {
                data = JsonUtility.FromJson<PrefabData>(File.ReadAllText(path));
            }
            catch (System.Exception ex)
            {
                Log.Error($"[DeltaV] HomePrefab: failed to parse {path}: {ex.Message}");
                return null;
            }
            if (data == null || data.version != FormatVersion)
            {
                Log.Error($"[DeltaV] HomePrefab: unsupported format in {path} "
                    + $"(version {data?.version ?? -1} != {FormatVersion}).");
                return null;
            }
            data.buildings ??= new List<ThingRecord>();
            data.items ??= new List<ThingRecord>();
            return data;
        }
    }
}
