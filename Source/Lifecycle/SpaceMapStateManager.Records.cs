using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    public enum SpaceMapStateKind : byte
    {
        Unmaterialized,
        Loaded,
        Archived,
        Deleted
    }

    public enum PlayerContactKind : byte
    {
        PawnPresence,
        ShipPresence,
        Docking,
        Trade,
        Mission,
        CargoTransfer,
        StructureChange,
        ManualRetention
    }

    public readonly struct SpaceMapRetentionInfo
    {
        public readonly SpaceMapStateKind state;
        public readonly int lastMeaningfulPlayerContactTick;
        public readonly int exactRetentionExpiryTick;
        public readonly int compressedBytes;
        public readonly bool manualKeepExact;
        public readonly bool dormant;
        public readonly bool coldRebuild;
        public readonly string blockReason;

        internal SpaceMapRetentionInfo(SpaceMapStateKind state, int lastContact, int expiry,
            int compressedBytes, bool manualKeepExact, bool dormant, bool coldRebuild,
            string blockReason)
        {
            this.state = state;
            lastMeaningfulPlayerContactTick = lastContact;
            exactRetentionExpiryTick = expiry;
            this.compressedBytes = compressedBytes;
            this.manualKeepExact = manualKeepExact;
            this.dormant = dormant;
            this.coldRebuild = coldRebuild;
            this.blockReason = blockReason;
        }
    }

    public sealed partial class SpaceMapStateManager
    {
        internal sealed class MapRetentionRecord : IExposable
        {
            public int objectId;
            public int firstMaterializedTick = -1;
            public int lastMeaningfulPlayerContactTick = -1;
            public bool manualKeepExact;
            public int compressedBytes;

            public void ExposeData()
            {
                Scribe_Values.Look(ref objectId, "objectId");
                Scribe_Values.Look(ref firstMaterializedTick, "firstMaterializedTick", -1);
                Scribe_Values.Look(ref lastMeaningfulPlayerContactTick,
                    "lastMeaningfulPlayerContactTick", -1);
                Scribe_Values.Look(ref manualKeepExact, "manualKeepExact");
                Scribe_Values.Look(ref compressedBytes, "compressedBytes");
            }
        }

        private List<MapRetentionRecord> retentionRecords = new List<MapRetentionRecord>();
        [Unsaved] private Dictionary<int, MapRetentionRecord> retentionByObject;

        internal void ExposeData()
        {
            Scribe_Collections.Look(ref retentionRecords, "mapRetentionRecords", LookMode.Deep);
            retentionRecords ??= new List<MapRetentionRecord>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) RebuildRetentionIndexes();
        }

        private void RebuildRetentionIndexes()
        {
            retentionRecords.RemoveAll(record => record == null || record.objectId <= 0);
            retentionByObject = retentionRecords.GroupBy(record => record.objectId)
                .ToDictionary(group => group.Key, group => group.Last());
            retentionRecords = retentionByObject.Values.OrderBy(record => record.objectId).ToList();
            foreach (SpaceObject obj in universe.objects) NotifyObjectRegistered(obj);
        }

        private Dictionary<int, MapRetentionRecord> RetentionByObject
        {
            get
            {
                if (retentionByObject == null) RebuildRetentionIndexes();
                return retentionByObject;
            }
        }

        private MapRetentionRecord RecordFor(SpaceObject obj, bool create)
        {
            if (obj == null || obj.objectId <= 0 || obj.IsTransient) return null;
            if (RetentionByObject.TryGetValue(obj.objectId, out MapRetentionRecord record)) return record;
            if (!create) return null;
            record = new MapRetentionRecord
            {
                objectId = obj.objectId,
                firstMaterializedTick = obj.map != null ? CurrentGameTick : -1
            };
            retentionRecords.Add(record);
            RetentionByObject.Add(record.objectId, record);
            return record;
        }

        private static int CurrentGameTick => Find.TickManager?.TicksGame ?? 0;

        internal void NotifyObjectRegistered(SpaceObject obj)
        {
            MapRetentionRecord record = RecordFor(obj, create: true);
            if (record != null && obj.map != null && record.firstMaterializedTick < 0)
                record.firstMaterializedTick = CurrentGameTick;
        }

        internal void NotifyRepresentationChanged(SpaceObject previous, SpaceObject current)
        {
            MapRetentionRecord record = RecordFor(current, create: true);
            if (record == null) return;
            if (current.map != null && record.firstMaterializedTick < 0)
                record.firstMaterializedTick = CurrentGameTick;
            if (current is ArchivedSpaceObject archive)
                record.compressedBytes = archive.mapData == null ? 0 : archive.mapData.Length * sizeof(char);
        }

        public SpaceMapStateKind StateOf(SpaceObject obj)
        {
            if (obj == null) return SpaceMapStateKind.Deleted;
            if (obj is ArchivedSpaceObject) return SpaceMapStateKind.Archived;
            if (obj.map != null) return SpaceMapStateKind.Loaded;
            return SpaceMapStateKind.Unmaterialized;
        }

        public SpaceMapStateKind StateOf(int objectId)
        {
            SpaceObject obj = universe.ObjectById(objectId);
            return obj != null ? StateOf(obj) : SpaceMapStateKind.Deleted;
        }

        private void ReceivePlayerMapActivity(Map map, Thing source, PlayerContactKind kind)
        {
            if (map == null || map.Disposed || !Find.Maps.Contains(map)) return;
            if (source != null && source.Destroyed) return;
            SpaceObject host = universe.FrameOf(map);
            if (host == null || host.map != map) return;
            RecordPlayerContact(host.objectId, kind);
        }

        internal void RecordPlayerContact(int objectId, PlayerContactKind kind)
        {
            SpaceObject obj = universe.ObjectById(objectId);
            MapRetentionRecord record = RecordFor(obj, create: true);
            if (record == null) return;
            record.lastMeaningfulPlayerContactTick = CurrentGameTick;
        }

        public void SetManualKeepExact(int objectId, bool keep)
        {
            MapRetentionRecord record = RecordFor(universe.ObjectById(objectId), create: true);
            if (record == null) return;
            record.manualKeepExact = keep;
            if (keep) RecordPlayerContact(objectId, PlayerContactKind.ManualRetention);
        }

        private int RetentionAnchor(MapRetentionRecord record)
            => Math.Max(record?.firstMaterializedTick ?? -1,
                record?.lastMeaningfulPlayerContactTick ?? -1);

        private int ExactRetentionTicks => Math.Max(0,
            (DeltaVMod.CurrentSettings?.exactMapRetentionDays ?? 60) * GenDate.TicksPerDay);

        internal bool ExactRetentionExpired(SpaceObject obj)
        {
            MapRetentionRecord record = RecordFor(obj, create: false);
            if (record == null || record.manualKeepExact) return false;
            int anchor = RetentionAnchor(record);
            return anchor >= 0 && CurrentGameTick - anchor >= ExactRetentionTicks;
        }

        public SpaceMapRetentionInfo RetentionInfo(int objectId)
        {
            SpaceObject obj = universe.ObjectById(objectId);
            MapRetentionRecord record = RecordFor(obj, create: false);
            int anchor = RetentionAnchor(record);
            int expiry = anchor < 0 ? -1 : anchor + ExactRetentionTicks;
            string block = PendingTransitionName(objectId) != null ? "transition in progress"
                : record?.manualKeepExact == true ? "manual exact retention"
                : obj != null && universe.Missions.Protects(obj) ? "active mission"
                : obj?.map == null ? null
                : SpaceMapGovernance.ColdRetirementBlockReason(obj.map, universe);
            return new SpaceMapRetentionInfo(StateOf(obj),
                record?.lastMeaningfulPlayerContactTick ?? -1, expiry,
                record?.compressedBytes ?? 0, record?.manualKeepExact == true,
                obj?.map != null && IsDormant(obj.map),
                obj?.map == null && obj?.externalPrefabGenerationCompleted == true,
                block);
        }

        private void CommitRetirement(SpaceObject obj, string reason)
        {
            MapRetentionRecord record = RecordFor(obj, create: false);
            universe.UnregisterObject(obj);
            if (record != null)
            {
                retentionRecords.Remove(record);
                RetentionByObject.Remove(record.objectId);
            }
        }

        private void ResetPersistentState()
        {
            retentionRecords.Clear();
            retentionByObject?.Clear();
        }

        internal void ClearPersistentStateForSceneReset() => ResetPersistentState();
    }
}
