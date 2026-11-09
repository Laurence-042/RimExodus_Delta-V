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

        private static ModContentPack Content => LoadedModManager.GetMod<DeltaVMod>()?.Content
            ?? throw new InvalidOperationException("Delta-V content pack is not loaded.");

        /// <summary>用户导出目录：RimWorld SaveData/DeltaV/Prefabs，不随 Mod 安装或发布包移动。</summary>
        public static string PrefabDirectory
        {
            get
            {
                return Path.Combine(GenFilePaths.SaveDataFolderPath, "DeltaV", "Prefabs");
            }
        }

        /// <summary>随 Mod 发布、两版共用的唯一内置 prefab 目录。</summary>
        internal static string BundledPrefabDirectory => Path.Combine(Content.RootDir, "Common", "Prefabs");

        // CombatStrategies 等独立本地资源不能再从 PrefabDirectory 的父目录反推 Mod 根。
        internal static string ModRootDirectory => Content.RootDir;

        /// <summary>
        /// 导出当前聚焦图掩码内的内容（PrefabUtility.CreatePrefab 硬编码当前图）。
        /// thing 白名单 Building+Item（pawn/filth/blueprint 等丢弃）；多格建筑按 anchor
        /// 在掩码内判定；terrain 逐格裁剪且 isFoundation 条目丢弃（foundation 走侧车）。
        /// 掩码为空抛异常。
        /// </summary>
        public static MapPrefabDocument Export(string name, IEnumerable<IntVec3> cells)
        {
            return SharedMapBackend.Export(Find.CurrentMap, name, cells);
        }

        /// <summary>加载导出目录内最后修改的一份 prefab；目录或文件缺失抛异常（触发调用方回退）。</summary>
        public static MapPrefabDocument LoadLatest()
        {
            string dir = PrefabDirectory;
            string latest = new DirectoryInfo(dir).GetFiles("*.xml", SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
            if (latest == null)
            {
                throw new FileNotFoundException($"No exported map prefab found in {dir}.");
            }
            return MapPrefabDocument.Load(latest);
        }

        /// <summary>
        /// 按目录内相对路径加载（支持子目录，正斜杠或平台分隔符均可）。
        /// 解析后必须仍位于当前目录根内（拒绝 .. 逃逸与绝对路径），越界抛异常。
        /// </summary>
        public static MapPrefabDocument LoadRelative(string relativePath)
        {
            return MapPrefabDocument.Load(ResolveRelativePath(relativePath));
        }

        // User exports override the single shipped catalog. Version folders do not carry prefab copies.
        private static IEnumerable<string> CatalogDirectories => new[]
        {
            PrefabDirectory,
            BundledPrefabDirectory
        };

        /// <summary>递归枚举用户与内置目录下全部 prefab 的相对路径（正斜杠分隔，稳定排序）。</summary>
        public static List<string> ListPrefabFiles()
            => ListPrefabFiles(CatalogDirectories);

        internal static List<string> ListPrefabFiles(IEnumerable<string> directories)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in directories)
            {
                DirectoryInfo info = new DirectoryInfo(dir);
                if (!info.Exists) continue;
                string root = info.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                foreach (FileInfo file in info.GetFiles("*.xml", SearchOption.AllDirectories))
                    result.Add(file.FullName.Substring(root.Length).Replace('\\', '/'));
            }
            return result.OrderBy(path => path, StringComparer.Ordinal).ToList();
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

        /// <summary>目录内相对路径 → 绝对路径，带越界防护。</summary>
        private static string ResolveRelativePath(string relativePath)
            => ResolveRelativePath(CatalogDirectories, relativePath);

        internal static string ResolveRelativePath(IEnumerable<string> directories, string relativePath)
        {
            foreach (string directory in directories)
            {
                string path = ResolveWithin(directory, relativePath);
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException($"Map prefab '{relativePath}' is missing from the prefab catalog.");
        }

        internal static string ResolveWithin(string directory, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("MapPrefab relative path is empty.");
            }
            string root = Path.GetFullPath(directory)
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
            return SharedMapBackend.Spawn(doc, map, position, faction, ownerId, rot, universe, blankSpace);
        }
    }
}
