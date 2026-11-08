using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // Unity's native LookRotation is unavailable offline. Supply a known 45-degree
    // render rotation; projection, texture extent, fitting and hit layout stay production.
    static bool GlobeRenderRotation(ref Quaternion __result)
    { __result = new Quaternion(0, 0, .38268343f, .92387953f); return false; }
    static void CheckGlobeNavigation(UniverseManager universe)
    {
        CheckGlobeFilters(universe);
        CheckGlobeTextureTransform();
        var state = new GlobeNavigation(new List<GlobeTarget>());
        var rect = new Rect(8, 40, 232, 212);
        var compactRect = new Rect(8, 40, 288, 210);
        List<Rect> compactReserved = GlobeNavigationPanel.CompactReservedRects(compactRect);
        Check(compactReserved.Count == 5 && compactReserved.All(r => PlanetGlobeRenderer.Contains(compactRect, r)),
            "compact globe exposes bounded avoidance geometry for four controls and the scale");
        Check(!compactReserved.SelectMany((a, i) => compactReserved.Skip(i + 1).Select(b => a.Overlaps(b))).Any(v => v),
            "compact control and scale avoidance rectangles leave separate label-safe regions");
        Check(GlobeNavigationPanel.CompactScaleRect(compactRect).width < compactRect.width * .55f,
            "compact scale no longer blankets the map width");
        GlobeDisplayEntry Entry(int id, float x, float y, float radius = 10000f)
        {
            float angle = (float)Math.Sqrt(x * x + y * y) / radius;
            Vector2 direction = new Vector2(x, y).normalized;
            Vector3 pos = new Vector3(direction.x * (float)Math.Sin(angle), direction.y * (float)Math.Sin(angle), (float)Math.Cos(angle));
            return new GlobeDisplayEntry { owner = new SpaceObject(id, new SpacePose(pos, .3f)), area = 25 };
        }
        foreach (float radius in new[] { 100f, 20000f, 100000f })
        foreach (float side in new[] { 5f, 32f, 150f })
        {
            var view = new GlobeView(rect, Vector3.forward, radius, 1000f, side, Vector3.up, Vector3.right);
            float expected = Math.Min(1000f, view.MaximumWidth);
            Check(Math.Abs(view.Width - expected) < .001f, "fixed span ignores reference hull and clamps only at globe boundary");
            if (view.MaximumWidth > 1250f)
            {
                Check(Math.Abs(view.PixelsPerCell - .232f) < .00001f, "same preset has the same center scale across sector sizes");
                Check(Math.Abs(GlobeNavigation.Zoom(view.Width, 1, view.MaximumWidth) - 1250f) < .001f,
                    "wheel step has a fixed multiplier across sector sizes");
            }
        }
        var local = new GlobeView(rect, Vector3.forward, 10000, 1000, 32, Vector3.up, Vector3.right);
        var origin = Entry(100, 0, 0);
        foreach (var offset in new[] { new Vector2(600, 0), new Vector2(-600, 0), new Vector2(0, 600),
            new Vector2(0, -600), new Vector2(500, 500), new Vector2(-500, -500), Vector2.zero })
        {
            var target = Entry(101, offset.x, offset.y);
            SpacePose before = target.owner.pose;
            Check(PlanetGlobeRenderer.TryFit(local, new[] { origin, target }, out float width), "fit succeeds for near-field target in every quadrant");
            Check(width < 4000f, "long labels do not force a local target out to planetary scale");
            var fitted = local.WithWidth(width);
            Rect safe = rect.ContractedBy(Math.Min(rect.width, rect.height) * .1f);
            foreach (var e in new[] { origin, target })
            {
                var layout = PlanetGlobeRenderer.LayoutLegend(fitted, e, true);
                Check(!layout.edge && PlanetGlobeRenderer.Contains(safe, layout.hitRect)
                    && PlanetGlobeRenderer.Contains(safe, layout.labelRect), "fit and paint use identical safe hit and label geometry");
            }
            Check(before.Equals(target.owner.pose), "fit never changes absolute position angle or velocity");
            var edge = PlanetGlobeRenderer.LayoutLegend(local.WithWidth(250), target, true);
            Check(edge != null && edge.hitRect.width >= 24, "tiny selected targets retain a usable hit area");
        }
        var behind = Entry(102, 0, 0); behind.owner.pose = new SpacePose(-Vector3.forward, 0);
        Map projectionMap = TestMap(6100);
        var large = Entry(103, 500, -400); large.owner = new MapSpaceObject(103, projectionMap, large.owner.pose);
        large.cells = new List<IntVec3> { new IntVec3(0, 0, 0), new IntVec3(210, 0, 80) };
        large.mapAnchor = new Vector2(100, 40);
        large.projectionSide = ShipSilhouette.AnchoredSide(large.cells, large.mapAnchor) / (float)ShipSilhouette.AnchoredPixelsPerCell;
        var wide = new GlobeView(new Rect(20, 30, 650, 450), Vector3.forward, 10000, 1000, 100, Vector3.up, Vector3.right);
        var renderHarness = new Harmony("DeltaV.GlobeRegression.NativeRotation");
        renderHarness.Patch(AccessTools.Method(typeof(SpaceTransform), nameof(SpaceTransform.UniverseWorldMarkerRotation)),
            prefix: new HarmonyMethod(typeof(Program), nameof(GlobeRenderRotation)));
        try
        {
            Check(PlanetGlobeRenderer.TryFit(wide, new[] { origin, large }, out float largeWidth),
                "large 45-degree silhouette fixture fits in the expanded rectangular viewport");
            var largeLayout = PlanetGlobeRenderer.LayoutLegend(wide.WithWidth(largeWidth), large, true);
            Check(largeLayout.projection && PlanetGlobeRenderer.Contains(wide.Rect.ContractedBy(45), largeLayout.hitRect),
                "fit includes rotated silhouette extents measured by the texture's production geometry");
        }
        finally { renderHarness.UnpatchAll(renderHarness.Id); }
        SpacePose originalFrame = large.owner.pose;
        foreach (Vector3 pole in new[] { Vector3.up, -Vector3.up, new Vector3(.00001f, 1, .00001f).normalized, Vector3.forward })
        {
            large.owner.pose = new SpacePose(pole, .2f);
            GlobeView polar = GlobeView.Create(rect, universe, large.owner);
            Check(Math.Abs(Vector3.Dot(polar.Up, polar.Reference)) < .0001f
                && Math.Abs(Vector3.Dot(polar.Right, polar.Reference)) < .0001f
                && Math.Abs(polar.Right.magnitude - 1) < .0001f, "pole fallback remains a nondegenerate local view basis");
            Check((polar.Project(pole) - rect.center).magnitude < .01f, "focused frame stays at projection center through pole changes");
        }
        large.owner.pose = originalFrame;
        var navigation = universe.GlobeNavigation;
        navigation.Width = 1000f;
        foreach (Vector3 center in new[] { Vector3.forward, Vector3.up, -Vector3.up })
        {
            large.owner.pose = new SpacePose(center, .2f);
            navigation.Recenter();
            GlobeView beforePan = GlobeView.Create(wide.Rect, universe, large.owner);
            SpacePose unchanged = large.owner.pose;
            Vector2 start = wide.Rect.center;
            navigation.BeginDrag(start);
            navigation.Drag(beforePan, start + Vector2.one);
            Check(!navigation.ViewCenter.HasValue && navigation.ReleaseDrag(start + Vector2.one).HasValue,
                "a small pointer movement remains a click without panning");
            navigation.BeginDrag(start);
            Vector2 delta = new Vector2(24f, 12f);
            navigation.Drag(beforePan, start + delta);
            GlobeView panned = GlobeView.Create(wide.Rect, universe, large.owner);
            Check(navigation.ViewCenter.HasValue && !navigation.ReleaseDrag(start + delta).HasValue,
                "drag consumes the gesture without selecting a marker on release");
            Check((panned.Reference - beforePan.Reference).magnitude < .00001f && unchanged.Equals(large.owner.pose),
                "panning preserves the distance reference and absolute world pose");
            Check(Math.Abs(panned.Center.magnitude - 1f) < .0001f
                && Math.Abs(Vector3.Dot(panned.Center, panned.Right)) < .0001f,
                "panned view remains orthonormal at the poles");
            if (center == Vector3.forward)
                Check((panned.Project(beforePan.Center) - beforePan.Rect.center - delta).magnitude < .2f,
                    "near-field map follows the mouse in both axes at the current scale");
            navigation.Recenter();
            Check((GlobeView.Create(wide.Rect, universe, large.owner).Project(center) - wide.Rect.center).magnitude < .01f,
                "recenter restores ship following without changing zoom");
            Check(navigation.Width == 1000f, "recenter preserves the chosen distance range");
        }
        large.owner.pose = originalFrame;
        Find.Maps.Remove(projectionMap);
        Check(!PlanetGlobeRenderer.TryFit(local, new[] { origin, behind }, out float farWidth)
            && farWidth == local.MaximumWidth, "far-side targets report projection limit instead of inventing a front-side location");
        var antipodal = PlanetGlobeRenderer.LayoutLegend(local, behind, true);
        Check(antipodal.behind && antipodal.edge && local.Direction(behind.Pose.position).sqrMagnitude < 1e-12f,
            "antipodal target has no fabricated directional arrow");

        var first = PlanetGlobeRenderer.LayoutLegend(local, Entry(200, 0, 0), false);
        var second = PlanetGlobeRenderer.LayoutLegend(local, Entry(201, 10, 0), false);
        var selected = PlanetGlobeRenderer.LayoutLegend(local, Entry(202, 5, 0), true);
        var projected = PlanetGlobeRenderer.LayoutLegend(local, Entry(203, 6, 0), false); projected.projection = true;
        var legends = new List<PlanetGlobeRenderer.Legend> { first, second, selected, projected };
        PlanetGlobeRenderer.Aggregate(legends);
        Check(legends.Count == 3 && first.members.Count == 2 && legends.Contains(selected) && legends.Contains(projected),
            "aggregation preserves protected targets and true-scale hulls and exposes every ordinary member");

        var station = Entry(300, 7000, 0); station.station = true; station.owner.ambientLabel = "Harbor";
        var ship = Entry(301, 20, 0); ship.ship = new GlobeShipSummary { key = 401, label = "Courier", area = 25 };
        state.Entries.AddRange(new[] { station, ship }); state.CourseTarget = station;
        state.Select(ship); state.ToggleBookmark(station); state.ToggleBookmark(ship);
        var moved = Entry(302, 60, 0); moved.ship = ship.ship;
        state.Entries.Remove(ship); state.Entries.Add(moved);
        Check(state.Resolve(state.Selected) == moved && state.IsBookmarked(moved), "ship selection and bookmark follow identity into a new container");
        state.Filters.hiddenTypes.Add("FoundationShip");
        Check(state.Listed(Vector3.forward).Single() == station, "station listing includes a distant place outside the current view");
        state.Filters = GlobeFilters.Everything(); state.Filters.keepTracked = false; state.Search = "cour";
        Check(state.Listed(Vector3.forward).Single() == moved, "place search matches names without case sensitivity");
        state.Search = "";
        state.ForgetObject(301);
        Check(state.Selected.shipKey == 401 && state.Bookmarks.Any(b => b.shipKey == 401), "retiring old map cannot delete a moved ship's target or pin");
        state.ForgetObject(300);
        Check(!state.Bookmarks.Any(b => b.objectId == 300), "deleted environment releases its bookmark");

        var marker = Entry(450, 100, 0);
        state.Entries.Add(marker); state.Select(marker); state.ToggleBookmark(marker);
        var materialized = Entry(450, 100, 0);
        materialized.ship = new GlobeShipSummary { key = 451, label = "Materialized station", area = 25 };
        materialized.station = true;
        state.Entries.Remove(marker); state.Entries.Add(materialized);
        Check(state.Resolve(state.Selected) == materialized && state.IsBookmarked(materialized),
            "unloaded object target and pin survive materialization into member-only display entries");
        state.ToggleBookmark(materialized);
        Check(!state.Bookmarks.Any(b => b.objectId == 450 || b.shipKey == 451),
            "unpinning a materialized member removes its original object pin rather than creating a duplicate");

        var archive = new ArchivedSpaceObject { objectId = 500, pose = new SpacePose(Vector3.forward, 0),
            globe = new GlobeDisplaySummary { ships = new List<GlobeShipSummary> {
                new GlobeShipSummary { key = 501, area = 50, label = "Packed station", station = true } } } };
        universe.objects.Add(archive);
        var secondArchive = new ArchivedSpaceObject { objectId = 510, pose = archive.pose,
            globe = new GlobeDisplaySummary { ships = new List<GlobeShipSummary> {
                new GlobeShipSummary { key = 511, area = 40, label = "Second destination", station = true } } } };
        universe.objects.Add(secondArchive);
        var firstMission = new SpaceMission { id = 601, quest = new Quest { id = 601, acceptanceTick = 0 },
            destination = new MissionTarget { shipId = 501, objectId = 500 } };
        var secondMission = new SpaceMission { id = 602, quest = new Quest { id = 602, acceptanceTick = 0 },
            destination = new MissionTarget { shipId = 511, objectId = 510 } };
        universe.missionState.missions.AddRange(new[] { firstMission, secondMission });
        universe.GlobeNavigation.Refresh(universe);
        universe.GlobeFilters.missions = GlobeMission.Tracked | GlobeMission.Active;
        Check(universe.GlobeNavigation.Listed(Vector3.forward).Count() == 2,
            "mission filter includes both independently active commissions");
        universe.GlobeNavigation.MissionSelection = secondMission.destination.Navigation;
        universe.GlobeNavigation.Refresh(universe);
        Check(universe.GlobeNavigation.CourseTarget.owner == secondArchive
            && universe.GlobeNavigation.Listed(Vector3.forward).Count() == 2,
            "selecting a commission preserves the other mission marker");
        universe.missionState.missions.Remove(firstMission); universe.missionState.missions.Remove(secondMission);
        universe.objects.Remove(secondArchive); universe.GlobeFilters.missions = GlobeMission.All;
        universe.GlobeNavigation.Refresh(universe);
        var archivedEntry = universe.GlobeNavigation.Entries.Single(e => e.owner == archive);
        universe.GlobeNavigation.Select(archivedEntry);
        var replacement = new ArchivedSpaceObject { objectId = 500, pose = archive.pose, globe = archive.globe };
        universe.objects.Remove(archive); universe.objects.Add(replacement);
        universe.GlobeNavigation.Refresh(universe);
        Check(universe.GlobeNavigation.SelectedEntry.owner == replacement
            && universe.GlobeNavigation.SelectedEntry.station, "representation replacement resolves fresh display facts and station summary");
        universe.objects.Remove(replacement); universe.GlobeNavigation.Refresh(universe);
        Check(universe.GlobeNavigation.SelectedEntry == null && universe.GlobeNavigation.SelectionLost,
            "removed target cannot retain stale map or representation references");

        var quest = new Quest { id = 701, acceptanceTick = 0 };
        var run = new TutorialRunState { quest = quest, salvageShelf = (Building_Storage)Raw(typeof(Building_Storage)),
            rangePort = (Building_DockingPort)Raw(typeof(Building_DockingPort)), yardPort = (Building_DockingPort)Raw(typeof(Building_DockingPort)) };
        universe.tutorialRun = run;
        foreach (TutorialStage stage in Enum.GetValues(typeof(TutorialStage)))
        {
            run.stage = stage;
            Thing expected = stage == TutorialStage.Salvage ? run.salvageShelf : stage == TutorialStage.Visit ? run.yardPort
                : stage == TutorialStage.Range ? run.rangePort : null;
            Check(universe.Tutorial.NavigationRole == expected, "course navigation derives the correct role for " + stage);
        }
        universe.tutorialRun = null;

        List<GlobeTarget> pins = new List<GlobeTarget> { GlobeTarget.Of(station), GlobeTarget.Of(moved) }, loaded = null;
        string path = Path.Combine(AppContext.BaseDirectory, "navigation-pins.xml");
        try
        {
            Scribe.saver.InitSaving(path, "navigation"); Scribe_Collections.Look(ref pins, "pins", LookMode.Deep); Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path); Scribe_Collections.Look(ref loaded, "pins", LookMode.Deep); Scribe.loader.FinalizeLoading();
        }
        finally { Scribe.ForceStop(); }
        Check(loaded.SequenceEqual(pins), "persistent bookmarks round-trip object and member identity through native Scribe");
        Check(!File.ReadAllText(path).Contains("Width") && !File.ReadAllText(path).Contains("Selected"), "view range and navigation selection are not persisted as world facts");
        var fresh = new GlobeNavigation(loaded);
        Check(fresh.Width == 1000 && fresh.Selected == null && fresh.Bookmarks.Count == 2,
            "new runtime resets viewport and selection while retaining saved bookmarks");
    }

    static void CheckGlobeTextureTransform()
    {
        // Local vertices are transformed before GUI group offsets, as in the native
        // DrawLine path. Exercise expanded-window offsets and non-unit UI scales.
        var rect = new Rect(120f, 85f, 180f, 180f);
        Vector3 pivot = rect.center;
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        foreach (float angle in new[] { 0f, 45f, 90f, 185f, 270f })
        {
            var rotation = PlanetGlobeRenderer.LocalTextureRotation(Matrix4x4.identity, rect.center, angle);
            var rotatedCenter = rotation.MultiplyPoint3x4(pivot);
            Vector3 group = new Vector3(170f, 95f, 0f);
            Check(((rotatedCenter + group) * scale - (pivot + group) * scale).magnitude < .001f,
                "rotated silhouette stays anchored to its label in a scaled expanded window");
            var a = rotation.MultiplyPoint3x4(new Vector3(rect.xMin, rect.yMin, 0));
            var b = rotation.MultiplyPoint3x4(new Vector3(rect.xMax, rect.yMax, 0));
            Check(((a + b) * .5f - pivot).magnitude < .001f,
                "opposite silhouette vertices remain centered at every heading");
        }
    }
}
