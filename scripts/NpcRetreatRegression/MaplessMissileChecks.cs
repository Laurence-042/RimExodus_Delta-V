using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static bool OfflineMissileSelector(ref Selector __result)
    {
        __result = new Selector();
        return false;
    }

    static bool OfflineMissileFlammability(ref bool __result)
    {
        __result = false;
        return false;
    }

    static void MaplessSoundEndpoint(SpaceProjectileService service, SoundDef mapSound, SoundDef shellSound,
        SpaceObject owner, Vector2 local, Vector3 point, int spacing, float volume, bool combatSetting, float shellPitch) { }
    static IEnumerable<CodeInstruction> MaplessSoundBoundary(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(AccessTools.Method(typeof(SpaceProjectileService), "PlayCombatSound")))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(Program), nameof(MaplessSoundEndpoint)); }
            yield return instruction;
        }
    }

    static IEnumerable<CodeInstruction> OfflineMissileDlc(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo getter && getter.DeclaringType == typeof(ModsConfig))
            {
                instruction.opcode = OpCodes.Ldc_I4_0;
                instruction.operand = null;
            }
            yield return instruction;
        }
    }

    static void CheckMaplessMissile(UniverseManager universe)
    {
        Check(!SpaceCollision.TargetView.Of(new SpaceObject(41999,new SpacePose(Vector3.forward,0))).CanHullContact,
            "an empty mapless record cannot become a rigid hull contact source");
        var def = TestBuildingDef("maplessMissile");
        def.comps.Add(new CompProperties_SpaceProjectile { guided = true, hasMap = false });
        def.useHitPoints = true;
        def.destroyable = true;
        def.fillPercent = .4f;
        var body = (Building)Raw(typeof(Building));
        body.def = def;
        body.stackCount = 1;
        Set(typeof(Thing), body, "mapIndexOrState", (sbyte)-1);
        Set(typeof(Thing), body, "hitPointsInt", 25);
        var rayDef = TestBuildingDef("maplessPd");
        rayDef.comps.Add(new CompProperties_SpaceProjectile { guaranteedHitMissiles = true });
        var damageDef = new DamageDef { defName = "offlinePdDamage", workerClass = typeof(DamageWorker),
            harmsHealth = true, buildingDamageFactor = 1f, buildingDamageFactorImpassable = 1f,
            buildingDamageFactorPassable = 1f };
        var hitEffect = new SpaceImpactEffectDef
        {
            defName = "offlineMaplessHit", projectileDefs = new List<ThingDef> { rayDef },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap,
                damageDef = damageDef, damage = 10, incidenceMinFactor = 1, speedExponent = 0 },
            attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf }
        };
        DefDatabase<SpaceImpactEffectDef>.Add(hitEffect);
        var flak = new Harmony("DeltaV.Regression.MaplessMissile");
        flak.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.Selector)),
            prefix: new HarmonyMethod(typeof(Program), nameof(OfflineMissileSelector)));
        flak.Patch(AccessTools.Method(typeof(DamageWorker), nameof(DamageWorker.Apply)),
            transpiler: new HarmonyMethod(typeof(Program), nameof(OfflineMissileDlc)));
        flak.Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.FlammableNow)),
            prefix: new HarmonyMethod(typeof(Program), nameof(OfflineMissileFlammability)));
        // The newly lethal deferred case reaches audio; only that Unity endpoint is isolated.
        flak.Patch(AccessTools.Method(typeof(SpaceProjectileService), "RegisterImpactFx"),
            transpiler: new HarmonyMethod(typeof(Program), nameof(MaplessSoundBoundary)));
        SpaceObject missile = universe.CapturePayload(body, new SpacePose(Vector3.forward, .3f), null);
        Map[] maps = Find.Maps.ToArray();
        Find.Maps.Clear(); // The production hit should need no Unity scene or map reservation managers.
        try
        {
            Check(missile.Payload is AbstractMissile && missile.IsTransient && missile.InterceptionEligible
                && missile.map == null && missile.InterceptionBody == body, "mapless missile uses its real Building and transient role");
            var shots = Enumerable.Range(0, 4).Select(i => new SpaceCollision.SweptBody
            {
                attacker = new SpaceObject(45090 + i, missile.pose), projectileDef = rayDef,
                requiredDefender = missile, damageScale = 1f,
                from = SpaceTransform.ShipLocalToUniverse(missile, new Vector2(-3, 0)),
                to = SpaceTransform.ShipLocalToUniverse(missile, new Vector2(3, 0))
            }).ToList();
            var contacts = new List<SpaceCollision.VisualContact>();
            foreach (var shot in shots) shot.onVisualContact = contacts.Add;
            SpaceCollision.SweepAndResolve(universe, shots.GetRange(0, 2));
            Check(shots[0].resolvedHit && shots[1].resolvedHit && body.HitPoints == 5,
                "two real mapless point-defense contacts consume twenty hit points");
            Check(contacts.Count == 2 && contacts.All(c => c.defender == missile
                && OrbitSphere.ArcCells(c.position, SpaceTransform.ShipLocalToUniverse(missile, c.localPoint)) < .003f),
                "entity contact visuals and damage consume the same rigid-local hit point");
            SpaceCollision.SweepAndResolve(universe, shots.GetRange(2, 2));
            Check(shots[2].resolvedHit && !shots[3].resolvedHit && body.Destroyed && !missile.InterceptionEligible,
                "same-tick sweep skips the destroyed missile");
            Check(contacts.Count == 3, "destroyed entity does not produce a second visual or damage contact");

            var explosiveBody = new Building { def = def, HitPoints = 25 };
            var explosiveTarget = universe.CapturePayload(explosiveBody, missile.pose, null);
            try
            {
                int tick = Find.TickManager.TicksGame;
                hitEffect.defenderEffect.explosionRadius = 1.6f;
                var hit = new SpaceCollision.ImpactEvent
                {
                    defender = explosiveTarget, defenderBuilding = explosiveBody,
                    projectileDef = rayDef, damageScale = 1f
                };
                SpaceImpactEffects.ApplyToDefender(hit, hitEffect.defenderEffect);
                universe.Projectiles.Tick();
                Check(explosiveBody.HitPoints == 25, "mapless explosion leaves the current tick's missile HP intact");
                Set(Find.TickManager, "ticksGameInt", tick + 1);
                universe.Projectiles.Tick();
                Check(explosiveBody.HitPoints == 15, "next-tick explosion applies the old center-cell damage");
            }
            finally
            {
                universe.UnregisterObject(explosiveTarget);
            }
            CheckDeferredDamageAdapters(universe, def, hitEffect, rayDef);
        }
        finally
        {
            flak.UnpatchAll(flak.Id);
            universe.UnregisterObject(missile);
            Find.Maps.AddRange(maps);
        }
    }

    static void CheckDeferredDamageAdapters(UniverseManager universe, ThingDef bodyDef,
        SpaceImpactEffectDef effect, ThingDef projectile)
    {
        int Pending() => (int)AccessTools.Property(
            AccessTools.Field(typeof(SpaceProjectileService), "delayedMissileDamage").FieldType, "Count")
            .GetValue(AccessTools.Field(typeof(SpaceProjectileService), "delayedMissileDamage").GetValue(universe.Projectiles));
        void Advance()
        {
            Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
            universe.Projectiles.Tick();
        }
        foreach (string scenario in new[] { "once", "replace", "destroy", "remove", "multiple" })
        {
            var entity = new CollisionEntity { def = bodyDef, HitPoints = 25 };
            var target = universe.CapturePayload(entity, new SpacePose(Vector3.forward, .4f), null);
            var hit = new SpaceCollision.ImpactEvent {
                defender = target, defenderBuilding = entity, projectileDef = projectile, damageScale = 1,
                hitDefenderLocal = new Vector2(.2f, .1f)
            };
            try
            {
                int count = scenario == "multiple" ? 4 : 1;
                for (int i = 0; i < count; i++) SpaceImpactEffects.ApplyToDefender(hit, effect.defenderEffect);
                universe.Projectiles.Tick();
                Check(entity.HitPoints == 25 && Pending() == count, "deferred structure damage waits for the next game tick");
                if (scenario == "replace") AccessTools.Property(typeof(SpaceObject), nameof(SpaceObject.RepresentationVersion))
                    .SetValue(target, target.RepresentationVersion + 1);
                if (scenario == "destroy") entity.Destroy();
                if (scenario == "remove") universe.UnregisterObject(target);
                Advance();
                if (scenario == "once")
                {
                    Check(entity.HitPoints == 15 && Pending() == 0, "dequeued damage executes once through the adapter without being queued again");
                    universe.Projectiles.Tick(); Advance();
                    Check(entity.HitPoints == 15, "later ticks cannot repeat an already consumed damage receipt");
                }
                else if (scenario == "replace") Check(entity.HitPoints == 25 && Pending() == 0,
                    "representation changes invalidate pending structure damage");
                else if (scenario == "multiple") Check(entity.Destroyed && entity.destroys == 1 && Pending() == 0,
                    "multiple queued receipts stop at entity destruction and release the remaining work");
                else Check(entity.destroys == 1 && Pending() == 0,
                    "destroyed or removed structures cannot receive stale queued damage: " + scenario);
            }
            finally { universe.UnregisterObject(target); }
        }
    }
}
