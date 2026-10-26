using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DeltaV;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // Only the Unity-backed Building destruction endpoint is observed here; queueing,
    // identity lookup, retirement, map pooling and reset stages use the production DLL.
    sealed class CleanupProbeBuilding : Building
    {
        public int destroyCalls;
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish) => destroyCalls++;
    }

    static IList ProjectileRecords(SpaceProjectileService service) =>
        (IList)typeof(SpaceProjectileService).GetField("projectiles", Private).GetValue(service);
    static int RecycledCount(SpaceProjectileService service) =>
        ((ICollection)typeof(SpaceProjectileService).GetField("projectilePool", Private).GetValue(service)).Count;
    static object AddProjectileRecord(UniverseManager universe, SpaceObject container, bool shuttle = false)
    {
        Type type = typeof(SpaceProjectileService).GetNestedType("Projectile", BindingFlags.NonPublic);
        object record = Activator.CreateInstance(type, true);
        Set(record, "containerId", container.objectId);
        var def = (ThingDef)Raw(typeof(ThingDef));
        def.defName = "reset_test";
        Set(record, "def", def);
        Set(record, "props", new CompProperties_SpaceProjectile());
        Set(record, "adoptedShuttle", shuttle);
        container.projectileRoleOverride = shuttle;
        universe.RegisterObject(container);
        ProjectileRecords(universe.Projectiles).Add(record);
        if (container.map != null)
        {
            PreparePoolMap(container.map, SpaceObjectTag.Projectile);
            Set(record, "mapLease", universe.MapPool.Restore(container.map));
        }
        return record;
    }

    static void QueueFrame(UniverseManager universe, Action action) => Call(universe, "QueueFrameAction", action);

    static void CheckSceneReset(UniverseManager universe)
    {
        var service = universe.Projectiles;
        int before = RecycledCount(service);
        var plain = new SpaceObject(1800, new SpacePose(Vector3.forward, 0)) { baseTag = SpaceObjectTag.Projectile };
        object first = AddProjectileRecord(universe, plain);
        Check(universe.ObjectById(plain.objectId) == plain && service.InFlightCount == 1,
            "projectile fixture registers its identity before destruction or reset");
        Call(service, "DestroyProjectile", first, "test");
        Action oldCleanup = Actions(universe)[Actions(universe).Count - 1];
        service.ResetFlow();
        service.ResetFlow();
        Check(universe.ObjectById(plain.objectId) == plain && service.InFlightCount == 1,
            "reset retains projectile identity until its already-pending cleanup runs");
        Call(universe, "RunFrameActions");
        oldCleanup();
        Check(universe.ObjectById(plain.objectId) == null && service.InFlightCount == 0
            && RecycledCount(service) == before + 1,
            "dead projectile plus repeated reset and callback replay recycles exactly once");

        // Reuse the actual pooled record, then replay its old cleanup callback.
        object pool = typeof(SpaceProjectileService).GetField("projectilePool", Private).GetValue(service);
        object reused = pool.GetType().GetMethod("Pop").Invoke(pool, null);
        var next = new SpaceObject(1801, plain.pose) { baseTag = SpaceObjectTag.Projectile };
        universe.RegisterObject(next);
        Set(reused, "containerId", next.objectId);
        var nextDef = (ThingDef)Raw(typeof(ThingDef));
        nextDef.defName = "next_projectile";
        Set(reused, "def", nextDef);
        Set(reused, "props", new CompProperties_SpaceProjectile());
        ProjectileRecords(service).Add(reused);
        Call(service, "DestroyProjectile", reused, "next");
        oldCleanup();
        Check(service.InFlightCount == 1 && universe.ObjectById(next.objectId) == next,
            "stale cleanup cannot touch a reused projectile record with a new identity");
        Call(universe, "RunFrameActions");

        var external = new SpaceObject(1802, plain.pose) { baseTag = SpaceObjectTag.Projectile };
        AddProjectileRecord(universe, external);
        universe.UnregisterObject(external);
        universe.UnregisterObject(external);
        Check(service.InFlightCount == 1, "external unregister queues ordinary projectile retirement");
        Call(universe, "RunFrameActions");
        Check(service.InFlightCount == 0, "retirement completes even after registry lookup returns null");

        var missing = new SpaceObject(1803, plain.pose) { baseTag = SpaceObjectTag.Projectile };
        AddProjectileRecord(universe, missing);
        universe.objects.Remove(missing); // Simulate a pre-existing orphan, bypassing the supported unregister entry.
        Set(universe, "objectById", null);
        Check(universe.ObjectById(missing.objectId) == null,
            "orphan fixture has no registered identity before reset");
        service.ResetFlow();
        Call(universe, "RunFrameActions");
        Check(service.InFlightCount == 0, "reset also retires an already-missing container without dereferencing it");

        Map map = TestMap(1804);
        var mapped = new MapSpaceObject(1804, map, plain.pose) { baseTag = SpaceObjectTag.Projectile };
        object mappedRecord = AddProjectileRecord(universe, mapped);
        var building = (CleanupProbeBuilding)Raw(typeof(CleanupProbeBuilding));
        Set(typeof(Thing), building, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        Set(mappedRecord, "building", building);
        universe.UnregisterObject(mapped);
        Call(universe, "RunFrameActions");
        Check(building.destroyCalls == 1 && universe.MapPool.IsIdle(map),
            "mapped external unregister retains the old map, destroys its body and returns it to the real pool");
        // Native rendering/disposal needs Unity; model its completed notification and
        // verify reset forgets the disposed map instead of disposing it a second time.
        Set(map, "<Disposed>k__BackingField", true);
        service.ResetFlow();
        Call(universe, "RunFrameActions");
        Check(universe.MapPool.TrackedCount == 0 && universe.MapPool.IdleCount == 0,
            "reset removes disposed pooled maps and empty pool buckets");

        var removedMap = TestMap(1805);
        var removed = new MapSpaceObject(1805, removedMap, plain.pose) { baseTag = SpaceObjectTag.Projectile };
        object removedRecord = AddProjectileRecord(universe, removed);
        var removedBody = (CleanupProbeBuilding)Raw(typeof(CleanupProbeBuilding));
        Set(removedRecord, "building", removedBody);
        Call(universe.Lifecycle, "NotifyMapRemoved", removedMap);
        Set(removedMap, "<Disposed>k__BackingField", true);
        service.ForgetMap(removedMap);
        Call(universe, "RunFrameActions");
        Check(service.InFlightCount == 0 && universe.ObjectById(1805) == null && removedBody.destroyCalls == 0,
            "native map-removal notification retires projectile without destroying a body on a disposed map");

        var shuttle = new SpaceObject(1806, plain.pose);
        AddProjectileRecord(universe, shuttle, shuttle: true);
        service.ResetFlow();
        Call(universe, "RunFrameActions");
        Check(service.InFlightCount == 0 && universe.ObjectById(shuttle.objectId) == shuttle
            && !shuttle.projectileRoleOverride,
            "reset releases intrusion control and preserves the shuttle for scene-level destruction");
        universe.UnregisterObject(shuttle);
        Find.Maps.Clear();

        for (int cycle = 0; cycle < 2; cycle++)
        {
            var ordinary = new SpaceObject(1810 + cycle * 2, plain.pose);
            universe.RegisterObject(ordinary);
            var active = new SpaceObject(1811 + cycle * 2, plain.pose) { baseTag = SpaceObjectTag.Projectile };
            AddProjectileRecord(universe, active);
            bool nestedRan = false;
            QueueFrame(universe, () => QueueFrame(universe, () =>
            {
                Check(universe.ObjectById(ordinary.objectId) == ordinary && universe.ObjectById(active.objectId) == active,
                    "nested old-scene cleanup still resolves both identities");
                nestedRan = true;
            }));
            Call(universe, "BeginSceneReset", (Action)(() => throw new Exception("Unity generation must not run offline")));
            var steps = (Queue<Action>)typeof(UniverseManager).GetField("sceneResetSteps", Private).GetValue(universe);
            Call(universe, "BeginSceneReset", (Action)(() => { }));
            Check(steps.Count == 4 && universe.IsResettingScene, "duplicate rebuild request keeps one reset sequence");
            universe.GameComponentTick(); // Must stop before accessing any ticking services.
            Call(universe, "RunFrameActions");
            Call(universe, "AdvanceSceneReset");
            Check(!nestedRan && steps.Count == 4, "reset barrier waits for callbacks queued by earlier callbacks");
            Call(universe, "RunFrameActions");
            Call(universe, "AdvanceSceneReset"); // Stop flows and queue projectile cleanup.
            Check(nestedRan && universe.ObjectById(active.objectId) == active && steps.Count == 3,
                "flow shutdown schedules projectile cleanup before deleting old containers");
            Call(universe, "AdvanceSceneReset");
            Check(steps.Count == 3, "scene destruction cannot overtake pending projectile cleanup");
            Call(universe, "RunFrameActions");
            Call(universe, "AdvanceSceneReset"); // Destroy remaining scene identities.
            Check(universe.objects.Count == 0 && service.InFlightCount == 0 && steps.Count == 2,
                "old scene destruction follows projectile retirement");
            QueueFrame(universe, () => QueueFrame(universe, () => nestedRan = false));
            Call(universe, "RunFrameActions");
            Call(universe, "AdvanceSceneReset");
            Check(steps.Count == 2, "cache reset waits for follow-up disposal callbacks");
            Call(universe, "RunFrameActions");
            Check(!nestedRan && steps.Count == 2 && Actions(universe).Count == 0
                && steps.Peek().Method.Name == "ResetRuntimeServices",
                "old cleanup is exhausted before the rendering-cache reset and native generation stages");
            // The remaining stages initialize Unity rendering and native map generation.
            // Do not mock their production implementations in this headless runner.
            steps.Clear();
            Call(universe, "AdvanceSceneReset");
            Check(!universe.IsResettingScene, "reset gate releases only when steps and frame callbacks are exhausted");
        }

        bool tailRan = false;
        QueueFrame(universe, () => throw new InvalidOperationException("intentional cleanup failure"));
        QueueFrame(universe, () => tailRan = true);
        try { Call(universe, "RunFrameActions"); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        Call(universe, "RunFrameActions");
        Check(tailRan, "a failed frame action does not discard the remaining cleanup batch");
    }
}
