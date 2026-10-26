using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>One map gesture; no flight order changes until a complete target is confirmed.</summary>
    internal sealed class PlayerNavigationInput
    {
        private readonly UniverseManager universe;
        internal readonly DriveSubject OwnerId;
        private readonly Map map;
        private readonly List<SpaceObject> visible = new List<SpaceObject>();
        private bool dragging;
        private Vector2 startMap, startScreen;
        private Vector3 startUniverse;
        private SpacePose frozenFrame;
        private const float MinimumDragPixels = 8f;

        internal PlayerNavigationInput(UniverseManager universe, DriveSubject ownerId)
        {
            this.universe = universe;
            OwnerId = ownerId;
            map = Find.CurrentMap;
        }

        private bool SessionValid()
        {
            ShipBase ship = ShipRegistry.ShipByIdentity(OwnerId);
            return Application.isFocused && map != null && Find.CurrentMap == map
                && ship.IsValid && ship.Map == map && universe.FrameOf(map) != null
                && WorldRendererUtility.DrawingMap;
        }

        private Vector3 FrozenPoint(Vector2 mapPoint)
            => SpaceTransform.ShipLocalToUniverse(frozenFrame,
                SpaceTransform.MapToShipLocal(map, mapPoint));

        internal void ProcessEvent()
        {
            if (!SessionValid()) { universe.Pilot.EndNavigationTargeting(); return; }
            Event ev = Event.current;
            if (ev == null) return;
            if (ev.type == EventType.Used && ev.rawType == EventType.MouseUp)
            { dragging = false; return; }
            if (KeyBindingDefOf.Cancel.KeyDownEvent || ev.type == EventType.MouseDown && ev.button == 1)
            {
                universe.Pilot.EndNavigationTargeting();
                ev.Use();
                return;
            }
            universe.PlayerInput.InvalidateGate();
            bool mapAllowed = universe.PlayerInput.MapAllowed(Time.frameCount)
                && !SpaceAimController.MouseOverUiWindow();
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                if (!mapAllowed) return;
                Vector3 mouse = UI.MouseMapPosition();
                startMap = new Vector2(mouse.x, mouse.z);
                startScreen = ev.mousePosition;
                frozenFrame = universe.FrameOf(map).pose;
                startUniverse = FrozenPoint(startMap);
                dragging = true;
                ev.Use();
                return;
            }
            if (!dragging) return;
            if (ev.type == EventType.MouseDrag && ev.button == 0)
            {
                ev.Use();
                return;
            }
            if (ev.type != EventType.MouseUp || ev.button != 0) return;
            dragging = false;
            if (!mapAllowed || (ev.mousePosition - startScreen).sqrMagnitude
                < MinimumDragPixels * MinimumDragPixels)
            {
                ev.Use();
                return;
            }
            Vector3 mouseUp = UI.MouseMapPosition();
            Vector3 endUniverse = FrozenPoint(new Vector2(mouseUp.x, mouseUp.z));
            Vector2 direction = OrbitSphere.Offset(startUniverse, endUniverse);
            if (direction.sqrMagnitude < 1e-8f) { ev.Use(); return; }
            ShipBase ship = ShipRegistry.ShipByIdentity(OwnerId);
            float angle = SpaceTransform.WrapAngle(Mathf.Atan2(direction.y, direction.x)
                - ship.FrameAngle);
            var goal = new PlayerNavigationTarget(startUniverse, angle);
            bool relative = DeltaVDefOf.DeltaV_NavigationReferenceModifier?.IsDown == true;
            ev.Use();
            universe.Pilot.EndNavigationTargeting();
            if (!relative)
            {
                universe.Controls.SelectPlayerAutoTask(ship, PlayerAutoTaskKind.Navigate, goal);
                return;
            }
            CollectVisibleReferences();
            if (visible.Count == 0)
            {
                Messages.Message("DeltaV_NavigationNoReference".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            var options = new List<FloatMenuOption>();
            foreach (SpaceObject candidate in visible)
            {
                int id = candidate.objectId;
                options.Add(new FloatMenuOption(candidate.label + " #" + id, () =>
                {
                    SpaceObject host = universe.ObjectById(id);
                    ShipBase currentShip = ShipRegistry.ShipByIdentity(OwnerId);
                    if (host == null || !currentShip.IsValid || host.map == null
                        || host.map.Disposed || host.IsTransient || host.IsScattered
                        || host.objectId == universe.FrameOf(currentShip.Map)?.objectId)
                    {
                        Messages.Message("DeltaV_NavigationNoReference".Translate(),
                            MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    Vector2 offset = SpaceTransform.UniverseToShipLocal(host, startUniverse);
                    var movingGoal = new PlayerNavigationTarget(startUniverse, angle, id, offset);
                    universe.Controls.SelectPlayerAutoTask(currentShip,
                        PlayerAutoTaskKind.Navigate, movingGoal);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void CollectVisibleReferences()
        {
            visible.Clear();
            SpaceObject frame = universe.FrameOf(map);
            if (frame == null) return;
            var shells = new List<SpaceObject>();
            ShellRenderer.OrderShellsById(universe, frame, shells);
            CellRect view = Find.CameraDriver.CurrentViewRect;
            foreach (SpaceObject candidate in shells)
            {
                Map other = candidate.map;
                if (other == null || other.Disposed || candidate.IsTransient || candidate.IsScattered
                    || universe.Generation?.IsGenerating(other) == true) continue;
                float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
                foreach (Vector2 corner in new[] { Vector2.zero, new Vector2(other.Size.x, 0f),
                    new Vector2(other.Size.x, other.Size.z), new Vector2(0f, other.Size.z) })
                {
                    Vector3 world = SpaceTransform.MapPointToUniverse(candidate, other, corner);
                    Vector2 projected = SpaceTransform.UniverseToMapPoint(frame, map, world);
                    minX = Mathf.Min(minX, projected.x); maxX = Mathf.Max(maxX, projected.x);
                    minY = Mathf.Min(minY, projected.y); maxY = Mathf.Max(maxY, projected.y);
                }
                if (maxX < view.minX || minX > view.maxX || maxY < view.minZ || minY > view.maxZ)
                    continue;
                visible.Add(candidate);
            }
        }

        internal void DrawPreview()
        {
            if (!dragging || !SessionValid()) return;
            SpaceObject frame = universe.FrameOf(map);
            Vector3 mouse = UI.MouseMapPosition();
            Vector3 endUniverse = FrozenPoint(new Vector2(mouse.x, mouse.z));
            Vector2 a = SpaceTransform.UniverseToMapPoint(frame, map, startUniverse);
            Vector2 b = SpaceTransform.UniverseToMapPoint(frame, map, endUniverse);
            Vector3 start = new Vector3(a.x, 0.15f, a.y);
            Vector3 end = new Vector3(b.x, 0.15f, b.y);
            GenDraw.DrawLineBetween(start, end, SimpleColor.White);
            GenDraw.DrawLineBetween(start + new Vector3(-0.5f, 0f, 0f),
                start + new Vector3(0.5f, 0f, 0f), SimpleColor.White);
            GenDraw.DrawLineBetween(start + new Vector3(0f, 0f, -0.5f),
                start + new Vector3(0f, 0f, 0.5f), SimpleColor.White);
        }

        internal void DrawHint()
        {
            if (SessionValid()) GenUI.DrawMouseAttachment(null, "DeltaV_NavigationDragHint".Translate(), 0f);
        }
    }
}
