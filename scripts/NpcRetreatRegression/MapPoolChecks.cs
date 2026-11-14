using System;
using System.Linq;
using HarmonyLib;
using DeltaV;
using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void PreparePoolMap(Map map, SpaceObjectTag tag)
    {
        if (TerrainDefOf.Space == null)
        {
            TerrainDefOf.Space = (TerrainDef)Raw(typeof(TerrainDef));
            TerrainDefOf.Space.defName = "Space";
        }
        for (int i = 0; i < map.terrainGrid.topGrid.Length; i++)
            map.terrainGrid.topGrid[i] = TerrainDefOf.Space;
        map.components.Add(new DeltaVPocketMapMetadata(map) { pooledMap = true, poolTag = tag });
    }

    static void CheckMapPool(UniverseManager universe)
    {
        Current.Game.currentMapIndex = -1;
        SpaceMapPool pool = universe.MapPool;
        Map a = TestMap(48001), b = TestMap(48002);
        PreparePoolMap(a, SpaceObjectTag.Projectile);
        PreparePoolMap(b, SpaceObjectTag.Container);
        SpaceMapLease original = pool.Restore(a), cargo = pool.Restore(b);
        Check(pool.TryReturn(original) && pool.TryReturn(cargo) && pool.IdleCount == 2,
            "common pool restores marked maps and accepts empty rentals of different tags");
        int previousLimit = DeltaVMod.CurrentSettings.loadedMapLimit;
        DeltaVMod.CurrentSettings.loadedMapLimit = Find.Maps.Count;
        Check(!pool.TryMakeRoom(3), "capacity rejects creation until enough slots are available");
        DeltaVMod.CurrentSettings.loadedMapLimit = 1;
        Check(!pool.TryMakeRoom(2), "a request larger than the whole map budget cannot start eviction");
        float nearby = SpaceLifecycleService.MapRetentionScore(100, 0f, 300f);
        float distant = SpaceLifecycleService.MapRetentionScore(100, 300f, 300f);
        Check(nearby > distant && distant > SpaceLifecycleService.MapRetentionScore(25, 300f, 300f),
            "map retention score increases independently with proximity and area");
        DeltaVMod.CurrentSettings.loadedMapLimit = Find.Maps.Count + 1;
        Check(pool.TryMakeRoom(), "raising the loaded map limit allows generation immediately");
        DeltaVMod.CurrentSettings.loadedMapLimit = Find.Maps.Count;
        int calls = 0;
        Check(pool.TryRent(SpaceObjectTag.Container, new IntVec2(5, 5), map =>
        {
            calls++;
            Check(map == b && !pool.TryReturn(pool.LeaseFor(map)), "population cannot return its own busy rental");
        }, out SpaceMapLease rented) && rented.Map == b && calls == 1,
            "matching idle map can be rented at capacity without creating another map");
        DeltaVMod.CurrentSettings.loadedMapLimit = previousLimit;
        Check(!pool.TryReturn(cargo, _ => calls++) && calls == 1,
            "stale lease cannot invoke cleanup on a newly populated map");
        var body = (Thing)Raw(typeof(Thing));
        body.def = TestBuildingDef("pool_cargo");
        body.def.category = ThingCategory.Item;
        body.def.thingClass = typeof(Thing);
        b.listerThings.AllThings.Add(body);
        Check(!pool.TryReturn(rented) && pool.IsCurrent(rented) && b.listerThings.AllThings.Contains(body),
            "return with cargo is rejected without deleting content or losing recovery lease");
        b.listerThings.AllThings.Remove(body);
        var owner = new MapSpaceObject(48002, b, new SpacePose(Vector3.forward, 0));
        universe.RegisterObject(owner);
        Check(!pool.TryReturn(rented, _ => calls++) && calls == 1,
            "owning SpaceObject must be removed before cleanup callback may run");
        universe.UnregisterObject(owner);
        Check(pool.TryReturn(rented) && !pool.TryReturn(rented) && pool.IdleCount == 2,
            "return is single-use and cannot insert duplicate idle map");

        Check(!pool.TryRent(SpaceObjectTag.Container, new IntVec2(5, 5), map =>
        {
            map.listerThings.AllThings.Add(body);
            throw new InvalidOperationException("intentional content callback failure");
        }, out SpaceMapLease failed) && failed.Map == b && pool.IsCurrent(failed)
            && b.listerThings.AllThings.Contains(body),
            "population exception preserves real content and exposes its recovery lease");
        Check(pool.TryReturn(failed, map => map.listerThings.AllThings.Remove(body)),
            "failed rental can be recovered explicitly after content handoff");

        pool.QueueReleaseUnused(SpaceObjectTag.Projectile);
        Check(pool.TryRent(SpaceObjectTag.Projectile, new IntVec2(5, 5), _ => { }, out SpaceMapLease next),
            "projectile can reuse idle map after reset snapshot");
        Call(universe, "RunFrameActions");
        Check(pool.IsCurrent(next) && !a.Disposed, "old reset callback cannot delete a new rental of the same Map");
        Check(pool.TryReturn(next), "later rental can return normally");

        Check(pool.TryRent(SpaceObjectTag.Container, new IntVec2(5, 5), _ => { }, out SpaceMapLease dirty),
            "lease for terrain protection scenario is available");
        b.terrainGrid.topGrid[0] = (TerrainDef)Raw(typeof(TerrainDef));
        Check(!pool.TryReturn(dirty), "return rejects remaining terrain even when no Things remain");
        pool.QueueReleaseUnused(SpaceObjectTag.Container);
        Call(universe, "RunFrameActions");
        Check(pool.IsCurrent(dirty) && !b.Disposed, "reset applies the same terrain protection as normal return");
        b.terrainGrid.topGrid[0] = TerrainDefOf.Space;
        Current.Game.currentMapIndex = (sbyte)Find.Maps.IndexOf(b);
        Check(!pool.TryReturn(dirty, _ => calls++ ) && calls == 1,
            "return does not mutate the currently viewed map");
        Current.Game.currentMapIndex = -1;
        Check(pool.TryReturn(dirty), "return succeeds after terrain is transferred and focus leaves");

        Set(a, "<Disposed>k__BackingField", true);
        Set(b, "<Disposed>k__BackingField", true);
        pool.Forget(a);
        pool.Forget(b);
        Check(pool.TrackedCount == 0 && pool.IdleCount == 0, "map removal releases buckets and invalidates leases");
        Find.Maps.Clear();

        Map[] sizes = { TestMap(48003), TestMap(48004) };
        for (int i = 0; i < sizes.Length; i++)
        {
            Map map = sizes[i];
            map.info.Size = new IntVec3(i + 1, 1, 1);
            map.cellIndices = new CellIndices(map);
            map.terrainGrid = new TerrainGrid(map);
            PreparePoolMap(map, SpaceObjectTag.Projectile);
            Check(pool.TryReturn(pool.Restore(map)), "pool accepts exact single-row map sizes");
        }
        foreach (Map map in sizes.Reverse())
        {
            Check(pool.TryRent(SpaceObjectTag.Projectile, new IntVec2(map.Size.x, 1), _ => { },
                out SpaceMapLease sizeLease) && sizeLease.Map == map,
                "different sizes under the same tag cannot cross rental buckets");
            pool.Forget(map);
        }
        Find.Maps.Clear();

        foreach (var sample in new[] { (30f, Rot4.East), (50f, Rot4.North), (60f, Rot4.North),
            (45f, Rot4.East), (-45f, Rot4.East), (135f, Rot4.West),
            (-60f, Rot4.South), (170f, Rot4.West), (390f, Rot4.East) })
        {
            float angle = sample.Item1 * Mathf.Deg2Rad;
            Rot4 facing = ShipOrientation.Rot4FromAngle(angle);
            Check(facing == sample.Item2 && ShipOrientation.Rot4FromAngle(ShipOrientation.SnapAngle(angle)) == facing,
                $"docking and intrusion share nearest-quarter direction for {sample.Item1} degrees");
            foreach (Rot4 source in Rot4.AllRotations)
                Check(ShipOrientation.PlacementRotation(source, facing).Rotated((RotationDirection)source.AsByte) == facing,
                    "placement retains vanilla rotation handedness");
        }
        CheckNativeSmallMapContracts();
        CheckPlacementGeometry();
    }

    static void CheckPlacementGeometry()
    {
        Map source = TestMap(48101), target = TestMap(48102);
        source.info.Size = new IntVec3(32, 1, 29);
        IntVec3 sourceRoot = new IntVec3(9, 0, 8);
        var pose = new SpacePose(Vector3.forward, 0.23f) { velocity = new Vector2(3, -2), angularVelocity = 0.04f };
        foreach (Rot4 rotation in Rot4.AllRotations)
        {
            var gravship = (Gravship)Activator.CreateInstance(typeof(Gravship), true);
            Set(gravship, "bounds", new CellRect(0, 0, 8, 5));
            var body = (Thing)Raw(typeof(Thing));
            body.def = TestBuildingDef("overhanging_body");
            body.def.size = new IntVec2(3, 5);
            body.def.rotatable = true;
            ((Dictionary<Thing, PositionData>)typeof(Gravship).GetField("things", Private).GetValue(gravship))
                .Add(body, new PositionData(new IntVec3(7, 0, 3), Rot4.North));
            gravship.Rotation = rotation;
            CellRect bounds = ShipPocketMapFactory.PlacedBounds(gravship);
            target.info.Size = ShipPocketMapFactory.CompactSize(bounds.Size);
            IntVec3 root = new IntVec3(12 - bounds.minX, 0, 12 - bounds.minZ);
            var placed = gravship.ThingPlacements[body];
            Check(GenAdj.OccupiedRect(root + placed.local, placed.rotation, body.def.size)
                .All(c => c.x >= 12 && c.z >= 12 && c.x < target.Size.x - 12 && c.z < target.Size.z - 12),
                "compact rotation includes the complete overhanging building and leaves twelve cells on every side");
            SpacePose moved = SpaceTransform.PoseAfterPlacement(pose, source, sourceRoot, target, root, rotation);
            Vector2 origin = SpaceTransform.ShipOriginFromPlacementRoot(source, sourceRoot, root, rotation);
            foreach (IntVec3 cell in new[] { sourceRoot, sourceRoot + new IntVec3(7, 0, 4) })
            {
                IntVec3 destination = root + PrefabUtility.GetAdjustedLocalPosition(cell - sourceRoot, rotation);
                Vector2 oldLocal = SpaceTransform.MapToShipLocal(source, new Vector2(cell.x + .5f, cell.z + .5f));
                Vector2 newLocal = SpaceTransform.MapToShipLocal(target, new Vector2(destination.x + .5f, destination.z + .5f));
                Vector3 before = SpaceTransform.ShipLocalToUniverse(pose, oldLocal);
                Vector3 after = SpaceTransform.ShipLocalToUniverse(moved, newLocal);
                Check(OrbitSphere.ArcCells(before, after) < .03f, "four-way map relocation preserves actual cell-center positions");
                Vector2 vBefore = SpaceTransform.VelocityAtLocalPoint(pose, oldLocal, before);
                Vector2 vAfter = SpaceTransform.VelocityAtLocalPoint(moved, newLocal, before);
                Check((vBefore - vAfter).magnitude < .01f && moved.angularVelocity == pose.angularVelocity,
                    "relocation preserves rigid-point velocity and spin");
                Vector2 expected = origin + SpaceTransform.RotatePlacementOffset(oldLocal, rotation);
                Check((expected - new Vector2(destination.x + .5f, destination.z + .5f)).sqrMagnitude < .00001f,
                    "registered landing origin agrees with vanilla integer placement at cell centers");
            }
        }
        Find.Maps.Clear();
    }

    static void CheckNativeSmallMapContracts()
    {
        var harmony = new Harmony("DeltaV.Regression.SingleCellMap");
        Type patch = typeof(UniverseManager).Assembly.GetType("DeltaV.Patch_SingleCellMapGeneration", true);
        Map previous = MapGenerator.mapBeingGenerated;
        var iterations = AccessTools.Field(typeof(Rand), "iterations");
        try
        {
            Check(harmony.CreateClassProcessor(patch).Patch().Count == 1,
                "single-cell compatibility postfix binds to the actual vanilla GasGrid constructor");
            foreach (IntVec2 size in new[] { new IntVec2(1, 1), new IntVec2(2, 1), new IntVec2(3, 2) })
            {
                var map = (Map)Raw(typeof(Map));
                map.info = new MapInfo { Size = new IntVec3(size.x, 1, size.z),
                    parent = (ShipPocketParent_DeltaV)Raw(typeof(ShipPocketParent_DeltaV)) };
                map.cellIndices = new CellIndices(map);
                foreach (bool generating in new[] { false, true })
                {
                    MapGenerator.mapBeingGenerated = generating ? map : null;
                    Rand.PushState(1268);
                    try
                    {
                        var gas = new GasGrid(map);
                        uint expected = map.Area == 1 && !generating ? 0u : 1u;
                        Check((uint)iterations.GetValue(null) == expected,
                            $"native {size.x}x{size.z} gas construction consumes {expected} RNG draws, generating={generating}");
                        gas.ClearCellUnsafe(IntVec3.Zero);
                        gas.RecalculateEverHadGas();
                        Check(!gas.CalculateGasEffects, "native gas grid can be reset on the exact map footprint");
                    }
                    finally { Rand.PopState(); }
                }
                CellRect rect = GenAdj.OccupiedRect(IntVec3.Zero, Rot4.North, size);
                IntVec3 anchor = new IntVec3(-rect.minX, 0, -rect.minZ);
                CellRect placed = GenAdj.OccupiedRect(anchor, Rot4.North, size);
                Check(placed.minX == 0 && placed.minZ == 0
                    && placed.maxX == size.x - 1 && placed.maxZ == size.z - 1,
                    $"native building anchor fills exact {size.x}x{size.z} footprint without clipping");
            }
        }
        finally
        {
            MapGenerator.mapBeingGenerated = previous;
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
