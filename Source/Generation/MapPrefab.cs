using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 通用地图内容 prefab 门面（单实现入口）：以任意 cell 掩码把当前聚焦图的一片内容
    /// 导出为可重复实例化、派系无关的模板；重建时以指定派系整体放回任意地图。
    /// 地形与布局坐标复用原版 PrefabUtility；Thing 逐实例走原版 Scribe 存档机制。
    /// foundation/roof 另存侧车，整片落位后触发 CompPrefabMarker 展开。
    /// 多格建筑按 anchor（Thing.Position）是否在掩码内判定归属。
    /// </summary>
    public static class MapPrefab
    {
        internal static CellRect ContentBounds(MapPrefabDocument doc, Rot4 rotation)
        {
            var cells = new HashSet<IntVec3>();
            foreach (var entry in doc.foundations) cells.Add(PrefabUtility.GetAdjustedLocalPosition(entry.pos, rotation));
            foreach (var entry in doc.roofs) cells.Add(PrefabUtility.GetAdjustedLocalPosition(entry.pos, rotation));
            foreach (var terrain in doc.terrain)
                if (terrain.def != TerrainDefOf.Space)
                    foreach (CellRect rect in terrain.rects)
                        foreach (IntVec3 cell in rect) cells.Add(PrefabUtility.GetAdjustedLocalPosition(cell, rotation));
            foreach (var entry in doc.thingEntries)
            {
                IntVec3 cell = PrefabUtility.GetAdjustedThingLocalPosition(entry.def, rotation, entry.pos);
                Rot4 facing = entry.def.rotatable
                    ? rotation.Rotated(Rot4.GetRelativeRotation(Rot4.North, entry.rotation)) : entry.def.defaultPlacingRot;
                foreach (IntVec3 occupied in GenAdj.OccupiedRect(cell, facing, entry.def.Size)) cells.Add(occupied);
            }
            if (cells.Count == 0) throw new InvalidDataException($"Prefab '{doc.name}' has no physical content.");
            return CellRect.FromCellList(cells.ToList());
        }

        internal static IntVec3 CenteredPosition(MapPrefabDocument doc, Map map, Rot4 rotation)
        {
            CellRect bounds = ContentBounds(doc, rotation);
            IntVec3 root = new IntVec3((map.Size.x - bounds.Width) / 2 - bounds.minX, 0,
                (map.Size.z - bounds.Height) / 2 - bounds.minZ);
            return root - PrefabUtility.GetRoot(IntVec3.Zero, doc.size, rotation);
        }

        /// <summary>与 About.xml 的 packageId 保持一致，用于定位导出目录。</summary>
        private const string ModPackageId = "RimExodus.DeltaV.dev";

        /// <summary>导出目录：mod 根下 Prefabs/（gitignore；运行时经原版 XML 管线加载，不进 Defs）。</summary>
        public static string PrefabDirectory
        {
            get
            {
                ModContentPack pack = LoadedModManager.RunningMods.FirstOrDefault(p =>
                    string.Equals(p.PackageId, ModPackageId, StringComparison.OrdinalIgnoreCase));
                if (pack == null)
                {
                    throw new InvalidOperationException($"Mod content pack '{ModPackageId}' not found.");
                }
                return Path.Combine(pack.RootDir, "Prefabs");
            }
        }

        /// <summary>
        /// 导出当前聚焦图掩码内的内容（PrefabUtility.CreatePrefab 硬编码当前图）。
        /// thing 白名单 Building+Item（pawn/filth/blueprint 等丢弃）；多格建筑按 anchor
        /// 在掩码内判定；terrain 逐格裁剪且 isFoundation 条目丢弃（foundation 走侧车）。
        /// 掩码为空抛异常。
        /// </summary>
        public static MapPrefabDocument Export(string name, IEnumerable<IntVec3> cells)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("MapPrefab export requires a focused map.");
            }
            var mask = new HashSet<IntVec3>();
            foreach (IntVec3 cell in cells)
            {
                if (cell.InBounds(map))
                {
                    mask.Add(cell);
                }
            }
            if (mask.Count == 0)
            {
                throw new InvalidOperationException("MapPrefab export mask is empty.");
            }
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (IntVec3 cell in mask)
            {
                minX = Math.Min(minX, cell.x);
                minZ = Math.Min(minZ, cell.z);
                maxX = Math.Max(maxX, cell.x);
                maxZ = Math.Max(maxZ, cell.z);
            }
            var bounds = new CellRect(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);

            PrefabDef core = PrefabUtility.CreatePrefab(bounds, copyAllThings: true, copyTerrain: true);
            var doc = new MapPrefabDocument
            {
                name = name,
                size = bounds.Size,
            };

            // CreatePrefab 仍负责地形；实体逐实例保存，不能按相同 def/外观合并。
            foreach (Thing thing in map.listerThings.AllThings
                .Where(t => (t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Item)
                    && mask.Contains(t.Position))
                .OrderBy(t => t.def.building?.isAttachment == true
                    || t.TryGetComp<CompSpaceWeaponAttachment>() != null ? 1 : 0)
                .ThenByDescending(t => t.def.category == ThingCategory.Building)
                .ThenByDescending(t => t.TryGetComp<CompDeltaVPilotConsole>()?.isMain == true)
                .ThenByDescending(t => t.def.shortHash))
            {
                string state = null;
                try { state = MapPrefabThingState.Capture(thing); }
                catch (Exception ex)
                {
                    Log.Warning($"[DeltaV] MapPrefab '{name}' could not save {thing.def.defName} at {thing.Position}; it will use default state: {ex}");
                }
                doc.thingEntries.Add(new MapPrefabThingEntry
                {
                    pos = thing.Position - bounds.Min,
                    def = thing.def,
                    stuff = thing.Stuff,
                    rotation = thing.Rotation,
                    hitPoints = thing.HitPoints,
                    stackCount = thing.stackCount,
                    stateXml = state,
                });
            }

            var terrainByDef = new Dictionary<(TerrainDef def, ColorDef color), PrefabTerrainData>();
            foreach (var (data, cell) in core.GetTerrain())
            {
                if (data.def.isFoundation)
                {
                    continue;
                }
                if (!mask.Contains(bounds.Min + cell))
                {
                    continue;
                }
                if (!terrainByDef.TryGetValue((data.def, data.color), out PrefabTerrainData entry))
                {
                    entry = new PrefabTerrainData
                    {
                        def = data.def,
                        color = data.color,
                        rects = new List<CellRect>(),
                    };
                    terrainByDef[(data.def, data.color)] = entry;
                }
                entry.rects.Add(new CellRect(cell.x, cell.z, 1, 1));
            }
            doc.terrain.AddRange(terrainByDef.Values);

            // 侧车：掩码内逐格扫描 foundation 与 roof（原版 prefab 词汇不覆盖）。
            foreach (IntVec3 cell in mask)
            {
                TerrainDef foundation = map.terrainGrid.FoundationAt(cell);
                if (foundation != null)
                {
                    doc.foundations.Add(new PrefabFoundationEntry { pos = cell - bounds.Min, def = foundation });
                }
                RoofDef roof = map.roofGrid.RoofAt(cell);
                if (roof != null)
                {
                    doc.roofs.Add(new PrefabRoofEntry { pos = cell - bounds.Min, def = roof });
                }
            }

            string path = Path.Combine(PrefabDirectory, name + ".xml");
            doc.Save(path);
            Log.Message($"[DeltaV] MapPrefab '{name}' exported to {path}: {doc.ThingCellCount} things, "
                + $"{doc.terrain.Count} terrain entries, {doc.foundations.Count} foundations, {doc.roofs.Count} roofs "
                + $"over {mask.Count} masked cells.");
            return doc;
        }

        /// <summary>加载导出目录内最后修改的一份 prefab；目录或文件缺失抛异常（触发调用方回退）。</summary>
        public static MapPrefabDocument LoadLatest()
        {
            string dir = PrefabDirectory;
            string latest = new DirectoryInfo(dir).GetFiles("*.xml")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
            if (latest == null)
            {
                throw new FileNotFoundException($"No exported map prefab found in {dir}.");
            }
            return MapPrefabDocument.Load(latest);
        }

        /// <summary>
        /// 按相对路径加载（相对 Prefabs/，支持子目录，正斜杠或平台分隔符均可）。
        /// 解析后必须仍位于 Prefabs/ 内（拒绝 .. 逃逸与绝对路径），越界抛异常。
        /// </summary>
        public static MapPrefabDocument LoadRelative(string relativePath)
        {
            return MapPrefabDocument.Load(ResolveRelativePath(relativePath));
        }

        /// <summary>递归枚举 Prefabs/ 下全部 prefab 的相对路径（正斜杠分隔，稳定排序）。</summary>
        public static List<string> ListPrefabFiles()
        {
            string dir = PrefabDirectory;
            var result = new List<string>();
            DirectoryInfo info = new DirectoryInfo(dir);
            if (!info.Exists) return result;
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (FileInfo file in info.GetFiles("*.xml", SearchOption.AllDirectories))
            {
                string relative = file.FullName.Substring(root.Length);
                result.Add(relative.Replace(Path.AltDirectorySeparatorChar, '/'));
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// 确定性解析 prefab 池：过滤磁盘上仍存在的条目后按 seed 取模选一份（排序保证
        /// 与设置列表的书写顺序无关）；池为空、全部失效或路径越界返回 null，由调用方
        /// 回退程序生成。
        /// </summary>
        public static string ResolvePoolPath(List<string> pool, int seed)
        {
            if (pool == null || pool.Count == 0) return null;
            List<string> valid = new List<string>();
            foreach (string entry in pool)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                try
                {
                    if (File.Exists(ResolveRelativePath(entry)))
                    {
                        valid.Add(entry);
                    }
                }
                catch (Exception)
                {
                    // 越界/非法路径条目直接视为失效，不中断池解析。
                }
            }
            if (valid.Count == 0) return null;
            valid.Sort(StringComparer.Ordinal);
            return valid[(seed & int.MaxValue) % valid.Count];
        }

        /// <summary>相对路径 → Prefabs/ 内的绝对路径，带越界防护。</summary>
        private static string ResolveRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("MapPrefab relative path is empty.");
            }
            string root = Path.GetFullPath(PrefabDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"MapPrefab relative path escapes the prefab directory: {relativePath}");
            }
            return full;
        }

        /// <summary>
        /// 把文档整片放置到 map：foundation（+指定归属的域认领）→ 原版地形 →
        /// 整批 Thing 状态恢复与落位 → roofs → 设施重链 → 标记建筑展开。ownerId ≤ 0 时不认领（驾驶台 spawn
        /// 时会自行认领连通域）。返回仍在地图上的 prefab thing（marker 产物不含）。
        /// </summary>
        public static List<Thing> Spawn(MapPrefabDocument doc, Map map, IntVec3 position, Faction faction,
            int ownerId = 0, Rot4 rot = default, UniverseManager universe = null, bool blankSpace = false)
        {
            PrefabDef prefabDef = doc.BuildPrefabDef(omitSpace: blankSpace);
            // foundation/roof 与 SpawnPrefab 内部用同一 root 与旋转变换（显示锚 = 逻辑锚）。
            IntVec3 root = PrefabUtility.GetRoot(prefabDef, position, rot);
            Rot4 rotation = PrefabUtility.ValidateRotation(prefabDef, rot);

            IntVec3 firstFoundation = IntVec3.Invalid;
            foreach (PrefabFoundationEntry entry in doc.foundations)
            {
                IntVec3 cell = root + PrefabUtility.GetAdjustedLocalPosition(entry.pos, rotation);
                if (!cell.InBounds(map))
                {
                    continue;
                }
                map.terrainGrid.SetFoundation(cell, entry.def);
                if (map.terrainGrid.FoundationAt(cell) != entry.def)
                    throw new InvalidDataException($"MapPrefab '{doc.name}' cannot set foundation {entry.def.defName} at {cell}.");
                if (!firstFoundation.IsValid)
                {
                    firstFoundation = cell;
                }
            }
            if (ownerId > 0 && firstFoundation.IsValid)
            {
                // 指定归属（如容器 ownerId）：驾驶台 spawn 时发现已归属即服从，不自分配。
                SubstructureOwnership.For(map).ClaimConnectedDomain(firstFoundation, ownerId);
            }

            // 原版负责 terrain；Thing 由 Scribe 读取完整实例后统一落位。
            PrefabUtility.SpawnPrefab(prefabDef, map, position, rotation, faction);
            List<Thing> loaded = MapPrefabThingState.Instantiate(doc.thingEntries, doc.name, faction);
            var spawned = new List<Thing>();
            for (int i = 0; i < doc.thingEntries.Count; i++)
            {
                MapPrefabThingEntry entry = doc.thingEntries[i];
                Thing thing = i < loaded.Count ? loaded[i] : null;
                bool restoredState = thing != null && thing.def == entry.def;
                if (!restoredState)
                {
                    Log.Warning($"[DeltaV] MapPrefab '{doc.name}' using default state for {entry.def.defName} at {entry.pos}.");
                    thing = ThingMaker.MakeThing(entry.def, entry.stuff);
                    if (entry.def.useHitPoints && entry.hitPoints > 0)
                        thing.HitPoints = Math.Min(entry.hitPoints, thing.MaxHitPoints);
                    thing.stackCount = Math.Max(1, entry.stackCount);
                    // ThingMaker already assigns fresh IDs, including any default nested objects.
                    if (thing.def.CanHaveFaction) thing.SetFactionDirect(faction);
                }
                thing.spawnedTick = Find.TickManager.TicksGame;
                thing.despawnedTick = -1;
                if (thing.TryGetComp<CompDeltaVPilotConsole>() is { } console)
                {
                    console.shipId = -1;
                    console.isMain = false;
                }
                thing.TryGetComp<CompSpaceWeapon>()?.ResetPrefabIdentity();
                thing.TryGetComp<CompSpaceWeaponTraverse>()?.RotateForPrefab(rotation);
                if (thing is Building_DockingPort port)
                    port.RebasePrefabLocalState(map, thing.Position - entry.pos, root, rotation);

                IntVec3 cell = root + PrefabUtility.GetAdjustedThingLocalPosition(entry.def, rotation, entry.pos);
                Rot4 thingRot = entry.def.rotatable
                    ? rotation.Rotated(Rot4.GetRelativeRotation(Rot4.North, entry.rotation))
                    : entry.def.defaultPlacingRot;
                Thing placed;
                try
                {
                    placed = MapContentSpawner.SpawnExisting(thing, map, cell, thingRot, restoredState);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"MapPrefab '{doc.name}' failed placing entry {i} "
                        + $"({entry.def.defName}) at {cell}, rotation {thingRot}.", ex);
                }
                if (placed == null)
                    throw new InvalidDataException($"MapPrefab '{doc.name}' cannot place entry {i} "
                        + $"({entry.def.defName}) at {cell}, rotation {thingRot}.");
                spawned.Add(placed);
            }
            int placedCount = spawned.Count;

            foreach (PrefabRoofEntry entry in doc.roofs)
            {
                IntVec3 cell = root + PrefabUtility.GetAdjustedLocalPosition(entry.pos, rotation);
                if (cell.InBounds(map))
                {
                    map.roofGrid.SetRoof(cell, entry.def);
                }
            }

            MapContentSpawner.RelinkGravshipComponents(map);

            var context = new PrefabSpawnContext(map, faction, ownerId, root, rotation, universe);
            foreach (Thing thing in spawned.ToList())
            {
                thing.TryGetComp<CompPrefabMarker>()?.Expand(context);
            }
            spawned.RemoveAll(thing => thing.Destroyed);

            if (placedCount != doc.ThingCellCount)
            {
                Log.Warning($"[DeltaV] MapPrefab '{doc.name}' spawn mismatch: expected {doc.ThingCellCount} "
                    + $"things, placed {placedCount} (see placement warnings above).");
            }
            return spawned;
        }
    }
}
