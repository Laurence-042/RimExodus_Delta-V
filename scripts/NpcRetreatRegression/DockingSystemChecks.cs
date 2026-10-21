using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckDockingSystem(UniverseManager universe)
    {
        var link = new SoftDockService.Link();
        for (int i = 0; i < 179; i++) link.Advance(true);
        Check(!link.connected, "soft docking cannot transmit before extension completes");
        for (int i = 0; i < 3; i++) link.Advance(true);
        Check(link.connected && link.progress == 1f, "aligned soft docking completes and clamps extension");
        for (int i = 0; i < 20; i++) link.Advance(false);
        Check(link.connected, "brief alignment loss preserves an established portal above release threshold");
        for (int i = 0; i < 4; i++) link.Advance(false);
        Check(!link.connected && link.progress > 0f, "portal disconnects before the visual passage fully retracts");
        link.Advance(true);
        Check(!link.connected, "recovery above release threshold does not prematurely reopen an incomplete portal");
        for (int i = 0; i < 200; i++) link.Advance(false);
        Check(link.progress == 0f, "retraction never produces negative passage length");
        var service = new SoftDockService(universe);
        Set(service, "<Enabled>k__BackingField", true);
        ((List<SoftDockService.Link>)typeof(SoftDockService).GetField("links", Private).GetValue(service)).Add(link);
        service.SetEnabled(false);
        Check(!service.Enabled && service.Pair(null) == null, "toggle off immediately invalidates every portal without waiting for a tick");

        var body = new SpaceObject(49700, new SpacePose(Vector3.forward, .3f)
            { velocity = new Vector2(2f, -1f), angularVelocity = .03f });
        SpacePose original = body.pose;
        SpacePose destination = new SpacePose(OrbitSphere.Move(original.position, new Vector2(10, 3)), 1f)
            { velocity = new Vector2(2f, -1f), angularVelocity = .04f };
        ShipPhysics.AttractPoint(body, Vector2.zero, destination, .25f, .2f, .04f);
        Check(body.pose.position == original.position && body.pose.angle == original.angle,
            "external docking force never performs a second position or angle integration");
        Check((body.pose.velocity - original.velocity).magnitude <= .25f / 60f + 1e-6f
            && Mathf.Abs(body.pose.angularVelocity - original.angularVelocity) <= .04f / 60f + 1e-6f,
            "soft docking impulses respect per-game-tick linear and angular acceleration caps");
        body.pose = original;
        ShipPhysics.AttractPoint(body, Vector2.zero, original, .25f, 0f, .04f);
        Check((body.pose.velocity - original.velocity).magnitude < 1e-5f
            && body.pose.angularVelocity == original.angularVelocity,
            "matching motion has no attraction impulse or artificial braking");
        SpacePose movingAway = original;
        movingAway.velocity = Vector2.zero; movingAway.angularVelocity = 0f;
        ShipPhysics.AttractPoint(body, Vector2.zero, movingAway, .25f, 0f, .04f, velocityDamping: 0f);
        Check(body.pose.velocity == original.velocity && body.pose.angularVelocity == original.angularVelocity,
            "soft alignment alone does not brake manual translation or spin at coincident guides");
        CheckSmallScaleMovement();

        Map map = TestMap(49701);
        map.info.Size = new IntVec3(1, 1, 1);
        map.cellIndices = new CellIndices(map);
        map.terrainGrid = new TerrainGrid(map);
        var cargo = (Thing)Raw(typeof(Thing));
        cargo.def = TestBuildingDef("scattered_cargo"); cargo.def.category = ThingCategory.Item;
        map.listerThings.AllThings.Add(cargo);
        var scattered = new MapSpaceObject(49701, map, original) { baseTag = SpaceObjectTag.Scattered };
        var cell = scattered.CollisionCells.Single();
        Check(cell.kind == SpaceCellKind.Cargo && cell.building == null && cell.cell == IntVec3.Zero,
            "scattered map owns its one-cell collision surface independently of item or pawn hulls");
        Check(!SpaceImpactEffects.HitTest(null, cell.kind, null, null), "ordinary impacts pass through scattered maps");
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
                SpacePose destination = new SpacePose(direction, 0f);
                body.pose = new SpacePose(OrbitSphere.Move(direction, new Vector2(1.1f, 0f)), 0f);
                bool collectable = false;
                for (int tick = 0; tick < 1800; tick++)
                {
                    ShipPhysics.AttractPoint(body, Vector2.zero, destination, 2.5f);
                    if (OrbitSphere.ArcCells(body.pose.position, destination.position) <= .4f
                        && SpaceTransform.RelativeVelocity(body.pose, destination).magnitude <= .5f)
                    { collectable = true; break; }
                    ShipPhysics.Integrate(body, ShipPhysics.PointMassReport, Vector2.zero, 0f, 1f / 60f, false, false, false);
                }
                Check(collectable, "real tractor attraction crosses the collection threshold instead of stalling at low speed");
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
        var beam = TestBuildingDef("tractor_test_beam");
        beam.comps = new List<CompProperties> { new CompProperties_SpaceProjectile { continuousBeam = true, ignoresShields = true } };
        var effect = new SpaceImpactEffectDef { defName = "tractor_test_effect", projectileDefs = new List<ThingDef> { beam },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.Tractor },
            attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf } };
        DefDatabase<SpaceImpactEffectDef>.AllDefsListForReading.Add(effect);
        SpaceImpactEffects.ResetCache();
        try
        {
            foreach (bool tractor in new[] { true, false })
            {
                var sweep = new SpaceCollision.SweptBody { attacker = new SpaceObject(49702, scattered.pose),
                    projectileDef = tractor ? beam : null, requiredDefender = scattered, sweepRadius = 0f,
                    from = SpaceTransform.MapPointToUniverse(scattered, scattered.map, new Vector2(-2, .5f)),
                    to = SpaceTransform.MapPointToUniverse(scattered, scattered.map, new Vector2(3, .5f)) };
                SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { sweep });
                Check(sweep.resolvedHit == tractor && (!tractor || sweep.resolvedHitCell == IntVec3.Zero
                    && sweep.resolvedHitDefender == scattered && sweep.dead),
                    "production sweep hits the map-owned cargo cell only for the tractor effect");
            }
        }
        finally { DefDatabase<SpaceImpactEffectDef>.AllDefsListForReading.Remove(effect); SpaceImpactEffects.ResetCache(); }
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
        var pair = new SoftDockService.Link { a = Port(a, new IntVec3(6, 0, 17)), b = Port(b, new IntVec3(27, 0, 9)) };
        var service = universe.SoftDock;
        foreach (Rot4 ar in Rot4.AllRotations)
        foreach (Rot4 br in Rot4.AllRotations)
        {
            Set(typeof(Thing), pair.a, "rotationInt", ar); Set(typeof(Thing), pair.b, "rotationInt", br);
            own.pose = service.AlignmentReference(pair);
            Check((float)Call(service, "Error", pair.a, pair.b) < .01f
                && Math.Abs((float)Call(service, "AngleError", pair.a, pair.b)) < 1e-5f,
                "one reference aligns both crossed guides for every port direction");
            Vector2 ap = SpaceTransform.MapToShipLocal(a, SoftDockService.MapPoint(pair.a, 4f));
            Vector2 bp = SpaceTransform.MapToShipLocal(b, SoftDockService.MapPoint(pair.b, 4f));
            SpacePose at = SpaceTransform.RigidPointPose(own.pose, ap, 0f), bt = SpaceTransform.RigidPointPose(host.pose, bp, 0f);
            Check(SpaceTransform.RelativeVelocity(at, bt).magnitude < .002f
                && own.pose.angularVelocity == host.pose.angularVelocity,
                "soft docking guide reference includes moving-host point velocity and spin");
        }
        universe.objects.Remove(own); universe.objects.Remove(host);
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
