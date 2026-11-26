using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceCollision
    {
        // A disposable view of the real representation, never a second structure or pose authority.
        internal readonly struct TargetView
        {
            internal readonly SpaceObject owner;
            internal readonly Map map;
            internal readonly Thing entity;
            internal readonly SpacePose pose;
            private readonly int version;
            private readonly bool entityBacked;
            private readonly bool requiresPersonnelProxy;

            private TargetView(SpaceObject owner, SpacePose pose)
            {
                this.owner = owner; this.pose = pose;
                map = owner?.map;
                Thing resolved = owner?.InterceptionThing;
                bool personnel = owner?.HasTag(SpaceObjectTag.Personnel) == true;
                requiresPersonnelProxy = map != null && personnel;
                entityBacked = resolved != null && (map == null || personnel);
                entity = entityBacked ? resolved : null;
                version = owner?.RepresentationVersion ?? 0;
            }
            private TargetView(TargetView source, SpacePose pose)
            {
                owner = source.owner; map = source.map; entity = source.entity;
                entityBacked = source.entityBacked;
                requiresPersonnelProxy = source.requiresPersonnelProxy;
                version = source.version; this.pose = pose;
            }
            internal static TargetView Of(SpaceObject owner) => new TargetView(owner, owner?.pose ?? default);
            internal TargetView AtPose(SpacePose value) => new TargetView(this, value);
            internal bool Valid => owner != null && owner.RepresentationVersion == version
                && owner.map == map && (!requiresPersonnelProxy
                    || Current.Game?.GetComponent<UniverseManager>()?.Projectiles
                        .HasPersonnelProxy(owner, entity) == true) && (entityBacked
                    ? entity != null && !entity.Destroyed && (map == null || entity.Spawned && entity.Map == map)
                    : map != null && !map.Disposed);
            internal bool HasEntity => Valid && entityBacked;
            internal bool HasRigidStructure => Valid && map != null && !entityBacked;
            internal bool CanHullContact => HasRigidStructure && !owner.projectileRoleOverride && !owner.IsScattered;
            internal Vector2 ToLocal(Vector2 backendPoint) => map != null
                ? SpaceTransform.MapToShipLocal(map, backendPoint) : backendPoint;
            internal Vector2 ToBackend(Vector2 localPoint) => map != null
                ? SpaceTransform.ShipLocalToMap(map, localPoint) : localPoint;
            internal Vector3 ToUniverse(Vector2 localPoint) => SpaceTransform.ShipLocalToUniverse(pose, localPoint);
            internal Vector2 FromUniverse(Vector3 point) => SpaceTransform.UniverseToShipLocal(pose, point);
            internal Vector2 DirectionToUniverse(Vector2 point, Vector2 direction) =>
                SpaceTransform.ShipDirectionToUniverseTangent(pose, point, direction);
            internal Vector2 DirectionFromUniverse(Vector3 point, Vector2 direction) =>
                SpaceTransform.UniverseTangentToShipDirection(pose, point, direction);
            internal float BoundsRadius => !Valid ? 0f : entityBacked
                ? entity.def.Size.ToVector2().magnitude * .5f : SurfaceFor(owner).boundsRadius;
            internal float OriginBoundsRadius
            {
                get
                {
                    if (!Valid) return 0f;
                    if (entityBacked) return BoundsRadius;
                    Surface surface = SurfaceFor(owner);
                    return surface.boundsRadius + ToLocal(surface.boundsCenter).magnitude;
                }
            }

            internal bool TryRay(RayQuery query, Vector2 from, Vector2 direction, float length,
                ThingDef projectile, float radius, HashSet<Thing> passed, HashSet<IntVec3> passedCells,
                Predicate<CompSpaceShield> includeLine, Predicate<ShieldArc> includeGuard,
                out RayContact contact, bool skipSubstructure, HashSet<Thing> coverBuildings,
                HashSet<IntVec3> coverCells, bool skipHull)
            {
                contact = default;
                if (!Valid || length <= 0f || direction.sqrMagnitude < 1e-8f) return false;
                bool found = !entityBacked
                    ? query.NextMap(ToBackend(from), direction.normalized, length, projectile, radius,
                        passed, passedCells, includeLine, includeGuard, out contact,
                        skipSubstructure, coverBuildings, coverCells, skipHull)
                    : !skipHull && coverBuildings?.Contains(entity) != true && passed?.Contains(entity) != true
                        && TryEntityFootprint(entity, from, direction.normalized, length, radius, out contact);
                if (!found) return false;
                contact = LocalContact(contact);
                return true;
            }

            internal bool TrySweep(SweptBody body, bool sourceStructure, PassedContacts passed, out SweepContact hit)
            {
                hit = default;
                if (!Valid) return false;
                bool found = !entityBacked
                    ? TrySweepContact(body, owner, map, sourceStructure, passed, out hit)
                    : TrySweepEntity(body, this, passed?.buildings, out hit);
                if (!found) return false;
                // Geometry backends retain their native input; all shared contacts use rigid local coordinates.
                RayContact ray = hit.ray;
                if (hit.target.owner == null) hit.target = this;
                hit.ray = LocalContact(ray);
                if (!entityBacked)
                {
                    hit.lineStart = ToLocal(hit.lineStart); hit.lineEnd = ToLocal(hit.lineEnd);
                }
                return true;
            }

            private RayContact LocalContact(RayContact contact)
            {
                // Entity geometry already uses rigid-local coordinates; only map geometry
                // needs conversion. Mapped personnel also need their real cell for Impact/explosions.
                return new RayContact(entityBacked ? contact.point : ToLocal(contact.point), contact.normal,
                    entityBacked && map != null ? entity.Position : contact.cell,
                    contact.kind, contact.building, contact.line, contact.guard);
            }

            internal bool TryStructure(IntVec3 cell, out SpaceCellKind kind, out Thing thing)
            {
                kind = SpaceCellKind.Empty; thing = null;
                if (!Valid) return false;
                if (entityBacked) { kind = SpaceCellKind.Building; thing = entity; return true; }
                if (!cell.InBounds(map)) return false;
                kind = SpaceStructureRules.ClassifyCell(map, cell, out thing);
                return kind != SpaceCellKind.Empty;
            }

            internal float ApplyDamage(IntVec3 cell, Thing thing, ImpactOutcomePart part, float amount,
                float penetration, DamageDef damage, Thing instigator, ThingDef projectile, Vector2 local)
            {
                if (!Valid) return 0f;
                if (!entityBacked) return SpaceImpactEffects.ApplyStructureDamage(this, cell, thing, part,
                    amount, penetration, damage, instigator, projectile);
                bool explosive = !part.penetrates && part.explosionRadius > 0f;
                var info = new DamageInfo(damage, explosive ? Mathf.Max(1, Mathf.RoundToInt(amount)) : amount,
                    penetration, -1f, instigator, null, projectile);
                if (explosive)
                {
                    Current.Game.GetComponent<UniverseManager>().Projectiles.QueueMissileDamage(owner, info, local);
                    return 0f;
                }
                float consumed = ApplyResolvedDamage(entity, info);
                return part.penetrates && entity.Destroyed && amount > 0f
                    ? Mathf.Clamp01((amount - Mathf.Max(0f, consumed)) / amount) : 0f;
            }

            // Execution only: damage already resolved or dequeued must never be scheduled again.
            internal float ApplyResolvedDamage(Thing thing, DamageInfo info)
            {
                if (!Valid) return 0f;
                Thing receiver = thing ?? entity;
                if (receiver == null || receiver.Destroyed
                    || (map != null ? receiver.Map != map : receiver != entity)) return 0f;
                return receiver.TakeDamage(info).totalDamageDealt;
            }
        }
    }
}
