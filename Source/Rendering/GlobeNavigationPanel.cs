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

        internal static void Draw(Rect rect, Rect mapRect, UniverseManager universe, SpaceObject context, bool expanded)
        {
            if (context == null || !OrbitSphere.Valid) return;
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
            {
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
            float third = (rect.width - 8f) / 3f;
            if (Widgets.ButtonText(new Rect(rect.x, y, third, 24f), "DeltaV_NavFit".Translate(), active: target != null))
                PlanetGlobeRenderer.FitTarget(universe, context, mapRect, GlobeTarget.Of(target));
            if (Widgets.ButtonText(new Rect(rect.x + third + 4f, y, third, 24f), "DeltaV_NavInspect".Translate(), active: target != null))
                Inspect(universe, GlobeTarget.Of(target));
            if (Widgets.ButtonText(new Rect(rect.x + 2f * (third + 4f), y, third, 24f),
                (target != null && state.IsBookmarked(target) ? "DeltaV_NavUnpin" : "DeltaV_NavPin").Translate(), active: target != null))
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

        internal static void Inspect(UniverseManager universe, GlobeTarget key)
        {
            var state = universe.GlobeNavigation;
            state.Refresh(universe);
            var entry = state.Resolve(key);
            if (entry?.owner.InteriorMap == null)
            { Messages.Message("DeltaV_PanelObjectNotLoaded".Translate(), MessageTypeDefOf.RejectInput, false); return; }
            int objectId = entry.owner.objectId, version = entry.owner.RepresentationVersion;
            // Window and panel callbacks use the same safe frame boundary as other view transitions.
            universe.QueueFrameAction(() =>
            {
                state.Refresh(universe);
                var current = state.Resolve(key);
                if (current?.owner.objectId != objectId || current.owner.RepresentationVersion != version
                    || current.owner.InteriorMap == null) return;
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
            float button = (rect.width - 9f) / 4f;
            for (int i = 0; i < 4; i++)
            {
                var filter = (GlobeFilter)i;
                Rect tab = new Rect(rect.x + i * (button + 3f), rect.y, button, 26f);
                if (Widgets.ButtonText(tab, ("DeltaV_NavFilter" + filter).Translate()))
                { state.Filter = filter; state.ListScroll = Vector2.zero; }
                if (state.Filter == filter) Widgets.DrawBox(tab, 2);
            }
            Rect search = new Rect(rect.x, rect.y + 32f, rect.width, 26f);
            string previousSearch = state.Search;
            state.Search = Widgets.TextField(search, state.Search);
            if (previousSearch != state.Search) state.ListScroll = Vector2.zero;
            TooltipHandler.TipRegion(search, "DeltaV_NavSearch".Translate());
            GlobeView view = GlobeView.Create(mapRect, universe, context);
            List<GlobeDisplayEntry> rows = state.Listed(view.Reference).ToList();
            Rect outer = new Rect(rect.x, rect.y + 64f, rect.width, rect.height - 64f);
            var missing = state.Filter == GlobeFilter.Bookmarks ? state.Bookmarks.Where(b => state.Resolve(b) == null).ToList()
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
                    + (entry.owner.InteriorMap == null ? "\n" + "DeltaV_NavUnloaded".Translate() : ""));
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
