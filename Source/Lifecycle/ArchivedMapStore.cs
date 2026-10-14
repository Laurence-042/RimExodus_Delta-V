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

        public static bool CanUnloadLosslessly(Map map)
        {
            if (map == null) return false;
            if (Find.Anomaly?.monolith?.Map == map) return false;
            foreach (QueuedIncident queued in Find.Storyteller.incidentQueue)
                if (queued.FiringIncident?.parms?.target == map) return false;
            foreach (Quest quest in Find.QuestManager.QuestsListForReading)
                if (!quest.Historical && (quest.QuestLookTargets.Any(target => target.Map == map)
                    || quest.QuestSelectTargets.Any(target => target.Map == map))) return false;
            return true;
        }

        public static bool TryUnload(Map map, bool preserveExternalEvents = true)
        {
            if (map == null || map.Disposed || GameMaps?.GetValue(Current.Game) is not List<Map> maps)
                return false;
            int index = maps.IndexOf(map);
            if (index < 0 || map == Find.CurrentMap) return false;
            // 这些全局事件不在 Map.ExposeData 内；先留图等待事件结束，避免归档时丢失。
            if (preserveExternalEvents && !CanUnloadLosslessly(map)) return false;
            Map focused = Current.Game.CurrentMap;
            bool detached = false;
            try
            {
                // 归档快照已保存；只清理这份即将废弃的运行时图实例。
                // 原版 MapDeiniter 会处理被移除图的索引，跳过它时仍须做同等的索引清理。
                // 不能调用 Thing.Notify_MyMapRemoved/PassPawnsToWorld，它们会改写游戏事实。
                Find.TickManager.RemoveAllFromMap(map);
                Find.SoundRoot.sustainerManager.EndAllInMap(map);
                map.weatherManager.EndAllSustainers();
                // 历史条目的 Thing 目标经 MapHeld 找图，必须在清除 Thing 的索引前摘除。
                Find.Archive.Notify_MapRemoved(map);
                foreach (Thing thing in map.spawnedThings)
                {
                    if (thing.def.receivesSignals) Find.SignalManager.DeregisterReceiver(thing);
                    thing.ForceSetStateToUnspawned();
                }
                foreach (Room room in map.regionGrid.AllRooms)
                    foreach (District district in room.Districts) district.Notify_MyMapRemoved();
                foreach (Region region in map.regionGrid.AllRegions_NoRebuild_InvalidAllowed)
                    region.Notify_MyMapRemoved();
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
                Find.Storyteller.incidentQueue.Notify_MapRemoved(map);
                Find.Scenario.MapRemoved(map);
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
                RegisterExternalReferences(loaded);
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
                if (added)
                {
                    // FinalizeLoading 可能已生成部分 Thing/Region；复用唯一卸图入口清理索引。
                    if (TryUnload(loaded, preserveExternalEvents: false))
                        loaded.spawnedThings.Clear();
                    else
                        Log.Error($"[DeltaV] Failed to remove partially restored map {UniverseManager.MapTag(loaded)}");
                }
                else loaded?.Dispose();
                return false;
            }
            finally { File.Delete(path); }
        }

        private static void RegisterExternalReferences(Map restoringMap)
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
            if (ModsConfig.IdeologyActive && Find.IdeoManager != null)
                foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading) Register(ideo);
            foreach (Quest quest in Find.QuestManager.QuestsListForReading) Register(quest);
            foreach (WorldObject obj in Find.WorldObjects.AllWorldObjects) Register(obj);
            foreach (PocketMapParent parent in Find.World.pocketMaps) Register(parent);
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead) Register(pawn);
            var things = new List<Thing>();
            foreach (Map other in Find.Maps)
            {
                if (other == restoringMap) continue;
                Register(other);
                ThingOwnerUtility.GetAllThingsRecursively(other, things);
                foreach (Thing thing in things) Register(thing);
            }
        }
    }
}
