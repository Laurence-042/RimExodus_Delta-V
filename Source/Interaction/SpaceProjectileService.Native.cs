using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed partial class SpaceProjectileService
    {
        private static readonly FieldInfo NativeOrigin = AccessTools.Field(typeof(Verse.Projectile), "origin");
        private static readonly FieldInfo NativeDestination = AccessTools.Field(typeof(Verse.Projectile), "destination");
        private static readonly FieldInfo NativeTicksToImpact = AccessTools.Field(typeof(Verse.Projectile), "ticksToImpact");
        private static readonly FieldInfo NativeLanded = AccessTools.Field(typeof(Verse.Projectile), "landed");
        private static readonly Dictionary<Type, MethodInfo> NativeImpactMethods = new Dictionary<Type, MethodInfo>();

        internal bool CapturePawnProjectile(Verse.Projectile projectile, Pawn launcher, Thing equipment,
            Vector3 origin, Vector2 mapDirection)
        {
            if (projectile?.Spawned != true || projectile.Destroyed || launcher?.Spawned != true
                || launcher.Map != projectile.Map || launcher.equipment?.Primary != equipment
                || launcher.equipment.PrimaryEq?.PrimaryVerb is not Verb verb
                || !PawnSpaceWeaponBridge.SupportsCapturedProjectile(verb)
                || universe.FrameOf(launcher.Map) is not MapSpaceObject source
                || source.PersonnelPawn != launcher || source.PersonnelShip?.IsValid != true)
                return false;

            ThingDef proxyDef = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_PawnNativeProjectileProxy");
            CompProperties_SpaceProjectile props = proxyDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            float speed = (projectile.def.projectile?.SpeedTilesPerTick ?? 0f) * 60f;
            if (props == null || speed <= .0001f) return false;

            if (mapDirection.sqrMagnitude < 1e-8f)
            {
                Vector3 facing = launcher.Rotation.FacingCell.ToVector3();
                mapDirection = new Vector2(facing.x, facing.z);
            }
            mapDirection.Normalize();
            Vector2 mapPoint = new Vector2(origin.x, origin.z);
            Vector3 muzzle = SpaceTransform.MapPointToUniverse(source, launcher.Map, mapPoint);
            Vector2 direction = SpaceTransform.MapDirectionToUniverseTangent(
                source, launcher.Map, mapPoint, mapDirection);
            if (direction.sqrMagnitude < 1e-8f) return false;

            var pose = new SpacePose(muzzle, Mathf.Atan2(direction.y, direction.x));
            Map sourceMap = projectile.Map;
            IntVec3 sourceCell = projectile.Position;
            SpaceObject created = universe.CapturePayload(projectile, pose, launcher.Faction,
                sourceMap, sourceCell);
            if (created == null) return false;

            created.pose.velocity = direction * speed
                + SpaceTransform.VelocityAtUniversePoint(source.pose, muzzle);
            Projectile flight = projectilePool.Count > 0 ? projectilePool.Pop() : new Projectile();
            flight.container = created;
            flight.def = proxyDef;
            flight.props = props;
            flight.building = null;
            flight.nativeProjectile = projectile;
            flight.timeoutTicks = Mathf.Max(1,
                Mathf.CeilToInt(Mathf.Max(1f, verb.EffectiveRange) / speed * 60f));
            flight.launcher = launcher;
            flight.weapon = null;
            flight.sourceContainer = source;
            flight.selfFire = default;
            flight.launchSweepStart = null;
            flight.reflectedByShield = false;
            flight.target = default;
            flight.cannonTrace = null;
            flight.targetSampleTick = -1;
            flight.targetContainerId = 0;
            flight.targetPose = default;
            flight.hardDockingExecutor = null;
            flight.personnelProxy = false;
            flight.hardDockingActive = false;
            flight.adoptedShuttle = false;
            flight.boardingDriver = default;
            flight.boardingReference = null;
            flight.spawnedAtTick = Find.TickManager.TicksGame;
            flight.dead = false;
            flight.globeEntry = null;
            flight.prevPosition = muzzle;
            flight.laser = false;
            flight.cruiseSpeed = speed;
            flight.coldLaunchEndsTick = flight.spawnedAtTick;
            flight.phase = FlightPhase.PoweredFlight;
            flight.motorStartedTick = flight.spawnedAtTick;
            flight.motorDeltaVelocityPerTick = 0f;
            flight.motorThrusting = false;
            flight.rcsTurnSign = 0f;
            flight.damageScale = flight.energyFraction = flight.armorPenetrationScale = 1f;
            flight.mass = flight.beamEnergyScale = 1f;
            projectiles.Add(flight);
            return true;
        }

        private bool QueueNativeImpact(Projectile flight, SpaceObject defender, Thing hitThing,
            IntVec3 hitCell, Vector3 hitUniverse, Vector2 localDirection)
        {
            if (flight?.dead != false) return true;
            flight.dead = true;
            flight.body.dead = true;
            int expectedId = flight.containerId;
            SpaceObject original = flight.container;
            int expectedVersion = original?.RepresentationVersion ?? 0;
            int defenderId = defender?.objectId ?? 0;
            int defenderVersion = defender?.RepresentationVersion ?? 0;
            Map targetMap = defender?.map;
            universe.QueueFrameAction(delegate
            {
                if (!flight.dead || flight.containerId != expectedId) return;
                try
                {
                    SpaceObject currentDefender = universe.ObjectById(defenderId);
                    AbstractProjectile payload = original?.Payload as AbstractProjectile;
                    bool validTarget = original != null
                        && universe.ObjectById(expectedId) == original
                        && original.RepresentationVersion == expectedVersion
                        && currentDefender == defender
                        && defender.RepresentationVersion == defenderVersion
                        && targetMap != null && !targetMap.Disposed && defender.map == targetMap
                        && hitCell.IsValid && hitCell.InBounds(targetMap)
                        && payload != null
                        && payload.Entity == flight.nativeProjectile;
                    if (validTarget)
                    {
                        Thing currentHit = hitThing?.Spawned == true && hitThing.Map == targetMap
                            && hitThing.OccupiedRect().Contains(hitCell) ? hitThing : null;
                        TractorReceiveResult result = payload.HandoffTransient(thing =>
                            ReplayNativeImpact((Verse.Projectile)thing, targetMap, hitCell,
                                currentHit, localDirection));
                        if (result != TractorReceiveResult.Complete)
                            Log.Error($"[DeltaV] Native Pawn projectile #{expectedId} could not be handed to its impact map.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[DeltaV] Native Pawn projectile impact replay failed: {ex}");
                    if (flight.nativeProjectile is { Destroyed: false } failed)
                    {
                        bool previous = Thing.allowDestroyNonDestroyable;
                        try { Thing.allowDestroyNonDestroyable = true; failed.Destroy(DestroyMode.Vanish); }
                        finally { Thing.allowDestroyNonDestroyable = previous; }
                    }
                }
                finally
                {
                    projectiles.Remove(flight);
                    if (original != null && universe.ObjectById(expectedId) == original
                        && original.RepresentationVersion == expectedVersion)
                        universe.UnregisterObject(original);
                    RecycleProjectile(flight);
                }
            });
            return true;
        }

        private static void ReplayNativeImpact(Verse.Projectile projectile, Map map, IntVec3 cell,
            Thing hitThing, Vector2 localDirection)
        {
            if (localDirection.sqrMagnitude < 1e-8f) localDirection = Vector2.up;
            localDirection.Normalize();
            Vector3 destination = cell.ToVector3Shifted();
            Vector3 origin = destination - new Vector3(localDirection.x, 0f, localDirection.y);
            NativeOrigin.SetValue(projectile, origin);
            NativeDestination.SetValue(projectile, destination);
            NativeTicksToImpact.SetValue(projectile, 0);
            NativeLanded.SetValue(projectile, false);
            LocalTargetInfo target = hitThing != null ? new LocalTargetInfo(hitThing) : new LocalTargetInfo(cell);
            projectile.usedTarget = target;
            projectile.intendedTarget = target;
            projectile.Rotation = Rot4.FromAngleFlat(
                Mathf.Atan2(localDirection.x, localDirection.y) * Mathf.Rad2Deg);
            GenSpawn.Spawn(projectile, cell, map, WipeMode.Vanish);
            ImpactMethod(projectile.GetType()).Invoke(projectile, new object[] { hitThing, false });
        }

        private static MethodInfo ImpactMethod(Type type)
        {
            if (NativeImpactMethods.TryGetValue(type, out MethodInfo method)) return method;
            for (Type current = type; current != null; current = current.BaseType)
            {
                method = current.GetMethod("Impact", BindingFlags.Instance | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly, null, new[] { typeof(Thing), typeof(bool) }, null);
                if (method != null) return NativeImpactMethods[type] = method;
            }
            throw new MissingMethodException(type.FullName, "Impact(Thing, bool)");
        }
    }
}
