using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckGlobeFilters(UniverseManager universe)
    {
        var owner = new SpaceObject(79001, new SpacePose(Vector3.forward, .7f));
        var foundation = new GlobeDisplayEntry { owner = owner, craft = true,
            ship = new GlobeShipSummary { key = 79002, armament = GlobeArmament.Armed, contentType = "Gunboat" } };
        var shuttle = new GlobeDisplayEntry { owner = owner, craft = true,
            ship = new GlobeShipSummary { key = 79003, shuttle = true, armament = GlobeArmament.Unarmed } };
        var station = new GlobeDisplayEntry { owner = owner, station = true, contentType = "Shipyard" };
        var wreck = new GlobeDisplayEntry { owner = owner, wreck = true };
        var asteroid = new GlobeDisplayEntry { owner = owner, wreck = true, natural = true };
        var scatter = new GlobeDisplayEntry { owner = new SpaceObject(79004, owner.pose) { baseTag = SpaceObjectTag.Scattered } };
        var def = TestBuildingDef("GlobeRound");
        def.graphicData = new GraphicData { drawSize = new Vector2(2f, 5f) };
        var projectile = new GlobeDisplayEntry { owner = new SpaceObject(79005, owner.pose) { baseTag = SpaceObjectTag.Projectile },
            projectile = new GlobeProjectileVisual(def) };
        var filters = new GlobeFilters();
        Check(filters.showCoordinateGrid, "unit latitude/longitude grid is visible by default");
        bool Match(GlobeDisplayEntry e) => filters.Matches(e, false, false, false);
        Check(Match(foundation) && Match(station) && Match(wreck) && Match(asteroid)
            && !Match(shuttle) && !Match(projectile) && !Match(scatter), "default filters hide integrated craft/projectiles/scatter while retaining places and foundations");
        owner.projectileRoleOverride = true;
        Check(GlobeFilters.TypeOf(shuttle) == "IntegratedShip", "intrusion role cannot reclassify a shuttle as a projectile");
        Check(GlobeFilters.TypeOf(asteroid) == "Natural" && GlobeFilters.TypeOf(wreck) == "Wreck",
            "uninhabited natural terrain remains separate from wrecks");
        filters = GlobeFilters.Everything();
        Check(Match(shuttle) && Match(projectile) && Match(scatter), "show all restores default-hidden categories");
        filters.hiddenClasses.Add("Gunboat");
        Check(!Match(foundation) && Match(shuttle) && Match(station), "ship class filtering cannot accidentally remove stations or unclassified craft");
        filters.hiddenClasses.Clear(); filters.armament = GlobeArmament.Armed;
        Check(Match(foundation) && !Match(shuttle) && !Match(station), "armament separates installed weapons from unarmed and unknown facts");
        filters.armament |= GlobeArmament.Unarmed;
        Check(Match(foundation) && Match(shuttle), "multiple values in one facet are OR alternatives");
        filters.hiddenTypes.Add("IntegratedShip");
        Check(!Match(shuttle), "type and armament facets combine with AND");
        filters = GlobeFilters.Everything(); filters.missions = GlobeMission.Active;
        Check(filters.Matches(foundation, false, true, false) && !Match(foundation), "active mission filter uses actual mission association");
        filters.bookmarksOnly = true;
        Check(!filters.Matches(foundation, false, true, false) && filters.Matches(foundation, false, true, true),
            "bookmark restriction composes with mission association");
        filters.hiddenTypes.Add("FoundationShip");
        Check(filters.Matches(foundation, true, true, false), "tracked-target exemption is independent of all category restrictions");
        filters.keepTracked = false;
        Check(!filters.Matches(foundation, true, true, true), "disabling tracked-target exemption honors the saved restrictions");
        Check(GlobeFilters.RelationOf(null) == GlobeRelation.None && GlobeFilters.RelationOf(Faction.OfPlayer) == GlobeRelation.Player,
            "unowned and player-owned entries have distinct relation categories");
        owner.faction = Faction.OfPlayer; shuttle.ship.faction = null;
        Check(shuttle.Faction == null, "unowned member must not inherit its container's player faction");

        var navigation = new GlobeNavigation(new List<GlobeTarget>());
        navigation.Entries.AddRange(new[] { foundation, shuttle, station, projectile, scatter });
        navigation.ToggleBookmark(projectile);
        Check(navigation.Bookmarks.Count == 0, "transient projectiles cannot become persistent bookmarks");
        GlobeFilters before = universe.GlobeFilters;
        var dialog = new Dialog_GlobeFilters(universe);
        dialog.Draft.hiddenTypes.Clear(); dialog.Draft.relations = GlobeRelation.Hostile;
        dialog.Draft.showCoordinateGrid = false;
        Check(before.hiddenTypes.Contains("IntegratedShip") && before.relations == GlobeRelation.All
            && before.showCoordinateGrid,
            "editing and cancelling a draft leaves live filter preferences unchanged");
        navigation.ApplyFilters(universe, dialog.Draft);
        dialog.Draft.hiddenTypes.Add("Wreck");
        Check(universe.GlobeFilters.relations == GlobeRelation.Hostile && !universe.GlobeFilters.hiddenTypes.Contains("Wreck")
            && !universe.GlobeFilters.showCoordinateGrid,
            "apply copies the complete draft without retaining mutable dialog lists");
        universe.GlobeFilters = before;

        string path = Path.Combine(AppContext.BaseDirectory, "globe-filters.xml");
        GlobeFilters loaded = null;
        filters.showCoordinateGrid = false;
        try
        {
            Scribe.saver.InitSaving(path, "filters"); Scribe_Deep.Look(ref filters, "preferences"); Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref loaded, "preferences"); Scribe.loader.FinalizeLoading();
        }
        finally { Scribe.ForceStop(); }
        Check(loaded.hiddenTypes.SequenceEqual(filters.hiddenTypes) && loaded.missions == filters.missions
            && loaded.bookmarksOnly && !loaded.keepTracked && !loaded.showCoordinateGrid,
            "native Scribe preserves facet lists, masks, grid and tracked-target preference");

        var harness = new Harmony("DeltaV.GlobeFilters.NativeRotation");
        harness.Patch(AccessTools.Method(typeof(SpaceTransform), nameof(SpaceTransform.UniverseWorldMarkerRotation)),
            prefix: new HarmonyMethod(typeof(Program), nameof(GlobeRenderRotation)));
        try
        {
            var view = new GlobeView(new Rect(0, 0, 300, 300), Vector3.forward, 10000, 300, 32, Vector3.up, Vector3.right);
            Check(PlanetGlobeRenderer.CoordinateGridStepDegrees(view)
                    < PlanetGlobeRenderer.CoordinateGridStepDegrees(view.WithWidth(100000)),
                "unit latitude/longitude grid coarsens with range instead of disappearing at local zoom");
            var shot = PlanetGlobeRenderer.LayoutLegend(view, projectile, true);
            var dot = PlanetGlobeRenderer.LayoutLegend(view, scatter, true);
            Check(shot.projection && shot.rect.size == def.graphicData.drawSize && !shot.important && shot.labelRect == default,
                "projectile stays a true-size sprite without protected label/frame or icon fallback");
            Check(!dot.projection && dot.rect.width == 3f && dot.labelRect == default,
                "scatter stays a small point rather than terrain projection or ship icon");
            var legends = new List<PlanetGlobeRenderer.Legend> { shot, dot };
            PlanetGlobeRenderer.Aggregate(legends);
            Check(legends.Count == 2 && legends.All(l => l.members.Count == 0), "minimal symbols cannot turn into numbered clusters");
            var far = PlanetGlobeRenderer.LayoutLegend(view.WithWidth(100000), projectile, true);
            Check(far.projection && far.rect.width < shot.rect.width, "distant projectiles shrink instead of gaining a minimum-size marker");
            projectile.owner.pose = new SpacePose(-Vector3.forward, 0);
            Check(PlanetGlobeRenderer.LayoutLegend(view, projectile, true) == null,
                "offscreen or far-side projectiles never acquire edge arrows");
        }
        finally { harness.UnpatchAll(harness.Id); }
        CheckGlobeProjectileReuse(universe, def);
    }

    static void CheckGlobeProjectileReuse(UniverseManager universe, ThingDef def)
    {
        Map map = TestMap(79010);
        PreparePoolMap(map, SpaceObjectTag.Projectile);
        var pool = universe.MapPool;
        var lease = pool.Restore(map);
        var visual = pool.GlobeVisual(lease, def);
        Check(ReferenceEquals(visual, pool.GlobeVisual(lease, def)), "mapped projectile resource is reused within a rental");
        Current.Game.currentMapIndex = -1;
        Check(pool.TryReturn(lease), "empty projectile map returns with its display resource");
        Check(pool.GlobeVisual(lease, def) == null, "idle projectile lease cannot expose an active sprite");
        Check(pool.TryRent(SpaceObjectTag.Projectile, new IntVec2(5, 5), _ => { }, out var next), "projectile map is rented again");
        Check(ReferenceEquals(visual, pool.GlobeVisual(next, def)) && pool.GlobeVisual(lease, def) == null,
            "same ammunition reuses the map resource while stale leases remain invalid");
        var otherDef = TestBuildingDef("GlobeDifferentRound");
        Check(pool.GlobeVisual(next, otherDef).Def == otherDef && !ReferenceEquals(visual, pool.GlobeVisual(next, otherDef)),
            "same-size pool rental changes ammunition appearance when its Def changes");
        pool.Forget(map);
        Check(pool.GlobeVisual(next, def) == null, "map removal releases the pooled display binding");
        Find.Maps.Remove(map);

        var shot = new SpaceObject(79011, new SpacePose(Vector3.forward, 0)) { baseTag = SpaceObjectTag.Projectile };
        object record = AddProjectileRecord(universe, shot);
        Set(record, "def", def);
        var entries = new List<GlobeDisplayEntry>();
        universe.Projectiles.CollectGlobeEntries(entries);
        var entry = entries.Single(e => e.owner == shot);
        entries.Clear(); universe.Projectiles.CollectGlobeEntries(entries);
        Check(ReferenceEquals(entry, entries.Single(e => e.owner == shot)), "in-flight projectile reuses its display entry without per-frame allocation");
        Set(record, "laser", true); entries.Clear(); universe.Projectiles.CollectGlobeEntries(entries);
        Check(entries.All(e => e.owner != shot), "instantaneous lasers do not become globe projectiles");
        Set(record, "laser", false); Set(record, "adoptedShuttle", true);
        entries.Clear(); universe.Projectiles.CollectGlobeEntries(entries);
        Check(entries.All(e => e.owner != shot), "intrusion shuttles do not get a duplicate projectile entry");
        ProjectileRecords(universe.Projectiles).Remove(record);
        universe.UnregisterObject(shot);
        Call(universe.Projectiles, "RecycleProjectile", record);
        Check(record.GetType().GetField("globeEntry").GetValue(record) == null, "recycling a projectile clears its old globe identity");
    }
}
