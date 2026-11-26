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

        private sealed class NativeFireAuthorization
        {
            internal Verb verb;
            internal CompSpaceWeapon weapon;
            internal Map sourceMap;
            internal Vector2 fallbackDirection;
            internal float range;
            internal Vector2 placeholderCenter;
            internal float spreadReferenceRange;
            internal float[] degenerateSpreadAngles;
            internal int emissionIndex;
            internal int expiresTick;

            internal bool Valid(int now) => sourceMap != null && !sourceMap.Disposed && now <= expiresTick
                && (weapon == null || weapon.parent?.Spawned == true && weapon.SpaceModeEnabled);

            internal float NextDegenerateSpreadAngle()
            {
                if (degenerateSpreadAngles == null || degenerateSpreadAngles.Length == 0) return 0f;
                return degenerateSpreadAngles[emissionIndex++ % degenerateSpreadAngles.Length];
            }
        }

        private sealed class NativeLaunchRecord
        {
            internal Map sourceMap;
            internal Thing launcher;
            internal Thing equipment;
            internal CompSpaceWeapon weapon;
            internal Vector2 fallbackDirection;
            internal float range;
            internal Vector2 placeholderCenter;
            internal float spreadReferenceRange;
            internal float syntheticSpreadAngle;
            internal bool authorized;
        }

        private readonly Dictionary<Verb, NativeFireAuthorization> nativeVerbAuthorizations =
            new Dictionary<Verb, NativeFireAuthorization>();
        private readonly Dictionary<CompSpaceWeapon, NativeFireAuthorization> nativeWeaponAuthorizations =
            new Dictionary<CompSpaceWeapon, NativeFireAuthorization>();
        private readonly Dictionary<Verse.Projectile, NativeLaunchRecord> pendingNativeProjectiles =
            new Dictionary<Verse.Projectile, NativeLaunchRecord>();

        // Harmony's projectile Tick hook reads only this process-wide quick gate while idle.
        // Authoritative records remain in this UniverseManager-owned service.
        private static int nativePendingCount;
        internal static bool HasPendingNativeProjectiles => nativePendingCount > 0;

        internal void AuthorizeNativeFire(Verb verb, CompSpaceWeapon weapon, Map sourceMap,
            Vector2 fallbackDirection, float range, Vector2 placeholderCenter,
            float spreadReferenceRange, int durationTicks)
        {
            if (verb == null || sourceMap == null) return;
            if (fallbackDirection.sqrMagnitude < 1e-8f) fallbackDirection = Vector2.up;
            fallbackDirection.Normalize();
            var authorization = new NativeFireAuthorization
            {
                verb = verb,
                weapon = weapon,
                sourceMap = sourceMap,
                fallbackDirection = fallbackDirection,
                range = Mathf.Max(1f, range),
                placeholderCenter = placeholderCenter,
                spreadReferenceRange = Mathf.Max(1f, spreadReferenceRange),
                degenerateSpreadAngles = BuildDegenerateSpreadAngles(verb, spreadReferenceRange),
                expiresTick = Find.TickManager.TicksGame + Mathf.Max(2, durationTicks)
            };
            nativeVerbAuthorizations[verb] = authorization;
            if (weapon != null) nativeWeaponAuthorizations[weapon] = authorization;
        }

        internal void CancelNativeFire(Verb verb, CompSpaceWeapon weapon)
        {
            if (verb != null) nativeVerbAuthorizations.Remove(verb);
            if (weapon != null) nativeWeaponAuthorizations.Remove(weapon);
        }

        internal bool NativeCastAuthorized(Verb verb)
        {
            if (verb == null || !nativeVerbAuthorizations.TryGetValue(verb, out NativeFireAuthorization authorization))
                return false;
            if (authorization.Valid(Find.TickManager.TicksGame)) return true;
            nativeVerbAuthorizations.Remove(verb);
            if (authorization.weapon != null) nativeWeaponAuthorizations.Remove(authorization.weapon);
            return false;
        }

        internal void CompleteNativeBurst(Verb verb)
        {
            if (verb == null || verb.state == VerbState.Bursting
                || !nativeVerbAuthorizations.TryGetValue(verb, out NativeFireAuthorization authorization))
                return;
            nativeVerbAuthorizations.Remove(verb);
            if (authorization.weapon == null) return;
            nativeWeaponAuthorizations.Remove(authorization.weapon);
            if (authorization.weapon.parent is not Building_Turret turret) return;
            MethodInfo orderAttack = turret.GetType().GetMethod(nameof(Building_TurretGun.OrderAttack),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(LocalTargetInfo) }, null);
            try { orderAttack?.Invoke(turret, new object[] { LocalTargetInfo.Invalid }); }
            catch (TargetInvocationException ex)
            {
                Log.Error($"[DeltaV] Adapted turret could not leave its native firing cycle: {ex.InnerException ?? ex}");
            }
        }

        internal void RegisterNativeLaunch(Verse.Projectile projectile, Thing launcher, Thing equipment)
        {
            if (projectile == null || projectile.Destroyed || projectile.Map == null) return;
            // A derived Launch commonly calls base.Launch, so Harmony sees both Postfixes.
            // One physical entity must consume exactly one emission slot.
            if (pendingNativeProjectiles.ContainsKey(projectile)) return;
            Map map = projectile.Map;
            int now = Find.TickManager.TicksGame;
            NativeFireAuthorization authorization = null;
            CompSpaceWeapon weapon = NativeWeaponSource(launcher) ?? NativeWeaponSource(equipment);
            bool eligible = false;

            if (weapon != null && weapon.parent?.Map == map && weapon.SpaceModeEnabled
                && AutoSpaceWeaponTurretBridge.IsNativeAdapted(weapon.parent.def))
            {
                eligible = true;
                if (nativeWeaponAuthorizations.TryGetValue(weapon, out NativeFireAuthorization candidate)
                    && candidate.Valid(now)) authorization = candidate;
            }
            else if (launcher is Pawn pawn && pawn.Map == map
                && pawn.equipment?.PrimaryEq?.PrimaryVerb is Verb pawnVerb
                && (equipment == null || equipment == pawnVerb.EquipmentSource)
                && PawnSpaceWeaponBridge.IsSpacePrimaryVerb(pawnVerb))
            {
                eligible = true;
                if (nativeVerbAuthorizations.TryGetValue(pawnVerb, out NativeFireAuthorization candidate)
                    && candidate.Valid(now)) authorization = candidate;
            }

            if (!eligible) return;
            float syntheticSpreadAngle = 0f;
            if (authorization != null)
            {
                Vector3 nativeOrigin = (Vector3)NativeOrigin.GetValue(projectile);
                Vector2 origin = new Vector2(nativeOrigin.x, nativeOrigin.z);
                if ((authorization.placeholderCenter - origin).sqrMagnitude < .01f)
                    syntheticSpreadAngle = authorization.NextDegenerateSpreadAngle();
            }
            var record = new NativeLaunchRecord
            {
                sourceMap = map,
                launcher = launcher,
                equipment = equipment,
                weapon = weapon,
                fallbackDirection = authorization?.fallbackDirection ?? Vector2.up,
                range = authorization?.range ?? 1f,
                placeholderCenter = authorization?.placeholderCenter ?? Vector2.zero,
                spreadReferenceRange = authorization?.spreadReferenceRange ?? 1f,
                syntheticSpreadAngle = syntheticSpreadAngle,
                authorized = authorization != null
            };
            nativePendingCount++;
            pendingNativeProjectiles.Add(projectile, record);
        }

        private static float[] BuildDegenerateSpreadAngles(Verb verb, float referenceRange)
        {
            VerbProperties props = verb?.verbProps;
            if (props == null || props.sprayNumExtraCells <= 0
                && props.sprayWidth <= .0001f && props.sprayArching <= .0001f) return null;
            int count = Mathf.Max(1, props.burstShotCount);
            var result = new float[count];
            float range = Mathf.Max(1f, referenceRange);
            float depth = Mathf.Max(0f, props.sprayThicknessCells) + 2f * Mathf.Max(0f, props.sprayArching);
            for (int i = 0; i < count; i++)
            {
                // Native spray paths are sorted across the tangent before the burst fires.
                // Recreate that monotonic sweep without consuming the global gameplay RNG.
                float across = count == 1 ? 0f : Mathf.Lerp(-1f, 1f, i / (float)(count - 1));
                float lateral = across * Mathf.Max(0f, props.sprayWidth);
                float sequence = Mathf.Repeat((i + 1) * .61803398875f, 1f) - .5f;
                float longitudinal = sequence * depth;
                result[i] = Mathf.Atan2(lateral, Mathf.Max(1f, range + longitudinal));
            }
            return result;
        }

        internal static Vector2 ResolveNativeLaunchDirection(Vector2 centerDirection,
            Vector2 launchOffset, float referenceRange, float syntheticSpreadAngle)
        {
            if (centerDirection.sqrMagnitude < 1e-8f) centerDirection = Vector2.up;
            centerDirection.Normalize();
            Vector2 tangent = new Vector2(-centerDirection.y, centerDirection.x);
            float lateral = Vector2.Dot(launchOffset, tangent);
            float longitudinal = Vector2.Dot(launchOffset, centerDirection);
            float angle = syntheticSpreadAngle + Mathf.Atan2(lateral,
                Mathf.Max(1f, Mathf.Max(1f, referenceRange) + longitudinal));
            Vector2 resolved = SpaceTransform.Rotate(centerDirection, angle);
            return resolved.sqrMagnitude > 1e-8f ? resolved.normalized : centerDirection;
        }

        private static CompSpaceWeapon NativeWeaponSource(Thing thing)
        {
            if (thing is not ThingWithComps comps) return null;
            CompSpaceWeapon weapon = comps.TryGetComp<CompSpaceWeapon>();
            return weapon != null && AutoSpaceWeaponTurretBridge.IsAdapted(thing.def) ? weapon : null;
        }

        /// <summary>Called before the first actual projectile Tick/TickInterval body.</summary>
        internal bool IsPendingNativeProjectile(Verse.Projectile projectile) =>
            projectile != null && pendingNativeProjectiles.ContainsKey(projectile);

        internal bool TryCapturePendingNativeProjectile(Verse.Projectile projectile)
        {
            if (projectile == null || !pendingNativeProjectiles.TryGetValue(projectile,
                    out NativeLaunchRecord record)) return false;
            pendingNativeProjectiles.Remove(projectile);
            nativePendingCount = Math.Max(0, nativePendingCount - 1);
            if (!record.authorized || !TryAdoptNativeProjectile(projectile, record))
                RetireNativeProjectile(projectile);
            return true;
        }

        private bool TryAdoptNativeProjectile(Verse.Projectile projectile, NativeLaunchRecord record)
        {
            if (projectile?.Spawned != true || projectile.Destroyed || projectile.Map != record.sourceMap
                || record.launcher?.Spawned != true || record.launcher.Map != record.sourceMap
                || universe.FrameOf(record.sourceMap) is not SpaceObject source) return false;

            ThingDef proxyDef = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_PawnNativeProjectileProxy");
            CompProperties_SpaceProjectile props = proxyDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            float speed = (projectile.def.projectile?.SpeedTilesPerTick ?? 0f) * 60f;
            if (props == null || speed <= .0001f) return false;

            Vector3 origin = (Vector3)NativeOrigin.GetValue(projectile);
            Vector3 destination = (Vector3)NativeDestination.GetValue(projectile);
            // The carrier map's placeholder distance is not a ballistic baseline: a 0.3-cell
            // launch jitter must not become 17 degrees merely because the placeholder is one
            // cell away. Convert displacement around the placeholder center using the source
            // Verb's semantic range, then apply that relative angle to the universe aim ray.
            Vector2 launchOffset = new Vector2(destination.x, destination.z) - record.placeholderCenter;
            Vector2 mapDirection = ResolveNativeLaunchDirection(record.fallbackDirection,
                launchOffset, record.spreadReferenceRange, record.syntheticSpreadAngle);
            if (mapDirection.sqrMagnitude < 1e-8f)
            {
                Vector3 facing = record.launcher.Rotation.FacingCell.ToVector3();
                mapDirection = new Vector2(facing.x, facing.z);
            }
            mapDirection.Normalize();
            Vector2 mapPoint = new Vector2(origin.x, origin.z);
            Vector3 muzzle = SpaceTransform.MapPointToUniverse(source, record.sourceMap, mapPoint);
            Vector2 direction = SpaceTransform.MapDirectionToUniverseTangent(
                source, record.sourceMap, mapPoint, mapDirection);
            if (direction.sqrMagnitude < 1e-8f) return false;

            var pose = new SpacePose(muzzle, Mathf.Atan2(direction.y, direction.x));
            IntVec3 sourceCell = projectile.Position;
            SpaceObject created = universe.CapturePayload(projectile, pose, record.launcher.Faction,
                record.sourceMap, sourceCell);
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
                Mathf.CeilToInt(Mathf.Max(1f, record.range) / speed * 60f));
            flight.launcher = record.launcher;
            flight.weapon = record.weapon;
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
            // Native guard interception mirrors the map interceptor: one hit spends the
            // source projectile instance's actual damage, including launcher/stat modifiers.
            flight.damageScale = Mathf.Max(0f, projectile.DamageAmount);
            flight.energyFraction = flight.armorPenetrationScale = 1f;
            flight.mass = flight.beamEnergyScale = 1f;
            projectiles.Add(flight);
            return true;
        }

        private static void RetireNativeProjectile(Verse.Projectile projectile)
        {
            if (projectile == null || projectile.Destroyed) return;
            try { projectile.Destroy(DestroyMode.Vanish); }
            catch (Exception ex) { Log.Error($"[DeltaV] Could not retire an uncaptured native projectile: {ex}"); }
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
                bool continueFlight = false;
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
                        TransientHandoffResult result = payload.HandoffTransient(thing =>
                            ReplayNativeImpact((Verse.Projectile)thing, targetMap, hitCell,
                                currentHit, localDirection));
                        continueFlight = result == TransientHandoffResult.Returned;
                        if (result == TransientHandoffResult.Rejected)
                            Log.Error($"[DeltaV] Native projectile #{expectedId} could not be handed to its impact map.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[DeltaV] Native projectile impact replay failed: {ex}");
                    RetireNativeProjectile(flight.nativeProjectile);
                }

                if (continueFlight && original != null && universe.ObjectById(expectedId) == original
                    && original.RepresentationVersion == expectedVersion)
                {
                    Vector2 velocity = SpaceTransform.TransportTangent(original.pose.velocity,
                        original.pose.position, hitUniverse);
                    Vector2 direction = velocity.sqrMagnitude > 1e-8f
                        ? velocity.normalized : new Vector2(Mathf.Cos(original.pose.angle), Mathf.Sin(original.pose.angle));
                    Vector3 safe = OrbitSphere.Move(hitUniverse, direction * 0.05f);
                    original.pose.position = safe;
                    original.pose.velocity = SpaceTransform.TransportTangent(velocity, hitUniverse, safe);
                    original.pose.angle = Mathf.Atan2(direction.y, direction.x);
                    flight.prevPosition = safe;
                    flight.launchSweepStart = safe;
                    flight.dead = false;
                    flight.body.dead = false;
                    return;
                }

                projectiles.Remove(flight);
                if (original != null && universe.ObjectById(expectedId) == original
                    && original.RepresentationVersion == expectedVersion)
                    universe.UnregisterObject(original);
                RecycleProjectile(flight);
            });
            return true;
        }

        private static TransientHandoffDisposition ReplayNativeImpact(Verse.Projectile projectile,
            Map map, IntVec3 cell, Thing hitThing, Vector2 localDirection)
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
            ApplyNativeFoundationDirectDamage(projectile, map, cell, hitThing);
            ImpactMethod(projectile.GetType()).Invoke(projectile, new object[] { hitThing, false });
            if (projectile.Destroyed) return TransientHandoffDisposition.Consumed;
            return (bool)NativeLanded.GetValue(projectile)
                ? TransientHandoffDisposition.Placed : TransientHandoffDisposition.Return;
        }

        private static void ApplyNativeFoundationDirectDamage(Verse.Projectile projectile,
            Map map, IntVec3 cell, Thing hitThing)
        {
            ProjectileProperties props = projectile?.def?.projectile;
            if (hitThing != null || projectile is not Bullet || props == null
                || projectile is Projectile_Explosive || props.explosionRadius > 0f
                || props.explosionDelay > 0 || !SubstructureOwnership.HasFoundation(map, cell)) return;

            ImpactOutcomePart part = SpaceImpactEffects.Resolve(projectile.def, null)?.defenderEffect;
            SubstructureDamageUtility.TryDamage(map, cell, projectile.DamageAmount,
                part?.substructureBreakThreshold ?? 60f, projectile.Launcher);
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

        private void ResetNativeProjectileFlow()
        {
            nativePendingCount = Math.Max(0, nativePendingCount - pendingNativeProjectiles.Count);
            pendingNativeProjectiles.Clear();
            nativeVerbAuthorizations.Clear();
            nativeWeaponAuthorizations.Clear();
        }
    }
}
