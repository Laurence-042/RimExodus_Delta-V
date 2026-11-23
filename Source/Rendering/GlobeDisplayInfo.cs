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
        public GlobeArmament armament = GlobeArmament.Unknown;
        public string label;
        public Faction faction;
        public Vector2 localCenter;
        public float localAngle, area;
        internal GlobeShipSummary Snapshot() => (GlobeShipSummary)MemberwiseClone();
        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref shuttle, "shuttle");
            Scribe_Values.Look(ref station, "station");
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
        internal GlobeProjectileVisual projectile;
        internal bool Minimal => projectile != null || owner.IsScattered;
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

        internal static bool IsNaturalRock(ThingDef def) => def?.building?.isNaturalRock == true;

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
            var occupied = TerrainCells(entries);
            foreach (var entry in doc.thingEntries)
                if (IsNaturalRock(entry.def))
                    occupied.UnionWith(GenAdj.OccupiedRect(entry.pos,
                        entry.def.rotatable ? entry.rotation : entry.def.defaultPlacingRot, entry.def.Size));
            return occupied.Count;
        }
        internal static bool HasComp(ThingDef def, Type comp) => def?.comps != null
            && def.comps.Any(p => p.compClass != null && comp.IsAssignableFrom(p.compClass));
        internal static bool HasEquipment(IEnumerable<ThingDef> defs) => defs.Any(IsEquipment);
        internal static bool IsWeapon(CompSpaceWeapon weapon) => !weapon.WeaponProps.manualOnly && !weapon.WeaponProps.tractor;
        internal static bool IsEquipment(ThingDef def) =>
            HasComp(def, typeof(CompSpaceWeapon)) || ThrusterPower.IsThrusterDef(def)
            || HasComp(def, typeof(CompSpaceShuttle)) || HasComp(def, typeof(CompDeltaVPilotConsole))
            || (def.thingClass != null && typeof(Building_Turret).IsAssignableFrom(def.thingClass));
    }

    /// <summary>Per-game cache; map structure invalidation is shared with physics and ship roles.</summary>
    internal sealed class GlobeDisplayInfo
    {
        private sealed class Member
        {
            internal HashSet<IntVec3> domain;
            internal int revision;
            internal HashSet<IntVec3> hull = new HashSet<IntVec3>();
            internal readonly HashSet<IntVec3> visible = new HashSet<IntVec3>();
            internal CellRect shuttleFootprint;
            internal GlobeDisplayEntry entry;
        }

        private sealed class MapInfo
        {
            internal SpaceObject owner;
            internal IDisposable structureChanges;
            internal bool populationDirty = true, updateMembers = true, allHulls;
            internal readonly Dictionary<DriveSubject, Member> members = new Dictionary<DriveSubject, Member>();
            internal readonly Dictionary<IntVec3, HashSet<Member>> covered = new Dictionary<IntVec3, HashSet<Member>>();
            internal readonly HashSet<IntVec3> dirtyCells = new HashSet<IntVec3>();
            internal readonly HashSet<IntVec3> visible = new HashSet<IntVec3>();
            internal readonly HashSet<IntVec3> environment = new HashSet<IntVec3>();
            internal readonly HashSet<IntVec3> natural = new HashSet<IntVec3>();
            internal readonly HashSet<IntVec3> armed = new HashSet<IntVec3>();
            internal GlobeDisplayEntry environmentEntry;
            internal readonly List<GlobeDisplayEntry> entries = new List<GlobeDisplayEntry>();
            internal readonly GlobeDisplaySummary summary = new GlobeDisplaySummary();

            internal void Record(ShipStructureBatch batch)
            {
                foreach (ShipStructureChange change in batch.Changes)
                {
                    if (change.Aspect != ShipStructureAspect.Terrain) updateMembers = true;
                    if (change.Aspect == ShipStructureAspect.Representation) allHulls = true;
                    if (change.Cell is IntVec3 cell) dirtyCells.Add(cell);
                    if (change.Before is CellRect before) dirtyCells.UnionWith(before);
                    if (change.After is CellRect after) dirtyCells.UnionWith(after);
                    if (change.Aspect == ShipStructureAspect.Membership) populationDirty = true;
                }
            }

            internal void SetHull(Member member, HashSet<IntVec3> next)
            {
                foreach (IntVec3 cell in member.hull)
                {
                    if (next.Contains(cell)) continue;
                    member.visible.Remove(cell);
                    var membersAtCell = covered[cell];
                    membersAtCell.Remove(member);
                    if (membersAtCell.Count == 0) covered.Remove(cell);
                    dirtyCells.Add(cell);
                }
                foreach (IntVec3 cell in next)
                {
                    if (member.hull.Contains(cell)) continue;
                    if (!covered.TryGetValue(cell, out var membersAtCell))
                        covered.Add(cell, membersAtCell = new HashSet<Member>());
                    membersAtCell.Add(member);
                    dirtyCells.Add(cell);
                }
                member.hull = next;
            }
        }

        private readonly Dictionary<Map, MapInfo> maps = new Dictionary<Map, MapInfo>();
        internal void Forget(Map map)
        {
            if (map != null && maps.Remove(map, out MapInfo info)) info.structureChanges?.Dispose();
        }
        internal void Clear()
        {
            foreach (MapInfo info in maps.Values) info.structureChanges?.Dispose();
            maps.Clear();
        }
        internal void NotifyPopulationChanged(Map map)
        {
            if (map != null && maps.TryGetValue(map, out MapInfo info)) info.populationDirty = true;
        }

        private static float ProjectionSide(IEnumerable<IntVec3> cells, Vector2 center) =>
            ShipSilhouette.AnchoredSide(cells, center) / (float)ShipSilhouette.AnchoredPixelsPerCell;

        private MapInfo ReadMap(SpaceObject obj)
        {
            Map map = obj.map;
            var universe = Current.Game.GetComponent<UniverseManager>();
            var index = universe.ShipStructure;
            var structure = index.For(map);
            if (!maps.TryGetValue(map, out var info) || info.owner != obj)
            {
                Forget(map);
                info = new MapInfo { owner = obj };
                info.dirtyCells.UnionWith(structure.VisibleTerrain);
                info.dirtyCells.UnionWith(structure.RockCells);
                info.structureChanges = index.WatchMap(map, ShipStructureAspect.Foundation | ShipStructureAspect.Terrain
                    | ShipStructureAspect.Ownership | ShipStructureAspect.Membership | ShipStructureAspect.Footprint
                    | ShipStructureAspect.Primary | ShipStructureAspect.Faction | ShipStructureAspect.Representation, info.Record);
                maps.Add(map, info);
            }
            if (!info.updateMembers && info.dirtyCells.Count == 0 && !info.populationDirty) return info;

            var ships = obj.Ships;
            var refresh = new HashSet<Member>();
            var projectionChanges = new HashSet<Member>();
            if (info.updateMembers)
            {
                var remaining = new HashSet<DriveSubject>(ships.Select(ship => ship.Identity));
                foreach (var removed in info.members.Keys.Where(key => !remaining.Contains(key)).ToArray())
                {
                    info.SetHull(info.members[removed], new HashSet<IntVec3>());
                    info.members.Remove(removed);
                    PlanetGlobeRenderer.ReleaseSilhouette(map, removed);
                }
                foreach (ShipBase ship in ships)
                {
                    if (!info.members.TryGetValue(ship.Identity, out var member))
                        info.members.Add(ship.Identity, member = new Member());
                    bool foundation = ship.GetType() == typeof(FoundationShip);
                    var domain = foundation ? ship.HullCells as HashSet<IntVec3> : null;
                    int revision = foundation ? universe.FoundationTopology.DomainRevision(map, domain) : -1;
                    CellRect footprint = ship is ShuttleShip ? ship.Anchor.OccupiedRect() : CellRect.Empty;
                    if (member.entry == null || info.allHulls || member.domain != domain || member.revision != revision
                        || member.shuttleFootprint != footprint || !foundation && ship is not ShuttleShip)
                    {
                        info.SetHull(member, new HashSet<IntVec3>(ship.HullCells));
                        member.domain = domain;
                        member.revision = revision;
                        member.shuttleFootprint = footprint;
                        refresh.Add(member);
                        projectionChanges.Add(member);
                    }
                }
            }

            bool environmentChanged = false;
            foreach (IntVec3 cell in info.dirtyCells)
            {
                if (!cell.InBounds(map)) continue;
                bool visible = structure.IsGlobeCell(cell);
                if (visible) info.visible.Add(cell); else info.visible.Remove(cell);
                bool covered = info.covered.TryGetValue(cell, out var coveredBy);
                if (covered)
                    foreach (Member member in coveredBy)
                    {
                        refresh.Add(member);
                        if (visible ? member.visible.Add(cell) : member.visible.Remove(cell))
                            projectionChanges.Add(member);
                    }
                bool inEnvironment = visible && !covered;
                environmentChanged |= inEnvironment ? info.environment.Add(cell) : info.environment.Remove(cell);
                if (inEnvironment && (structure.IsGlobeRock(cell) || map.terrainGrid.TerrainAt(cell)?.natural == true))
                    info.natural.Add(cell);
                else info.natural.Remove(cell);
                if (inEnvironment && structure.RecordsAt(cell).Any(record => record.Building.Position == cell && record.GlobeArmed))
                    info.armed.Add(cell);
                else info.armed.Remove(cell);
            }

            var summary = info.summary;
            info.entries.Clear();
            summary.ships.Clear();
            foreach (ShipBase ship in ships)
            {
                Member member = info.members[ship.Identity];
                Vector2 anchor = ship.HullCenter;
                if (projectionChanges.Contains(member) || member.entry.mapAnchor != anchor)
                {
                    var cells = ship is ShuttleShip && member.visible.Count == 0 ? member.hull.ToList() : member.visible.ToList();
                    if (member.entry == null || member.entry.mapAnchor != anchor || !member.entry.cells.SequenceEqual(cells))
                    {
                        if (cells.Count == 0) PlanetGlobeRenderer.ReleaseSilhouette(map, ship.Identity);
                        member.entry = new GlobeDisplayEntry { owner = obj,
                            ship = new GlobeShipSummary { key = ship.Identity.key, shuttle = ship is ShuttleShip, area = cells.Count },
                            cells = cells, area = cells.Count, mapAnchor = anchor, craft = true,
                            projectionSide = ProjectionSide(cells, anchor) };
                    }
                    refresh.Add(member);
                }
                if (refresh.Contains(member))
                    member.entry.ship.armament = ship.Weapons.Any(GlobeDisplayRules.IsWeapon) || ship.StructuralState?.HasTurret == true
                        ? GlobeArmament.Armed : GlobeArmament.Unarmed;
                GlobeShipSummary facts = member.entry.ship;
                facts.label = ship.Label;
                facts.faction = ship.Faction;
                facts.localCenter = SpaceTransform.MapToShipLocal(map, anchor);
                facts.localAngle = ship.FrameAngle;
                facts.station = member.entry.station = false;
                info.entries.Add(member.entry);
                summary.ships.Add(facts);
            }
            summary.area = info.visible.Count > 0 ? info.visible.Count : summary.ships.Sum(ship => ship.area);
            summary.environmentArea = info.environment.Count;
            summary.natural = info.natural.Count != 0;
            summary.armament = info.armed.Count != 0 ? GlobeArmament.Armed : GlobeArmament.Unarmed;
            bool native = obj is PoiState { kind: PoiKind.NativeParent };
            if (info.environmentEntry == null || environmentChanged)
            {
                if (info.environment.Count == 0) PlanetGlobeRenderer.ReleaseSilhouette(map, default);
                Vector2 anchor = info.environment.Count > 0 ? SpaceTransform.BoundsCenterOfCells(info.environment)
                    : SpaceTransform.ShipLocalOrigin(map);
                summary.environmentCenter = SpaceTransform.MapToShipLocal(map, anchor);
                info.environmentEntry = new GlobeDisplayEntry { owner = obj, area = info.environment.Count,
                    cells = info.environment.ToList(), mapAnchor = anchor, localCenter = summary.environmentCenter,
                    native = native, projectionSide = ProjectionSide(info.environment, anchor) };
            }
            var environmentEntry = info.environmentEntry;
            environmentEntry.natural = summary.natural;
            environmentEntry.armament = summary.armament;
            environmentEntry.station = native && obj is PoiState nativePoi && nativePoi.nativeParent?.def?.defName == "SpaceSettlement";
            if (info.environment.Count > 0 || summary.ships.Count == 0 || native)
                info.entries.Add(environmentEntry);
            if (info.populationDirty)
                summary.wreck = GlobeDisplayRules.IsWreck(map.mapPawns.AllPawns.Any(pawn => !pawn.Dead), structure.All.HasGlobeEquipment);
            environmentEntry.wreck = summary.wreck;
            info.populationDirty = info.updateMembers = info.allHulls = false;
            info.dirtyCells.Clear();
            return info;
        }

        internal GlobeDisplaySummary Capture(SpaceObject obj)
        {
            GlobeDisplaySummary current = ReadMap(obj).summary;
            return new GlobeDisplaySummary { area = current.area, environmentArea = current.environmentArea,
                navigationRadius = SpaceCollision.OriginBoundsRadiusOf(obj), environmentCenter = current.environmentCenter,
                wreck = current.wreck, natural = current.natural, armament = current.armament,
                ships = current.ships.Select(ship => ship.Snapshot()).ToList() };
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
                        wreck = archived.globe.wreck, natural = archived.globe.natural, armament = archived.globe.armament };
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
                    wreck = snapshot.snapshot.GlobeWreck };
                yield break;
            }
            yield return new GlobeDisplayEntry { owner = obj,
                area = PrefabGlobeMeasurements.Area(obj.sourcePrefabPath),
                natural = PrefabGlobeMeasurements.Natural(obj.sourcePrefabPath) };
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

    }

    /// <summary>Process-lifetime asset measurements. Replacing files never refreshes this table.</summary>
    [StaticConstructorOnStartup]
    internal static class PrefabGlobeMeasurements
    {
        private static readonly Dictionary<string, int> areas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> navigationRadii = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> natural = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    if (SectorContentService.PrefabHasNaturalBody(doc)) natural.Add(key);
                }
                catch (Exception ex) { Log.Warning($"[DeltaV] Cannot measure prefab '{path}' for globe: {ex.Message}"); }
            }
        }
        internal static float Area(string path) => !path.NullOrEmpty()
            && areas.TryGetValue(path.Replace('\\', '/'), out int area) ? area : -1f;
        internal static bool Natural(string path) => !path.NullOrEmpty() && natural.Contains(path.Replace('\\', '/'));

        internal static float NavigationRadius(SpaceObject obj)
        {
            if (obj == null) return -1f;
            string exact = obj.sourcePrefabPath?.Replace('\\', '/');
            if (!exact.NullOrEmpty() && navigationRadii.TryGetValue(exact, out float radius))
                return radius;
            return -1f;
        }
    }
}
