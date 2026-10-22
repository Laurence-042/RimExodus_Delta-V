using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // The initiating port owns the only progress value. The opposite port keeps
    // a disposable backlink so both doors can expose the same connection.
    public sealed class CompSoftDocking : ThingComp
    {
        private const float GuideReach = 4f;
        private const float CaptureRange = 1.5f;
        private const float AlignedRange = .3f;
        private Building_DockingPort peer;
        private float progress;
        private bool connected;
        private bool active;
        private CompSoftDocking incomingOwner;

        internal Building_DockingPort Port => (Building_DockingPort)parent;
        internal CompSoftDocking Owner => active ? this
            : incomingOwner?.active == true && incomingOwner.peer == Port ? incomingOwner : null;
        internal Building_DockingPort Peer => Owner == this ? peer : Owner?.Port;
        internal float Progress => Owner?.progress ?? 0f;
        internal bool Connected => Owner?.connected == true;
        internal bool IsActiveOwner => active;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_References.Look(ref peer, "softDockPeer");
            Scribe_Values.Look(ref progress, "softDockProgress", 0f);
            Scribe_Values.Look(ref connected, "softDockConnected", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                active = false;
                CompSoftDocking target = peer?.GetComp<CompSoftDocking>();
                if (target == null) { peer = null; progress = 0f; connected = false; }
                else target.incomingOwner = this;
            }
        }

        internal void Clear()
        {
            if (incomingOwner != null && incomingOwner != this)
            {
                CompSoftDocking owner = incomingOwner;
                incomingOwner = null;
                if (owner.peer == Port) owner.Clear();
            }
            if (peer?.GetComp<CompSoftDocking>()?.incomingOwner == this)
                peer.GetComp<CompSoftDocking>().incomingOwner = null;
            peer = null;
            progress = 0f;
            connected = false;
            active = false;
        }

        private void Capture(Building_DockingPort candidate)
        {
            if (peer != candidate)
            {
                if (peer?.GetComp<CompSoftDocking>()?.incomingOwner == this)
                    peer.GetComp<CompSoftDocking>().incomingOwner = null;
                progress = 0f;
                connected = false;
            }
            peer = candidate;
            active = true;
            candidate.GetComp<CompSoftDocking>().incomingOwner = this;
        }

        internal bool TryRestore(UniverseManager universe, SoftDockService service, DriveSubject controlled)
        {
            if (active || peer == null || progress <= 0f || !CanInitiate(service, controlled)
                || !CanCapture(universe, service, peer)) return false;
            Capture(peer);
            return true;
        }

        internal bool TryFindCapture(UniverseManager universe, SoftDockService service,
            DriveSubject controlled, IEnumerable<Building_DockingPort> ports)
        {
            if (!CanInitiate(service, controlled)) return false;
            Building_DockingPort best = null;
            float bestError = CaptureRange;
            foreach (Building_DockingPort candidate in ports)
            {
                if (!CanCapture(universe, service, candidate)) continue;
                float error = Error(universe, candidate);
                if (error < bestError || error == bestError && candidate.thingIDNumber < best.thingIDNumber)
                { best = candidate; bestError = error; }
            }
            if (best == null) return false;
            Capture(best);
            return true;
        }

        private bool CanInitiate(SoftDockService service, DriveSubject controlled)
            => !active && Owner == null && service.Live(Port)
                && ShipRegistry.ShipOf(Port)?.Identity == controlled;

        private bool CanCapture(UniverseManager universe, SoftDockService service, Building_DockingPort candidate)
        {
            if (candidate == Port || candidate?.Map == Port.Map || !service.Live(candidate)
                || candidate.GetComp<CompSoftDocking>()?.Owner != null) return false;
            SpaceObject target = universe.FrameOf(candidate.Map);
            if (OrbitSphere.ArcCells(Point(universe, Port, 0f), target.pose.position)
                > new Vector2(target.map.Size.x, target.map.Size.z).magnitude * .5f + 10f) return false;
            return Mathf.Abs(AngleError(universe, candidate)) <= .2f
                && Error(universe, candidate) < CaptureRange;
        }

        internal void TickConnection(UniverseManager universe, SoftDockService service, DriveSubject controlled)
        {
            if (!active) return;
            if (!service.Live(Port) || !service.Live(peer) || Port.Map == peer.Map
                || peer.GetComp<CompSoftDocking>()?.incomingOwner != this
                || ShipRegistry.ShipOf(Port)?.Identity != controlled)
            { Clear(); return; }
            float error = Error(universe, peer), angle = AngleError(universe, peer);
            Advance(error < AlignedRange && Mathf.Abs(angle) < .03f);
            if ((error > 2f || Mathf.Abs(angle) > .25f) && progress <= 0f) Clear();
        }

        internal void Advance(bool aligned)
        {
            progress = Mathf.Clamp01(progress + (aligned ? 1f : -2f) / 180f);
            if (progress >= 1f) connected = true;
            if (progress < .75f) connected = false;
        }

        internal Building_DockingPort AccessiblePeer(UniverseManager universe, Pawn pawn, bool requireOpen)
        {
            Building_DockingPort other = Peer;
            if (!Connected || !universe.SoftDock.Enabled || !universe.SoftDock.Live(Port)
                || !universe.SoftDock.Live(other)) return null;
            return !requireOpen || Port.PawnCanOpen(pawn) && other.PawnCanOpen(pawn) ? other : null;
        }

        internal bool CanSteer(UniverseManager universe, SoftDockService service, DriveSubject controlled)
            => active && service.Enabled && service.Live(Port) && service.Live(peer)
                && Port.Map != peer.Map && ShipRegistry.ShipOf(Port)?.Identity == controlled
                && Error(universe, peer) <= 2f && Mathf.Abs(AngleError(universe, peer)) <= .25f;

        internal SpacePose AlignmentReference(UniverseManager universe)
        {
            SpaceObject own = universe.FrameOf(Port.Map), host = universe.FrameOf(peer.Map);
            float relative = SpaceTransform.WrapAngle(ShipOrientation.AngleFromRot4(peer.Rotation)
                + Mathf.PI - ShipOrientation.AngleFromRot4(Port.Rotation));
            return SpaceTransform.AlignLocalPoint(host.pose,
                SpaceTransform.MapToShipLocal(host.map, MapPoint(peer, GuideReach)),
                SpaceTransform.MapToShipLocal(own.map, MapPoint(Port, GuideReach)), relative);
        }

        internal static float AssistFraction(float guideDistance)
        {
            if (guideDistance >= CaptureRange) return 0f;
            float distanceSquared = Mathf.Max(guideDistance * guideDistance, AlignedRange * AlignedRange);
            return (1f - Mathf.Exp(-1f / 30f)) * AlignedRange * AlignedRange / distanceSquared;
        }

        internal static bool CanAssist(SpaceObject own, SpaceObject host)
            => own != null && host != null && !own.PropulsionUsedThisTick && !host.PropulsionUsedThisTick;

        internal void ApplyAlignmentAssist(UniverseManager universe, SoftDockService service, DriveSubject controlled)
        {
            if (!CanSteer(universe, service, controlled)) return;
            SpaceObject own = universe.FrameOf(Port.Map), host = universe.FrameOf(peer.Map);
            if (!CanAssist(own, host)) return;
            float fraction = AssistFraction(Error(universe, peer));
            if (fraction > 0f) own.pose = SpaceTransform.LerpPose(own.pose, AlignmentReference(universe), fraction);
        }

        internal static Vector2 MapPoint(Building_DockingPort port, float forward, float side = 0f)
        {
            IntVec3 dir = port.Rotation.FacingCell;
            return new Vector2(port.Position.x + .5f + dir.x * forward - dir.z * side,
                port.Position.z + .5f + dir.z * forward + dir.x * side);
        }

        internal static Vector3 Point(UniverseManager universe, Building_DockingPort port, float forward, float side = 0f)
            => SpaceTransform.MapPointToUniverse(universe.FrameOf(port.Map), port.Map, MapPoint(port, forward, side));

        internal float Error(UniverseManager universe, Building_DockingPort other)
            => Mathf.Max(OrbitSphere.ArcCells(Point(universe, Port, GuideReach, .5f), Point(universe, other, GuideReach, -.5f)),
                OrbitSphere.ArcCells(Point(universe, Port, GuideReach, -.5f), Point(universe, other, GuideReach, .5f)));

        internal float AngleError(UniverseManager universe, Building_DockingPort other)
            => SpaceTransform.WrapAngle(universe.FrameOf(other.Map).pose.angle + ShipOrientation.AngleFromRot4(other.Rotation)
                + Mathf.PI - universe.FrameOf(Port.Map).pose.angle - ShipOrientation.AngleFromRot4(Port.Rotation));

        internal void DrawGuide(UniverseManager universe, SpaceObject focus, Map frameMap)
        {
            Vector3 DrawPoint(float forward, float side = 0f)
                => SpaceTransform.FocusedDrawPosition(focus, frameMap, Point(universe, Port, forward, side),
                    AltitudeLayer.MetaOverlays.AltitudeFor());
            foreach (float side in new[] { -.5f, .5f })
            {
                Vector3 tip = DrawPoint(GuideReach, side);
                GenDraw.DrawLineBetween(DrawPoint(.5f, side), tip);
                Vector3[] diamond = { DrawPoint(GuideReach + .22f, side), DrawPoint(GuideReach, side + .22f),
                    DrawPoint(GuideReach - .22f, side), DrawPoint(GuideReach, side - .22f) };
                for (int i = 0; i < 4; i++) GenDraw.DrawLineBetween(diamond[i], diamond[(i + 1) % 4]);
            }
        }

        internal void DrawPassage(UniverseManager universe, SpaceObject focus, Map frameMap)
        {
            if (!active || !universe.SoftDock.Live(Port) || !universe.SoftDock.Live(peer)) return;
            Vector3 DrawPoint(float forward)
                => SpaceTransform.FocusedDrawPosition(focus, frameMap, Point(universe, Port, forward),
                    AltitudeLayer.MetaOverlays.AltitudeFor());
            float length = Mathf.Max(0f, OrbitSphere.ArcCells(Point(universe, Port, 0f),
                Point(universe, peer, 0f)) - 1f) * progress;
            Quaternion rotation = SpaceTransform.FocusedSpriteRotation(focus,
                universe.FrameOf(Port.Map).pose.angle + ShipOrientation.AngleFromRot4(Port.Rotation));
            for (float offset = 0f; offset < length; offset += 1f)
            {
                float segment = Mathf.Min(1f, length - offset);
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(DrawPoint(.5f + offset + segment * .5f),
                    rotation, new Vector3(1f, 1f, segment)), DeltaVDefOf.DeltaV_DockingPassage.graphicData.Graphic.MatSingle, 0);
            }
        }
    }
}
