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
                Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(enemy.map);
                Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(own.map);
                report.version++;
                Plan(tick);
            }
            Check(universe.Performance.engagementCompleted == searches && reposition.nextTick == deadline
                && Field<uint>(reposition, "random") == random,
                "repeated target/own structure and drive notifications neither search early nor postpone the deadline nor consume randomness");
            Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(enemy.map);
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
            var observed = new System.Collections.Generic.List<ShipStructureBatch>();
            var unaffected = new System.Collections.Generic.List<ShipStructureBatch>();
            using var local = universe.ShipStructure.WatchMap(maps[0], ShipStructureAspect.Representation, observed.Add);
            using var remote = universe.ShipStructure.WatchMap(maps[1], ShipStructureAspect.Representation, unaffected.Add);
            var weapons = hulls.Select(hull => hull.Vessel.Weapons).ToArray();
            const ShipStructureAspect dependencies = ShipStructureAspect.Representation | ShipStructureAspect.Membership;
            int Revision(Map map) => universe.ShipStructure.RevisionOf(map, dependencies);
            var oldVersions = maps.Select(Revision).ToArray();
            long publications = universe.ShipStructure.Publications;
            using (universe.ShipStructure.BeginChanges())
            {
                for (int i = 0; i < 100; i++) ShipStructureChanges.Notify(maps[0]);
                Check(observed.Count == 0, "subscribers wait for the outer structural change scope");
            }
            Check(observed.Count == 1 && observed[0].Changes.Count == 100 && unaffected.Count == 0,
                "map subscriptions receive the locally ordered delta batch once");
            Check(universe.ShipStructure.Publications == publications + 1,
                "structural publication is counted on commit without a consumer query");
            Check(Revision(maps[0]) != oldVersions[0]
                && Revision(maps[1]) == oldVersions[1]
                && universe.ShipStructure.Publications == publications + 1,
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
                var surface = (SpaceCollision.Surface)surfaces[i];
                Check(surface.NeedsHullSnapshot == (i == 0), "collision changes stay within the affected map " + i);
                SpaceCollision.BoundsRadiusOf(bodies[i]);
            }
            int a = Revision(maps[0]);
            int b = Revision(maps[1]);
            Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
            Check(Revision(maps[0]) != a
                && Revision(maps[1]) != b,
                "explicit all-world invalidation retains the frozen recovery workload");
            a = Revision(maps[0]);
            b = Revision(maps[1]);
            int count = universe.ShipStructure.MapCount;
            universe.ShipStructure.Forget(maps[0]);
            Check(universe.ShipStructure.MapCount == count - 1
                && Revision(maps[0]) != a
                && Revision(maps[1]) == b,
                "map release removes metadata and pooled reuse cannot alias the old stamp");
            var otherGame = new ShipStructureIndex(new FoundationTopologyService());
            int isolated = otherGame.RevisionOf(maps[0], dependencies);
            Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(maps[0]);
            Check(otherGame.RevisionOf(maps[0], dependencies) == isolated && otherGame.GlobalVersion == 0,
                "structure version state belongs to its universe instance");
            a = Revision(maps[0]);
            Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(null);
            Check(Revision(maps[0]) == a,
                "missing local scope never silently becomes a global invalidation");
            int worldVersion = otherGame.GlobalVersion;
            otherGame.RevisionOf(maps[1], dependencies); otherGame.Forget(maps[1]);
            Check(otherGame.GlobalVersion == worldVersion,
                "transient query-only map release does not invalidate the global identity index");

            var otherMembers = bodies[1].Ships;
            var extra = AddTestHull(maps[0], faction, 121050);
            Current.Game.GetComponent<UniverseManager>().ShipStructure.Changed(maps[0]);
            Check(bodies[0].Ships.Select(s => s.Identity).Distinct().Count() == 2
                && ReferenceEquals(otherMembers, bodies[1].Ships),
                "same-map multiple ships rebuild together without changing another map");
            int sourceVersion = Revision(maps[0]);
            int targetVersion = Revision(maps[1]);
            var moved = extra.parent;
            TransferFixtureBuilding((Building)moved, maps[1]);
            Check(bodies[0].Ships.Count() == 1 && bodies[1].Ships.Count() == 2
                && Revision(maps[0]) != sourceVersion
                && Revision(maps[1]) != targetVersion
                && extra.Vessel.Map == maps[1],
                "transfer invalidates both maps and retains the moved ship identity");
        }
        finally
        {
            foreach (var body in bodies) universe.objects.Remove(body);
            foreach (var map in maps)
            {
                universe.ShipStructure.Forget(map);
                ShipRegistry.Forget(map); ShipPhysics.Forget(map); SpaceCollision.Forget(map);
                Find.Maps.Remove(map);
            }
        }
    }
}
