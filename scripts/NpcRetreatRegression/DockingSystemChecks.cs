using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    sealed class SoftDockThrustProbe : SpaceObject
    {
        private readonly ShipPhysics.MassReport report = new ShipPhysics.MassReport { mass = 100f, inertia = 100f };
        internal SoftDockThrustProbe(int id) : base(id, new SpacePose(Vector3.forward, 0f))
        {
            report.thrusters.Add(new ShipPhysics.Thruster
            { dir = Vector2.right, thrust = 2000f, operational = true });
        }
        protected override ShipPhysics.MassReport MotionReport => report;
        protected override void AllocateDrive(ShipPhysics.MassReport layout, ShipPhysics.Command command,
            out Vector2 force, out float torque)
        {
            layout.thrusters[0].throttle = Mathf.Clamp01(command.fx);
            force = layout.thrusters[0].dir * (layout.thrusters[0].thrust * layout.thrusters[0].throttle);
            torque = 0f;
        }
        internal void Drive(Vector2 force) => ExecuteDrive(report, 0f, new DriveSubject(objectId, 1),
            new DriveControl { force = force }, 1f / 60f, out _);
    }

    static void CheckDockingSystem(UniverseManager universe)
    {
        var dock = new CompSoftDocking();
        Set(dock, "active", true);
        for (int i = 0; i < 179; i++) dock.Advance(true);
        Check(!dock.Connected, "soft docking cannot transmit before extension completes");
        for (int i = 0; i < 3; i++) dock.Advance(true);
        Check(dock.Connected && dock.Progress == 1f, "aligned soft docking completes and clamps extension");
        for (int i = 0; i < 20; i++) dock.Advance(false);
        Check(dock.Connected, "brief alignment loss preserves an established portal above release threshold");
        for (int i = 0; i < 4; i++) dock.Advance(false);
        Check(!dock.Connected && dock.Progress > 0f, "portal disconnects before the visual passage fully retracts");
        dock.Advance(true);
        Check(!dock.Connected, "recovery above release threshold does not prematurely reopen an incomplete portal");
        for (int i = 0; i < 200; i++) dock.Advance(false);
        Check(dock.Progress == 0f, "retraction never produces negative passage length");
        var service = new SoftDockService(universe);
        universe.SoftDockEnabled = true;
        service.SetEnabled(false);
        Check(!service.Enabled, "toggle off immediately invalidates soft portals without waiting for a tick");

        var thrusting = new SoftDockThrustProbe(49698);
        thrusting.Drive(new Vector2(1000f, 0f));
        var coasting = new SoftDockThrustProbe(49699);
        coasting.Drive(Vector2.zero);
        Check(thrusting.PropulsionUsedThisTick && !coasting.PropulsionUsedThisTick
            && !CompSoftDocking.CanAssist(thrusting, coasting)
            && !CompSoftDocking.CanAssist(coasting, thrusting)
            && CompSoftDocking.CanAssist(coasting, new SpaceObject(49697, coasting.pose)),
            "soft docking disables interpolation when either ship actually uses an engine");

        var body = new SpaceObject(49700, new SpacePose(Vector3.forward, .3f)
            { velocity = new Vector2(2f, -1f), angularVelocity = .03f });
        SpacePose original = body.pose;
        CheckSmallScaleMovement();
        CheckSoftDockInterpolation();
        CheckDockingCompState(universe);

        Map map = TestMap(49701);
        map.info.Size = new IntVec3(1, 1, 1);
        map.cellIndices = new CellIndices(map);
        map.terrainGrid = new TerrainGrid(map);
        var cargo = (Thing)Raw(typeof(Thing));
        cargo.def = TestBuildingDef("scattered_cargo"); cargo.def.category = ThingCategory.Item;
        map.listerThings.AllThings.Add(cargo);
        var scattered = new MapSpaceObject(49701, map, original) { baseTag = SpaceObjectTag.Scattered };
        Check(!scattered.CollisionCells.Any(), "scattered cargo has no collision surface");
        universe.objects.Add(scattered);
        universe.Scatter.Register(scattered);
        Check(universe.Scatter.Candidates.Contains(scattered), "scattered registration populates the tractor candidate index");
        Check(CombatTarget.ForContainer(scattered).TryResolve(universe, out var resolved) && resolved.Container == scattered,
            "tractor locks resolve scattered object identity through existing fire control");
        CheckScatteredSweep(universe, scattered);
        universe.objects.Remove(scattered);
        universe.Scatter.Remove(scattered);
        Check(!universe.Scatter.Candidates.Contains(scattered), "scattered retirement releases the tractor candidate index");
        Check(!CombatTarget.ForContainer(scattered).TryResolve(universe, out _), "retired scattered identity cannot resolve a stale tractor lock");
        map.listerThings.AllThings.Clear();
        SpaceCollision.Forget(map);
        Find.Maps.Remove(map);
        CheckSoftDockReference(universe);
        CheckScatteredRetirement(universe);
        CheckTractorReceive(universe);
        CheckUndockHeading(universe);
    }

    static void CheckSoftDockInterpolation()
    {
        var start = new SpacePose(new Vector3(1, 2, 3).normalized, Mathf.PI - .08f)
            { velocity = new Vector2(2f, -1f), angularVelocity = .04f };
        var target = SpaceTransform.RigidPointPose(start, new Vector2(.8f, -.4f), .16f);
        SpacePose actual = start;
        float distance = OrbitSphere.ArcCells(actual.direction, target.direction);
        float angle = Mathf.Abs(SpaceTransform.WrapAngle(target.angle - actual.angle));
        bool monotonic = true;
        for (int tick = 0; tick < 240; tick++)
        {
            actual = SpaceTransform.LerpPose(actual, target, 1f - Mathf.Exp(-1f / 30f));
            float nextDistance = OrbitSphere.ArcCells(actual.direction, target.direction);
            float nextAngle = Mathf.Abs(SpaceTransform.WrapAngle(target.angle - actual.angle));
            monotonic &= nextDistance <= distance + 1e-5f && nextAngle <= angle + 1e-6f;
            distance = nextDistance; angle = nextAngle;
        }
        Check(monotonic && distance < .001f && angle < .001f,
            "soft pose interpolation converges without overshoot across the angle wrap");
        Check(Math.Abs(actual.velocity.magnitude - start.velocity.magnitude) < .001f
            && actual.angularVelocity == start.angularVelocity,
            "soft pose interpolation adds no acceleration or spin damping");
        SpacePose untouched = SpaceTransform.LerpPose(start, target, 0f);
        Check(untouched.direction.x == start.direction.x && untouched.direction.y == start.direction.y
            && untouched.direction.z == start.direction.z && untouched.angle == start.angle,
            "zero interpolation leaves the authoritative pose untouched");
    }

    static void CheckSmallScaleMovement()
    {
        var radii = (Dictionary<RimWorld.Planet.PlanetLayer, float>)typeof(OrbitSphere).GetField("RadiusByLayer", Private).GetValue(null);
        var scales = (Dictionary<RimWorld.Planet.PlanetLayer, float>)typeof(OrbitSphere).GetField("WorldPerCellByLayer", Private).GetValue(null);
        var layer = OrbitSphere.Layer;
        float radius = radii[layer], scale = scales[layer];
        bool drag = DeltaVMod.CurrentSettings.passiveDrag;
        try
        {
            // Same angular scale at different world-unit scales must produce the same motion.
            radii[layer] = 1f; scales[layer] = .0001f;
            DeltaVMod.CurrentSettings.passiveDrag = false;
            foreach (Vector3 direction in new[] { Vector3.forward, new Vector3(1, 2, 3).normalized })
            {
                var body = new SpaceObject(49740, new SpacePose(direction, 0f) { velocity = new Vector2(.2f, 0f) });
                for (int tick = 0; tick < 60; tick++)
                    ShipPhysics.Integrate(body, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
                Check(OrbitSphere.ArcCells(direction, body.pose.position) > .15f,
                    "sub-one-cell-per-second motion survives small world units in the real integrator");
            }
            radii[layer] = 100f;
            var precise = new SpaceObject(49741, new SpacePose(new Vector3(1, 2, 3).normalized, 0f)
                { velocity = new Vector2(.2f, 0f) });
            Vector3 before = precise.pose.position;
            for (int tick = 0; tick < 60; tick++)
                ShipPhysics.Integrate(precise, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
            Check(OrbitSphere.ArcCells(before, precise.pose.position) > .15f,
                "low-speed integration retains displacement at a million cells per radian");
            SpacePose saved = precise.pose, restored = default;
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "precise-pose.xml");
            try
            {
                Scribe.saver.InitSaving(path, "pose-test"); saved.ExposeData(); Scribe.saver.FinalizeSaving();
                Scribe.loader.InitLoading(path); restored.ExposeData(); Scribe.loader.FinalizeLoading();
            }
            finally { Scribe.ForceStop(); }
            Check(OrbitSphere.ArcCells(saved.direction, restored.direction) < 1e-7f
                && saved.velocity == restored.velocity && saved.angle == restored.angle,
                "real Scribe round trip preserves the sole double-precision position and motion");
            var replay = new SpaceObject(49742, restored);
            for (int tick = 0; tick < 60; tick++)
            {
                ShipPhysics.Integrate(precise, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
                ShipPhysics.Integrate(replay, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
            }
            Check(OrbitSphere.ArcCells(precise.pose.direction, replay.pose.direction) < 1e-6f,
                "continued low-speed integration agrees before and after save/load without a residual cache");
            Map ma = TestMap(49743), mb = TestMap(49744);
            var a = new MapSpaceObject(49743, ma, precise.pose);
            var b = new MapSpaceObject(49744, mb, SpaceTransform.RigidPointPose(precise.pose, new Vector2(30f, -21f), .7f));
            SpacePose ap = a.pose, bp = b.pose;
            for (int repeat = 0; repeat < 4; repeat++)
            foreach (SpaceObject focus in new SpaceObject[] { a, b })
            {
                Vector2 center = SpaceTransform.UniverseToMapPoint(focus, focus.map, focus.pose.position);
                Check((center - SpaceTransform.ShipLocalOrigin(focus.map)).magnitude < .001f,
                    "switching the draw reference keeps the focused map identity");
                foreach (SpaceObject obj in new SpaceObject[] { a, b })
                {
                    Vector2 drawn = SpaceTransform.UniverseToMapPoint(focus, focus.map, obj.pose.position);
                    Vector3 back = SpaceTransform.MapPointToUniverse(focus, focus.map, drawn);
                    Check(OrbitSphere.ArcCells(back, obj.pose.position) < .1f,
                        "relative position survives both draw references within float rendering precision");
                }
            }
            Check(a.pose.direction.x == ap.direction.x && a.pose.direction.y == ap.direction.y && a.pose.direction.z == ap.direction.z
                && b.pose.direction.x == bp.direction.x && b.pose.direction.y == bp.direction.y && b.pose.direction.z == bp.direction.z
                && a.pose.angle == ap.angle && b.pose.angle == bp.angle,
                "repeated focus transforms never rewrite absolute positions or relative angles");
            Find.Maps.Remove(mb); Find.Maps.Remove(ma);
        }
        finally { radii[layer] = radius; scales[layer] = scale; DeltaVMod.CurrentSettings.passiveDrag = drag; }
    }

    static void CheckScatteredSweep(UniverseManager universe, SpaceObject scattered)
    {
        var sweep = new SpaceCollision.SweptBody { attacker = new SpaceObject(49702, scattered.pose),
            requiredDefender = scattered, sweepRadius = 0f,
            from = SpaceTransform.MapPointToUniverse(scattered, scattered.map, new Vector2(-2, .5f)),
            to = SpaceTransform.MapPointToUniverse(scattered, scattered.map, new Vector2(3, .5f)) };
        SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { sweep });
        Check(!sweep.resolvedHit && !sweep.dead, "production projectile sweep skips scattered cargo entirely");
    }

    static void CheckSoftDockReference(UniverseManager universe)
    {
        Map a = TestMap(49710), b = TestMap(49711);
        a.info.Size = new IntVec3(31, 1, 28); b.info.Size = new IntVec3(44, 1, 39);
        var own = new MapSpaceObject(49710, a, new SpacePose(Vector3.forward, 0f));
        var host = new MapSpaceObject(49711, b, new SpacePose(new Vector3(1, 2, 3).normalized, .4f)
            { velocity = new Vector2(3f, -1f), angularVelocity = .05f });
        universe.objects.Add(own); universe.objects.Add(host);
        Building_DockingPort Port(Map map, IntVec3 position)
        {
            var port = (Building_DockingPort)Raw(typeof(Building_DockingPort));
            Set(typeof(Thing), port, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
            Set(typeof(Thing), port, "positionInt", position);
            return port;
        }
        Building_DockingPort ownPort = Port(a, new IntVec3(6, 0, 17));
        Building_DockingPort peerPort = Port(b, new IntVec3(27, 0, 9));
        var dock = new CompSoftDocking { parent = ownPort };
        Set(dock, "peer", peerPort);
        Set(dock, "active", true);
        foreach (Rot4 ar in Rot4.AllRotations)
        foreach (Rot4 br in Rot4.AllRotations)
        {
            Set(typeof(Thing), ownPort, "rotationInt", ar); Set(typeof(Thing), peerPort, "rotationInt", br);
            own.pose = dock.AlignmentReference(universe);
            Check(dock.Error(universe, peerPort) < .01f
                && Math.Abs(dock.AngleError(universe, peerPort)) < 1e-5f,
                "one reference aligns both crossed guides for every port direction");
            Vector2 ap = SpaceTransform.MapToShipLocal(a, CompSoftDocking.MapPoint(ownPort, 4f));
            Vector2 bp = SpaceTransform.MapToShipLocal(b, CompSoftDocking.MapPoint(peerPort, 4f));
            SpacePose at = SpaceTransform.RigidPointPose(own.pose, ap, 0f), bt = SpaceTransform.RigidPointPose(host.pose, bp, 0f);
            Check(SpaceTransform.RelativeVelocity(at, bt).magnitude < .002f
                && own.pose.angularVelocity == host.pose.angularVelocity,
                "soft docking guide reference includes moving-host point velocity and spin");
        }
        Set(typeof(Thing), ownPort, "rotationInt", Rot4.East);
        Set(typeof(Thing), peerPort, "rotationInt", Rot4.West);
        own.pose = SpaceTransform.RigidPointPose(dock.AlignmentReference(universe),
            new Vector2(.5f, -.25f), .08f);
        Check(dock.Error(universe, peerPort) < 1.5f,
            "perturbed soft docking starts inside the capture range");
        for (int tick = 0; tick < 180; tick++)
        {
            float guideError = dock.Error(universe, peerPort);
            own.pose = SpaceTransform.LerpPose(own.pose, dock.AlignmentReference(universe),
                CompSoftDocking.AssistFraction(guideError));
            ShipPhysics.Integrate(own, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
            ShipPhysics.Integrate(host, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
        }
        Check(dock.Error(universe, peerPort) < .3f
            && Mathf.Abs(dock.AngleError(universe, peerPort)) < .03f,
            "captured soft docking converges while both ships drift and rotate");
        float far = CompSoftDocking.AssistFraction(1.2f);
        float near = CompSoftDocking.AssistFraction(.6f);
        Check(CompSoftDocking.AssistFraction(1.5f) == 0f
            && near > far && Mathf.Abs(near / far - 4f) < .001f,
            "soft docking assist cuts off at capture range and follows inverse-square guide distance");
        Check(CompSoftDocking.AssistFraction(.1f) == CompSoftDocking.AssistFraction(0f)
            && CompSoftDocking.AssistFraction(.1f) < 1f,
            "coincident guides keep a finite interpolation strength");
        a.info.Size = new IntVec3(401, 1, 401);
        b.info.Size = new IntVec3(401, 1, 401);
        Set(typeof(Thing), ownPort, "positionInt", new IntVec3(390, 0, 200));
        Set(typeof(Thing), peerPort, "positionInt", new IntVec3(10, 0, 200));
        Vector2 ownGuide = SpaceTransform.MapToShipLocal(a, CompSoftDocking.MapPoint(ownPort, 4f));
        Vector2 hostGuide = SpaceTransform.MapToShipLocal(b, CompSoftDocking.MapPoint(peerPort, 4f));
        float relative = SpaceTransform.WrapAngle(ShipOrientation.AngleFromRot4(peerPort.Rotation)
            + Mathf.PI - ShipOrientation.AngleFromRot4(ownPort.Rotation));
        own.pose = SpaceTransform.RigidPointPose(SpaceTransform.AlignLocalPoint(host.pose,
            hostGuide, ownGuide, relative + .18f), new Vector2(1f, 0f), 0f);
        SpacePose exact = dock.AlignmentReference(universe), before = own.pose;
        float entranceError = dock.Error(universe, peerPort);
        float weighted = CompSoftDocking.AssistFraction(entranceError);
        SpacePose assisted = SpaceTransform.LerpPose(before, exact, weighted);
        SpacePose oldStep = SpaceTransform.LerpPose(before, exact, 1f - Mathf.Exp(-1f / 30f));
        Check(entranceError > .8f && entranceError < 1.5f
            && OrbitSphere.ArcCells(before.position, exact.position) > 10f
            && OrbitSphere.ArcCells(before.position, assisted.position)
                < OrbitSphere.ArcCells(before.position, oldStep.position) * .2f,
            "edge-port capture eases whole-map movement at the outer guide range");
        universe.objects.Remove(own); universe.objects.Remove(host);
        Find.Maps.Remove(a); Find.Maps.Remove(b);
    }

    sealed class DockingStateExpose : IExposable
    {
        internal CompSoftDocking dock;
        public void ExposeData() => dock.PostExposeData();
    }

    static void CheckDockingCompState(UniverseManager universe)
    {
        Map a = TestMap(49714), b = TestMap(49715);
        Building_DockingPort Port(Map map, int id)
        {
            var port = (Building_DockingPort)Raw(typeof(Building_DockingPort));
            port.def = TestBuildingDef("soft_dock_state_" + id);
            port.thingIDNumber = id;
            Set(typeof(Thing), port, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
            Set(typeof(Thing), port, "positionInt", new IntVec3(1, 0, 2));
            var state = new CompSoftDocking { parent = port };
            Set(typeof(ThingWithComps), port, "comps", new List<ThingComp> { state });
            map.listerThings.AllThings.Add(port);
            return port;
        }
        Building_DockingPort source = Port(a, 49716), original = Port(b, 49717), replacement = Port(b, 49718);
        CompSoftDocking state = source.Docking;
        Call(state, "Capture", original);
        for (int i = 0; i < 90; i++) state.Advance(true);
        Check(state.Peer == original && original.Docking.Peer == source && Mathf.Abs(state.Progress - .5f) < .001f,
            "initiating comp alone owns progress while the opposite port reads its runtime backlink");

        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "soft-dock-state.xml");
        CompSoftDocking RoundTrip(CompSoftDocking current)
        {
            var restored = new CompSoftDocking { parent = source };
            var wrapper = new DockingStateExpose { dock = restored };
            try
            {
                Scribe.saver.InitSaving(path, "soft-dock"); current.PostExposeData(); Scribe.saver.FinalizeSaving();
                Scribe.loader.InitLoading(path);
                Scribe.loader.curParent = wrapper;
                restored.PostExposeData();
                Scribe.loader.crossRefs.RegisterForCrossRefResolve(wrapper);
                Scribe.loader.crossRefs.RegisterForCrossRefResolve(original);
                Scribe.loader.initer.RegisterForPostLoadInit(wrapper);
                Scribe.loader.FinalizeLoading();
            }
            finally { Scribe.ForceStop(); }
            Set(typeof(ThingWithComps), source, "comps", new List<ThingComp> { restored });
            return restored;
        }
        CompSoftDocking loaded = RoundTrip(state);
        Check(original.Docking.Owner == null && loaded.Peer == null,
            "load keeps the stored pair inert until soft docking is restored");
        Call(loaded, "Capture", original);
        Check(loaded.Peer == original && Mathf.Abs(loaded.Progress - .5f) < .001f && !loaded.Connected,
            "real Scribe round trip resumes the same peer's extension without a service mirror");
        for (int i = 0; i < 90; i++) loaded.Advance(true);
        loaded = RoundTrip(loaded);
        Call(loaded, "Capture", original);
        Check(loaded.Connected && loaded.Progress == 1f && original.Docking.Peer == source,
            "real Scribe round trip preserves an already connected port");
        Call(loaded, "Capture", replacement);
        Check(loaded.Progress == 0f && loaded.Peer == replacement && original.Docking.Owner == null,
            "changing the peer starts a new extension and releases the old backlink");
        replacement.Docking.Clear();
        Check(loaded.Peer == null && loaded.Progress == 0f && !loaded.Connected,
            "removing the opposite port clears the initiating comp");
        Call(loaded, "Capture", original);
        var service = new SoftDockService(universe);
        universe.SoftDockEnabled = true;
        ((HashSet<Building_DockingPort>)typeof(SoftDockService).GetField("ports", Private).GetValue(service)).Add(source);
        service.SetEnabled(false);
        Check(loaded.Peer == null && loaded.Progress == 0f,
            "explicitly disabling soft docking clears the port comp immediately");
        var savedManager = new UniverseManager(Current.Game)
        {
            SoftDockEnabled = true,
            ShowThrusterDebugOverlay = true,
            ShowWeaponArcOverlay = true,
            ShowCollisionDebugOverlay = true
        };
        var loadedManager = new UniverseManager(Current.Game);
        string switchPath = System.IO.Path.Combine(AppContext.BaseDirectory, "soft-dock-switch.xml");
        try
        {
            Scribe.saver.InitSaving(switchPath, "soft-dock-switch");
            savedManager.ExposeData();
            Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(switchPath);
            loadedManager.ExposeData();
            Scribe.loader.FinalizeLoading();
        }
        finally { Scribe.ForceStop(); }
        Check(loadedManager.SoftDockEnabled && loadedManager.SoftDock.Enabled
            && loadedManager.ShowThrusterDebugOverlay && loadedManager.ShowWeaponArcOverlay
            && loadedManager.ShowCollisionDebugOverlay,
            "game component Scribe round trip restores all four play-settings row switches");
        var freshManager = new UniverseManager(Current.Game);
        Check(!freshManager.SoftDock.Enabled && !freshManager.ShowThrusterDebugOverlay
            && !freshManager.ShowWeaponArcOverlay && !freshManager.ShowCollisionDebugOverlay,
            "new games default all four play-settings row switches to off");
        Find.Maps.Remove(a); Find.Maps.Remove(b);
    }

    // Observe the vanilla destruction endpoint without Unity; retirement, revalidation,
    // unregistering and pool handoff all run through the production services.
    sealed class LostCargoProbe : Thing
    {
        public Map source;
        public int destroyCalls;
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Check(mode == DestroyMode.Vanish && Thing.allowDestroyNonDestroyable,
                "lost cargo vanishes through vanilla removal even when its Def is indestructible");
            destroyCalls++;
            source.listerThings.AllThings.Remove(this);
        }
    }

    static void CheckScatteredRetirement(UniverseManager universe)
    {
        Current.Game.currentMapIndex = -1;
        Map map = TestMap(49720);
        PreparePoolMap(map, SpaceObjectTag.Scattered);
        var home = new SpaceObject(49721, new SpacePose(Vector3.forward, 0f))
            { baseTag = SpaceObjectTag.Ship, faction = Faction.OfPlayer };
        var lost = new MapSpaceObject(49720, map, new SpacePose(OrbitSphere.Move(Vector3.forward,
            new Vector2(DeltaVMod.CurrentSettings.poiDeleteRadius + 1000f, 0f)), 0f)) { baseTag = SpaceObjectTag.Scattered };
        var cargo = (LostCargoProbe)Raw(typeof(LostCargoProbe));
        cargo.def = TestBuildingDef("lost_cargo"); cargo.def.category = ThingCategory.Item; cargo.source = map;
        Set(typeof(Thing), cargo, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        map.listerThings.AllThings.Add(cargo);
        universe.objects.Add(home); universe.objects.Add(lost); universe.Scatter.Register(lost);
        SpaceMapLease lease = universe.MapPool.Restore(map);
        SpacePose far = lost.pose;
        Call(universe.Lifecycle, "TickMaterializedContainer", lost, 100f, 200f);
        Check(cargo.destroyCalls == 0 && Actions(universe).Count > 0, "distance loss queues content retirement at the frame boundary");
        lost.pose = home.pose;
        Call(universe, "RunFrameActions");
        Check(cargo.destroyCalls == 0 && universe.objects.Contains(lost), "returning within range cancels queued loss");
        lost.pose = far;
        universe.Lifecycle.ArchiveContainer(lost, map);
        Call(universe, "RunFrameActions");
        Check(cargo.destroyCalls == 1 && universe.ObjectById(lost.objectId) == null
            && universe.MapPool.IsIdle(map) && !universe.Scatter.Candidates.Contains(lost) && !Thing.allowDestroyNonDestroyable,
            "even archive requests destroy lost cargo then unregister and pool the map without an archive");
        universe.objects.Remove(home); universe.MapPool.Forget(map); Find.Maps.Remove(map);
    }
}
