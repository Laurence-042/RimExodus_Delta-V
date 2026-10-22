using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Runtime links, never a map relationship. Index mutations follow port lifetime events.</summary>
    public sealed partial class SoftDockService
    {
        private const float GuideReach = 4f;
        private readonly UniverseManager universe;
        private readonly HashSet<Building_DockingPort> ports = new HashSet<Building_DockingPort>();
        private readonly List<Link> links = new List<Link>();
        private DriveSubject controlled;
        private int nextSearch;
        private Link steering;
        private DriveSubject steeringShip;
        private System.Func<SpacePose, SpacePose> steeringReference;
        public bool Enabled { get; private set; }
        internal sealed class Link
        {
            internal Building_DockingPort a, b;
            internal float progress;
            internal bool connected;
            internal Building_DockingPort Other(Building_DockingPort port) => port == a ? b : a;
            internal void Advance(bool aligned)
            {
                progress = Mathf.Clamp01(progress + (aligned ? 1f : -2f) / 180f);
                if (progress >= 1f) connected = true;
                if (progress < .75f) connected = false;
            }
        }
        public SoftDockService(UniverseManager universe) { this.universe = universe; }
        internal void Register(Building_DockingPort port) { if (Enabled) ports.Add(port); }
        internal void Remove(Building_DockingPort port)
        {
            CancelTransfersAt(port);
            if (steering?.a == port || steering?.b == port) StopSteering();
            ports.Remove(port); links.RemoveAll(p => p.a == port || p.b == port);
        }
        internal void Forget(Map map)
        { foreach (var port in ports.Where(p => p.Map == map).ToList()) Remove(port); }
        public void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            CancelAllTransfers();
            StopSteering();
            Enabled = enabled; links.Clear(); ports.Clear(); controlled = default; nextSearch = 0;
            if (!enabled) return;
            // Explicit user action initializes the index once. No periodic map/Thing scan.
            foreach (Map map in Find.Maps)
                foreach (var port in map.listerThings.ThingsOfDef(DeltaVDefOf.DeltaV_DockingPort).OfType<Building_DockingPort>())
                    ports.Add(port);
            SelectControl();
        }
        private void SelectControl()
        {
            ShipBase ship = universe.Pilot.SelectedShipOnMap(Find.CurrentMap);
            if (ship?.IsValid == true && !ship.Identity.shuttle && ship.Faction == Faction.OfPlayer)
                controlled = ship.Identity;
        }
        private bool Live(Building_DockingPort port) => port?.Spawned == true && !port.Destroyed
            && universe.FrameOf(port.Map) != null && !universe.Lifecycle.IsFrozen(port.Map)
            && !universe.ShipTransitions.IsAwaitingDockingCleanup(port.Map);
        internal Link Pair(Building_DockingPort port) => Enabled ? links.FirstOrDefault(p => p.a == port || p.b == port) : null;
        internal Building_DockingPort Peer(Building_DockingPort port, Pawn pawn, bool requireOpen = true)
        {
            Link pair = Pair(port);
            if (pair?.connected != true || !Live(port) || !Live(pair.Other(port))) return null;
            Building_DockingPort peer = pair.Other(port);
            return !requireOpen || port.PawnCanOpen(pawn) && peer.PawnCanOpen(pawn) ? peer : null;
        }
        internal static Vector2 MapPoint(Building_DockingPort port, float forward, float side = 0f)
        {
            IntVec3 dir = port.Rotation.FacingCell;
            return new Vector2(port.Position.x + .5f + dir.x * forward - dir.z * side,
                port.Position.z + .5f + dir.z * forward + dir.x * side);
        }
        internal Vector3 Point(Building_DockingPort port, float forward, float side = 0f)
            => SpaceTransform.MapPointToUniverse(universe.FrameOf(port.Map), port.Map, MapPoint(port, forward, side));
        private float Error(Building_DockingPort a, Building_DockingPort b)
            => Mathf.Max(OrbitSphere.ArcCells(Point(a, GuideReach, .5f), Point(b, GuideReach, -.5f)),
                OrbitSphere.ArcCells(Point(a, GuideReach, -.5f), Point(b, GuideReach, .5f)));
        private float AngleError(Building_DockingPort a, Building_DockingPort b)
            => SpaceTransform.WrapAngle(universe.FrameOf(b.Map).pose.angle + ShipOrientation.AngleFromRot4(b.Rotation)
                + Mathf.PI - universe.FrameOf(a.Map).pose.angle - ShipOrientation.AngleFromRot4(a.Rotation));

        internal SpacePose AlignmentReference(Link link)
        {
            SpaceObject own = universe.FrameOf(link.a.Map), host = universe.FrameOf(link.b.Map);
            float relative = SpaceTransform.WrapAngle(ShipOrientation.AngleFromRot4(link.b.Rotation)
                + Mathf.PI - ShipOrientation.AngleFromRot4(link.a.Rotation));
            // Opposite headings plus coincident guide midpoints align left→right and right→left.
            return SpaceTransform.AlignLocalPoint(host.pose,
                SpaceTransform.MapToShipLocal(host.map, MapPoint(link.b, GuideReach)),
                SpaceTransform.MapToShipLocal(own.map, MapPoint(link.a, GuideReach)), relative);
        }

        private bool SteeringValid(Link link) => Enabled && links.Contains(link) && Live(link.a) && Live(link.b)
            && link.a.Map != link.b.Map && ShipRegistry.ShipOf(link.a)?.Identity == controlled
            && Error(link.a, link.b) <= 2f && Mathf.Abs(AngleError(link.a, link.b)) <= .25f;

        private void StartSteering(Link link)
        {
            StopSteering();
            ShipBase ship = ShipRegistry.ShipOf(link.a);
            if (ship?.IsValid != true) return;
            steering = link; steeringShip = ship.Identity;
            // Capture may add an alignment correction during manual flight, but must
            // not take the controls away to engage a position-holding autopilot.
            if (universe.Pilot.PilotedTarget(universe).Identity == ship.Identity) return;
            var target = PilotTarget.Of(ship);
            if (universe.Approach.ActivePlan?.Target.Identity == ship.Identity)
                universe.Approach.CancelSelectionOrPlan(universe, silent: true);
            universe.Controls.PlayerFor(ship)?.SetAutoManeuver(false);
            steeringReference = _ => AlignmentReference(link);
            universe.Autopilot.FollowReference(steeringShip, steeringReference, false,
                () => SteeringValid(link), () => universe.Pilot.DisarmAutopilotInput(target));
            if (universe.Autopilot.IsFollowing(steeringShip, steeringReference))
                universe.Pilot.ArmAutopilotInput(target);
        }

        private void StopSteering()
        {
            if (steeringReference != null && universe.Autopilot.DetachReference(steeringShip, steeringReference))
                universe.Pilot.DisarmAutopilotInput(PilotTarget.Of(ShipRegistry.ShipByIdentity(steeringShip)));
            steering = null; steeringReference = null; steeringShip = default;
        }
        internal void Tick()
        {
            if (!Enabled) return;
            var piloted = universe.Pilot.PilotedTarget(universe);
            if (piloted.IsValid && !piloted.Identity.shuttle && piloted.Identity != controlled)
            { CancelAllTransfers(); StopSteering(); controlled = piloted.Identity; links.Clear(); }
            bool corrected = false;
            for (int i = links.Count - 1; i >= 0; i--)
            {
                Link link = links[i];
                if (!Live(link.a) || !Live(link.b) || link.a.Map == link.b.Map
                    || ShipRegistry.ShipOf(link.a)?.Identity != controlled)
                { CancelTransfersAt(link.a); if (steering == link) StopSteering(); links.RemoveAt(i); continue; }
                float error = Error(link.a, link.b), angle = AngleError(link.a, link.b);
                bool aligned = error < .3f && Mathf.Abs(angle) < .03f;
                link.Advance(aligned);
                if (!link.connected) CancelTransfersAt(link.a);
                if (error > 2f || Mathf.Abs(angle) > .25f)
                {
                    if (steering == link) StopSteering();
                    if (link.progress <= 0f) links.RemoveAt(i);
                    continue;
                }
                if (steering == null) StartSteering(link);
                if (corrected || link != steering) continue;
                corrected = true;
                SpaceObject own = universe.FrameOf(link.a.Map);
                SpacePose reference = AlignmentReference(link);
                own.pose = SpaceTransform.LerpPose(own.pose, reference, 1f - Mathf.Exp(-1f / 30f));
            }
            int now = Find.TickManager.TicksGame;
            if (now < nextSearch) return;
            nextSearch = now + 15;
            if (ShipRegistry.ShipByIdentity(controlled)?.IsValid != true) SelectControl();
            foreach (Building_DockingPort a in ports.OrderBy(p => p.thingIDNumber))
            {
                if (!Live(a) || ShipRegistry.ShipOf(a)?.Identity != controlled || Pair(a) != null) continue;
                Building_DockingPort best = null;
                float bestError = 1.5f;
                foreach (Building_DockingPort b in ports)
                {
                    if (b == a || b.Map == a.Map || !Live(b) || Pair(b) != null) continue;
                    SpaceObject target = universe.FrameOf(b.Map);
                    if (OrbitSphere.ArcCells(Point(a, 0f), target.pose.position)
                        > new Vector2(target.map.Size.x, target.map.Size.z).magnitude * .5f + 10f) continue;
                    if (Mathf.Abs(AngleError(a, b)) > .2f) continue;
                    float error = Error(a, b);
                    if (error < bestError || error == bestError && b.thingIDNumber < best.thingIDNumber)
                    { best = b; bestError = error; }
                }
                if (best != null)
                {
                    var link = new Link { a = a, b = best };
                    links.Add(link);
                    if (steering == null) StartSteering(link);
                }
            }
        }
        internal void Draw()
        {
            if (!Enabled || Find.CurrentMap == null) return;
            SpaceObject focus = universe.FrameOf(Find.CurrentMap);
            if (focus == null) return;
            Vector3 DrawPoint(Building_DockingPort port, float forward, float side = 0f)
                => SpaceTransform.FocusedDrawPosition(focus, Find.CurrentMap, Point(port, forward, side),
                    AltitudeLayer.MetaOverlays.AltitudeFor());
            foreach (Building_DockingPort port in ports)
            {
                if (!Live(port)) continue;
                foreach (float side in new[] { -.5f, .5f })
                {
                    Vector3 tip = DrawPoint(port, GuideReach, side);
                    GenDraw.DrawLineBetween(DrawPoint(port, .5f, side), tip);
                    Vector3[] diamond = { DrawPoint(port, GuideReach + .22f, side), DrawPoint(port, GuideReach, side + .22f),
                        DrawPoint(port, GuideReach - .22f, side), DrawPoint(port, GuideReach, side - .22f) };
                    for (int i = 0; i < 4; i++) GenDraw.DrawLineBetween(diamond[i], diamond[(i + 1) % 4]);
                }
            }
            foreach (Link link in links)
            {
                if (!Live(link.a) || !Live(link.b)) continue;
                float length = Mathf.Max(0f, OrbitSphere.ArcCells(Point(link.a, 0f), Point(link.b, 0f)) - 1f) * link.progress;
                Quaternion rotation = SpaceTransform.FocusedSpriteRotation(focus,
                    universe.FrameOf(link.a.Map).pose.angle + ShipOrientation.AngleFromRot4(link.a.Rotation));
                for (float offset = 0f; offset < length; offset += 1f)
                {
                    float segment = Mathf.Min(1f, length - offset);
                    Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(DrawPoint(link.a, .5f + offset + segment * .5f),
                        rotation, new Vector3(1f, 1f, segment)), DeltaVDefOf.DeltaV_DockingPassage.graphicData.Graphic.MatSingle, 0);
                }
            }
        }
    }
}
