using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // Object identity and ship identity deliberately occupy different fields. Ship targets
    // follow the member across maps; environment targets follow the object representation.
    public sealed class GlobeTarget : IExposable, IEquatable<GlobeTarget>
    {
        internal int objectId, shipKey;
        internal bool shuttle;
        public GlobeTarget() { }
        internal static GlobeTarget Of(GlobeDisplayEntry entry) => entry == null ? null : new GlobeTarget
        { objectId = entry.ship == null ? entry.owner.objectId : 0,
            shipKey = entry.ship?.key ?? 0, shuttle = entry.ship?.shuttle ?? false };
        public bool Equals(GlobeTarget other) => other != null && objectId == other.objectId
            && shipKey == other.shipKey && shuttle == other.shuttle;
        public override bool Equals(object obj) => Equals(obj as GlobeTarget);
        public override int GetHashCode() => (objectId * 397 ^ shipKey) * 397 ^ shuttle.GetHashCode();
        internal bool Matches(GlobeDisplayEntry entry) => entry != null && (entry.ship == null
            ? shipKey == 0 && objectId == entry.owner.objectId
            : objectId == 0 && shipKey == entry.ship.key && shuttle == entry.ship.shuttle);
        public void ExposeData()
        {
            Scribe_Values.Look(ref objectId, "objectId");
            Scribe_Values.Look(ref shipKey, "shipKey");
            Scribe_Values.Look(ref shuttle, "shuttle");
        }
    }

    internal sealed class GlobeNavigation
    {
        private const float NavigationClearance = 8f;
        internal const float DefaultWidth = 1000f;
        internal const float MinimumWidth = 64f;
        internal static readonly float[] Presets = { 250, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000 };
        internal float Width = DefaultWidth;
        internal GlobeTarget Selected;
        internal bool SelectionLost;
        internal GlobeFilters Filters = new GlobeFilters();
        internal string Search = "";
        internal Vector2 ListScroll;
        internal Vector3 StableUp = Vector3.up;
        internal Vector3? ViewCenter;
        private Vector2? dragStart;
        private Vector2 dragLast;
        private bool dragged;
        internal readonly List<GlobeDisplayEntry> Entries = new List<GlobeDisplayEntry>();
        internal GlobeDisplayEntry CourseTarget, SelectedEntry;
        internal GlobeTarget MissionSelection;
        private readonly HashSet<GlobeDisplayEntry> missionEntries = new HashSet<GlobeDisplayEntry>();
        internal bool CourseUnavailable;
        internal readonly List<GlobeTarget> Bookmarks;
        internal GlobeNavigation(List<GlobeTarget> bookmarks) { Bookmarks = bookmarks; }

        internal void Recenter() { ViewCenter = null; EndDrag(); }
        internal void EndDrag() { dragStart = null; dragged = false; }
        internal void BeginDrag(Vector2 point) { dragStart = dragLast = point; dragged = false; }
        internal void Drag(GlobeView view, Vector2 point, bool pan = true)
        {
            if (!dragStart.HasValue) return;
            if (!dragged && (point - dragStart.Value).sqrMagnitude < 16f) return;
            dragged = true;
            if (pan) ViewCenter = view.PannedCenter(point - dragLast);
            dragLast = point;
        }
        internal Vector2? ReleaseDrag(Vector2 point)
        {
            Vector2? click = dragStart.HasValue && !dragged
                && (point - dragStart.Value).sqrMagnitude < 16f ? point : (Vector2?)null;
            EndDrag();
            return click;
        }
        internal Vector2? HandleMapInput(GlobeView view, bool draggable, List<Rect> reserved)
        {
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                EndDrag();
                if (!AllowsMapClick(ev.mousePosition)) return null;
                BeginDrag(ev.mousePosition);
                GUIUtility.keyboardControl = 0;
                ev.Use();
            }
            else if (dragStart.HasValue && ev.type == EventType.MouseDrag && ev.button == 0)
            { Drag(view, ev.mousePosition, draggable); ev.Use(); }
            else if (dragStart.HasValue && ev.type == EventType.MouseUp && ev.button == 0)
            {
                bool allowed = AllowsMapClick(ev.mousePosition);
                var click = ReleaseDrag(ev.mousePosition);
                if (allowed) ev.Use();
                return allowed ? click : null;
            }
            else if (ev.type == EventType.Repaint && !Input.GetMouseButton(0)) EndDrag();
            return null;

            bool AllowsMapClick(Vector2 point) => view.Rect.Contains(point)
                && !Find.WindowStack.MouseObscuredNow
                && (draggable || !reserved.Exists(rect => rect.Contains(point)));
        }

        internal void Refresh(UniverseManager universe)
        {
            Filters = universe.GlobeFilters;
            Entries.Clear();
            foreach (SpaceObject obj in universe.objects) Entries.AddRange(universe.GlobeDisplay.Entries(obj));
            universe.Projectiles.CollectGlobeEntries(Entries);
            SelectedEntry = Resolve(Selected);
            if (Selected != null) SelectionLost = SelectedEntry == null;
            missionEntries.Clear();
            var targets = universe.Missions.NavigationTargets().ToList();
            foreach (var target in targets)
            {
                var entry = Resolve(target.target);
                if (entry != null) missionEntries.Add(entry);
            }
            if (MissionSelection == null || !targets.Any(t => t.target.Equals(MissionSelection)))
                MissionSelection = targets.FirstOrDefault().target;
            CourseTarget = Resolve(MissionSelection);
            CourseUnavailable = targets.Count > 0 && CourseTarget == null;
        }

        internal GlobeDisplayEntry Resolve(GlobeTarget target)
        {
            if (target == null) return null;
            if (target.shipKey != 0) return Entries.FirstOrDefault(target.Matches);
            // A marker can materialize into members without an environment entry. Its
            // object identity still resolves to the principal displayed structure.
            return Entries.Where(e => e.owner.objectId == target.objectId)
                .OrderByDescending(e => e.station).ThenByDescending(e => e.area).FirstOrDefault();
        }

        internal GlobeDisplayEntry ResolveRole(UniverseManager universe, Thing role)
        {
            if (role?.Spawned != true || role.Map.Disposed) return null;
            ShipBase ship = ShipRegistry.ShipOf(role);
            if (ship.IsValid) return Entries.FirstOrDefault(e => e.ship != null
                && e.ship.key == ship.Identity.key && e.ship.shuttle == (ship is ShuttleShip));
            SpaceObject owner = universe.FrameOf(role.Map);
            return Entries.FirstOrDefault(e => e.owner == owner && e.ship == null);
        }

        internal void Select(GlobeDisplayEntry entry)
        { Selected = GlobeTarget.Of(entry); SelectedEntry = entry; SelectionLost = false; }
        internal bool IsSelected(GlobeDisplayEntry entry) => SelectedEntry != null && ReferenceEquals(SelectedEntry, entry);
        internal bool IsCourse(GlobeDisplayEntry entry) => entry != null && missionEntries.Contains(entry);
        internal bool IsBookmarked(GlobeDisplayEntry entry) => Bookmarks.Any(b => b.shipKey != 0 ? b.Matches(entry)
            : b.objectId == entry.owner.objectId && ReferenceEquals(Resolve(b), entry));
        internal bool Important(GlobeDisplayEntry entry) => IsSelected(entry) || IsCourse(entry) || IsBookmarked(entry);
        internal void ToggleBookmark(GlobeDisplayEntry entry)
        {
            if (entry == null || entry.owner.IsTransient) return;
            GlobeTarget existing = Bookmarks.FirstOrDefault(b => ReferenceEquals(Resolve(b), entry));
            if (existing != null) Bookmarks.Remove(existing); else Bookmarks.Add(GlobeTarget.Of(entry));
        }
        internal void ForgetObject(int objectId)
        {
            // Member identities may already have moved into another map during transfer.
            if (Selected?.objectId == objectId) { Selected = null; SelectedEntry = null; SelectionLost = true; }
            Bookmarks.RemoveAll(b => b.objectId == objectId);
            Entries.Clear(); missionEntries.Clear(); CourseTarget = null; SelectedEntry = null;
        }
        internal void InvalidateEntries() { Entries.Clear(); missionEntries.Clear(); CourseTarget = null; SelectedEntry = null; }

        internal static float Zoom(float width, float steps, float maximum) =>
            Mathf.Clamp(width * (float)Math.Pow(1.25, steps), Mathf.Min(MinimumWidth, maximum), maximum);

        internal IEnumerable<GlobeDisplayEntry> Listed(Vector3 reference)
        {
            return Entries.Where(IsDisplayed)
                .OrderByDescending(IsCourse).ThenBy(e => OrbitSphere.ArcCells(reference, e.Pose.position))
                .ThenBy(e => e.owner.objectId).ThenBy(e => e.ship?.key ?? 0);
        }

        internal bool IsDisplayed(GlobeDisplayEntry entry)
        {
            if (entry == null) return false;
            bool tracked = entry != null && ReferenceEquals(entry, CourseTarget);
            return Filters.Matches(entry, tracked, IsCourse(entry), IsBookmarked(entry))
                && (Filters.keepTracked && tracked || Search.NullOrEmpty()
                    || (entry.Label ?? "").IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0
                    || (entry.Faction?.Name ?? "").IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal void ApplyFilters(UniverseManager universe, GlobeFilters filters)
        {
            universe.GlobeFilters = filters.Copy();
            Filters = universe.GlobeFilters;
            ListScroll = Vector2.zero;
            if (SelectedEntry != null && !IsDisplayed(SelectedEntry)) Select(null);
        }

        internal static bool CanAutopilot(UniverseManager universe, ShipBase ship, GlobeDisplayEntry entry)
        {
            if (universe == null || !ship.IsValid || !universe.Pilot.CanPlayerControl(ship)
                || entry?.owner == null || entry.owner.IsTransient || entry.owner.IsScattered)
                return false;
            SpaceObject own = universe.FrameOf(ship.Map);
            return own != null && own.objectId != entry.owner.objectId;
        }

        internal static Vector3 PerimeterWaypoint(Vector3 target, Vector3 approachFrom,
            float distance, int tieBreaker)
        {
            Vector2 outward = OrbitSphere.Offset(target, approachFrom);
            if (outward.sqrMagnitude < 1e-8f)
            {
                float phase = (tieBreaker * .61803398875f % 1f) * Mathf.PI * 2f;
                outward = new Vector2(Mathf.Cos(phase), Mathf.Sin(phase));
            }
            return OrbitSphere.Move(target, outward.normalized * Mathf.Max(0f, distance));
        }

        internal static bool StartAutopilot(UniverseManager universe, ShipBase ship,
            GlobeDisplayEntry entry)
        {
            if (!CanAutopilot(universe, ship, entry)) return false;
            SpaceObject targetOwner = entry.owner;
            SpacePose targetCenter = entry.Pose;
            SpacePose ownPose = ShipRegistry.ShipPose(ship);
            Vector2 targetLocalCenter = entry.ship != null ? entry.ship.localCenter : entry.localCenter;
            float targetRadius = universe.GlobeDisplay.NavigationRadius(targetOwner)
                + targetLocalCenter.magnitude;
            float clearance = targetRadius + ShipFireControl.HullRadius(ship) + NavigationClearance;
            Vector3 waypoint = PerimeterWaypoint(targetCenter.position, ownPose.position,
                clearance, targetOwner.objectId ^ ship.Identity.key);
            Vector3 referenceOrigin = targetOwner.pose.position;
            Vector2 referenceOffset = OrbitSphere.Offset(referenceOrigin, waypoint);
            Vector2 faceTarget = OrbitSphere.Offset(waypoint, targetCenter.position);
            float angle = SpaceTransform.WrapAngle(Mathf.Atan2(faceTarget.y, faceTarget.x)
                - ship.FrameAngle);
            var goal = new PlayerNavigationTarget(referenceOrigin, angle,
                targetOwner.objectId, referenceOffset, true, true);
            if (!universe.Controls.SelectPlayerAutoTask(ship, PlayerAutoTaskKind.Navigate, goal))
                return false;
            Messages.Message("DeltaV_NavAutopilotStarted".Translate(entry.Label),
                MessageTypeDefOf.NeutralEvent, false);
            return true;
        }
    }

    // The same projection and legend layout are used by painting, hit testing and fit.
    internal sealed class GlobeView
    {
        internal readonly Rect Rect;
        internal readonly Vector3 Reference, Center, Up, Right;
        internal readonly float CellsPerRadian, Width, PixelsPerCell, PixelsPerRad, MaximumWidth;
        internal readonly float ReferenceSide;
        internal readonly ShipBase ReferenceShip;

        internal GlobeView(Rect rect, Vector3 reference, float cellsPerRadian, float width,
            float referenceSide, Vector3 up, Vector3 right, ShipBase ship = default, Vector3? center = null)
        {
            Rect = rect; Reference = reference.normalized; CellsPerRadian = cellsPerRadian;
            Center = (center ?? reference).normalized;
            ReferenceSide = referenceSide; ReferenceShip = ship; Up = up; Right = right;
            MaximumWidth = cellsPerRadian * rect.width / Mathf.Max(1f, Mathf.Min(rect.width, rect.height) * .5f - 4f);
            Width = Mathf.Clamp(width, Mathf.Min(GlobeNavigation.MinimumWidth, MaximumWidth), MaximumWidth);
            PixelsPerCell = rect.width / Width;
            PixelsPerRad = PixelsPerCell * cellsPerRadian;
        }
        internal GlobeView WithWidth(float width) => new GlobeView(Rect, Reference, CellsPerRadian,
            width, ReferenceSide, Up, Right, ReferenceShip, Center);
        internal Vector2 Project(Vector3 position) => Rect.center + Direction(position) * PixelsPerRad;
        internal Vector2 Direction(Vector3 position) => new Vector2(Vector3.Dot(position, Right), -Vector3.Dot(position, Up));
        internal bool Front(Vector3 position) => Vector3.Dot(position, Center) > 0f;
        internal bool Whole => Width >= MaximumWidth * .9999f;

        internal Vector3 PannedCenter(Vector2 delta)
        {
            Vector3 tangent = (-Right * delta.x + Up * delta.y) / PixelsPerCell;
            OrbitSphere.Basis(Center, out Vector3 east, out Vector3 north);
            return OrbitSphere.Move(Center, new Vector2(Vector3.Dot(tangent, east), Vector3.Dot(tangent, north)));
        }

        internal static GlobeView Create(Rect rect, UniverseManager universe, SpaceObject context)
        {
            var state = universe.GlobeNavigation;
            ShipStatusPanel.TryGetSelectedShip(context?.map, out ShipBase ship);
            Vector3 reference = ship.IsValid ? ShipRegistry.ShipPose(ship).position : context.pose.position;
            Vector3 center = state.ViewCenter ?? reference;
            Vector3 forward = -center.normalized;
            Vector3 up = Vector3.up - forward * Vector3.Dot(Vector3.up, forward);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(state.StableUp, forward);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(Vector3.forward, forward);
            up.Normalize();
            Vector3 right = Vector3.Cross(up, forward).normalized;
            state.StableUp = up;
            float side = 100f;
            if (ship.IsValid)
            {
                CellRect bounds = CellRect.FromCellList(ship.HullCells.ToList());
                side = Mathf.Max(bounds.Width, bounds.Height, 1f);
            }
            return new GlobeView(rect, reference, OrbitSphere.CellsPerRadian, state.Width, side, up, right, ship, center);
        }

        internal Vector2 Edge(Vector3 position, float inset = 18f)
        {
            Vector2 dir = Direction(position);
            if (dir.sqrMagnitude < 1e-12f) return Rect.center;
            float halfX = Mathf.Max(1f, Rect.width * .5f - inset);
            float halfY = Mathf.Max(1f, Rect.height * .5f - inset);
            float denominator = Mathf.Max(Mathf.Abs(dir.x) / halfX, Mathf.Abs(dir.y) / halfY);
            return Rect.center + dir / denominator;
        }
    }
}
