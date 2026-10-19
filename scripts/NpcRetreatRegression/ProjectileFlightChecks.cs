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
    static T FlightField<T>(object flight, string name) =>
        (T)flight.GetType().GetField(name).GetValue(flight);

    static void CheckProjectileFlight(UniverseManager universe, Faction player)
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
            && Math.Abs(body.pose.velocity.magnitude - 8) < 0.001f && FlightField<float>(flight, "rcsTurnSign") > 0,
            "initial RCS turn is stronger than cruise with no premature main-engine acceleration");
        for (int tick = 31; tick <= 34; tick++) Step(tick);
        Check(Phase() == "PoweredFlight" && Math.Abs(body.pose.angle - Mathf.PI * 0.5f) < 0.001f,
            "right-angle departure completes in five ticks and exits the initial turn early");
        float previous = body.pose.velocity.magnitude;
        bool monotonic = true;
        for (int tick = 35; tick <= 41; tick++)
        {
            Step(tick);
            monotonic &= body.pose.velocity.magnitude >= previous - 0.001f && body.pose.velocity.magnitude <= 32.001f;
            previous = body.pose.velocity.magnitude;
        }
        Check(monotonic && Math.Abs(previous - 32) < 0.001f, "main engine ramps to the unchanged cruise speed without overshoot");
        Aim(Mathf.PI);
        float oldHeading = body.pose.angle;
        Step(42);
        Check(Math.Abs(SpaceTransform.WrapAngle(body.pose.angle - oldHeading) * Mathf.Rad2Deg - 9) < 0.01f,
            "cruise retains its original 540 degrees per second tracking authority");
        var velocity = body.pose.velocity;
        Step(43, false);
        Check((body.pose.velocity - velocity).magnitude < 0.0001f, "lost target coasts without another turn");

        // Restart the production phase machine; continuously changing bearing must not trap it in RCS.
        Set(flight, "phase", Enum.Parse(FlightField<object>(flight, "phase").GetType(), "ColdLaunch"));
        Set(flight, "coldLaunchEndsTick", 50);
        body.pose.velocity = new Vector2(8, 0); body.pose.angle = 0;
        for (int tick = 50; tick < 62; tick++) { Aim(body.pose.angle + Mathf.PI); Step(tick); }
        Check(Phase() == "PoweredFlight" && body.pose.velocity.magnitude > 7.99f,
            "opposite-bearing target cannot stall turning or extend RCS beyond its deadline");
        for (int tick = 62; tick <= 68; tick++) Step(tick, false);
        Check(Math.Abs(body.pose.velocity.magnitude - 32) < 0.001f,
            "losing the target during acceleration still finishes the main-engine ramp");

        props.guided = false;
        body.pose.velocity = new Vector2(65, 7);
        Step(100, false);
        Check(body.pose.velocity == new Vector2(65, 7), "unguided rounds retain inherited velocity instead of being reset to nominal speed");
        Call(service, "DestroyProjectile", flight, "flight regression");
        Call(universe, "RunFrameActions");
        Check(FlightField<float>(flight, "rcsTurnSign") == 0 && FlightField<int>(flight, "motorStartedTick") == 0,
            "pooled flight record discards stage and RCS state");

        // Read the real Def so nozzle checks detect drift between art alignment and physics configuration.
        XElement xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_ShuttlesAndMoonPool.xml")
            .Descendants("ThingDef").Single(x => (string)x.Element("defName") == "DeltaV_ShuttleBoarding");
        Vector2 Pair(string value)
        {
            string[] parts = value.Trim('(', ')').Split(',');
            return new Vector2(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        var layout = xml.Descendants("virtualThrusters").Single().Elements("li")
            .Select(x => new ShuttleThrusterConfig { position = Pair((string)x.Element("position")), force = Pair((string)x.Element("force")) }).ToList();
        Check(layout.Count(x => x.force.x > 0) == 2 && Math.Abs(layout.Sum(x => Math.Max(0, x.force.x)) - 280) < 0.001f
            && Math.Abs(layout.Where(x => x.force.x > 0).Sum(x => x.position.y * x.force.x)) < 0.001f,
            "boarding Def has two symmetric main engines preserving total forward thrust and zero net yaw");
        var targetBody = new SpaceObject(41001, new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(100, 0)), 0));
        universe.objects.Add(targetBody);
        for (int rotation = 0; rotation < 4; rotation++)
        {
            Map map = TestMap(41010 + rotation);
            var hull = AddTestHull(map, player, 41020 + rotation,
                new CompSpaceShuttle { props = new CompProperties_SpaceShuttle { virtualThrusters = layout } });
            hull.parent.Rotation = new Rot4(rotation);
            hull.parent.def.comps.Add(new CompProperties_SpaceProjectile { guided = true, hasMap = true });
            var container = new MapSpaceObject(41030 + rotation, map, new SpacePose(Vector3.forward, 0.71f));
            universe.objects.Add(container);
            Check(service.AdoptShuttle(container, (Building)hull.parent, CombatTarget.ForContainer(targetBody)),
                "intrusion adopts the real rotated shuttle " + rotation);
            object adopted = ProjectileRecords(service).Cast<object>().Single();
            Check((int)Call(service, "ExhaustCount", adopted) == 2, "intrusion uses both main nozzles " + rotation);
            var actualThrusters = hull.Vessel.BuildThrusters().Take(2).ToArray();
            for (int i = 0; i < 2; i++)
            {
                SpacePose nozzle = (SpacePose)Call(service, "ExhaustPose", adopted, i);
                Vector2 local = SpaceTransform.UniverseToShipLocal(container, nozzle.position);
                Check((local - actualThrusters[i].localPos).magnitude < 0.01f
                    && Math.Abs(SpaceTransform.WrapAngle(nozzle.angle - container.pose.angle - hull.Vessel.FrameAngle)) < 0.001f,
                    "afterburner shares physical nozzle position and heading for rotation/port " + rotation + "/" + i);
            }
            service.ReleaseShuttle(container);
            Call(universe, "RunFrameActions");
            Check(!container.projectileRoleOverride && FlightField<IList>(adopted, "exhaustPorts").Count == 0,
                "abort clears afterburner ports and restores ordinary flight " + rotation);
            universe.objects.Remove(container);
            Find.Maps.Remove(map);
        }
        universe.objects.Remove(targetBody);
    }
}
