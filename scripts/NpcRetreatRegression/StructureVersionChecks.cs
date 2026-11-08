using System;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckEngagementEventClock(UniverseManager universe, ManeuverMotionObject own, ManeuverMotionObject enemy)
    {
        int savedTick = Find.TickManager.TicksGame;
        T Field<T>(object value, string name) => (T)HarmonyLib.AccessTools.Field(value.GetType(), name).GetValue(value);
        var planner = new ShipAutoManeuverControl(universe, own.member.Identity);
        Set(planner, "anchorTarget", CombatTarget.ForShip(enemy.member.Identity));
        Set(planner, "anchorDistance", 100f); Set(planner, "offenseEnabled", true);
        own.TryGetDrive(own.member.Identity, out var drive);
        var report = Field<ShipPhysics.MassReport>(drive, "report");
        int savedVersion = report.version;
        var method = HarmonyLib.AccessTools.Method(typeof(ShipAutoManeuverControl), "PlanTacticTrajectory");
        void Plan(int tick)
        {
            Set(Find.TickManager, "ticksGameInt", tick);
            object[] args = { own, ShipRegistry.ShipPose(enemy.member), 10f, drive, null };
            Check((bool)method.Invoke(planner, args), "event-clock planner retains a valid continuous reference at " + tick);
        }
        try
        {
            Plan(0);
            var reposition = Field<CombatReposition>(planner, "reposition");
            int deadline = reposition.nextTick;
            uint random = Field<uint>(reposition, "random");
            long searches = universe.Performance.engagementCompleted;
            for (int tick = 30; tick < deadline; tick += 30)
            {
                SubstructureOwnership.BumpStructureVersion(enemy.map);
                SubstructureOwnership.BumpStructureVersion(own.map);
                report.version++;
                Plan(tick);
            }
            Check(universe.Performance.engagementCompleted == searches && reposition.nextTick == deadline
                && Field<uint>(reposition, "random") == random,
                "repeated target/own structure and drive notifications neither search early nor postpone the deadline nor consume randomness");
            SubstructureOwnership.BumpStructureVersion(enemy.map);
            Plan(deadline);
            Check(universe.Performance.engagementCompleted == searches + 1 && reposition.nextTick > deadline,
                "normal due decision still completes exactly once under continuous structural events");
        }
        finally { report.version = savedVersion; Set(Find.TickManager, "ticksGameInt", savedTick); }
    }

    static void CheckStructureVersions(UniverseManager universe, Faction faction)
    {
        var maps = new[] { TestMap(121000), TestMap(121100) };
        var hulls = maps.Select((map, i) => AddTestHull(map, faction, 121001 + i * 100,
            new ExtendedHullComp { props = new CompProperties_SpaceShuttle() })).ToArray();
        var bodies = maps.Select((map, i) => new MapSpaceObject(121000 + i * 100, map,
            new SpacePose(Vector3.forward, 0))).ToArray();
        foreach (var body in bodies) universe.objects.Add(body);
        try
        {
            var members = bodies.Select(body => body.Ships).ToArray();
            var reports = bodies.Select(ShipPhysics.GetReport).ToArray();
            var surfaces = bodies.Select(body => HarmonyLib.AccessTools.Method(typeof(SpaceCollision),
                "SurfaceFor").Invoke(null, new object[] { body })).ToArray();
            var surfaceVersions = surfaces.Select(surface => (int)HarmonyLib.AccessTools.Field(surface.GetType(), "structureVersion").GetValue(surface)).ToArray();
            var weapons = hulls.Select(hull => hull.Vessel.Weapons).ToArray();
            var oldVersions = maps.Select(SubstructureOwnership.StructureVersion).ToArray();
            long publications = universe.Structures.Publications;
            for (int i = 0; i < 100; i++) ShipStructureChanges.Notify(maps[0]);
            Check(universe.Structures.Publications == publications,
                "structure events mark dirty without eagerly rebuilding facts");
            Check(SubstructureOwnership.StructureVersion(maps[0]) != oldVersions[0]
                && SubstructureOwnership.StructureVersion(maps[1]) == oldVersions[1]
                && universe.Structures.Publications == publications + 1,
                "one hundred local events coalesce into one publication without touching the other map");
            Check(!ReferenceEquals(members[0], bodies[0].Ships) && ReferenceEquals(members[1], bodies[1].Ships),
                "local structure changes rebuild only local member discovery");
            Check(!ReferenceEquals(reports[0], ShipPhysics.GetReport(bodies[0]))
                && ReferenceEquals(reports[1], ShipPhysics.GetReport(bodies[1])),
                "local structure changes preserve unrelated physical report and allocator identity");
            Check(!ReferenceEquals(weapons[0], hulls[0].Vessel.Weapons)
                && ReferenceEquals(weapons[1], hulls[1].Vessel.Weapons),
                "local structure changes preserve unrelated weapon collections");
            for (int i = 0; i < 2; i++)
            {
                SpaceCollision.BoundsRadiusOf(bodies[i]);
                var current = (int)HarmonyLib.AccessTools.Field(surfaces[i].GetType(), "structureVersion").GetValue(surfaces[i]);
                Check((surfaceVersions[i] == current) == (i == 1),
                    "collision surface rebuild stays within the changed map " + i);
            }
            int a = SubstructureOwnership.StructureVersion(maps[0]);
            int b = SubstructureOwnership.StructureVersion(maps[1]);
            SubstructureOwnership.BumpStructureVersion();
            Check(SubstructureOwnership.StructureVersion(maps[0]) != a
                && SubstructureOwnership.StructureVersion(maps[1]) != b,
                "explicit all-world invalidation retains the frozen recovery workload");
            a = SubstructureOwnership.StructureVersion(maps[0]);
            b = SubstructureOwnership.StructureVersion(maps[1]);
            int count = universe.Structures.MapCount;
            universe.Structures.Forget(maps[0]);
            Check(universe.Structures.MapCount == count - 1
                && SubstructureOwnership.StructureVersion(maps[0]) != a
                && SubstructureOwnership.StructureVersion(maps[1]) == b,
                "map release removes metadata and pooled reuse cannot alias the old stamp");
            var otherGame = new StructureVersions();
            int isolated = otherGame.For(maps[0]);
            SubstructureOwnership.BumpStructureVersion(maps[0]);
            Check(otherGame.For(maps[0]) == isolated && otherGame.GlobalVersion == 0,
                "structure version state belongs to its universe instance");
            a = SubstructureOwnership.StructureVersion(maps[0]);
            SubstructureOwnership.BumpStructureVersion(null);
            Check(SubstructureOwnership.StructureVersion(maps[0]) == a,
                "missing local scope never silently becomes a global invalidation");
            int worldVersion = otherGame.GlobalVersion;
            otherGame.For(maps[1]); otherGame.Forget(maps[1]);
            Check(otherGame.GlobalVersion == worldVersion,
                "transient query-only map release does not invalidate the global identity index");

            var otherMembers = bodies[1].Ships;
            var extra = AddTestHull(maps[0], faction, 121050);
            SubstructureOwnership.BumpStructureVersion(maps[0]);
            Check(bodies[0].Ships.Select(s => s.Identity).Distinct().Count() == 2
                && ReferenceEquals(otherMembers, bodies[1].Ships),
                "same-map multiple ships rebuild together without changing another map");
            int sourceVersion = SubstructureOwnership.StructureVersion(maps[0]);
            int targetVersion = SubstructureOwnership.StructureVersion(maps[1]);
            var moved = extra.parent;
            maps[0].thingGrid.Deregister(moved);
            maps[0].listerThings.AllThings.Remove(moved);
            maps[0].listerThings.ThingsOfDef(moved.def).Remove(moved);
            maps[0].listerThings.ThingsInGroup(ThingRequestGroup.Transporter).Remove(moved);
            ((ThingOwner<Thing>)maps[0].spawnedThings).InnerListForReading.Remove(moved);
            Set(typeof(Thing), moved, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(maps[1]));
            maps[1].listerThings.AllThings.Add(moved);
            var byDef = (System.Collections.Generic.Dictionary<ThingDef, System.Collections.Generic.List<Thing>>)
                HarmonyLib.AccessTools.Field(typeof(ListerThings), "listsByDef").GetValue(maps[1].listerThings);
            byDef[moved.def] = new System.Collections.Generic.List<Thing> { moved };
            maps[1].listerThings.ThingsInGroup(ThingRequestGroup.Transporter).Add(moved);
            ((ThingOwner<Thing>)maps[1].spawnedThings).InnerListForReading.Add(moved);
            maps[1].thingGrid.Register(moved);
            SubstructureOwnership.BumpStructureVersion(maps[0]);
            SubstructureOwnership.BumpStructureVersion(maps[1]);
            Check(bodies[0].Ships.Count() == 1 && bodies[1].Ships.Count() == 2
                && SubstructureOwnership.StructureVersion(maps[0]) != sourceVersion
                && SubstructureOwnership.StructureVersion(maps[1]) != targetVersion
                && extra.Vessel.Map == maps[1],
                "transfer invalidates both maps and retains the moved ship identity");
        }
        finally
        {
            foreach (var body in bodies) universe.objects.Remove(body);
            foreach (var map in maps)
            {
                universe.Structures.Forget(map);
                ShipRegistry.Forget(map); ShipPhysics.Forget(map); SpaceCollision.Forget(map);
                Find.Maps.Remove(map);
            }
        }
    }
}
