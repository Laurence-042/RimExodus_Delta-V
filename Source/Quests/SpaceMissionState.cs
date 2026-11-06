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

    public sealed class SpaceMissionDef : Def
    {
        public List<SpaceMissionTier> tiers = new List<SpaceMissionTier>();
        public List<ThingDef> cargo = new List<ThingDef>();
        public List<string> craftTypes = new List<string>();
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
        public int objectId, shipId, thingId;
        public string label;
        public static MissionTarget Of(UniverseManager u, Thing thing)
        {
            ShipBase ship = ShipRegistry.ShipOf(thing);
            return new MissionTarget { objectId = u.FrameOf(thing.Map)?.objectId ?? 0,
                shipId = ship.IsValid ? ship.Identity.key : 0, thingId = thing.thingIDNumber,
                label = ship.IsValid ? ship.Label : thing.LabelCap.ToString() };
        }
        public Thing Resolve(UniverseManager u)
        {
            foreach (SpaceObject obj in u.objects)
            {
                if (obj.map == null || obj.map.Disposed) continue;
                Thing found = obj.map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
                if (found == null) continue;
                objectId = obj.objectId;
                ShipBase ship = ShipRegistry.ShipOf(found);
                shipId = ship.IsValid ? ship.Identity.key : 0;
                return found;
            }
            return null;
        }
        internal GlobeTarget Navigation => shipId > 0 ? new GlobeTarget { shipKey = shipId }
            : new GlobeTarget { objectId = objectId };
        public void ExposeData()
        {
            Scribe_Values.Look(ref objectId, "objectId"); Scribe_Values.Look(ref shipId, "shipId");
            Scribe_Values.Look(ref thingId, "thingId"); Scribe_Values.Look(ref label, "label");
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
        public string prefab, craftType;
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
            Scribe_Values.Look(ref craftType, "craftType"); Scribe_References.Look(ref quest, "quest");
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
        public MissionTarget terminal;
        public Faction faction;
        public int refreshed = -1;
        public List<SpaceMission> offers = new List<SpaceMission>();
        public void ExposeData()
        {
            Scribe_Deep.Look(ref terminal, "terminal"); Scribe_References.Look(ref faction, "faction");
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
