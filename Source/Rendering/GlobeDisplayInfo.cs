using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Numerical display facts retained only while the source Map is unloaded.</summary>
    public sealed class GlobeDisplaySummary : IExposable
    {
        public float area = -1f, environmentArea;
        public float navigationRadius;
        public Vector2 environmentCenter;
        public bool wreck;
        public bool natural;
        public GlobeArmament armament = GlobeArmament.Unknown;
        public List<GlobeShipSummary> ships = new List<GlobeShipSummary>();
        public void ExposeData()
        {
            Scribe_Values.Look(ref area, "area", -1f);
            Scribe_Values.Look(ref environmentArea, "environmentArea");
            Scribe_Values.Look(ref navigationRadius, "navigationRadius");
            Scribe_Values.Look(ref environmentCenter, "environmentCenter");
            Scribe_Values.Look(ref wreck, "wreck");
            Scribe_Values.Look(ref natural, "natural");
            Scribe_Values.Look(ref armament, "armament", GlobeArmament.Unknown);
            Scribe_Collections.Look(ref ships, "ships", LookMode.Deep);
            ships ??= new List<GlobeShipSummary>();
        }
    }

    public sealed class GlobeShipSummary : IExposable
    {
        public int key;
        public bool shuttle, station;
        public string contentType;
        public GlobeArmament armament = GlobeArmament.Unknown;
        public string label;
        public Faction faction;
        public Vector2 localCenter;
        public float localAngle, area;
        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref shuttle, "shuttle");
            Scribe_Values.Look(ref station, "station");
            Scribe_Values.Look(ref contentType, "contentType");
            Scribe_Values.Look(ref armament, "armament", GlobeArmament.Unknown);
            Scribe_Values.Look(ref label, "label");
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref localCenter, "localCenter");
            Scribe_Values.Look(ref localAngle, "localAngle");
            Scribe_Values.Look(ref area, "area");
        }
    }

    /// <summary>One entry drives visibility, drawing, labels and hit testing. Cells are map-local.</summary>
    internal sealed class GlobeDisplayEntry
    {
        internal SpaceObject owner;
        internal GlobeShipSummary ship;
        internal List<IntVec3> cells;
        internal Vector2 mapAnchor;
        internal Vector2 localCenter;
        internal float area, projectionSide;
        internal bool wreck, native, craft, station;
        internal bool natural;
        internal GlobeArmament armament = GlobeArmament.Unknown;
        internal string contentType;
        internal GlobeProjectileVisual projectile;
        internal bool Minimal => projectile != null || owner.IsScattered;
        internal string ContentType => ship != null ? ship.contentType : contentType ?? owner.contentTypeId;
        internal GlobeArmament Armament => ship?.armament ?? armament;
        internal string Label => projectile?.Def.label ?? ship?.label ?? owner.label;
        internal Faction Faction => ship != null ? ship.faction : owner.faction;
        internal bool AlwaysVisible => native || (ship != null && ship.faction == Faction.OfPlayerSilentFail);
        internal SpacePose Pose => ship != null
            ? SpaceTransform.RigidPointPose(owner.pose, ship.localCenter, ship.localAngle)
            : SpaceTransform.RigidPointPose(owner.pose, localCenter, 0f);
    }

    internal static class GlobeDisplayRules
    {
        internal static float ProjectedSize(float area, float pixelsPerCell) =>
            area < 0f ? -1f : (float)Math.Sqrt(area) * pixelsPerCell;
        internal static bool Visible(float area, float pixelsPerCell, bool always) =>
            always || area < 0f || ProjectedSize(area, pixelsPerCell) >= 0.5f;
        // One zoom transition for the whole view; small members must not keep oversized
        // symbols on top of nearby projections. The floor lets shuttle-only views reach it.
        internal static bool UseProjection(float referenceSideCells, float pixelsPerCell, bool available) =>
            available && Math.Max(16f, referenceSideCells) * pixelsPerCell >= 8f;
        internal static bool ShowLabel(float area, float pixelsPerCell, bool wreck) =>
            !wreck && (area < 0f || ProjectedSize(area, pixelsPerCell) >= 2f);
        internal static bool IsWreck(bool pawn, bool equipment) => !pawn && !equipment;
        internal static bool IsTerrain(TerrainDef terrain) => terrain != null
            && terrain.defName != "Space" && terrain.defName != "DeltaV_Void";

        internal static HashSet<IntVec3> TerrainCells(IEnumerable<(IntVec3 cell, TerrainDef terrain)> entries)
        {
            var cells = new HashSet<IntVec3>();
            foreach (var entry in entries) if (IsTerrain(entry.terrain)) cells.Add(entry.cell);
            return cells;
        }

        internal static int Measure(MapPrefabDocument doc)
        {
            var entries = new List<(IntVec3, TerrainDef)>();
            foreach (var terrain in doc.terrain)
                foreach (CellRect rect in terrain.rects)
                    foreach (IntVec3 cell in rect) entries.Add((cell, terrain.def));
            foreach (var foundation in doc.foundations) entries.Add((foundation.pos, foundation.def));
            return TerrainCells(entries).Count;
        }
        internal static bool HasComp(ThingDef def, Type comp) => def?.comps != null
            && def.comps.Any(p => p.compClass != null && comp.IsAssignableFrom(p.compClass));
        internal static bool HasEquipment(IEnumerable<ThingDef> defs) => defs.Any(def =>
            HasComp(def, typeof(CompSpaceWeapon)) || ThrusterPower.IsThrusterDef(def)
            || HasComp(def, typeof(CompSpaceShuttle)) || HasComp(def, typeof(CompDeltaVPilotConsole))
            || (def.thingClass != null && typeof(Building_Turret).IsAssignableFrom(def.thingClass)));
    }

    /// <summary>Per-game cache; map structure invalidation is shared with physics and ship roles.</summary>
    internal sealed class GlobeDisplayInfo
    {
        private sealed class MapInfo
        {
            internal int version;
            internal bool populationDirty, equipment;
            internal GlobeDisplaySummary summary;
            internal List<GlobeDisplayEntry> entries;
        }
        private readonly Dictionary<Map, MapInfo> maps = new Dictionary<Map, MapInfo>();
        internal void Forget(Map map) { if (map != null) maps.Remove(map); }
        internal void Clear() => maps.Clear();
        internal void NotifyPopulationChanged(Map map)
        {
            if (map != null && maps.TryGetValue(map, out MapInfo info)) info.populationDirty = true;
        }

        private static void RefreshPopulation(Map map, MapInfo info)
        {
            info.summary.wreck = GlobeDisplayRules.IsWreck(
                map.mapPawns.AllPawns.Any(p => !p.Dead), info.equipment);
            foreach (var entry in info.entries) if (entry.ship == null) entry.wreck = info.summary.wreck;
            info.populationDirty = false;
        }

        private MapInfo ReadMap(SpaceObject obj)
        {
            Map map = obj.map;
            if (maps.TryGetValue(map, out MapInfo cached)
                && !SubstructureOwnership.CacheNeedsRefresh(cached.version, map))
            {
                if (cached.populationDirty) RefreshPopulation(map, cached);
                return cached;
            }
            var terrain = new HashSet<IntVec3>();
            foreach (IntVec3 cell in map.AllCells)
                if (GlobeDisplayRules.IsTerrain(map.terrainGrid.TerrainAt(cell))
                    || GlobeDisplayRules.IsTerrain(map.terrainGrid.FoundationAt(cell))) terrain.Add(cell);
            var environment = new HashSet<IntVec3>(terrain);
            var entries = new List<GlobeDisplayEntry>();
            var summary = new GlobeDisplaySummary { area = terrain.Count };
            foreach (ShipBase ship in obj.Ships)
            {
                var hull = new HashSet<IntVec3>(ship.HullCells);
                var cells = hull.Where(terrain.Contains).ToList();
                if (ship is ShuttleShip && cells.Count == 0) cells.AddRange(hull);
                environment.ExceptWith(hull);
                var member = new GlobeShipSummary
                {
                    key = ship.Identity.key, shuttle = ship is ShuttleShip,
                    label = ship.Label, faction = ship.Faction,
                    localCenter = SpaceTransform.MapToShipLocal(map, ship.HullCenter),
                    localAngle = ship.FrameAngle, area = cells.Count,
                    armament = ship.Weapons.Any(w => !w.WeaponProps.manualOnly && !w.WeaponProps.tractor)
                        || map.listerThings.AllThings.OfType<Building_Turret>().Any(b => ship.Contains(b))
                        ? GlobeArmament.Armed : GlobeArmament.Unarmed
                };
                summary.ships.Add(member);
                entries.Add(new GlobeDisplayEntry { owner = obj, ship = member, area = member.area,
                    cells = cells, mapAnchor = ship.HullCenter, craft = true,
                    projectionSide = ShipSilhouette.AnchoredSide(cells, ship.HullCenter) / (float)ShipSilhouette.AnchoredPixelsPerCell });
            }
            if (summary.area == 0f) summary.area = summary.ships.Sum(s => s.area);
            summary.environmentArea = environment.Count;
            summary.natural = environment.Any(c => map.terrainGrid.TerrainAt(c)?.natural == true);
            summary.armament = map.listerThings.AllThings.OfType<Building>().Any(b =>
                environment.Contains(b.Position) && (b is Building_Turret
                    || b.TryGetComp<CompSpaceWeapon>() is CompSpaceWeapon w
                        && !w.WeaponProps.manualOnly && !w.WeaponProps.tractor))
                ? GlobeArmament.Armed : GlobeArmament.Unarmed;
            Vector2 environmentAnchor = environment.Count > 0
                ? SpaceTransform.BoundsCenterOfCells(environment) : SpaceTransform.ShipLocalOrigin(map);
            summary.environmentCenter = SpaceTransform.MapToShipLocal(map, environmentAnchor);
            bool equipment = GlobeDisplayRules.HasEquipment(map.listerThings.AllThings
                    .Where(t => t is Building && t.Spawned && !t.Destroyed).Select(t => t.def));
            bool native = obj is PoiState poi && poi.kind == PoiKind.NativeParent;
            if (environment.Count > 0 || summary.ships.Count == 0 || native)
                entries.Add(new GlobeDisplayEntry { owner = obj, area = environment.Count,
                    cells = environment.ToList(), mapAnchor = environmentAnchor,
                    localCenter = summary.environmentCenter,
                    natural = summary.natural, armament = summary.armament,
                    native = native, projectionSide = ShipSilhouette.AnchoredSide(environment, environmentAnchor)
                        / (float)ShipSilhouette.AnchoredPixelsPerCell });
            cached = new MapInfo { version = SubstructureOwnership.StructureVersion(map),
                summary = summary, entries = entries, equipment = equipment };
            // Generated class belongs to the principal structure, never every visiting member.
            var principalEntry = entries.OrderByDescending(e => e.area).FirstOrDefault();
            if (principalEntry?.ship != null) principalEntry.ship.contentType = obj.contentTypeId;
            if (IsStationType(obj.contentTypeId))
            {
                // The station's principal structure represents the location; visiting ships
                // remain separate entries in All and never duplicate the station in Stations.
                var principal = entries.OrderByDescending(e => e.area).FirstOrDefault();
                if (principal != null)
                {
                    principal.station = true;
                    if (principal.ship != null) principal.ship.station = true;
                }
            }
            if (native && obj is PoiState nativePoi && nativePoi.nativeParent?.def?.defName == "SpaceSettlement")
                entries.First(e => e.native).station = true;
            RefreshPopulation(map, cached);
            maps[map] = cached;
            return cached;
        }

        internal GlobeDisplaySummary Capture(SpaceObject obj)
        {
            Forget(obj.map); // Capture current facts even when a transfer happened in this tick.
            GlobeDisplaySummary summary = ReadMap(obj).summary;
            summary.navigationRadius = SpaceCollision.OriginBoundsRadiusOf(obj);
            return summary;
        }

        internal IEnumerable<GlobeDisplayEntry> Entries(SpaceObject obj)
        {
            if (!obj.ParticipatesInOverlays) yield break;
            if (obj.Payload?.Recoverable == true)
            {
                yield return new GlobeDisplayEntry { owner = obj, area = 1f };
                yield break;
            }
            if (obj.map != null && !obj.map.Disposed)
            {
                foreach (var entry in ReadMap(obj).entries) yield return entry;
                yield break;
            }
            if (obj is ArchivedSpaceObject archived && archived.globe != null)
            {
                foreach (var ship in archived.globe.ships)
                    yield return new GlobeDisplayEntry { owner = obj, ship = ship, area = ship.area, craft = true, station = ship.station };
                if (archived.globe.environmentArea > 0 || archived.globe.ships.Count == 0)
                    yield return new GlobeDisplayEntry { owner = obj, area = archived.globe.environmentArea,
                        localCenter = archived.globe.environmentCenter,
                        wreck = archived.globe.wreck, natural = archived.globe.natural, armament = archived.globe.armament,
                        station = IsStationType(obj.contentTypeId) && !archived.globe.ships.Any(s => s.station) };
                yield break;
            }
            if (obj is PoiState poiState && poiState.kind == PoiKind.NativeParent)
            {
                if (poiState.nativeParent != null && !poiState.nativeParent.Destroyed)
                    yield return new GlobeDisplayEntry { owner = obj, native = true, area = -1f,
                        station = poiState.nativeParent.def?.defName == "SpaceSettlement" };
                yield break;
            }
            if (obj is PoiState snapshot && snapshot.snapshot != null)
            {
                yield return new GlobeDisplayEntry { owner = obj, area = snapshot.snapshot.GlobeArea,
                    wreck = snapshot.snapshot.GlobeWreck, station = IsStationType(obj.contentTypeId),
                    craft = GlobeFilters.IsCraftType(obj.contentTypeId) };
                yield break;
            }
            float area = PrefabGlobeMeasurements.AverageArea(obj.contentTypeId);
            SectorContentType type = SectorContentTypes.ById(obj.contentTypeId);
            bool craft = type != null && IsUnder(SectorContentTypes.ById("Craft"), type);
            yield return new GlobeDisplayEntry { owner = obj, area = area, craft = craft, station = IsStationType(obj.contentTypeId) };
        }

        /// <summary>
        /// Conservative radius around the SpaceObject origin used to freeze an exterior
        /// navigation anchor. Loaded objects read the collision surface itself; unloaded
        /// representations use the summary or the exact content source without materializing.
        /// </summary>
        internal float NavigationRadius(SpaceObject obj)
        {
            if (obj == null) return 0f;
            if (obj.map != null && !obj.map.Disposed)
                return SpaceCollision.OriginBoundsRadiusOf(obj);
            if (obj is ArchivedSpaceObject archived && archived.globe?.navigationRadius > 0f)
                return archived.globe.navigationRadius;
            if (obj is PoiState marker)
            {
                if (marker.snapshot?.HasSourceSize == true)
                {
                    IntVec3 size = marker.snapshot.SourceMapSize;
                    return new Vector2(size.x, size.z).magnitude * .5f;
                }
                if (marker.kind == PoiKind.NativeParent && marker.nativeParent != null)
                {
                    IntVec3 size = SpaceMapGovernance.GenerationSize(marker.nativeParent);
                    return new Vector2(size.x, size.z).magnitude * .5f;
                }
            }
            float prefab = PrefabGlobeMeasurements.NavigationRadius(obj);
            if (prefab > 0f) return prefab;
            // Unknown third-party passive objects have no materialized collision surface.
            // Staying at least one activation radius away is conservative until one exists.
            return Mathf.Max(32f, DeltaVMod.CurrentSettings?.poiActiveRadius ?? 300f);
        }

        internal static bool IsStationType(string typeId) => typeId != "Asteroid"
            && IsUnder(SectorContentTypes.ById("Station"), SectorContentTypes.ById(typeId));
        private static bool IsUnder(SectorContentType root, SectorContentType type) => root != null
            && (root == type || root.children.Any(child => IsUnder(child, type)));
    }

    /// <summary>Process-lifetime asset measurements. Replacing files never refreshes this table.</summary>
    [StaticConstructorOnStartup]
    internal static class PrefabGlobeMeasurements
    {
        private static readonly Dictionary<string, int> areas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> navigationRadii = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static PrefabGlobeMeasurements()
        {
            List<string> paths;
            try { paths = MapPrefab.ListPrefabFiles(); }
            catch (Exception ex)
            {
                Log.Warning($"[DeltaV] Cannot enumerate prefabs for globe: {ex.Message}");
                return;
            }
            foreach (string path in paths)
            {
                try
                {
                    MapPrefabDocument doc = MapPrefab.LoadRelative(path);
                    string key = path.Replace('\\', '/');
                    areas[key] = GlobeDisplayRules.Measure(doc);
                    navigationRadii[key] = new Vector2(doc.size.x, doc.size.z).magnitude * .5f + .71f;
                }
                catch (Exception ex) { Log.Warning($"[DeltaV] Cannot measure prefab '{path}' for globe: {ex.Message}"); }
            }
        }
        internal static float AverageArea(string typeId)
        {
            SectorContentType type = SectorContentTypes.ById(typeId);
            var paths = type == null ? null : DeltaVMod.CurrentSettings?.EffectiveSectorRule(type)?.prefabPaths;
            if (paths == null || paths.Count == 0) return -1f;
            float sum = 0f;
            var unique = paths.Select(path => path?.Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string path in unique)
            {
                if (path == null || !areas.TryGetValue(path, out int area)) return -1f;
                sum += area;
            }
            return sum / unique.Count;
        }

        internal static float NavigationRadius(SpaceObject obj)
        {
            if (obj == null) return -1f;
            string exact = obj.sourcePrefabPath?.Replace('\\', '/');
            if (!exact.NullOrEmpty() && navigationRadii.TryGetValue(exact, out float radius))
                return radius;
            SectorContentType type = SectorContentTypes.ById(obj.contentTypeId);
            var paths = type == null ? null : DeltaVMod.CurrentSettings?.EffectiveSectorRule(type)?.prefabPaths;
            if (paths == null || paths.Count == 0) return -1f;
            float maximum = -1f;
            foreach (string path in paths.Select(p => p?.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (path == null || !navigationRadii.TryGetValue(path, out radius)) return -1f;
                maximum = Mathf.Max(maximum, radius);
            }
            return maximum;
        }
    }
}
