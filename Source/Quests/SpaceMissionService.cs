using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.QuestGen;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed partial class SpaceMissionService
    {
        private readonly UniverseManager universe;
        private readonly HashSet<int> accepting = new HashSet<int>();
        private readonly HashSet<Pawn> preservingGuestStatus = new HashSet<Pawn>();
        private SpaceMissionState Data => universe.missionState;
        internal SpaceMissionService(UniverseManager u) { universe = u; }
        internal SpaceMission Find(int id) => Data.missions.FirstOrDefault(m => m.id == id);
        internal IEnumerable<SpaceMission> Active => Data.missions.Where(m => m.Active);
        internal bool PreserveGuestStatus(Pawn pawn) => preservingGuestStatus.Contains(pawn);
        internal Faction PassengerFaction(Pawn pawn) => Active.FirstOrDefault(m => m.passengers.Any(p => p.pawn == pawn))?.faction;
        internal IEnumerable<Building_SpaceTradeTerminal> Terminals => universe.objects.Where(o => o.map != null && !o.map.Disposed)
            .SelectMany(o => ShipStructureIndex.Read(o.map).BuildingsOf<Building_SpaceTradeTerminal>());
        private int Now => FindTick;
        private static int FindTick => Verse.Find.TickManager.TicksGame;
        internal static bool HasPilotConsole(MapPrefabDocument document) => document.thingEntries.Any(e =>
            e.def?.thingClass != null && typeof(Building).IsAssignableFrom(e.def.thingClass)
            && e.def.HasComp<CompDeltaVPilotConsole>());
        private MissionBoard BoardFor(Building_SpaceTradeTerminal terminal)
        {
            int hostObjectId = universe.FrameOf(terminal.Map)?.objectId ?? 0;
            return Data.boards.FirstOrDefault(board => board.hostObjectId == hostObjectId);
        }

        private static MissionTarget TargetFor(MissionBoard board) => board == null ? null
            : new MissionTarget { objectId = board.hostObjectId, label = board.label };

        private bool DestinationAvailable(MissionTarget address, Faction faction)
        {
            if (address.Resolve(universe) is Building_SpaceTradeTerminal terminal)
                return terminal.MissionIssuer && terminal.Faction == faction;
            SpaceObject host = universe.objects.FirstOrDefault(address.References);
            if (host == null) return false;
            address.objectId = host.objectId;
            return Data.boards.Any(b => b.hostObjectId == address.objectId && b.faction == faction);
        }

        internal MissionBoard Board(Building_SpaceTradeTerminal terminal)
        {
            // Discover only at an interaction boundary. Addresses survive unloading and docking.
            Data.boards.RemoveAll(board => universe.ObjectById(board.hostObjectId) == null
                && !Active.Any(m => m.source?.objectId == board.hostObjectId
                    || m.destination?.objectId == board.hostObjectId));
            foreach (var t in Terminals.Where(t => t.MissionIssuer).OrderBy(t => t.thingIDNumber))
            {
                var known = BoardFor(t);
                MissionTarget target = MissionTarget.Of(universe, t);
                if (known == null) Data.boards.Add(new MissionBoard
                    { hostObjectId = target.objectId, label = target.label, faction = t.Faction });
                else { known.label = target.label; known.faction = t.Faction; }
            }
            MissionBoard board = BoardFor(terminal);
            if (board == null || !terminal.MissionIssuer) return board;
            SpaceMissionDef settings = SpaceMissionDef.Settings;
            if (board.refreshed >= 0 && Now - board.refreshed < settings.refreshDays * GenDate.TicksPerDay) return board;
            board.refreshed = Now;
            board.offers.Clear();
            int tier = settings.Tier(MissionWealth.Total(universe), terminal.Faction.GoodwillWith(Faction.OfPlayer));
            SpaceMissionTier limits = settings.tiers[tier];
            var destinations = Data.boards.Where(b => b != board && b.faction == terminal.Faction
                && b.hostObjectId != board.hostObjectId
                && DestinationAvailable(TargetFor(b), terminal.Faction)).ToList();
            if (destinations.Count > 0)
            {
                for (int i = 0; i < 2; i++)
                {
                    SpaceMission m = Offer(board, tier, (SpaceMissionKind)i);
                    m.destination = TargetFor(destinations.RandomElement());
                    m.quantity = Rand.RangeInclusive(i == 0 ? 2 : 1, i == 0 ? limits.boxes : limits.passengers);
                    m.rewardEach = i == 0 ? settings.freightReward : settings.passengerReward;
                    if (i == 0)
                    {
                        m.cargoDef = settings.cargo.RandomElement();
                        float value = m.cargoDef.GetStatValueAbstract(StatDefOf.MarketValue);
                        if (value <= 0f || value > settings.cargoValueLimit) continue;
                        m.cargoCount = Mathf.Max(1, Mathf.FloorToInt(settings.cargoValueLimit / value));
                        m.depositEach = Mathf.CeilToInt(m.cargoCount * value);
                    }
                    board.offers.Add(m);
                }
            }
            var enemies = SectorContentService.SpaceFactionCandidates().Where(f => f.HostileTo(Faction.OfPlayer)
                && f.HostileTo(terminal.Faction)).ToList();
            var craft = new List<(int tier, string path)>();
            if (enemies.Count > 0)
                for (int i = 0; i <= limits.craft && i < settings.combatCraft.Count; i++)
                    foreach (string path in settings.combatCraft[i].prefabs)
                    {
                        try
                        {
                            var doc = MapPrefab.LoadRelative(path);
                            if (HasPilotConsole(doc)) craft.Add((i, path));
                        }
                        catch (Exception ex) { Log.Warning("[DeltaV] Mission prefab unavailable: " + path + ": " + ex.Message); }
                    }
            if (enemies.Count > 0 && craft.Count > 0)
            {
                var selected = craft.RandomElement();
                SpaceMission m = Offer(board, tier, SpaceMissionKind.Combat);
                m.quantity = 1; m.rewardEach = settings.combatRewards[selected.tier]; m.enemy = enemies.RandomElement();
                m.prefab = selected.path;
                board.offers.Add(m);
            }
            return board;
        }

        private SpaceMission Offer(MissionBoard board, int tier, SpaceMissionKind kind)
        {
            SpaceMissionDef s = SpaceMissionDef.Settings;
            return new SpaceMission { id = Data.nextId++, tier = tier, kind = kind, faction = board.faction,
                source = TargetFor(board), successGoodwill = s.successGoodwill, failureGoodwill = s.failureGoodwill,
                passengerGoodwill = s.passengerGoodwill };
        }

        internal string Describe(SpaceMission m)
        {
            if (m == null) return "";
            string text = "DeltaV_Mission_Details".Translate(m.tier + 1, m.quantity, m.rewardEach,
                m.quantity * m.depositEach, m.destination?.label ?? m.source?.label ?? "—");
            if (m.Active && m.expires >= 0)
                text += "\n" + "DeltaV_Mission_Time".Translate(Math.Max(0, m.deadline - Now).ToStringTicksToPeriod(),
                    Math.Max(0, m.expires - Now).ToStringTicksToPeriod());
            text += "\n" + ("DeltaV_Mission_Rules_" + m.kind).Translate(
                SpaceMissionDef.Settings.deadlineDays, SpaceMissionDef.Settings.graceDays,
                m.successGoodwill, m.failureGoodwill, m.passengerGoodwill);
            if (m.settledAt >= 0)
                text += "\n" + "DeltaV_Mission_Settlement".Translate(m.deliveredCount, m.paidSilver, m.refundedDeposit);
            if (m.kind == SpaceMissionKind.Passengers && m.passengers.Count > 0)
                text += "\n" + string.Join("\n", m.passengers.Select(p => (p.name ?? "—") + ": "
                    + ("DeltaV_Mission_Passenger_" + p.result).Translate().ToString()
                    + (p.reason == null ? "" : " (" + ("DeltaV_Mission_Reason_" + p.reason).Translate().ToString() + ")")));
            if (m.combatComplete) text += "\n" + "DeltaV_Mission_CombatComplete".Translate();
            return text;
        }

        internal void RequestAccept(Building_SpaceTradeTerminal terminal, Pawn pawn, SpaceMission m)
        {
            if (!accepting.Add(m.id)) return;
            universe.QueueFrameAction(() =>
            {
                try { Accept(terminal, pawn, m); }
                catch (Exception ex) { Log.Error("[DeltaV] Mission acceptance failed: " + ex); Reject("DeltaV_Mission_SetupFailed"); }
                finally { accepting.Remove(m.id); }
            });
        }

        private void Accept(Building_SpaceTradeTerminal terminal, Pawn pawn, SpaceMission m)
        {
            var board = BoardFor(terminal);
            if (!terminal.MissionInteraction(pawn) || !terminal.MissionIssuer || board?.offers.Contains(m) != true
                || m.quest != null || m.faction != terminal.Faction) { Reject("DeltaV_Mission_Unavailable"); return; }
            var settings = SpaceMissionDef.Settings;
            if (settings.Tier(MissionWealth.Total(universe), m.faction.GoodwillWith(Faction.OfPlayer)) < m.tier)
            { Reject("DeltaV_Mission_LevelChanged"); return; }
            if (m.kind != SpaceMissionKind.Combat && !DestinationAvailable(m.destination, m.faction))
            { Reject("DeltaV_Mission_Unavailable"); return; }
            var cargo = terminal.MissionCargo;
            int deposit = m.quantity * m.depositEach;
            var silver = terminal.MissionSilver().ToList();
            if (silver.Sum(t => t.stackCount) < deposit) { Reject("DeltaV_Mission_NoSilver"); return; }
            if (m.kind == SpaceMissionKind.Freight && (cargo == null || cargo.CurrentSlotLimit - cargo.Contents.Count < m.quantity))
            { Reject("DeltaV_Mission_NoRoom"); return; }
            var created = new List<Thing>();
            var escrow = new List<(Thing item, Thing original, ThingOwner holder, Map map, IntVec3 position)>();
            Map enemyMap = null;
            bool committed = false;
            try
            {
                m.source = MissionTarget.Of(universe, terminal);
                if (m.kind == SpaceMissionKind.Freight)
                    for (int i = 0; i < m.quantity; i++)
                    {
                        var box = (MissionCargo)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DeltaV_MissionCargo"));
                        box.missionId = m.id; box.boxNumber = i + 1; box.issuer = m.faction;
                        box.contents = m.cargoDef; box.count = m.cargoCount; created.Add(box);
                        m.cargoIds.Add(box.thingIDNumber);
                        if (!cargo.TryStore(box)) throw new InvalidOperationException("Cargo placement refused.");
                    }
                else if (m.kind == SpaceMissionKind.Passengers)
                    CreatePassengers(terminal, m, created);
                else enemyMap = CreateEnemy(terminal, m);

                Data.missions.Add(m);
                var slate = new Slate(); slate.Set("missionId", m.id);
                Quest quest = QuestGen.Generate(DefDatabase<QuestScriptDef>.GetNamed("DeltaV_SpaceMission"), slate);
                int left = deposit;
                foreach (Thing money in silver)
                {
                    if (left == 0) break;
                    int take = Math.Min(left, money.stackCount);
                    Map map = money.MapHeld; IntVec3 pos = money.PositionHeld;
                    ThingOwner holder = money.holdingOwner;
                    escrow.Add((money.SplitOff(take), money, holder, map, pos)); left -= take;
                }
                if (left != 0) throw new InvalidOperationException("Deposit changed during acceptance.");
                m.deadline = m.kind == SpaceMissionKind.Combat ? -1 : Now + settings.deadlineDays * GenDate.TicksPerDay;
                m.expires = m.deadline < 0 ? -1 : m.deadline + settings.graceDays * GenDate.TicksPerDay;
                Verse.Find.QuestManager.Add(quest);
                quest.Accept(pawn);
                foreach (var p in m.passengers) p.pawn.SetFaction(Faction.OfPlayer);
                board.offers.Remove(m);
                committed = true;
                foreach (var held in escrow) held.item.Destroy();
                universe.PublishPlayerMapActivity(terminal.Map, PlayerContactKind.Mission, terminal);
                Messages.Message("DeltaV_Mission_Accepted".Translate(m.Title), MessageTypeDefOf.PositiveEvent, false);
            }
            finally
            {
                if (!committed)
                {
                    if (m.quest != null && Verse.Find.QuestManager.Contains(m.quest))
                    {
                        m.settled = true; m.quest.End(QuestEndOutcome.Unknown, false, false);
                        Verse.Find.QuestManager.Remove(m.quest);
                    }
                    foreach (Thing thing in created) if (!thing.Destroyed) thing.Destroy();
                    foreach (var held in escrow)
                    {
                        if (held.item.Destroyed) continue;
                        if (held.item != held.original && !held.original.Destroyed)
                            held.original.TryAbsorbStack(held.item, respectStackLimit: true);
                        if (held.item.Destroyed || held.holder?.TryAdd(held.item) == true) continue;
                        if (!GenPlace.TryPlaceThing(held.item, held.position, held.map, ThingPlaceMode.Near))
                            GenSpawn.Spawn(held.item, held.position, held.map);
                    }
                    if (enemyMap != null)
                    {
                        SpaceObject enemyObject = universe.FrameOf(enemyMap);
                        if (enemyObject != null) universe.UnregisterObject(enemyObject);
                        universe.MapStates.DestroyPocketMap(enemyMap);
                    }
                    Data.missions.Remove(m); m.quest = null; m.passengers.Clear(); m.cargoIds.Clear(); m.settled = false;
                    m.goodwillApplied = false; m.deadline = m.expires = m.settledAt = -1;
                    m.enemyTarget = null;
                }
            }
        }

        private static void Reject(string key) => Messages.Message(key.Translate(), MessageTypeDefOf.RejectInput, false);
        internal void Resume()
        {
            foreach (var m in Active.ToList())
            {
                foreach (var p in m.passengers.Where(p => p.result != PassengerResult.Travelling && p.pawn != null))
                    universe.QueueFrameAction(() => ReleasePassenger(m, p));
                if (m.kind == SpaceMissionKind.Combat && m.enemyTarget != null)
                    ShipChanged(new DriveSubject(0, m.enemyTarget.shipId));
            }
        }
        internal void Tick(SpaceMission m)
        {
            if (m?.Active == true && !m.settled && m.expires >= 0 && Now >= m.expires) Fail(m);
        }
        internal void Fail(SpaceMission m)
        {
            if (m?.Active != true || m.settled) return;
            m.settled = true;
            m.settledAt = Now;
            ApplyOutcome(m, false);
            m.quest.End(QuestEndOutcome.Fail);
        }
        private static void ApplyOutcome(SpaceMission m, bool success)
        {
            if (m.goodwillApplied) return;
            m.goodwillApplied = true;
            m.faction?.TryAffectGoodwillWith(Faction.OfPlayer, success ? m.successGoodwill : m.failureGoodwill, true, true);
        }
        internal void Cleanup(SpaceMission m)
        {
            if (m == null) return;
            if (!m.settled) { m.settled = true; ApplyOutcome(m, false); }
            foreach (var p in m.passengers) ReleasePassenger(m, p);
        }
    }
}
