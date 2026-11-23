using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    public enum SpaceMissionKind { Freight, Passengers, Combat }
    public enum PassengerResult { Travelling, Delivered, Failed }

    public sealed class SpaceMissionTier
    {
        public float wealth;
        public int goodwill, boxes, passengers, craft;
    }

    public sealed class SpaceMissionCraftTier
    {
        public List<string> prefabs = new List<string>();
    }

    public sealed class SpaceMissionDef : Def
    {
        public List<SpaceMissionTier> tiers = new List<SpaceMissionTier>();
        public List<ThingDef> cargo = new List<ThingDef>();
        public List<SpaceMissionCraftTier> combatCraft = new List<SpaceMissionCraftTier>();
        public List<int> combatRewards = new List<int>();
        public int refreshDays = 5, deadlineDays = 5, graceDays = 2;
        public int freightReward = 100, passengerReward = 200, successGoodwill = 3, failureGoodwill = -5, passengerGoodwill = -5;
        public float cargoValueLimit = 300f;
        public static SpaceMissionDef Settings => DefDatabase<SpaceMissionDef>.GetNamed("DeltaV_Missions");
        public int Tier(float wealth, int goodwill)
        {
            int result = 0;
            for (int i = 0; i < tiers.Count; i++)
                if (wealth >= tiers[i].wealth && goodwill >= tiers[i].goodwill) result = i;
            return result;
        }
    }

    // Stable addresses, not external Thing references into unloadable maps.
    public sealed class MissionTarget : IExposable
    {
        public int objectId, shipId;
        public string label;
        public static MissionTarget Of(UniverseManager u, Thing thing)
        {
            SpaceObject host = u.FrameOf(thing.Map);
            if (thing is Building_SpaceTradeTerminal)
                return new MissionTarget { objectId = host?.objectId ?? 0,
                    label = host?.label ?? thing.LabelCap.ToString() };
            ShipBase ship = ShipRegistry.ShipOf(thing);
            return new MissionTarget { objectId = host?.objectId ?? 0,
                shipId = ship.IsValid ? ship.Identity.key : 0,
                label = ship.IsValid ? ship.Label : thing.LabelCap.ToString() };
        }
        public Thing Resolve(UniverseManager u)
        {
            if (u == null) return null;
            if (shipId <= 0)
            {
                SpaceObject host = u.ObjectById(objectId);
                return host?.map == null || host.map.Disposed ? null
                    : ShipStructureIndex.Read(host.map).BuildingsOf<Building_SpaceTradeTerminal>()
                        .Where(terminal => terminal.Spawned).OrderBy(terminal => terminal.thingIDNumber)
                        .FirstOrDefault();
            }
            foreach (SpaceObject obj in u.objects)
            {
                if (obj.map == null || obj.map.Disposed) continue;
                ShipBase ship = obj.Ships.FirstOrDefault(candidate => candidate.Identity.key == shipId);
                if (ship?.IsValid == true)
                {
                    objectId = obj.objectId;
                    return ship.Anchor;
                }
            }
            return null;
        }
        public bool Matches(UniverseManager u, Thing thing)
        {
            if (thing == null || !thing.Spawned) return false;
            SpaceObject obj = u?.FrameOf(thing.Map);
            if (obj == null) return false;
            if (shipId <= 0)
                return thing is Building_SpaceTradeTerminal && obj.objectId == objectId;
            ShipBase ship = ShipRegistry.ShipOf(thing);
            return ship.IsValid && ship.Identity.key == shipId;
        }
        internal bool References(SpaceObject obj)
        {
            if (obj == null) return false;
            if (objectId == obj.objectId) return true;
            return shipId > 0 && (obj is ArchivedSpaceObject archived
                ? archived.globe?.ships.Any(ship => !ship.shuttle && ship.key == shipId) == true
                : obj.Ships.Any(ship => ship.Identity.key == shipId));
        }
        internal GlobeTarget Navigation => shipId > 0 ? new GlobeTarget { shipKey = shipId }
            : new GlobeTarget { objectId = objectId };
        public void ExposeData()
        {
            Scribe_Values.Look(ref objectId, "objectId"); Scribe_Values.Look(ref shipId, "shipId");
            Scribe_Values.Look(ref label, "label");
        }
    }

    public sealed class MissionPassenger : IExposable
    {
        public Pawn pawn;
        public int pawnId;
        public string name;
        public PassengerResult result;
        public string reason;
        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn"); Scribe_Values.Look(ref result, "result");
            Scribe_Values.Look(ref pawnId, "pawnId");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref reason, "reason");
        }
    }

    public sealed class SpaceMission : IExposable
    {
        public int id, tier, quantity, rewardEach, depositEach, deadline = -1, expires = -1;
        public int successGoodwill, failureGoodwill, passengerGoodwill;
        public SpaceMissionKind kind;
        public Faction faction, enemy;
        public MissionTarget source, destination, enemyTarget;
        public ThingDef cargoDef;
        public int cargoCount;
        public string prefab;
        public Quest quest;
        public bool combatComplete, settled, goodwillApplied;
        public int deliveredCount, paidSilver, refundedDeposit, settledAt = -1;
        public List<MissionPassenger> passengers = new List<MissionPassenger>();
        public List<int> cargoIds = new List<int>();
        public bool Active => quest != null && !quest.Historical;
        public string Title => ("DeltaV_Mission_" + kind).Translate() + " #" + id;
        public bool Late(int tick) => deadline >= 0 && tick >= deadline;
        public int Payment(int count, int tick) => checked(count * (rewardEach + (kind == SpaceMissionKind.Freight && !Late(tick) ? depositEach : 0)));
        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id"); Scribe_Values.Look(ref tier, "tier");
            Scribe_Values.Look(ref quantity, "quantity"); Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref rewardEach, "rewardEach"); Scribe_Values.Look(ref depositEach, "depositEach");
            Scribe_Values.Look(ref deadline, "deadline", -1); Scribe_Values.Look(ref expires, "expires", -1);
            Scribe_Values.Look(ref successGoodwill, "successGoodwill"); Scribe_Values.Look(ref failureGoodwill, "failureGoodwill");
            Scribe_Values.Look(ref passengerGoodwill, "passengerGoodwill");
            Scribe_References.Look(ref faction, "faction"); Scribe_References.Look(ref enemy, "enemy");
            Scribe_Deep.Look(ref source, "source"); Scribe_Deep.Look(ref destination, "destination");
            Scribe_Deep.Look(ref enemyTarget, "enemyTarget"); Scribe_Defs.Look(ref cargoDef, "cargoDef");
            Scribe_Values.Look(ref cargoCount, "cargoCount"); Scribe_Values.Look(ref prefab, "prefab");
            Scribe_References.Look(ref quest, "quest");
            Scribe_Values.Look(ref combatComplete, "combatComplete"); Scribe_Values.Look(ref settled, "settled");
            Scribe_Values.Look(ref goodwillApplied, "goodwillApplied");
            Scribe_Values.Look(ref deliveredCount, "deliveredCount"); Scribe_Values.Look(ref paidSilver, "paidSilver");
            Scribe_Values.Look(ref refundedDeposit, "refundedDeposit"); Scribe_Values.Look(ref settledAt, "settledAt", -1);
            Scribe_Collections.Look(ref passengers, "passengers", LookMode.Deep);
            Scribe_Collections.Look(ref cargoIds, "cargoIds", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) passengers ??= new List<MissionPassenger>();
        }
    }

    public sealed class MissionBoard : IExposable
    {
        public int hostObjectId;
        public string label;
        public Faction faction;
        public int refreshed = -1;
        public List<SpaceMission> offers = new List<SpaceMission>();
        public void ExposeData()
        {
            Scribe_Values.Look(ref hostObjectId, "hostObjectId");
            Scribe_Values.Look(ref label, "label");
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref refreshed, "refreshed", -1);
            Scribe_Collections.Look(ref offers, "offers", LookMode.Deep);
        }
    }

    public sealed class SpaceMissionState : IExposable
    {
        public int nextId = 1;
        internal TutorialRunState tutorial;
        public List<SpaceMission> missions = new List<SpaceMission>();
        public List<MissionBoard> boards = new List<MissionBoard>();
        public void ExposeData()
        {
            Scribe_Values.Look(ref nextId, "nextId", 1);
            Scribe_Deep.Look(ref tutorial, "tutorial");
            Scribe_Collections.Look(ref missions, "missions", LookMode.Deep);
            Scribe_Collections.Look(ref boards, "boards", LookMode.Deep);
        }
    }
}
