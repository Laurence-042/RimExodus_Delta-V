using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DeltaV
{
    internal sealed partial class SpaceMissionService
    {
        private void CreatePassengers(Building_SpaceTradeTerminal terminal, SpaceMission m, List<Thing> created)
        {
            var cells = terminal.MissionCells.Where(c => c.Standable(terminal.Map)
                && !c.GetTerrain(terminal.Map).IsWater && c.GetRoom(terminal.Map)?.PsychologicallyOutdoors == false).ToList();
            if (cells.Count == 0) throw new InvalidOperationException("Station has no safe passenger boarding area.");
            for (int i = 0; i < m.quantity; i++)
            {
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, m.faction,
                    forceGenerateNewPawn: true, allowDead: false, allowDowned: false, canGeneratePawnRelations: false));
                created.Add(pawn);
                GenSpawn.Spawn(pawn, cells[i % cells.Count], terminal.Map);
                m.passengers.Add(new MissionPassenger { pawn = pawn, pawnId = pawn.thingIDNumber, name = pawn.LabelShortCap });
            }
        }

        private Map CreateEnemy(Building_SpaceTradeTerminal terminal, SpaceMission m)
        {
            if (!SectorContentService.SpaceFactionCandidates().Contains(m.enemy)
                || !m.enemy.HostileTo(Faction.OfPlayer) || !m.enemy.HostileTo(m.faction))
                throw new InvalidOperationException("Mission opponent is no longer eligible.");
            MapPrefabDocument doc = MapPrefab.LoadRelative(m.prefab);
            Map map = null;
            SpaceObject obj = null;
            try
            {
                map = ShipPocketMapFactory.Generate(ShipPocketMapFactory.CompactSize(doc.size), universe);
                if (map == null) throw new InvalidOperationException("Mission ship map generation failed.");
                SpaceObject home = universe.FrameOf(terminal.Map);
                obj = new MapSpaceObject(universe.AllocateOwnerId(), map, home.pose);
                universe.RegisterObject(obj);
                bool placed = false;
                for (int attempt = 0; attempt < 128; attempt++)
                {
                    obj.pose = DeltaVTutorialService.CoursePose(home.pose, home.map.Size, map.Size, 64,
                        Mathf.Max(DeltaVMod.CurrentSettings.poiDeleteRadius, 1200), 200f, attempt);
                    if (universe.objects.Any(o => o != obj && o.HasGravityRegion
                        && OrbitalWorkService.IntersectsGravity(obj, CellRect.WholeMap(map).ExpandedBy(64), o))) continue;
                    float footprint = new Vector2(doc.size.x, doc.size.z).magnitude * .5f + 64f;
                    if (universe.objects.Any(o => o != obj && !o.IsTransient
                        && OrbitSphere.ArcCells(obj.pose.position, o.pose.position) < footprint
                        + (o.IsArchived ? Mathf.Max(DeltaVMod.CurrentSettings.poiActiveRadius, 256f)
                            : SpaceCollision.OriginBoundsRadiusOf(o)))) continue;
                    placed = true; break;
                }
                if (!placed) throw new InvalidOperationException("No clear mission ship location.");
                int owner = universe.AllocateOwnerId();
                var things = MapPrefab.Spawn(doc, map, MapPrefab.CenteredPosition(doc, map, Rot4.North), m.enemy, owner, Rot4.North, universe);
                int expected = doc.thingEntries.Count(e => !e.def.comps.Any(c => typeof(CompPrefabMarker).IsAssignableFrom(c.compClass)));
                if (things.Count != expected) throw new InvalidOperationException("Incomplete mission ship prefab.");
                ShipBase ship = ShipRegistry.AllShipsOnMap(map).OfType<FoundationShip>().FirstOrDefault(s => s.IsValid && s.Faction == m.enemy);
                if (ship == null) throw new InvalidOperationException("Mission ship has no main console.");
                m.enemyTarget = MissionTarget.Of(universe, ship.Anchor);
                obj.contentTypeId = m.craftType;
                universe.NoteStructureName(ship.Identity.key, m.Title);
                return map;
            }
            catch
            {
                if (obj != null) universe.UnregisterObject(obj);
                if (map != null) universe.Lifecycle.DestroyPocketMap(map);
                throw;
            }
        }

        internal IEnumerable<MissionPassenger> DeliverablePassengers(Building_SpaceTradeTerminal terminal, SpaceMission m)
        {
            var cells = terminal.MissionCells;
            return m.passengers.Where(p => p.result == PassengerResult.Travelling && p.pawn?.Spawned == true
                && !p.pawn.Dead && !p.pawn.IsPrisoner && !p.pawn.IsSlave && p.pawn.Map == terminal.Map && cells.Contains(p.pawn.Position));
        }
        internal List<MissionCargo> DeliverableBoxes(Building_SpaceTradeTerminal terminal, SpaceMission m)
            => terminal.MissionCargo?.Contents.OfType<MissionCargo>()
                .Where(b => b.Sealed && b.missionId == m.id && b.issuer == m.faction && m.cargoIds.Contains(b.thingIDNumber))
                .ToList() ?? new List<MissionCargo>();

        internal bool MayStore(Thing shelf, Thing item)
        {
            if (!(item is MissionCargo box) || !box.Sealed || shelf?.Spawned != true) return false;
            SpaceMission m = Find(box.missionId);
            if (m?.Active != true || m.settled || m.faction != shelf.Faction || box.issuer != m.faction) return false;
            return shelf.Map.listerThings.AllThings.OfType<Building_SpaceTradeTerminal>()
                .Any(t => t.thingIDNumber == m.destination?.thingId && t.MissionCargo?.Thing == shelf);
        }

        internal int Deliverable(Building_SpaceTradeTerminal terminal, SpaceMission m)
        {
            if (m?.Active != true || m.settled || terminal.Faction != m.faction) return 0;
            if ((m.kind == SpaceMissionKind.Combat ? m.source : m.destination)?.thingId != terminal.thingIDNumber) return 0;
            return m.kind == SpaceMissionKind.Freight ? DeliverableBoxes(terminal, m).Count
                : m.kind == SpaceMissionKind.Passengers ? DeliverablePassengers(terminal, m).Count()
                : m.combatComplete ? 1 : 0;
        }

        internal void ConfirmDelivery(Building_SpaceTradeTerminal terminal, Pawn pawn, SpaceMission m)
        {
            int count = Deliverable(terminal, m), payment = m.Payment(count, Now);
            if (count == 0) { Reject("DeltaV_Mission_NothingToDeliver"); return; }
            string text = "DeltaV_Mission_ConfirmDelivery".Translate(count, m.quantity, payment, m.successGoodwill);
            Verse.Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, () =>
            {
                if (!terminal.MissionInteraction(pawn) || Deliverable(terminal, m) != count || m.Payment(count, Now) != payment)
                { Reject("DeltaV_Mission_Changed"); return; }
                Settle(terminal, m, count, payment);
            }));
        }

        private void Settle(Building_SpaceTradeTerminal terminal, SpaceMission m, int count, int payment)
        {
            if (m?.Active != true || m.settled) return;
            if (m.expires >= 0 && Now >= m.expires) { Fail(m); return; }
            ISpaceCargoContainer cargo = terminal.MissionCargo;
            var boxes = DeliverableBoxes(terminal, m);
            var people = DeliverablePassengers(terminal, m).ToList();
            int stacks = (payment + ThingDefOf.Silver.stackLimit - 1) / ThingDefOf.Silver.stackLimit;
            if (cargo == null || cargo.CurrentSlotLimit - cargo.Contents.Count + boxes.Count < stacks)
            { Reject("DeltaV_Mission_NoRoom"); return; }
            // Reserve physical output first. Detach delivered boxes reversibly to free their slots.
            var detached = new List<Thing>(); var rewards = new List<Thing>();
            try
            {
                foreach (var box in boxes)
                {
                    Thing taken = cargo.Take(box, 1);
                    if (taken == null) throw new InvalidOperationException("Delivery changed.");
                    detached.Add(taken);
                }
                int remaining = payment;
                while (remaining > 0)
                {
                    Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                    silver.stackCount = Math.Min(remaining, silver.def.stackLimit);
                    rewards.Add(silver);
                    if (!cargo.TryStore(silver)) throw new InvalidOperationException("Reward placement refused.");
                    remaining -= silver.stackCount;
                }
            }
            catch (Exception ex)
            {
                foreach (Thing silver in rewards) if (!silver.Destroyed) silver.Destroy();
                foreach (Thing box in detached)
                    if (!cargo.TryStore(box)) GenPlace.TryPlaceThing(box, terminal.Position, terminal.Map, ThingPlaceMode.Near);
                Log.Warning("[DeltaV] Delivery rolled back: " + ex.Message);
                Reject("DeltaV_Mission_NoRoom"); return;
            }
            m.settled = true;
            m.deliveredCount = count; m.paidSilver = payment; m.settledAt = Now;
            m.refundedDeposit = m.kind == SpaceMissionKind.Freight && !m.Late(Now) ? count * m.depositEach : 0;
            foreach (var p in people) p.result = PassengerResult.Delivered;
            foreach (Thing box in detached) box.Destroy();
            ApplyOutcome(m, true);
            m.quest.End(QuestEndOutcome.Success);
        }

        internal bool IsPassenger(Pawn pawn) => Active.Any(m => m.passengers.Any(p => p.pawn == pawn && p.result == PassengerResult.Travelling));
        internal bool HasPassengerContract(Pawn pawn) => pawn != null && Active.Any(m => m.passengers.Any(p => p.pawn == pawn)
            && pawn.questTags?.Contains("Quest" + m.quest.id + ".Passengers") == true);
        internal void FailPassenger(Pawn pawn, string reason)
        {
            foreach (var m in Active.ToList()) FailPassenger(m, pawn, reason);
        }
        internal void FailPassenger(SpaceMission m, Pawn pawn, string reason)
        {
            if (m?.Active != true || m.settled) return;
            var p = m.passengers.FirstOrDefault(x => x.pawn == pawn);
            if (p == null || p.result != PassengerResult.Travelling) return;
            p.result = PassengerResult.Failed; p.reason = reason;
            m.faction.TryAffectGoodwillWith(Faction.OfPlayer, m.passengerGoodwill, true, true);
            // The arrest/surgery caller must finish changing the individual before release.
            universe.QueueFrameAction(() => ReleasePassenger(m, p));
        }

        private void ReleasePassenger(SpaceMission m, MissionPassenger passenger)
        {
            Pawn pawn = passenger.pawn;
            if (pawn == null || m.quest == null) return;
            foreach (var part in m.quest.PartsListForReading.OfType<QuestPart_ExtraFaction>()) part.affectedPawns.Remove(pawn);
            foreach (var part in m.quest.PartsListForReading.OfType<QuestPart_WorkDisabled>()) part.pawns.Remove(pawn);
            pawn.questTags?.Remove("Quest" + m.quest.id + ".Passengers");
            if (!pawn.Dead && !pawn.Destroyed && pawn.Faction == Faction.OfPlayer && pawn.IsPrisoner)
            {
                // SetFaction normally clears guest status. Keep the actual imprisonment intact.
                preservingGuestStatus.Add(pawn);
                try { pawn.SetFaction(m.faction); }
                finally { preservingGuestStatus.Remove(pawn); }
            }
            if (!pawn.Dead && !pawn.Destroyed && pawn.Faction == Faction.OfPlayer && !pawn.IsPrisoner && !pawn.IsSlave)
            {
                pawn.SetFaction(m.faction);
                if (pawn.Spawned && pawn.GetLord() == null)
                    LordMaker.MakeNewLord(m.faction, new LordJob_DefendPoint(pawn.Position, addFleeToil: false), pawn.Map, new[] { pawn });
            }
            pawn.Notify_DisabledWorkTypesChanged();
            passenger.pawn = null;
        }

        internal void ShipChanged(DriveSubject identity, bool consoleDestroyed = false)
        {
            // Reconcile after the whole transfer/backup succession, never in a transient DeSpawn callback.
            if (!Active.Any(m => m.kind == SpaceMissionKind.Combat && !m.combatComplete && m.enemyTarget?.shipId == identity.key)) return;
            universe.QueueFrameAction(() =>
            {
                foreach (var m in Active.Where(m => m.kind == SpaceMissionKind.Combat && !m.combatComplete && m.enemyTarget?.shipId == identity.key))
                {
                    ShipBase ship = ShipRegistry.ShipByIdentity(identity);
                    if (ship.IsValid)
                    {
                        m.enemyTarget.objectId = universe.FrameOf(ship.Map)?.objectId ?? m.enemyTarget.objectId;
                        if (ship.Faction != m.enemy) m.combatComplete = true;
                    }
                    else
                    {
                        // Archive retains the exact ship identity. Its absent live console is not a defeat.
                        if (universe.objects.OfType<ArchivedSpaceObject>().Any(o => o.globe?.ships.Any(d => d.key == identity.key) == true)) continue;
                        // An unresolved/unloaded container is not evidence of console destruction.
                        SpaceObject owner = universe.ObjectById(m.enemyTarget.objectId);
                        if (consoleDestroyed || owner?.map != null && !owner.map.Disposed) m.combatComplete = true;
                    }
                }
            });
        }
        internal bool Protects(SpaceObject obj) => Active.Any(m => m.kind == SpaceMissionKind.Combat
            && (m.enemyTarget?.objectId == obj.objectId || (obj is ArchivedSpaceObject archived
                ? archived.globe?.ships.Any(s => !s.shuttle && s.key == m.enemyTarget?.shipId) == true
                : obj.Ships.Any(s => s.Identity.key == m.enemyTarget?.shipId))));
    }
}
