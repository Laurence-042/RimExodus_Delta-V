using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal static class GlobeNavigationPanel
    {
        internal const float Height = 150f;
        internal const float CompactHeight = 44f;

        private static void CompactControlRects(Rect mapRect, out Rect range, out Rect expand,
            out Rect course, out Rect filter)
        {
            range = new Rect(mapRect.x + 5f, mapRect.y + 5f,
                Mathf.Min(94f, mapRect.width * .42f), 24f);
            expand = new Rect(mapRect.xMax - 89f, mapRect.y + 5f, 84f, 24f);
            course = new Rect(mapRect.x + 5f, mapRect.yMax - 29f,
                Mathf.Min(104f, mapRect.width * .48f), 24f);
            filter = new Rect(mapRect.xMax - 31f, mapRect.yMax - 29f, 26f, 24f);
        }

        internal static Rect CompactScaleRect(Rect mapRect)
        {
            float width = Mathf.Min(104f, mapRect.width * .48f);
            return new Rect(mapRect.x + 5f, mapRect.yMax - 54f, width, 18f);
        }

        internal static List<Rect> CompactReservedRects(Rect mapRect)
        {
            CompactControlRects(mapRect, out Rect range, out Rect expand, out Rect course, out Rect filter);
            return new List<Rect>
            {
                range.ExpandedBy(3f), expand.ExpandedBy(3f), course.ExpandedBy(3f),
                filter.ExpandedBy(3f), CompactScaleRect(mapRect).ExpandedBy(3f)
            };
        }

        internal static void Draw(Rect rect, Rect mapRect, UniverseManager universe, SpaceObject context, bool expanded)
        {
            if (context == null || !OrbitSphere.Valid) return;
            if (!expanded)
            {
                DrawCompact(rect, mapRect, universe, context);
                return;
            }
            var state = universe.GlobeNavigation;
            GlobeView view = GlobeView.Create(mapRect, universe, context);
            Text.Font = GameFont.Tiny;
            float y = rect.y;
            if (Widgets.ButtonText(new Rect(rect.x, y, 28f, 24f), "−"))
                state.Width = GlobeNavigation.Zoom(view.Width, 1f, view.MaximumWidth);
            if (Widgets.ButtonText(new Rect(rect.xMax - 28f, y, 28f, 24f), "+"))
                state.Width = GlobeNavigation.Zoom(view.Width, -1f, view.MaximumWidth);
            string range = view.Whole ? "DeltaV_NavWhole".Translate().ToString()
                : "DeltaV_NavWidth".Translate(view.Width.ToString("N0")).ToString();
            if (Widgets.ButtonText(new Rect(rect.x + 32f, y, rect.width - 64f, 24f), range))
                ShowRangeMenu(mapRect, universe, context, view);
            y += 28f;
            float half = (rect.width - 4f) * .5f;
            if (!expanded && Widgets.ButtonText(new Rect(rect.x, y, half, 24f), "DeltaV_NavExpand".Translate()))
                Open(universe);
            if (expanded && Widgets.ButtonText(new Rect(rect.x, y, half, 24f), "DeltaV_NavRecenter".Translate()))
                state.Recenter();
            bool hasCourse = state.CourseTarget != null;
            if (Widgets.ButtonText(new Rect(rect.x + half + 4f, y, half, 24f), "DeltaV_NavCourse".Translate(),
                active: hasCourse))
                universe.Missions.ChooseNavigation();
            y += 28f;
            var target = state.SelectedEntry ?? state.CourseTarget;
            string title = target == null ? (state.SelectionLost || state.CourseUnavailable
                ? "DeltaV_NavUnavailable" : "DeltaV_NavNoTarget").Translate().ToString()
                : target.Label + "\n" + "DeltaV_PanelGlobeDistance".Translate(
                    OrbitSphere.ArcCells(view.Reference, target.Pose.position).ToString("N0"));
            Rect titleRect = new Rect(rect.x, y, rect.width, 36f);
            Widgets.Label(titleRect, target == null ? title : PlanetGlobeRenderer.ShortText(target.Label, rect.width)
                + "\n" + "DeltaV_PanelGlobeDistance".Translate(OrbitSphere.ArcCells(view.Reference, target.Pose.position).ToString("N0")).ToString());
            TooltipHandler.TipRegion(titleRect, title);
            y += 38f;
            float actionWidth = (rect.width - 16f) / 5f;
            Rect ActionRect(int index) => new Rect(rect.x + index * (actionWidth + 4f), y, actionWidth, 24f);
            bool canAutopilot = GlobeNavigation.CanAutopilot(universe, view.ReferenceShip, target);
            if (Widgets.ButtonText(ActionRect(0), "DeltaV_NavAutopilot".Translate(), active: canAutopilot)
                && !GlobeNavigation.StartAutopilot(universe, view.ReferenceShip, target))
                Messages.Message("DeltaV_NavCannotAutopilot".Translate(), MessageTypeDefOf.RejectInput, false);
            if (Widgets.ButtonText(ActionRect(1), "DeltaV_NavFit".Translate(), active: target != null))
                PlanetGlobeRenderer.FitTarget(universe, context, mapRect, GlobeTarget.Of(target));
            if (Widgets.ButtonText(ActionRect(2), "DeltaV_NavInspect".Translate(), active: target != null && !target.owner.IsTransient))
                Inspect(universe, GlobeTarget.Of(target));
            if (Widgets.ButtonText(ActionRect(3), "DeltaV_NavFocusTarget".Translate(),
                active: TargetCameraFocus.CanStart(universe, context.map, target?.owner)))
                FocusTarget(universe, context.map, target?.owner);
            if (Widgets.ButtonText(ActionRect(4),
                (target != null && state.IsBookmarked(target) ? "DeltaV_NavUnpin" : "DeltaV_NavPin").Translate(), active: target != null && !target.owner.IsTransient))
                state.ToggleBookmark(target);
            y += 28f;
            string course = state.CourseTarget != null ? "DeltaV_NavCourseName".Translate(state.CourseTarget.Label).ToString()
                : (state.CourseUnavailable ? "DeltaV_NavUnavailable" : "DeltaV_NavNoCourse").Translate().ToString();
            Widgets.Label(new Rect(rect.x, y, rect.width - 38f, 20f),
                PlanetGlobeRenderer.ShortText(course, rect.width - 38f));
            TooltipHandler.TipRegion(new Rect(rect.x, y, rect.width, 20f), course);
            if (Widgets.ButtonText(new Rect(rect.xMax - 36f, y, 36f, 20f), "DeltaV_NavClear".Translate()))
                state.Select(null);
            Text.Font = GameFont.Small;
        }

        private static void DrawCompact(Rect rect, Rect mapRect, UniverseManager universe, SpaceObject context)
        {
            GlobeNavigation state = universe.GlobeNavigation;
            GlobeView view = GlobeView.Create(mapRect, universe, context);
            Text.Font = GameFont.Tiny;
            string range = view.Whole ? "DeltaV_NavWhole".Translate().ToString()
                : "DeltaV_NavWidth".Translate(view.Width.ToString("N0")).ToString();
            CompactControlRects(mapRect, out Rect rangeRect, out Rect expandRect,
                out Rect courseRect, out Rect filterRect);
            if (DeltaVPanelStyle.Button(rangeRect, range, DeltaVButtonTone.Quiet))
                ShowRangeMenu(mapRect, universe, context, view);
            if (DeltaVPanelStyle.Button(expandRect, "DeltaV_NavExpand".Translate(), DeltaVButtonTone.Quiet))
                Open(universe);
            if (DeltaVPanelStyle.Button(courseRect, "DeltaV_NavCourse".Translate(),
                selected: state.CourseTarget != null))
                universe.Missions.ChooseNavigation();
            DrawFilterButton(filterRect, universe);

            GlobeDisplayEntry target = state.SelectedEntry ?? state.CourseTarget;
            string title;
            string details = null;
            if (target == null)
            {
                title = (state.SelectionLost || state.CourseUnavailable
                    ? "DeltaV_NavUnavailable" : "DeltaV_NavNoTarget").Translate();
            }
            else
            {
                details = "DeltaV_NavDistanceSummary".Translate(
                    OrbitSphere.ArcCells(view.Reference, target.Pose.position).ToString("N0"));
                if (state.CourseTarget == target)
                    details += " · " + "DeltaV_NavCourseSelected".Translate();
                title = target.Label + "\n" + details;
            }
            DeltaVPanelStyle.DrawSurface(rect);
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width, DeltaVPanelStyle.Border);
            Rect textRect = new Rect(rect.x + 4f, rect.y + 2f, rect.width - 42f, rect.height - 4f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(textRect, target == null ? title
                : PlanetGlobeRenderer.ShortText(target.Label, textRect.width) + "\n"
                    + PlanetGlobeRenderer.ShortText(details, textRect.width));
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(textRect, title);
            var menuRect = new Rect(rect.xMax - 32f, rect.y + (rect.height - 28f) * .5f, 30f, 28f);
            if (DeltaVPanelStyle.Button(menuRect, "•••", DeltaVButtonTone.Quiet) && target != null)
            {
                var options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("DeltaV_NavFit".Translate(),
                        () => PlanetGlobeRenderer.FitTarget(universe, context, mapRect, GlobeTarget.Of(target)))
                };
                if (GlobeNavigation.CanAutopilot(universe, view.ReferenceShip, target))
                    options.Insert(0, new FloatMenuOption("DeltaV_NavAutopilot".Translate(), () =>
                    {
                        if (!GlobeNavigation.StartAutopilot(universe, view.ReferenceShip, target))
                            Messages.Message("DeltaV_NavCannotAutopilot".Translate(), MessageTypeDefOf.RejectInput, false);
                    }));
                if (!target.owner.IsTransient)
                {
                    options.Add(new FloatMenuOption("DeltaV_NavInspect".Translate(),
                        () => Inspect(universe, GlobeTarget.Of(target))));
                    options.Add(new FloatMenuOption("DeltaV_NavFocusTarget".Translate(),
                        TargetCameraFocus.CanStart(universe, context.map, target.owner)
                            ? () => FocusTarget(universe, context.map, target.owner) : null));
                    options.Add(new FloatMenuOption((state.IsBookmarked(target)
                            ? "DeltaV_NavUnpin" : "DeltaV_NavPin").Translate(),
                        () => state.ToggleBookmark(target)));
                }
                options.Add(new FloatMenuOption("DeltaV_NavClear".Translate(), () => state.Select(null)));
                Find.WindowStack.Add(new FloatMenu(options));
            }
            Text.Font = GameFont.Small;
        }

        private static void ShowRangeMenu(Rect mapRect, UniverseManager universe,
            SpaceObject context, GlobeView view)
        {
            GlobeNavigation state = universe.GlobeNavigation;
            var options = new List<FloatMenuOption>();
            foreach (float preset in GlobeNavigation.Presets)
            {
                float width = preset;
                options.Add(new FloatMenuOption("DeltaV_NavWidth".Translate(width.ToString("N0")),
                    () => state.Width = Mathf.Min(width, view.MaximumWidth)));
            }
            options.Add(new FloatMenuOption("DeltaV_NavFitShip".Translate(),
                () => { state.Recenter(); state.Width = Mathf.Min(Mathf.Max(250f, view.ReferenceSide * 6f), view.MaximumWidth); }));
            options.Add(new FloatMenuOption("DeltaV_NavFitSector".Translate(),
                () => FitSector(universe, context, mapRect)));
            options.Add(new FloatMenuOption("DeltaV_NavWhole".Translate(), () => state.Width = view.MaximumWidth));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        internal static void Open(UniverseManager universe, GlobeTarget target = null)
        {
            var window = Find.WindowStack.WindowOfType<Window_SectorNavigation>();
            if (window == null)
            { window = new Window_SectorNavigation(universe); Find.WindowStack.Add(window); }
            window.RequestFit = target;
        }

        private static void FitSector(UniverseManager universe, SpaceObject context, Rect mapRect)
        {
            universe.GlobeNavigation.Recenter();
            GlobeView view = GlobeView.Create(mapRect, universe, context);
            PlanetTile tile = OrbitSphere.TileFromDirection(view.Reference);
            if (!tile.Valid) return;
            var vertices = new List<Vector3>();
            OrbitSphere.Layer.GetTileVertices(tile, vertices);
            float width = GlobeNavigation.MinimumWidth;
            foreach (Vector3 vertex in vertices)
            {
                if (!view.Front(vertex.normalized)) { width = view.MaximumWidth; break; }
                Vector2 offset = view.Direction(vertex.normalized) * view.CellsPerRadian;
                width = Mathf.Max(width, Mathf.Abs(offset.x) * 2.5f,
                    Mathf.Abs(offset.y) * 2.5f * mapRect.width / mapRect.height);
            }
            universe.GlobeNavigation.Width = Mathf.Min(width, view.MaximumWidth);
        }

        private static void FocusTarget(UniverseManager universe, Map sourceMap, SpaceObject target)
        {
            if (TargetCameraFocus.QueueStart(universe, sourceMap, target))
                Find.WindowStack.WindowOfType<Window_SectorNavigation>()?.Close();
        }

        internal static void Inspect(UniverseManager universe, GlobeTarget key)
        {
            var state = universe.GlobeNavigation;
            state.Refresh(universe);
            var entry = state.Resolve(key);
            if (entry?.owner.InteriorMap == null || entry.owner.IsTransient)
            { Messages.Message("DeltaV_PanelObjectNotLoaded".Translate(), MessageTypeDefOf.RejectInput, false); return; }
            int objectId = entry.owner.objectId, version = entry.owner.RepresentationVersion;
            // Window and panel callbacks use the same safe frame boundary as other view transitions.
            universe.QueueFrameAction(() =>
            {
                state.Refresh(universe);
                var current = state.Resolve(key);
                if (current?.owner.objectId != objectId || current.owner.RepresentationVersion != version
                    || current.owner.InteriorMap == null || current.owner.IsTransient) return;
                if (Current.Game?.GetComponent<UniverseManager>() != universe) return;
                Map map = current.owner.InteriorMap;
                Vector2 point = current.ship != null ? SpaceTransform.ShipLocalToMap(map, current.ship.localCenter)
                    : SpaceTransform.ShipLocalToMap(map, current.localCenter);
                universe.SetFocusedWithCamera(current.owner, point, Find.CameraDriver.RootSize);
            });
            Find.WindowStack.WindowOfType<Window_SectorNavigation>()?.Close();
        }

        internal static void DrawList(Rect rect, Rect mapRect, UniverseManager universe, SpaceObject context)
        {
            var state = universe.GlobeNavigation;
            Text.Font = GameFont.Tiny;
            Rect filterButton = new Rect(rect.x, rect.y, 28f, 26f);
            DrawFilterButton(filterButton, universe);
            Rect search = new Rect(rect.x + 34f, rect.y, rect.width - 34f, 26f);
            string previousSearch = state.Search;
            state.Search = Widgets.TextField(search, state.Search);
            if (previousSearch != state.Search) state.ListScroll = Vector2.zero;
            TooltipHandler.TipRegion(search, "DeltaV_NavSearch".Translate());
            GlobeView view = GlobeView.Create(mapRect, universe, context);
            List<GlobeDisplayEntry> rows = state.Listed(view.Reference).ToList();
            Widgets.Label(new Rect(rect.x, rect.y + 30f, rect.width, 22f),
                "DeltaV_NavFilterCount".Translate(rows.Count, state.Entries.Count));
            Rect outer = new Rect(rect.x, rect.y + 56f, rect.width, rect.height - 56f);
            var missing = state.Filters.bookmarksOnly ? state.Bookmarks.Where(b => state.Resolve(b) == null).ToList()
                : new List<GlobeTarget>();
            Rect content = new Rect(0, 0, outer.width - 18f, Mathf.Max(outer.height, (rows.Count + missing.Count) * 58f + 25f));
            Widgets.BeginScrollView(outer, ref state.ListScroll, content);
            float y = 0;
            foreach (GlobeDisplayEntry entry in rows)
            {
                Rect row = new Rect(0, y, content.width, 54f);
                if (state.IsSelected(entry)) Widgets.DrawBoxSolid(row, new Color(.16f, .24f, .3f));
                Rect text = new Rect(4f, y, content.width - 60f, 54f);
                string faction = entry.Faction?.Name ?? "DeltaV_NavNoFaction".Translate().ToString();
                string details = faction + " · " + "DeltaV_PanelGlobeDistance".Translate(
                    OrbitSphere.ArcCells(view.Reference, entry.Pose.position).ToString("N0"));
                Widgets.Label(text, PlanetGlobeRenderer.ShortText(entry.Label, text.width) + "\n"
                    + PlanetGlobeRenderer.ShortText(details, text.width)
                    + (entry.owner.InteriorMap == null && !entry.owner.IsTransient ? "\n" + "DeltaV_NavUnloaded".Translate() : ""));
                TooltipHandler.TipRegion(text, entry.Label + "\n" + details);
                if (Widgets.ButtonInvisible(text)) state.Select(entry);
                if (Widgets.ButtonText(new Rect(content.width - 52f, y + 12f, 50f, 26f), "DeltaV_NavLocate".Translate()))
                    PlanetGlobeRenderer.FitTarget(universe, context, mapRect, GlobeTarget.Of(entry));
                y += 58f;
            }
            foreach (GlobeTarget key in missing)
            {
                Widgets.Label(new Rect(4f, y, content.width - 60f, 50f), "DeltaV_NavUnavailable".Translate());
                if (Widgets.ButtonText(new Rect(content.width - 52f, y + 12f, 50f, 26f), "DeltaV_NavUnpin".Translate()))
                    state.Bookmarks.Remove(key);
                y += 58f;
            }
            if (rows.Count + missing.Count == 0)
                Widgets.Label(new Rect(4f, 4f, content.width - 8f, 44f), "DeltaV_NavEmptyList".Translate());
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }

        private static void DrawFilterButton(Rect rect, UniverseManager universe)
        {
            bool restricted = universe.GlobeFilters.Restricted;
            bool clicked = DeltaVPanelStyle.Button(rect, "", DeltaVButtonTone.Quiet, selected: restricted);
            Color color = restricted ? DeltaVPanelStyle.AccentBorder : DeltaVPanelStyle.Muted;
            // Vector funnel remains sharp at every UI scale and needs no new texture asset.
            Vector2 a = new Vector2(rect.x + 5f, rect.y + 5f), b = new Vector2(rect.xMax - 5f, rect.y + 5f);
            Vector2 c = new Vector2(rect.center.x + 2f, rect.y + 13f), d = new Vector2(rect.center.x - 2f, rect.y + 13f);
            Widgets.DrawLine(a, b, color, 2f); Widgets.DrawLine(b, c, color, 2f);
            Widgets.DrawLine(c, new Vector2(c.x, rect.yMax - 5f), color, 2f);
            Widgets.DrawLine(new Vector2(c.x, rect.yMax - 5f), new Vector2(d.x, rect.yMax - 3f), color, 2f);
            Widgets.DrawLine(new Vector2(d.x, rect.yMax - 3f), d, color, 2f); Widgets.DrawLine(d, a, color, 2f);
            TooltipHandler.TipRegion(rect, "DeltaV_NavFilters".Translate());
            if (clicked && Find.WindowStack.WindowOfType<Dialog_GlobeFilters>() == null)
                Find.WindowStack.Add(new Dialog_GlobeFilters(universe));
        }
    }

    internal sealed class Window_SectorNavigation : Window
    {
        private readonly UniverseManager universe;
        internal GlobeTarget RequestFit;
        internal Window_SectorNavigation(UniverseManager universe)
        {
            this.universe = universe;
            doCloseX = true; absorbInputAroundWindow = true; closeOnAccept = false;
            optionalTitle = "DeltaV_NavTitle".Translate();
        }
        public override Vector2 InitialSize => new Vector2(Mathf.Min(1120f, UI.screenWidth - 40f), Mathf.Min(850f, UI.screenHeight - 50f));
        public override void PostClose()
        {
            universe.GlobeNavigation.Recenter();
            base.PostClose();
        }
        public override void DoWindowContents(Rect inRect)
        {
            if (Current.Game?.GetComponent<UniverseManager>() != universe || universe.Focused == null
                || !WorldRendererUtility.DrawingMap || !OrbitSphere.Valid) { Close(); return; }
            SpaceObject context = universe.Focused;
            float listWidth = Mathf.Min(320f, inRect.width * .38f);
            float mapWidth = inRect.width - listWidth - 16f;
            Rect map = new Rect(inRect.x, inRect.y, mapWidth, inRect.height - GlobeNavigationPanel.Height - 8f);
            if (RequestFit != null)
            {
                PlanetGlobeRenderer.FitTarget(universe, context, map, RequestFit);
                RequestFit = null;
            }
            Widgets.DrawBoxSolid(map, new Color(.035f, .05f, .07f));
            PlanetGlobeRenderer.Draw(map, universe, context, true);
            TooltipHandler.TipRegion(new Rect(map.x, map.yMax - 20f, map.width, 20f), "DeltaV_NavDragTip".Translate());
            GlobeNavigationPanel.Draw(new Rect(map.x, map.yMax + 8f, map.width, GlobeNavigationPanel.Height), map, universe, context, true);
            GlobeNavigationPanel.DrawList(new Rect(map.xMax + 16f, inRect.y, listWidth, inRect.height), map, universe, context);
        }
    }
}
