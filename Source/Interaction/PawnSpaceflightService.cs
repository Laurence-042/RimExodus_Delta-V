using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>人员抽象表示与独立 1x1 飞行表示之间的唯一升降格编排。</summary>
    public sealed class PawnSpaceflightService
    {
        private readonly UniverseManager universe;
        private readonly HashSet<int> pendingPromotion = new HashSet<int>();
        private readonly HashSet<int> pendingTransition = new HashSet<int>();
        private readonly Dictionary<int, ShipFlightTransitionService.LaunchFocusHandoff> launchFocus =
            new Dictionary<int, ShipFlightTransitionService.LaunchFocusHandoff>();
        private readonly Dictionary<int, string> promotionStatus = new Dictionary<int, string>();
        private int nextReviewTick;

        internal PawnSpaceflightService(UniverseManager universe) { this.universe = universe; }

        internal bool IsActive(Pawn pawn) => pawn?.Spawned == true
            && universe.FrameOf(pawn.Map) is MapSpaceObject { PersonnelPawn: { } active }
            && active == pawn;

        internal PawnSpaceflightShip ShipFor(Pawn pawn) => IsActive(pawn)
            ? (universe.FrameOf(pawn.Map) as MapSpaceObject)?.PersonnelShip : null;

        internal void NotifyRegistered(SpaceObject obj)
        {
            if (obj?.Payload is AbstractPawn && obj.Payload.Entity is Pawn { Dead: false })
                RequestPromotion(obj);
        }

        internal void RequestLaunchFocus(SpaceObject obj,
            ShipFlightTransitionService.LaunchFocusHandoff handoff)
        {
            if (obj == null || !handoff.Valid) return;
            launchFocus[obj.objectId] = handoff;
            RequestPromotion(obj);
        }

        internal void NotifyPawnChanged(Pawn pawn)
        {
            if (pawn == null) return;
            SpaceObject active = pawn.Spawned ? universe.FrameOf(pawn.Map) : null;
            if (active is MapSpaceObject { PersonnelPawn: { } represented,
                PersonnelSnapshot: { } snapshot } personnel && represented == pawn)
            {
                if (!snapshot.CanMove(pawn)) RequestDemotion(active);
                else universe.Weapons.RefreshPersonnelWeapons(personnel.PersonnelShip);
            }
            else
            {
                SpaceObject floating = universe.objects.FirstOrDefault(obj =>
                    obj.Payload is AbstractPawn && obj.Payload.Entity == pawn);
                if (floating != null) RequestPromotion(floating);
            }
        }

        internal void RequestAbstract(SpaceObject obj) => RequestDemotion(obj);

        internal bool TryCarryActive(Pawn carrier, MapSpaceObject target)
        {
            Pawn pawn = target?.PersonnelPawn;
            Map map = target?.map;
            if (carrier?.Spawned != true || pawn?.Spawned != true || pawn.Map != map
                || universe.ObjectById(target.objectId) != target
                || !CompPawnGrapple.CanCarry(carrier, pawn)) return false;
            int version = target.RepresentationVersion;
            IntVec3 cell = pawn.Position;
            Rot4 rotation = pawn.Rotation;
            SpaceScatterService.ClearPawnTransferJob(pawn);
            AbstractThing payload = AbstractThing.Create(pawn);
            if (payload == null || !payload.Capture(pawn, map, cell, null)) return false;
            TractorReceiveResult result;
            try
            {
                result = payload.Handoff(thing => carrier.carryTracker.TryStartCarry(thing));
            }
            catch
            {
                RestoreCarriedTarget(payload, pawn, map, cell, rotation);
                throw;
            }
            if (result != TractorReceiveResult.Complete)
            {
                RestoreCarriedTarget(payload, pawn, map, cell, rotation);
                return false;
            }
            if (universe.ObjectById(target.objectId) != target || target.RepresentationVersion != version)
                throw new InvalidOperationException("Personnel representation changed during grapple handoff.");
            universe.UnregisterObject(target);
            universe.ShipTransitions.QueueEmptyDepartedMapCleanup(universe, map);
            pendingPromotion.Remove(target.objectId);
            pendingTransition.Remove(target.objectId);
            return true;
        }

        private static void RestoreCarriedTarget(AbstractThing payload, Pawn pawn, Map map,
            IntVec3 cell, Rot4 rotation)
        {
            if (pawn.Destroyed || pawn.Spawned || pawn.holdingOwner != null || map?.Disposed != false) return;
            payload.Handoff(thing =>
            {
                thing.Rotation = rotation;
                GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
            });
        }

        internal void Resume()
        {
            pendingPromotion.Clear();
            pendingTransition.Clear();
            launchFocus.Clear();
            promotionStatus.Clear();
            universe.Projectiles.RebuildPersonnelProxies();
            foreach (SpaceObject obj in universe.objects)
                if (obj?.Payload is AbstractPawn && obj.Payload.Entity is Pawn { Dead: false })
                {
                    pendingPromotion.Add(obj.objectId);
                    promotionStatus[obj.objectId] = "abstract/pending eligibility";
                }
            nextReviewTick = Find.TickManager?.TicksGame ?? 0;
        }

        internal void Reset()
        {
            pendingPromotion.Clear();
            pendingTransition.Clear();
            launchFocus.Clear();
            promotionStatus.Clear();
            nextReviewTick = 0;
        }

        internal string DescribePromotion(SpaceObject obj)
        {
            if (obj is MapSpaceObject { PersonnelPawn: { } pawn })
                return $"active mapped personnel; canMove={obj is MapSpaceObject mapped && mapped.PersonnelSnapshot?.CanMove(pawn) == true}; "
                    + $"vacuumRisk={pawn.HarmedByVacuum}";
            if (obj?.Payload is not AbstractPawn || obj.Payload.Entity is not Pawn pawnEntity)
                return "not a personnel representation";
            PawnSpaceflightEligibility eligibility = PawnSpaceflightSnapshot.Eligibility(pawnEntity);
            string last = promotionStatus.TryGetValue(obj.objectId, out string value)
                ? value : "no promotion attempt recorded";
            return $"abstract personnel; mobilityEligibility={eligibility}; vacuumRisk={pawnEntity.HarmedByVacuum}; stage={last}; "
                + $"pending={pendingPromotion.Contains(obj.objectId)} transitionQueued={pendingTransition.Contains(obj.objectId)}";
        }

        internal void CompleteHardDocking(HardDockingContact contact)
        {
            if (contact.source is not MapSpaceObject personnel || personnel.PersonnelPawn == null
                || contact.target?.map == null
                || !PersonnelAccess.TryResolveLanding(contact.target.map, contact.targetCell,
                    out PersonnelLandingSite landing))
            {
                Messages.Message("DeltaV_PersonnelLandingBlocked".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            int sourceId = contact.source.objectId;
            int sourceVersion = contact.source.RepresentationVersion;
            int targetId = contact.target.objectId;
            int targetVersion = contact.target.RepresentationVersion;
            universe.QueueFrameAction(() =>
            {
                if (universe.ObjectById(sourceId) is not MapSpaceObject current
                    || current.RepresentationVersion != sourceVersion
                    || universe.ObjectById(targetId) is not SpaceObject target
                    || target.RepresentationVersion != targetVersion || target.map != landing.map
                    || !PersonnelAccess.StillValid(landing))
                {
                    Messages.Message("DeltaV_PersonnelLandingFailed".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                    return;
                }
                PawnSpaceflightShip vessel = current.PersonnelShip;
                if (!universe.ShipTransitions.TransferPersonnelIntoHost(universe, current,
                    landing.map, landing.cell, out Map sourceMap))
                {
                    Messages.Message("DeltaV_PersonnelLandingFailed".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                    return;
                }
                var completion = new DockingCompletion(universe, vessel,
                    landing.map, sourceMap, default, "DeltaV_PersonnelLandingComplete");
                completion.OnTransferedIntoHost();
                universe.QueueFrameAction(completion.Finish);
            });
        }

        internal void Tick()
        {
            int now = Find.TickManager.TicksGame;
            if (now < nextReviewTick) return;
            nextReviewTick = now + 15;
            foreach (SpaceObject obj in universe.objects.ToArray())
                if (obj is MapSpaceObject { PersonnelPawn: { } pawn, PersonnelSnapshot: { } snapshot }
                    && !snapshot.CanMove(pawn)) RequestDemotion(obj);
            foreach (int objectId in pendingPromotion.ToArray())
            {
                SpaceObject obj = universe.ObjectById(objectId);
                if (obj?.Payload is not AbstractPawn || obj.Payload.Entity is not Pawn { Dead: false })
                {
                    pendingPromotion.Remove(objectId);
                    continue;
                }
                RequestPromotion(obj);
            }
        }

        private void RequestPromotion(SpaceObject obj)
        {
            pendingPromotion.Add(obj.objectId);
            if (!pendingTransition.Add(obj.objectId)) return;
            int id = obj.objectId;
            int version = obj.RepresentationVersion;
            universe.QueueFrameAction(() =>
            {
                pendingTransition.Remove(id);
                SpaceObject current = universe.ObjectById(id);
                if (current != obj || current.RepresentationVersion != version
                    || current.Payload is not AbstractPawn || current.Payload.Entity is not Pawn pawn)
                {
                    pendingPromotion.Remove(id);
                    return;
                }
                promotionStatus[id] = "eligibility check";
                if (!PawnSpaceflightSnapshot.TryCreate(pawn, out PawnSpaceflightSnapshot snapshot,
                    out PawnSpaceflightEligibility eligibility))
                {
                    promotionStatus[id] = "eligibility rejected: " + eligibility;
                    pendingPromotion.Remove(id);
                    return;
                }
                promotionStatus[id] = "eligible snapshot created";
                if (!Promote(current, pawn, snapshot, out bool retryable))
                {
                    if (retryable) pendingPromotion.Add(id);
                    else pendingPromotion.Remove(id);
                }
                else
                {
                    pendingPromotion.Remove(id);
                    if (launchFocus.Remove(id, out var handoff)
                        && universe.ObjectById(id) is MapSpaceObject promoted)
                        handoff.Apply(universe, promoted, promoted.PersonnelShip.Identity);
                }
            });
        }

        private bool Promote(SpaceObject previous, Pawn pawn, PawnSpaceflightSnapshot snapshot,
            out bool retryable)
        {
            retryable = false;
            // Resolve the runtime-only collision contract before consuming the abstract payload.
            // Accessing Projectiles also installs the synchronous representation listener.
            if (!universe.Projectiles.PersonnelProxyDefinitionAvailable)
            {
                promotionStatus[previous.objectId] = "proxy definition unavailable";
                return false;
            }
            promotionStatus[previous.objectId] = "creating 1x1 flight map";
            Map map = ShipPocketMapFactory.Generate(new IntVec3(1, 1, 1), universe);
            if (map == null)
            {
                promotionStatus[previous.objectId] = "flight map creation failed (retryable)";
                retryable = true;
                return false;
            }
            AbstractThing payload = previous.Payload;
            TractorReceiveResult transfer;
            promotionStatus[previous.objectId] = "transferring pawn into flight map";
            try
            {
                transfer = payload.Handoff(thing =>
                {
                    if (thing != pawn || GenSpawn.Spawn(pawn, IntVec3.Zero, map, WipeMode.Vanish) != pawn
                        || !pawn.Spawned)
                        throw new InvalidOperationException("Could not spawn promoted spaceflight pawn.");
                });
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Personnel promotion #{previous.objectId} failed: {ex}");
                promotionStatus[previous.objectId] = "pawn transfer threw (retryable)";
                retryable = true;
                universe.MapStates.DestroyPocketMap(map);
                return false;
            }
            if (transfer != TractorReceiveResult.Complete)
            {
                promotionStatus[previous.objectId] = "pawn transfer rejected (retryable)";
                retryable = true;
                universe.MapStates.DestroyPocketMap(map);
                return false;
            }
            try
            {
                promotionStatus[previous.objectId] = "publishing mapped representation";
                var current = new MapSpaceObject(previous.objectId, map, previous.pose);
                previous.CopyFactsTo(current);
                current.SetPersonnel(pawn, snapshot);
                previous.Payload = null;
                universe.ReplaceRepresentation(previous, current);
                if (!universe.Projectiles.EnsurePersonnelProxy(current))
                {
                    Log.Error($"[DeltaV] Personnel promotion #{current.objectId} has no collision proxy; demoting it again.");
                    promotionStatus[current.objectId] = "collision proxy attachment failed; demotion queued";
                    RequestDemotion(current);
                }
                else promotionStatus[current.objectId] = "active mapped personnel";
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Personnel promotion #{previous.objectId} publication failed: {ex}");
                promotionStatus[previous.objectId] = "mapped representation publication failed (retryable)";
                retryable = true;
                previous.Payload = payload;
                if (pawn.Spawned && pawn.Map == map)
                    payload.Capture(pawn, map, pawn.Position, null);
                universe.MapStates.DestroyPocketMap(map);
                return false;
            }
        }

        private void RequestDemotion(SpaceObject obj)
        {
            if (obj is not MapSpaceObject { PersonnelPawn: { } } || !pendingTransition.Add(obj.objectId)) return;
            int id = obj.objectId;
            int version = obj.RepresentationVersion;
            universe.QueueFrameAction(() =>
            {
                pendingTransition.Remove(id);
                if (universe.ObjectById(id) is MapSpaceObject current
                    && current == obj && current.RepresentationVersion == version)
                    Demote(current);
            });
        }

        private bool Demote(MapSpaceObject previous)
        {
            Pawn pawn = previous.PersonnelPawn;
            Map map = previous.map;
            Thing entity = pawn?.Spawned == true && pawn.Map == map ? pawn
                : pawn?.Corpse is Corpse { Spawned: true } corpse && corpse.Map == map ? corpse : null;
            if (entity == null) return false;
            AbstractThing payload = AbstractThing.Create(entity);
            if (payload == null || !payload.Capture(entity, map, entity.Position, null)) return false;
            try
            {
                var current = new SpaceObject(previous.objectId, previous.pose) { Payload = payload };
                previous.CopyFactsTo(current);
                universe.ReplaceRepresentation(previous, current);
                universe.ShipTransitions.QueueEmptyDepartedMapCleanup(universe, map);
                pendingPromotion.Remove(current.objectId);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Personnel demotion #{previous.objectId} publication failed: {ex}");
                payload.Handoff(thing => GenSpawn.Spawn(thing, IntVec3.Zero, map, WipeMode.Vanish));
                return false;
            }
        }
    }
}
