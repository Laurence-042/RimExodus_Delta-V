using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 原版 Map.ExposeData 的离线承载。Game.DeinitAndRemoveMap 会转移船员、清空图内容，
    /// 因而归档仅移出 Game.maps 并释放运行时图资源；恢复交给原版 Map.FinalizeLoading。
    /// 整段只能在帧边界、Scribe 空闲时执行。
    /// </summary>
    internal static class ArchivedMapStore
    {
        private static readonly FieldInfo GameMaps = AccessTools.Field(typeof(Game), "maps");
        private static readonly FieldInfo LoadedDirectory = AccessTools.Field(typeof(CrossRefHandler),
            "loadedObjectDirectory");

        public static bool TryCapture(Map map, out string data)
        {
            data = null;
            if (map == null || map.Disposed || Scribe.mode != LoadSaveMode.Inactive) return false;
            string path = Path.GetTempFileName();
            try
            {
                Scribe.saver.InitSaving(path, "archivedMap");
                Map current = map;
                Scribe_Deep.Look(ref current, "map");
                Scribe.saver.FinalizeSaving();
                byte[] xml = File.ReadAllBytes(path);
                var document = new XmlDocument();
                document.Load(path);
                XmlNode mapNode = document.DocumentElement?["map"];
                if (mapNode?["mapInfo"] == null || mapNode["things"] == null)
                    throw new InvalidDataException("Serialized map core nodes are missing.");
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                    gzip.Write(xml, 0, xml.Length);
                data = Convert.ToBase64String(output.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Full map archive capture failed ({UniverseManager.MapTag(map)}): {ex}");
                Scribe.ForceStop();
                return false;
            }
            finally { File.Delete(path); }
        }

        public static bool TryUnload(Map map)
        {
            if (map == null || map.Disposed || GameMaps?.GetValue(Current.Game) is not List<Map> maps)
                return false;
            int index = maps.IndexOf(map);
            if (index < 0 || map == Find.CurrentMap) return false;
            Map focused = Current.Game.CurrentMap;
            bool detached = false;
            try
            {
                // 只维护仍在 Game.maps 中的地图索引。被卸图的完整 Thing 状态已写入 XML；
                // MapDeiniter 的 Notify_MyMapRemoved/PassPawnsToWorld 会改写事实，不能调用。
                Find.TickManager.RemoveAllFromMap(map);
                Find.SoundRoot.sustainerManager.EndAllInMap(map);
                map.weatherManager.EndAllSustainers();
                for (int i = index + 1; i < maps.Count; i++)
                {
                    Map remaining = maps[i];
                    foreach (Thing thing in remaining.spawnedThings) thing.DecrementMapIndex();
                    foreach (Room room in remaining.regionGrid.AllRooms)
                        foreach (District district in room.Districts) district.DecrementMapIndex();
                    foreach (Region region in remaining.regionGrid.AllRegions_NoRebuild_InvalidAllowed)
                        region.DecrementMapIndex();
                }
                maps.RemoveAt(index);
                detached = true;
                Current.Game.currentMapIndex = focused == null
                    ? (sbyte)-1 : (sbyte)maps.IndexOf(focused);
                MapComponentUtility.MapRemoved(map);
                map.Dispose();
                Find.ColonistBar.MarkColonistsDirty();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Full map archive unload failed ({UniverseManager.MapTag(map)}): {ex}");
                if (!detached) return false;
                // 地图已经离开 Game.maps；交还成功以便调用方保留完整归档数据。
                try { if (!map.Disposed) map.Dispose(); }
                catch (Exception disposeEx)
                { Log.Error($"[DeltaV] Archive map resource cleanup failed: {disposeEx}"); }
                return true;
            }
        }

        public static bool TryRestore(string data, int expectedMapId,
            PocketMapParent expectedParent, out Map map)
        {
            map = null;
            if (data.NullOrEmpty() || Scribe.mode != LoadSaveMode.Inactive) return false;
            string path = Path.GetTempFileName();
            Map loaded = null;
            bool added = false;
            try
            {
                byte[] compressed = Convert.FromBase64String(data);
                using (var input = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress))
                using (var output = File.Create(path)) input.CopyTo(output);
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref loaded, "map");
                if (loaded == null) throw new InvalidDataException("Archive contains no map.");
                RegisterExternalReferences();
                Current.Game.AddMap(loaded);
                added = true;
                Scribe.loader.FinalizeLoading();
                if (loaded.uniqueID != expectedMapId || loaded.Parent != expectedParent)
                    throw new InvalidDataException("Restored map identity or pocket parent differs from archive.");
                loaded.FinalizeLoading();
                map = loaded;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Full map archive restore failed: {ex}");
                Scribe.ForceStop();
                if (added && GameMaps?.GetValue(Current.Game) is List<Map> maps)
                {
                    Find.TickManager.RemoveAllFromMap(loaded);
                    maps.Remove(loaded);
                }
                loaded?.Dispose();
                return false;
            }
            finally { File.Delete(path); }
        }

        private static void RegisterExternalReferences()
        {
            if (LoadedDirectory?.GetValue(Scribe.loader.crossRefs) is not LoadedObjectDirectory directory)
                throw new MissingFieldException("CrossRefHandler.loadedObjectDirectory");
            var seen = new HashSet<string>();
            void Register(ILoadReferenceable value)
            {
                if (value == null) return;
                string id = value.GetUniqueLoadID();
                if (seen.Add(id)) directory.RegisterLoaded(value);
            }
            foreach (Faction faction in Find.FactionManager.AllFactionsListForReading) Register(faction);
            foreach (WorldObject obj in Find.WorldObjects.AllWorldObjects) Register(obj);
            foreach (PocketMapParent parent in Find.World.pocketMaps) Register(parent);
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead) Register(pawn);
            foreach (Map other in Find.Maps)
            {
                Register(other);
                foreach (Thing thing in other.listerThings.AllThings) Register(thing);
            }
        }
    }
}
