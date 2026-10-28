using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // Preview uses the same allocation core without writing Unity's render clock.
    // Budget/throttle consumption is checked separately below; drive gates and integration remain real.
    sealed class OfflineShuttleSpaceObject : MapSpaceObject
    {
        public OfflineShuttleSpaceObject(int id, Map map, SpacePose pose) : base(id, map, pose) { }
        protected override void AllocateDrive(ShipPhysics.MassReport report, ShipPhysics.Command command,
            out Vector2 force, out float torque) => ShipPhysics.PreviewAllocation(report, command, out force, out torque);
    }
    static T FlightField<T>(object flight, string name) =>
        (T)flight.GetType().GetField(name).GetValue(flight);

    static void CheckProjectileFlight(UniverseManager universe, Faction player, bool flightOnly = false)
    {
        var service = universe.Projectiles;
        var props = new CompProperties_SpaceProjectile
        {
            guided = true, speed = 32, coldLaunchTicks = 30, initialTurnTicks = 12,
            initialTurnRateDegPerSec = 1080, turnRateDegPerSec = 540, accelerationTicks = 6
        };
        var body = new SpaceObject(41000, new SpacePose(Vector3.forward, 0))
            { baseTag = SpaceObjectTag.Projectile };
        object flight = AddProjectileRecord(universe, body);
        Set(flight, "props", props);
        Set(flight, "cruiseSpeed", props.speed);
        Set(flight, "motorDeltaVelocityPerTick", 4f);
        Set(flight, "coldLaunchEndsTick", 30);
        body.pose.velocity = new Vector2(8, 0);
        void Aim(float angle) => Set(flight, "targetPose", new SpacePose(
            OrbitSphere.Move(body.pose.position, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 100), 0));
        void Step(int tick, bool target = true) => Call(service, "AdvanceFlight", flight, target, tick);
        string Phase() => FlightField<object>(flight, "phase").ToString();
        Aim(Mathf.PI * 0.5f);
        for (int tick = 0; tick < 30; tick++) Step(tick);
        Check(body.pose.velocity == new Vector2(8, 0) && body.pose.angle == 0 && Phase() == "ColdLaunch",
            "cold launch preserves ejection velocity and cannot turn or accelerate");
        Step(30);
        Check(Phase() == "InitialTurn" && Math.Abs(body.pose.angle * Mathf.Rad2Deg - 18) < 0.01f
            && body.pose.velocity == new Vector2(8, 0) && FlightField<float>(flight, "rcsTurnSign") > 0,
            "initial RCS turns the nose without rotating inherited translational velocity");
        for (int tick = 31; tick <= 34; tick++) Step(tick);
        Check(Phase() == "PoweredFlight" && body.pose.velocity.x > 7f
            && body.pose.velocity.y > 0f && (body.pose.velocity - new Vector2(8, 0)).magnitude <= 4.001f,
            "right-angle departure starts bounded thrust after the initial turn");
        for (int tick = 35; tick <= 59; tick++)
        {
            Vector2 before = body.pose.velocity;
            Step(tick);
            Check((body.pose.velocity - before).magnitude <= 4.001f,
                "powered guidance changes velocity by at most one thrust step " + tick);
        }
        Check((body.pose.velocity - new Vector2(0, 32)).magnitude < 4f
            && Math.Abs(body.pose.angle - Mathf.PI * 0.5f) < 0.1f,
            "powered guidance approaches cruise without turning the nose away from the target");
        Aim(Mathf.PI);
        float oldHeading = body.pose.angle;
        Vector2 beforeTurn = body.pose.velocity;
        Step(60);
        Check(Math.Abs(SpaceTransform.WrapAngle(body.pose.angle - oldHeading) * Mathf.Rad2Deg) <= 9.01f
            && (body.pose.velocity - beforeTurn).magnitude <= 4.001f,
            "cruise turn and thrust stay within their angular and acceleration limits");
        var velocity = body.pose.velocity;
        Step(61, false);
        Check((body.pose.velocity - velocity).magnitude < 0.0001f, "lost target coasts without another turn");

        // Restart the production phase machine; continuously changing bearing must not trap it in RCS.
        Set(flight, "phase", Enum.Parse(FlightField<object>(flight, "phase").GetType(), "ColdLaunch"));
        Set(flight, "coldLaunchEndsTick", 70);
        body.pose.velocity = new Vector2(8, 0); body.pose.angle = 0;
        for (int tick = 70; tick < 82; tick++) { Aim(body.pose.angle + Mathf.PI); Step(tick); }
        Check(Phase() == "PoweredFlight" && body.pose.velocity.magnitude > 0f,
            "opposite-bearing target cannot stall turning or extend RCS beyond its deadline");
        Vector2 beforeLostTargetBurn = body.pose.velocity;
        for (int tick = 82; tick <= 86; tick++) Step(tick, false);
        Check((body.pose.velocity - beforeLostTargetBurn).magnitude > 0f,
            "losing the target during ignition finishes the bounded motor burn");

        Set(flight, "phase", Enum.Parse(FlightField<object>(flight, "phase").GetType(), "ColdLaunch"));
        Set(flight, "coldLaunchEndsTick", 130);
        body.pose.velocity = new Vector2(1008, 0); body.pose.angle = 0;
        Set(flight, "targetPose", new SpacePose(
            OrbitSphere.Move(body.pose.position, new Vector2(100, 0)), 0)
            { velocity = new Vector2(1000, 0) });
        for (int tick = 100; tick < 130; tick++) Step(tick);
        Check(body.pose.velocity == new Vector2(1008, 0),
            "cold launch preserves a shared 1000 cells per second drift");
        for (int tick = 130; tick < 136; tick++) Step(tick);
        Vector2 targetVelocityAtMissile = SpaceTransform.TransportVelocity(
            FlightField<SpacePose>(flight, "targetPose"), body.pose.position);
        Check((body.pose.velocity - targetVelocityAtMissile - new Vector2(32, 0)).magnitude < 0.1f
            && body.pose.velocity.x > 1000f,
            "guidance accelerates to 32 cells per second relative to a co-drifting target");
        Step(136);
        Check((body.pose.velocity - targetVelocityAtMissile - new Vector2(32, 0)).magnitude < 0.1f
            && !FlightField<bool>(flight, "motorThrusting"),
            "guidance retains common drift and stops thrust at the desired relative velocity");
        float separation = OrbitSphere.Offset(body.pose.position,
            FlightField<SpacePose>(flight, "targetPose").position).magnitude;
        for (int tick = 137; tick < 350 && separation > 1f; tick++)
        {
            Step(tick);
            Vector3 missileBefore = body.pose.position;
            body.pose.position = OrbitSphere.Move(missileBefore, body.pose.velocity / 60f);
            body.pose.velocity = SpaceTransform.TransportTangent(body.pose.velocity,
                missileBefore, body.pose.position);
            SpacePose movingTarget = FlightField<SpacePose>(flight, "targetPose");
            Vector3 targetBefore = movingTarget.position;
            movingTarget.position = OrbitSphere.Move(targetBefore, movingTarget.velocity / 60f);
            movingTarget.velocity = SpaceTransform.TransportTangent(movingTarget.velocity,
                targetBefore, movingTarget.position);
            Set(flight, "targetPose", movingTarget);
            separation = OrbitSphere.Offset(body.pose.position, movingTarget.position).magnitude;
        }
        Check(separation <= 1f,
            "missile closes on a target while both retain their shared 1000 cells per second drift");

        // A tiny speed residual must not make the nose chase alternating correction signs.
        body.pose.position = Vector3.forward;
        body.pose.angle = 0f;
        body.pose.velocity = new Vector2(1032.1f, 0f);
        Vector3 steadyTarget = OrbitSphere.Move(body.pose.position, new Vector2(100, 0));
        bool stableHeading = true;
        for (int tick = 350; tick < 380; tick++)
        {
            float jitter = tick % 2 == 0 ? 0.1f : -0.1f;
            Set(flight, "targetPose", new SpacePose(steadyTarget, 0f)
                { velocity = new Vector2(1000f + jitter, jitter) });
            Step(tick);
            stableHeading &= Mathf.Abs(body.pose.angle * Mathf.Rad2Deg) < 1f
                && !FlightField<bool>(flight, "motorThrusting");
        }
        Check(stableHeading, "near-cruise target velocity jitter cannot flip the missile nose");

        props.guided = false;
        body.pose.velocity = new Vector2(65, 7);
        Step(400, false);
        Check(body.pose.velocity == new Vector2(65, 7), "unguided rounds retain inherited velocity instead of being reset to nominal speed");
        Call(service, "DestroyProjectile", flight, "flight regression");
        Call(universe, "RunFrameActions");
        Check(FlightField<float>(flight, "rcsTurnSign") == 0
            && FlightField<int>(flight, "motorStartedTick") == 0
            && FlightField<float>(flight, "motorDeltaVelocityPerTick") == 0
            && !FlightField<bool>(flight, "motorThrusting"),
            "pooled flight record discards stage and RCS state");
        if (flightOnly) return;

        // Read the real Def so nozzle checks detect drift between art alignment and physics configuration.
        XElement xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_ShuttlesAndMoonPool.xml")
            .Descendants("ThingDef").Single(x => (string)x.Element("defName") == "DeltaV_ShuttleBoarding");
        XElement flightXml = xml.Element("comps").Elements("li")
            .Single(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_SpaceProjectile");
        var boardingProps = new CompProperties_SpaceProjectile
        {
            hasMap = true,
            ignoresShields = (bool)flightXml.Element("ignoresShields")
        };
        Vector2 Pair(string value)
        {
            string[] parts = value.Trim('(', ')').Split(',');
            return new Vector2(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        var layout = xml.Descendants("virtualThrusters").Single().Elements("li")
            .Select(x => new ShuttleThrusterConfig { position = Pair((string)x.Element("position")), force = Pair((string)x.Element("force")) }).ToList();
        Check(layout.Count == 6 && layout.Count(x => x.force.x > 0 && x.force.y == 0) == 2
            && Math.Abs(layout.Where(x => x.force.x > 0).Sum(x => x.position.y * x.force.x)) < 0.001f,
            "boarding Def has six thrusters including two symmetric main engines");
        var targetBody = new SpaceObject(41001, new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(100, 0)), 0));
        universe.objects.Add(targetBody);
        Set(universe, "objectById", null); // Fixture bypasses RegisterObject.
        var savedGrid = Find.World.grid;
        var fixtureLayer = (RimWorld.Planet.PlanetLayer)Raw(typeof(RimWorld.Planet.PlanetLayer));
        Set(fixtureLayer, "def", new PlanetLayerDef { isSpace = true });
        Find.World.grid = (RimWorld.Planet.WorldGrid)Raw(typeof(RimWorld.Planet.WorldGrid));
        Set(Find.World.grid, "planetLayers", new Dictionary<int, RimWorld.Planet.PlanetLayer> { [0] = fixtureLayer });
        for (int rotation = 0; rotation < 4; rotation++)
        {
            Map map = TestMap(41010 + rotation);
            var hull = AddTestHull(map, player, 41020 + rotation,
                new CompSpaceShuttle { props = new CompProperties_SpaceShuttle { virtualThrusters = layout } });
            hull.parent.Rotation = new Rot4(rotation);
            hull.parent.def.comps.Add(boardingProps);
            hull.parent.def.modExtensions = new List<DefModExtension> { new DefModExtension_BoardingProfile
                { canBoard = true, boostMultiplier = 2f, boostTicks = 600, virtualThrusters = layout } };
            var container = new OfflineShuttleSpaceObject(41030 + rotation, map, new SpacePose(Vector3.forward, 0.71f));
            universe.objects.Add(container);
            Set(universe, "objectById", null);
            Check(service.AdoptShuttle(container, (Building)hull.parent, CombatTarget.ForContainer(targetBody)),
                "intrusion adopts the real rotated shuttle " + rotation);
            object adopted = ProjectileRecords(service).Cast<object>().Single();
            var physical = new ShipPhysics.MassReport { mass = 140f, inertia = 400f };
            physical.thrusters.AddRange(hull.Vessel.BuildThrusters());
            var drive = new DriveInfo(physical, hull.Vessel.FrameAngle);
            ShipPhysics.RefreshOperational(physical);
            Check(physical.thrusters.Select((t, i) => Math.Abs(t.thrust - layout[i].force.magnitude * (i < 2 ? 2 : 1)) < 0.001f).All(x => x),
                "only the two physical tail engines double their thrust " + rotation);
            var baseline = new ShipPhysics.MassReport { mass = physical.mass, inertia = physical.inertia };
            baseline.thrusters.AddRange(hull.Vessel.BuildThrusters());
            hull.intrusionDriveActive = false;
            var expectedModel = ShipPhysics.BuildControlModel(baseline);
            hull.intrusionDriveActive = true;
            var futureModel = drive.PreviewUnboostedModel(new ShipPhysics.ControlModel());
            Check(Math.Abs(futureModel.positiveX - expectedModel.positiveX) < 0.001f
                && Math.Abs(futureModel.positiveTorque - expectedModel.positiveTorque) < 0.001f
                && Math.Abs(physical.thrusters[0].thrust - layout[0].force.magnitude * 2) < 0.001f,
                "post-boost preview matches ordinary allocation without changing live thrust " + rotation);
            for (int i = 0; i < 6; i++)
                Check((SpaceTransform.RotateInverse(physical.thrusters[i].dir, hull.Vessel.FrameAngle) - layout[i].force.normalized).magnitude < 0.001f,
                    "physical thruster follows hull rotation " + rotation + "/" + i);
            hull.ConsumeBoostTick(drive);
            physical.thrusters[2].throttle = 1;
            hull.ConsumeBoostTick(drive);
            Check(hull.BoostTicksRemaining == 600, "idle and oblique thrusters do not consume boost " + rotation);
            physical.thrusters[0].throttle = physical.thrusters[1].throttle = 1;
            hull.ConsumeBoostTick(drive);
            Check(hull.BoostTicksRemaining == 599, "two firing main engines consume one shared boost tick " + rotation);
            if (rotation == 0)
            {
                var restored = new CompSpaceShuttle { parent = hull.parent, props = hull.props };
                string path = System.IO.Path.Combine(AppContext.BaseDirectory, "shuttle-boost.xml");
                try
                {
                    Scribe.saver.InitSaving(path, "shuttle"); hull.PostExposeData(); Scribe.saver.FinalizeSaving();
                    Scribe.loader.InitLoading(path); restored.PostExposeData(); Scribe.loader.FinalizeLoading();
                }
                finally { Scribe.ForceStop(); }
                Check(restored.BoostTicksRemaining == 599 && !restored.intrusionDriveActive,
                    "real Scribe preserves remaining boost but does not restore the intrusion task");
            }
            Check(universe.Autopilot.IsActive(container) && container.AppliesPassiveDrag,
                "intrusion uses real autopilot and ordinary ship drag " + rotation);
            Vector2 inherited = new Vector2(50, 3);
            container.pose.velocity = inherited;
            container.pose.angularVelocity = 0.4f;
            service.Tick();
            Check(container.pose.velocity == inherited && container.pose.angularVelocity == 0.4f,
                "collision service does not steer or integrate the boarding shuttle " + rotation);
            service.ReleaseShuttle(container);
            Call(universe, "RunFrameActions");
            Check(!container.projectileRoleOverride && !universe.Autopilot.IsActive(container)
                && !hull.intrusionDriveActive,
                "abort detaches intrusion autopilot and turns off boost " + rotation);
            Check(container.pose.velocity == inherited && container.pose.angularVelocity == 0.4f,
                "aborting intrusion preserves linear and angular momentum " + rotation);
            Check(hull.BoostTicksRemaining == 599 && service.AdoptShuttle(container, (Building)hull.parent, CombatTarget.ForContainer(targetBody)),
                "restarting intrusion preserves its consumed boost " + rotation);
            for (int tick = 0; tick < 650; tick++) hull.ConsumeBoostTick(drive);
            ShipPhysics.RefreshOperational(physical);
            Check(hull.BoostTicksRemaining == 0 && physical.thrusters.Select((t, i) => Math.Abs(t.thrust - layout[i].force.magnitude) < 0.001f).All(x => x),
                "boost expires at 600 burn ticks and every thruster returns to its ordinary rating " + rotation);
            // Same runtime order as Universe: capture sweep start, MPC, then drift guard.
            service.Tick();
            object resumed = ProjectileRecords(service).Cast<object>().Single();
            Vector3 start = container.pose.position;
            universe.Autopilot.Tick();
            Vector3 end = container.pose.position;
            container.AdvanceDrift(1f / 60f);
            Check(OrbitSphere.Offset(start, end).magnitude > 0.01f && container.pose.position == end
                && FlightField<Vector3>(resumed, "prevPosition") == start,
                "MPC integrates once and the collision sweep retains the pre-drive start " + rotation
                + " distance=" + OrbitSphere.Offset(start, end).magnitude);
            service.ReleaseShuttle(container);
            hull.Vessel.NotifyLanded();
            Check(hull.BoostTicksRemaining == 600 && !hull.intrusionDriveActive, "landing rearms boost " + rotation);
            Call(universe, "RunFrameActions");
            universe.objects.Remove(container);
            Find.Maps.Remove(map);
        }
        universe.objects.Remove(targetBody);
        Find.World.grid = savedGrid;
    }
}
