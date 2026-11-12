using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using RimExodus.Maps;
using RimWorld;
using Verse;

namespace DeltaV
{
    public sealed class ShipPlanZone : DefModExtension
    {
        public string module;
    }

    public sealed class PlaceWorker_ShipPlanMarker : PlaceWorker_PrefabMarker
    {
        public override bool IsBuildDesignatorVisible(BuildableDef def) => ShipPlannerService.Visible;

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map,
            Thing thingToIgnore = null, Thing thing = null)
        {
            AcceptanceReport authoring = base.AllowsPlacing(checkingDef, loc, rot, map, thingToIgnore, thing);
            if (!authoring.Accepted) return authoring;
            if (!ShipPlannerService.Visible)
                return new AcceptanceReport("DeltaV_SettingsShowShipPlannerTip".Translate());
            return Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) != null &&
                SubstructureOwnership.FoundationAt(map, loc)?.defName == "Substructure"
                ? AcceptanceReport.WasAccepted : new AcceptanceReport("DeltaV_ShipPlanOrbit".Translate());
        }
    }

    public sealed class CompProperties_ShipPlanner : CompProperties
    {
        public CompProperties_ShipPlanner() => compClass = typeof(CompShipPlanner);
    }

    internal enum ShipPlanTask { Balanced, Transport, Production, Escort }

    public sealed class CompShipPlanner : ThingComp
    {
        private int width;
        private ShipPlanTask task;
        private ShipPlanUndo undo;
        private UniverseManager Universe => Current.Game?.GetComponent<UniverseManager>();
        internal static bool IsAuthoringMarker(Thing thing) =>
            thing.TryGetComp<CompShipPlanner>() != null || thing.def.GetModExtension<ShipPlanZone>() != null;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref width, "corridorWidth");
            Scribe_Values.Look(ref task, "mission", ShipPlanTask.Balanced);
            Scribe_Deep.Look(ref undo, "shellBeforePreview");
        }

        public override string CompInspectStringExtra() => ShipPlannerService.Visible && parent.Spawned
            ? Universe?.ShipPlanner.ProgressText(this) : null;

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!ShipPlannerService.Visible || parent.Faction != Faction.OfPlayer || Universe?.FrameOf(parent.Map) == null) yield break;
            if (undo != null)
            {
                yield return Command("DeltaV_ShipPlanUndo", () => Run(() => undo.Restore(parent, false, out undo)));
                yield return Command("DeltaV_ShipPlanExport", () => Run(Export));
                yield break;
            }
            if (Universe.ShipPlanner.IsPending(this))
            {
                yield return Command("DeltaV_ShipPlanCancel", () => Universe.ShipPlanner.Cancel(this));
                yield break;
            }
            yield return Command("DeltaV_ShipPlanWidth", () => Find.WindowStack.Add(new FloatMenu(
                Enumerable.Range(0, 4).Select(w => new FloatMenuOption(WidthLabel(w), () =>
                { if (ShipPlannerService.Visible) width = w; })).ToList())), WidthLabel(width));
            yield return Command("DeltaV_ShipPlanTask", () => Find.WindowStack.Add(new FloatMenu(
                Enum.GetValues(typeof(ShipPlanTask)).Cast<ShipPlanTask>().Select(value =>
                    new FloatMenuOption(TaskLabel(value), () => { if (ShipPlannerService.Visible) task = value; }))
                    .ToList())), TaskLabel(task));
            yield return Command("DeltaV_ShipPlanStart", () => Run(() =>
                Universe.ShipPlanner.Start(Universe, this, width, task)));
            yield return Command("DeltaV_ShipPlanRemove", () => parent.DeSpawn());
        }

        private static string WidthLabel(int value) => value == 0 ? "DeltaV_ShipPlanAuto".Translate().ToString()
            : "DeltaV_ShipPlanCells".Translate(value).ToString();

        private static string TaskLabel(ShipPlanTask value) => ("DeltaV_ShipPlanTask" + value).Translate().ToString();

        private Command_Action Command(string key, Action action, string suffix = null) => new Command_Action
        {
            defaultLabel = key.Translate().ToString() + (suffix == null ? "" : ": " + suffix),
            defaultDesc = (key + "Desc").Translate(), icon = parent.def.uiIcon,
            action = () => { if (ShipPlannerService.Visible) action(); }
        };

        internal void Apply(ShipPlanUndo source, string file, XElement result, Action<string> stage)
        {
            if (!ShipPlannerService.Visible || !parent.Spawned || parent.Faction != Faction.OfPlayer ||
                undo != null || !source.Matches(parent))
                throw new InvalidOperationException("DeltaV_ShipPlanChanged".Translate().ToString());
            MapPrefabDocument doc = MapPrefabDocument.Load(file);
            Map map = parent.Map;
            IntVec3 origin = source.Origin;
            var shell = new HashSet<IntVec3>(source.cells);
            var fitted = doc.foundations.Select(f => origin + f.pos).ToHashSet();
            var removed = shell.Except(fitted).ToHashSet();
            var retained = source.Markers(parent).Except(source.zones).ToHashSet();
            int cutLimit = Math.Min(60, shell.Count / 12);
            if (result.Name != "ShipPlanResult" || result.Attribute("layout")?.Value != "fixed-corridor" ||
                result.Element("Rim") is not XElement rimNode)
                throw new InvalidDataException("DeltaV_ShipPlanBadResult".Translate().ToString());
            var rim = rimNode.Elements("Cell").Select(c => origin +
                new IntVec3((int)c.Attribute("x"), 0, (int)c.Attribute("z"))).ToHashSet();
            if (!string.IsNullOrEmpty(doc.mapState) || doc.thingEntries.Count == 0 ||
                !fitted.Contains(parent.Position) || !fitted.IsSubsetOf(shell) || !rim.IsSubsetOf(removed) ||
                rim.Count > cutLimit || removed.Count - rim.Count > cutLimit ||
                rim.Any(c => GenAdj.CardinalDirections.All(d => shell.Contains(c + d))) ||
                removed.Any(c => !source.Trimmable(map,c)) ||
                doc.roofs.Any(r => !fitted.Contains(origin + r.pos)))
                throw new InvalidDataException("DeltaV_ShipPlanBadResult".Translate().ToString());
            var connected = new List<IntVec3>();
            SubstructureOwnership.CollectConnected(map,parent.Position,connected,fitted.Contains,fitted.Count);
            if (connected.Count != fitted.Count)
                throw new InvalidDataException("DeltaV_ShipPlanBadResult".Translate().ToString());
            var exhaustCells = new HashSet<IntVec3>();
            foreach (MapPrefabThingEntry entry in doc.thingEntries)
            {
                IntVec3 cell = origin + entry.pos;
                CellRect occupied = GenAdj.OccupiedRect(cell, entry.def.rotatable ? entry.rotation :
                    entry.def.defaultPlacingRot, entry.def.Size);
                if (entry.def.category != ThingCategory.Building || !occupied.All(fitted.Contains) ||
                    occupied.Any(c => c.GetThingList(map).Any(t => retained.Contains(t) &&
                        GenSpawn.SpawningWipes(entry.def, t.def))))
                    throw new InvalidDataException("DeltaV_ShipPlanBadResult".Translate().ToString());
                if (entry.def.GetCompProperties<CompProperties_GravshipThruster>() is { } thruster)
                {
                    var clearance = new List<IntVec3>();
                    thruster.GetExclusionZone(cell,entry.rotation,ref clearance);
                    exhaustCells.UnionWith(clearance);
                    if (clearance.Any(c => c.InBounds(map) &&
                        (SubstructureOwnership.HasFoundation(map,c) && !removed.Contains(c) ||
                         c.GetThingList(map).Any(t => t.def.blockWind && !(t is Plant)))))
                        throw new InvalidDataException("DeltaV_ShipPlanBadResult".Translate().ToString());
                }
                if (typeof(Building_DockingPort).IsAssignableFrom(entry.def.thingClass))
                    for (int i = 1; i <= 4; i++)
                    {
                        IntVec3 approach = cell + entry.rotation.FacingCell * i;
                        if (!approach.InBounds(map) || SubstructureOwnership.HasFoundation(map, approach) ||
                            approach.GetThingList(map).Any(t => t.def.blockWind))
                            throw new InvalidDataException("DeltaV_ShipPlanBadResult".Translate().ToString());
                    }
            }
            if (removed.Any(c => !rim.Contains(c) && !exhaustCells.Contains(c)))
                throw new InvalidDataException("DeltaV_ShipPlanBadResult".Translate().ToString());
            source.cutouts = removed.OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            source.cutoutColors = source.cutouts.Select(map.terrainGrid.ColorAt).ToList();
            doc.foundations.Clear();
            doc.terrain.Clear();
            doc.noRoof.Clear();
            doc.environment.Clear();
            stage("PrepareMap");
            var roofCells = doc.roofs.Select(r => origin + r.pos).ToHashSet();
            var original = source.Markers(parent).ToHashSet();
            try
            {
                foreach (Thing zone in source.zones) zone.DeSpawn();
                foreach (IntVec3 c in source.cutouts) map.terrainGrid.RemoveFoundation(c, false);
                for (int i = 0; i < source.cells.Count; i++)
                {
                    bool roofed = roofCells.Contains(source.cells[i]);
                    source.SetRoofOrder(map, i, roofed ? 0 : 2);
                    if (!roofed && source.roofs[i] != null)
                        map.roofGrid.SetRoof(source.cells[i], null);
                }
                IntVec3 position = origin - PrefabUtility.GetRoot(IntVec3.Zero, doc.size, Rot4.North);
                stage("PlaceMap");
                PrefabPlacementResult placement = SharedMapBackend.Place(doc, map, position, Faction.OfPlayer,
                    0, Rot4.North, Universe, false, false);
                if (!placement.Success)
                    throw new InvalidDataException(string.Join("\n", placement.Errors));
                stage("VerifyMap");
                if (!source.SameFoundation(parent, true))
                    throw new InvalidDataException("DeltaV_ShipPlanBadResult".Translate().ToString());
                undo = source;
            }
            catch
            {
                stage("Rollback");
                source.Restore(parent, true, out _, original);
                throw;
            }
            Messages.Message("DeltaV_ShipPlanReady".Translate(), parent, MessageTypeDefOf.PositiveEvent, false);
        }

        private void Export()
        {
            string name = "ShipPlan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            SharedMapBackend.Export(parent.Map, name, undo.cells);
            Messages.Message("DeltaV_ShipPlanSaved".Translate(name), parent, MessageTypeDefOf.PositiveEvent, false);
        }

        internal static void Run(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                Messages.Message(error.Message, MessageTypeDefOf.RejectInput, false);
            }
        }
    }

    internal sealed class ShipPlanUndo : IExposable
    {
        internal List<IntVec3> cells;
        internal List<RoofDef> roofs;
        internal List<Thing> zones;
        internal List<int> owners;
        internal List<IntVec3> cutouts;
        internal List<ColorDef> cutoutColors;
        private List<int> roofOrders;
        private int mapId;
        private List<Thing> retained;
        private List<IntVec3> retainedPositions, zonePositions;
        internal IntVec3 Origin => CellRect.FromCellList(cells).Min - new IntVec3(7, 0, 7);

        public void ExposeData()
        {
            Scribe_Collections.Look(ref cells, "shellCells", LookMode.Value);
            Scribe_Collections.Look(ref roofs, "originalRoofs", LookMode.Def);
            Scribe_Collections.Look(ref zones, "zoneMarkers", LookMode.Deep);
            Scribe_Collections.Look(ref retained, "retainedMarkers", LookMode.Reference);
            Scribe_Collections.Look(ref owners, "originalOwners", LookMode.Value);
            Scribe_Collections.Look(ref cutouts, "exhaustCutouts", LookMode.Value);
            Scribe_Collections.Look(ref cutoutColors, "cutoutColors", LookMode.Def);
            Scribe_Collections.Look(ref roofOrders, "originalRoofOrders", LookMode.Value);
            Scribe_Values.Look(ref mapId, "mapId");
            Scribe_Collections.Look(ref retainedPositions, "retainedPositions", LookMode.Value);
            Scribe_Collections.Look(ref zonePositions, "zonePositions", LookMode.Value);
        }

        internal static ShipPlanUndo Capture(Thing controller)
        {
            Map map = controller.Map;
            var result = new ShipPlanUndo { cells = new List<IntVec3>(), mapId = map.uniqueID };
            SubstructureOwnership.CollectConnected(map, controller.Position, result.cells, null, 1601);
            if (result.cells.Count == 0 || result.cells.Count > 1600 ||
                result.cells.Any(c => SubstructureOwnership.FoundationAt(map, c)?.defName != "Substructure"))
                throw new InvalidOperationException("DeltaV_ShipPlanShell".Translate().ToString());
            CellRect bounds = CellRect.FromCellList(result.cells);
            if (bounds.Width > 100 || bounds.Height > 100)
                throw new InvalidOperationException("DeltaV_ShipPlanShell".Translate().ToString());
            var things = result.Things(map).ToList();
            var markers = result.Markers(controller).ToHashSet();
            if (!things.All(markers.Contains) || markers.Count(t => t.TryGetComp<CompShipPlanner>() != null) != 1 ||
                markers.Any(t => t.Faction != null && t.Faction != Faction.OfPlayer))
                throw new InvalidOperationException("DeltaV_ShipPlanEmpty".Translate().ToString());
            result.cells = result.cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            result.roofs = result.cells.Select(map.roofGrid.RoofAt).ToList();
            result.roofOrders = result.cells.Select(c => RoofOrder(map,c)).ToList();
            result.owners = result.cells.Select(SubstructureOwnership.For(map).OwnerAt).ToList();
            result.zones = markers.Where(t => t.def.GetModExtension<ShipPlanZone>() != null)
                .OrderBy(t => t.Position.z).ThenBy(t => t.Position.x).ToList();
            result.retained = markers.Except(result.zones).OrderBy(t => t.thingIDNumber).ToList();
            result.zonePositions = result.zones.Select(t => t.Position).ToList();
            result.retainedPositions = result.retained.Select(t => t.Position).ToList();
            return result;
        }

        internal IEnumerable<Thing> Things(Map map) => cells.SelectMany(c => c.GetThingList(map)).Distinct()
            .Where(t => t is Pawn || t.def.category == ThingCategory.Building ||
                t.def.category == ThingCategory.Item || t.def.IsBlueprint || t.def.IsFrame);

        internal IEnumerable<Thing> Markers(Thing controller) => Things(controller.Map).Where(t =>
            CompShipPlanner.IsAuthoringMarker(t) || t.TryGetComp<CompPrefabMarker>() != null);

        internal bool SameFoundation(Thing controller, bool preview = false)
        {
            if (controller.Map.uniqueID != mapId) return false;
            var current = new List<IntVec3>();
            SubstructureOwnership.CollectConnected(controller.Map, controller.Position, current, null, cells.Count + 1);
            var expected = new HashSet<IntVec3>(cells);
            if (preview) expected.ExceptWith(cutouts);
            return expected.SetEquals(current) &&
                current.All(c => SubstructureOwnership.FoundationAt(controller.Map, c)?.defName == "Substructure");
        }

        internal bool Matches(Thing controller) => SameFoundation(controller) &&
            new HashSet<Thing>(retained.Concat(zones)).SetEquals(Things(controller.Map)) &&
            retained.Select(t => t.Position).SequenceEqual(retainedPositions) &&
            zones.Select(t => t.Position).SequenceEqual(zonePositions) &&
            cells.Select(controller.Map.roofGrid.RoofAt).SequenceEqual(roofs) &&
            cells.Select(c => RoofOrder(controller.Map,c)).SequenceEqual(roofOrders) &&
            cells.Select(SubstructureOwnership.For(controller.Map).OwnerAt).SequenceEqual(owners);

        private static int RoofOrder(Map map, IntVec3 cell) => (map.areaManager.BuildRoof[cell] ? 1 : 0) |
            (map.areaManager.NoRoof[cell] ? 2 : 0);

        internal void SetRoofOrder(Map map, int index, int order)
        {
            map.areaManager.BuildRoof[cells[index]] = (order & 1) != 0;
            map.areaManager.NoRoof[cells[index]] = (order & 2) != 0;
        }

        internal XElement Request(Thing controller, int width, ShipPlanTask task)
        {
            IntVec3 origin = Origin;
            XElement Cell(IntVec3 c) => new XElement("Cell", new XAttribute("x", c.x - origin.x),
                new XAttribute("z", c.z - origin.z));
            return new XElement("ShipPlan",
                new XAttribute("task", task.ToString().ToLowerInvariant()),
                new XAttribute("outsideTemp", controller.Map.mapTemperature.OutdoorTemp.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("widths", width == 0 ? "1,2,3" : width.ToString(CultureInfo.InvariantCulture)),
                new XElement("Hull", cells.Select(Cell)),
                new XElement("Trimmable", cells.Where(c => Trimmable(controller.Map,c)).Select(Cell)),
                new XElement("Modules", zones.Select(t =>
                {
                    XElement cell = Cell(t.Position);
                    cell.Add(new XAttribute("module", t.def.GetModExtension<ShipPlanZone>().module));
                    return cell;
                })),
                new XElement("Reserved", retained.Select(t =>
                {
                    XElement cell = Cell(t.Position);
                    if (t.TryGetComp<CompPrefabPawnSpawner>() != null) cell.Add(new XAttribute("crew", 1));
                    return cell;
                })));
        }

        internal bool Trimmable(Map map, IntVec3 c) => map.terrainGrid.TempTerrainAt(c) == null &&
            map.terrainGrid.UnderTerrainAt(map.cellIndices.CellToIndex(c)) == null &&
            map.terrainGrid.TopTerrainAt(map.cellIndices.CellToIndex(c)) == TerrainDefOf.Space &&
            !c.GetThingList(map).Any(t => retained.Contains(t) || zones.Contains(t));

        internal void Restore(Thing controller, bool failedApply, out ShipPlanUndo remaining,
            HashSet<Thing> original = null)
        {
            remaining = this;
            Map map = controller.Map;
            var shell = new HashSet<IntVec3>(cells);
            bool Keep(Thing thing) => original?.Contains(thing) == true || retained.Contains(thing) || zones.Contains(thing);
            var clearing = Things(map).Where(t => !Keep(t)).ToList();
            var holders = new List<IThingHolder>();
            ThingOwnerUtility.AppendThingHoldersFromThings(holders, clearing);
            var contents = new List<Thing>();
            bool ContainsPawn(IThingHolder holder)
            {
                ThingOwnerUtility.GetAllThingsRecursively(holder, contents, false);
                return contents.Any(x => x is Pawn);
            }
            bool CanRestoreFoundation(IntVec3 c)
            {
                TerrainDef foundation = SubstructureOwnership.FoundationAt(map,c);
                return foundation == TerrainDefOf.Substructure || foundation == null &&
                    map.terrainGrid.TempTerrainAt(c) == null &&
                    map.terrainGrid.UnderTerrainAt(map.cellIndices.CellToIndex(c)) == null;
            }
            if (map.uniqueID != mapId || !shell.Contains(controller.Position) ||
                !cells.All(CanRestoreFoundation) ||
                clearing.Any(t => t is Pawn || !t.OccupiedRect().All(shell.Contains)) || holders.Any(ContainsPawn))
                throw new InvalidOperationException("DeltaV_ShipPlanUndoBlocked".Translate().ToString());
            foreach (Thing thing in clearing.Where(t => t.def.category != ThingCategory.Item))
                if (thing.Spawned) thing.Destroy(DestroyMode.Vanish);
            foreach (Thing thing in Things(map).Where(t => !Keep(t) && t.def.category == ThingCategory.Item).ToList())
                if (thing.Spawned) thing.Destroy(DestroyMode.Vanish);
            foreach (IntVec3 c in cells)
                if (!SubstructureOwnership.HasFoundation(map,c))
                    map.terrainGrid.SetFoundation(c,TerrainDefOf.Substructure);
            for (int i = 0; i < cutouts.Count; i++)
            {
                if (cutoutColors[i] != null) map.terrainGrid.SetTerrainColor(cutouts[i],cutoutColors[i]);
            }
            for (int i = 0; i < cells.Count; i++)
            {
                map.roofGrid.SetRoof(cells[i], roofs[i]);
                SetRoofOrder(map, i, roofOrders[i]);
            }
            foreach (var group in Enumerable.Range(0, cells.Count).GroupBy(i => owners[i]))
                SubstructureOwnership.For(map).SetOwner(group.Select(i => cells[i]), group.Key);
            for (int i = 0; i < zones.Count; i++)
                if (!zones[i].Spawned && MapContentSpawner.SpawnExisting(zones[i], map, zonePositions[i],
                    zones[i].Rotation, true) == null)
                    throw new InvalidOperationException("DeltaV_ShipPlanUndoBlocked".Translate().ToString());
            MapContentSpawner.RelinkGravshipComponents(map);
            remaining = null;
            if (!failedApply)
                Messages.Message("DeltaV_ShipPlanRestored".Translate(), controller, MessageTypeDefOf.PositiveEvent, false);
        }
    }

    internal sealed class ShipPlannerService
    {
        internal static bool Visible => DebugSettings.godMode && DeltaVMod.CurrentSettings?.showShipPlanner == true;

        private sealed class Job : IDisposable
        {
            private readonly object logLock = new object();
            private readonly StreamWriter log;
            private readonly string mapTag;
            private bool closed;
            private IOException logError;
            private volatile string progress;
            private string lastReported;
            private long lastReportMs;
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal readonly Process Process;
            internal readonly string LogPath;

            internal Job(Process process, string work, Map map)
            {
                Process = process;
                mapTag = UniverseManager.MapTag(map);
                LogPath = Path.Combine(work, "worker.log");
                log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(false)) { AutoFlush = true };
            }

            private void Write(string channel, string text)
            {
                lock (logLock)
                {
                    if (closed || logError != null) return;
                    try { log.WriteLine($"{Clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)}s [{channel}] {text}"); }
                    catch (IOException error) { logError = error; }
                }
            }

            internal void Output(string line, bool error)
            {
                if (line == null) return;
                if (!error && line.StartsWith("SHIPPLAN\t", StringComparison.Ordinal)) progress = line;
                Write(error ? "stderr" : "stdout", line);
            }

            internal string InspectText()
            {
                string[] fields = progress?.Split('\t');
                string stage = ("DeltaV_ShipPlanStage" + (fields?.Length == 6 ? fields[1] : "Launch"))
                    .Translate().ToString();
                if (fields?.Length == 6 && fields[2] != "0" && fields[3] != "0")
                    stage = "DeltaV_ShipPlanStep".Translate(stage, fields[2], fields[3]).ToString();
                return "DeltaV_ShipPlanProgress".Translate(stage, Clock.Elapsed.ToString(@"hh\:mm\:ss")).ToString();
            }

            internal void ReportWorkerProgress()
            {
                if (!DeltaVLog.Enabled(DeltaVLogModule.Development)) return;
                string latest = progress;
                long now = Clock.ElapsedMilliseconds;
                if (latest == null || latest == lastReported || now - lastReportMs < 1000) return;
                lastReportMs = now;
                lastReported = latest;
                DevelopmentLog(latest);
            }

            internal void Stage(string detail)
            {
                Write("game", detail);
                if (DeltaVLog.Enabled(DeltaVLogModule.Development)) DevelopmentLog(detail);
            }

            private void DevelopmentLog(string detail) => DeltaVLog.Message(DeltaVLogModule.Development,
                $"ShipPlanner {mapTag} [{Path.GetFileName(Path.GetDirectoryName(LogPath))}] {Clock.ElapsedMilliseconds} ms {detail}");

            public void Dispose()
            {
                lock (logLock)
                {
                    if (closed) return;
                    closed = true;
                    try { log.Dispose(); }
                    catch (IOException error) { logError = error; }
                }
                if (logError != null) Log.Warning($"[DeltaV] ShipPlanner could not write {LogPath}: {logError.Message}");
            }
        }

        private readonly Dictionary<CompShipPlanner, Job> jobs = new Dictionary<CompShipPlanner, Job>();
        internal bool IsPending(CompShipPlanner controller) => jobs.ContainsKey(controller);
        internal string ProgressText(CompShipPlanner controller) => jobs.TryGetValue(controller, out Job job)
            ? job.InspectText() : null;

        internal void Cancel(CompShipPlanner controller)
        {
            if (!jobs.TryGetValue(controller, out Job job)) return;
            jobs.Remove(controller);
            job.Stage("Canceled");
            try { if (!job.Process.HasExited) job.Process.Kill(); }
            catch (InvalidOperationException) { }
            finally { job.Dispose(); }
        }

        internal void Start(UniverseManager universe, CompShipPlanner controller, int width, ShipPlanTask task)
        {
            if (!Visible || IsPending(controller)) return;
#if RIMWORLD_1_7
            const string version = "1.7";
#else
            const string version = "1.6";
#endif
            string settingsPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "DeltaV", $"ShipPlanner-{version}.xml");
            if (!File.Exists(settingsPath))
                throw new FileNotFoundException("DeltaV_ShipPlanConfigure".Translate(settingsPath).ToString());
            XElement configuration = XElement.Load(settingsPath);
            string Required(string key)
            {
                string value = configuration.Element(key)?.Value.Trim();
                if (string.IsNullOrEmpty(value))
                    throw new InvalidDataException("DeltaV_ShipPlanConfigure".Translate(settingsPath).ToString());
                return value;
            }
            string worker = configuration.Element("worker")?.Value.Trim();
            if (string.IsNullOrEmpty(worker))
                worker = Path.Combine(MapPrefab.ModRootDirectory, "scripts", "TutorialContent", "generate_ships.py");
            string work = Path.Combine(Path.GetDirectoryName(settingsPath), "ShipPlanJobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string request = Path.Combine(work, "request.shipplan");
            string[] args = { "-X", "utf8", worker, "--shell-request", request, "--output", work,
                "--data", Required("data"), "--native", Required("native"), "--managed", Required("managed"),
                "--native-dotnet", Required("dotnet"), "--proposals", "8", "--programs", "3",
                "--layout-budget", "2", "--engine-budget", "1.2", "--engine-attempts", "3",
                "--finalists", "1", "--trace", "--progress" };
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Required("python"), Arguments = string.Join(" ", args.Select(Quote)),
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
                }
            };
            string pythonPath = configuration.Element("pythonPath")?.Value.Trim();
            if (!string.IsNullOrEmpty(pythonPath)) process.StartInfo.EnvironmentVariables["PYTHONPATH"] = pythonPath;
            var job = new Job(process, work, controller.parent.Map);
            ShipPlanUndo source;
            try
            {
                job.Stage($"CaptureShell task={task} width={width} log={job.LogPath}");
                source = ShipPlanUndo.Capture(controller.parent);
                job.Stage($"WriteRequest cells={source.cells.Count}");
                source.Request(controller.parent, width, task).Save(request);
                process.OutputDataReceived += (_, e) => job.Output(e.Data, false);
                process.ErrorDataReceived += (_, e) => job.Output(e.Data, true);
                job.Stage("LaunchWorker");
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                jobs.Add(controller, job);
            }
            catch (Exception error)
            {
                job.Stage("StartFailed " + error);
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                finally { job.Dispose(); process.Dispose(); }
                throw;
            }
            Task<int> wait = Task.Run(() =>
            {
                using (process) { process.WaitForExit(); return process.ExitCode; }
            });
            void Poll()
            {
                if (!jobs.TryGetValue(controller, out Job active) || active != job)
                    return;
                if (!Visible || Current.Game?.GetComponent<UniverseManager>() != universe || !controller.parent.Spawned)
                {
                    Cancel(controller);
                    return;
                }
                if (!wait.IsCompleted)
                {
                    job.ReportWorkerProgress();
                    universe.QueueFrameAction(Poll);
                    return;
                }
                jobs.Remove(controller);
                CompShipPlanner.Run(() =>
                {
                    using (job)
                    {
                        try
                        {
                            if (wait.Status != TaskStatus.RanToCompletion)
                                throw new InvalidOperationException(FailureMessage(work), wait.Exception);
                            job.Stage($"WorkerExited code={wait.Result}");
                            if (wait.Result != 0) throw new InvalidOperationException(FailureMessage(work));
                            job.Stage("ReadAndValidateResult");
                            controller.Apply(source, Path.Combine(work, "AssemblyPlayerPreview.xml"),
                                XElement.Load(Path.Combine(work, "result.shipplan")), job.Stage);
                            job.Stage("PreviewReady");
                        }
                        catch (Exception error)
                        {
                            job.Stage("Failed " + error);
                            throw;
                        }
                    }
                });
            }
            universe.QueueFrameAction(Poll);
        }

        private static string FailureMessage(string work)
        {
            string path = Path.Combine(work, "result.shipplan");
            string reason = File.Exists(path) ? XElement.Load(path).Attribute("failure")?.Value : null;
            string key = reason switch
            {
                "packing" => "DeltaV_ShipPlanPacking",
                "layout" => "DeltaV_ShipPlanLayout",
                "propulsion" => "DeltaV_ShipPlanPropulsion",
                "budget" => "DeltaV_ShipPlanBudget",
                _ => "DeltaV_ShipPlanWorkerFailure"
            };
            return key.Translate(work).ToString();
        }

        internal void Reset()
        {
            foreach (CompShipPlanner controller in jobs.Keys.ToList()) Cancel(controller);
        }

        internal void Forget(Map map)
        {
            foreach (CompShipPlanner controller in jobs.Keys.Where(c => c.parent.Map == map).ToList())
                Cancel(controller);
        }

        // ProcessStartInfo.ArgumentList is unavailable on the RimWorld 1.6 target.
        private static string Quote(string arg)
        {
            if (arg.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) throw new ArgumentException(nameof(arg));
            return "\"" + arg + new string('\\', arg.Length - arg.TrimEnd('\\').Length) + "\"";
        }
    }
}
