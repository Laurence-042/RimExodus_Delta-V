using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Xml;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    static void MissionChecks(Game game, Harmony harmony, string prefabs)
    {
        var consolePrefab = new RimExodus.Maps.MapPrefabDocument();
        var consoleEntry = new RimExodus.Maps.MapPrefabThingEntry();
        consolePrefab.thingEntries.Add(consoleEntry);
        foreach (Type type in new[] { typeof(CompDeltaVPilotConsole), typeof(DerivedPilotConsole) })
        {
            consoleEntry.def = PilotFixtureDef("missionCustomConsole", type);
            Check(SpaceMissionService.HasPilotConsole(consolePrefab),
                "combat prefab eligibility accepts custom Def with " + type.Name);
        }
        consoleEntry.def = PilotFixtureDef("PilotConsole");
        Check(!SpaceMissionService.HasPilotConsole(consolePrefab),
            "combat prefab eligibility does not infer identity from the console Def name");
        consoleEntry.def = PilotFixtureDef("missionItemConsole", typeof(DerivedPilotConsole));
        consoleEntry.def.thingClass = typeof(ThingWithComps);
        Check(!SpaceMissionService.HasPilotConsole(consolePrefab),
            "combat prefab eligibility excludes non-building components");
        var xml = new XmlDocument();
        xml.Load(Path.Combine(prefabs, "../../1.6/Defs/Quests/DeltaV_Missions.xml"));
        var settings = new SpaceMissionDef();
        foreach (XmlNode node in xml.SelectNodes("/Defs/DeltaV.SpaceMissionDef/tiers/li"))
            settings.tiers.Add(new SpaceMissionTier { wealth = float.Parse(node["wealth"].InnerText),
                goodwill = int.Parse(node["goodwill"].InnerText), boxes = int.Parse(node["boxes"].InnerText),
                passengers = int.Parse(node["passengers"].InnerText), craft = int.Parse(node["craft"].InnerText) });
        Check(settings.Tier(1000000, 0) == 0, "wealth alone cannot unlock trusted commissions");
        Check(settings.Tier(0, 100) == 0, "trust alone cannot unlock large commissions");
        for (int tier = 1; tier < settings.tiers.Count; tier++)
        {
            var t = settings.tiers[tier];
            Check(settings.Tier(t.wealth, t.goodwill) == tier, "both tier gates unlock " + tier);
            Check(settings.Tier(t.wealth - 1, 100) == tier - 1, "wealth boundary " + tier);
            Check(settings.Tier(1000000, t.goodwill - 1) == tier - 1, "goodwill boundary " + tier);
            Check(t.boxes > settings.tiers[tier - 1].boxes && t.passengers > settings.tiers[tier - 1].passengers,
                "higher tier raises both transport caps " + tier);
        }
        var mission = new SpaceMission { id = 27, kind = SpaceMissionKind.Freight, quantity = 4,
            depositEach = 300, rewardEach = 100, deadline = 1000, expires = 1200 };
        Check(mission.Payment(4, 999) == 1600, "on-time whole freight returns all deposits and rewards");
        Check(mission.Payment(1, 999) == 400, "partial freight refunds only delivered units");
        Check(mission.Payment(4, 1000) == 400 && mission.Payment(1, 1100) == 100,
            "late delivery loses deposit but keeps the same unit reward");
        Check(mission.Payment(0, 999) == 0, "zero delivery cannot refund deposit");
        var source = sourceDefs["DeltaV_MissionCargo"];
        Check(source["tradeability"].InnerText == "None" && source["statBases"]["MarketValue"].InnerText == "0",
            "sealed consignments are valueless and excluded from trade by native Def");
        Check(source["stackLimit"].InnerText == "1", "consignment identity cannot merge across missions");

        var stableEndpoint = new MissionTarget { objectId = 303, label = "stable endpoint" };
        string endpointSave = Scribe.saver.DebugOutputFor(stableEndpoint);
        Check(endpointSave.Contains("<objectId>303</objectId>")
            && !endpointSave.Contains("terminalUuid") && !endpointSave.Contains("cachedThingId"),
            "mission endpoint persistence contains no terminal or runtime Thing identity");

        var universe = game.GetComponent<UniverseManager>();
        foreach (Type patch in typeof(SpaceMission).Assembly.GetTypes().Where(t => t.Name.StartsWith("Patch_Mission")))
        { harmony.CreateClassProcessor(patch).Patch(); checks++; }
        var issuer = new Faction { def = new FactionDef() };
        var a = Raw<Pawn>(); var b = Raw<Pawn>();
        a.def = b.def = Raw<ThingDef>(); a.def.defName = "MissionPassengerFixture"; a.def.race = new RaceProperties();
        a.thingIDNumber = 91001; b.thingIDNumber = 91002;
        Set(a, "factionInt", Faction.OfPlayer); Set(b, "factionInt", Faction.OfPlayer);
        a.questTags = new List<string> { "Quest921.Passengers" };
        b.questTags = new List<string> { "Quest921.Passengers" };
        var quest = new Quest { id = 921, acceptanceTick = 0 };
        var run = new SpaceMission { id = 28, quest = quest, kind = SpaceMissionKind.Passengers, faction = issuer,
            passengerGoodwill = -5, passengers = new List<MissionPassenger>
            { new MissionPassenger { pawn = a, name = "A" }, new MissionPassenger { pawn = b, name = "B" } } };
        var other = new SpaceMission { id = 29, quest = new Quest { id = 922, acceptanceTick = 0 },
            kind = SpaceMissionKind.Passengers, faction = issuer, passengerGoodwill = -5,
            passengers = new List<MissionPassenger> { new MissionPassenger { pawn = b, name = "B" } } };
        universe.missionState.missions.Add(run); universe.missionState.missions.Add(other);
        var part = new QuestPart_MissionPassengers { quest = quest, missionId = run.id };
        quest.AddPart(part);
        var extra = new QuestPart_ExtraFaction { affectedPawns = new List<Pawn> { a, b },
            extraFaction = new ExtraFaction(issuer, ExtraFactionType.HomeFaction), areHelpers = false };
        quest.AddPart(extra);
        var work = new QuestPart_WorkDisabled { pawns = new List<Pawn> { a, b }, disabledWorkTags = WorkTags.AllWork };
        quest.AddPart(work); Set(work, "state", QuestPartState.Enabled);
        var previousQuests = game.questManager;
        game.questManager = new QuestManager();
        game.questManager.ExtraFactionQuestParts.Add(extra);
        game.questManager.ActiveQuestsListForReading.Add(quest);
        game.questManager.QuestsListForReading.Add(quest);
        game.signalManager.RegisterReceiver(quest);
        var goodwill = AccessTools.Method(typeof(Faction), nameof(Faction.TryAffectGoodwillWith));
        harmony.Patch(goodwill, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureGoodwill)));
        var queue = (List<Action>)AccessTools.Field(typeof(UniverseManager), "frameActions").GetValue(universe);
        int queueCount = queue?.Count ?? 0;
        try
        {
            goodwillEvents = goodwillDelta = 0;
            Check(a.IsQuestLodger() && a.HomeFaction == issuer && !a.IsQuestHelper(),
                "native guest identity retains issuer without reinforcement helper identity");
            Check(!WealthWatcher.WealthItemsFilter(a), "native wealth excludes the guest's held assets");
            Check(QuestUtility.GetWorkDisabledQuestPart(a).Contains(work) && (work.disabledWorkTags & WorkTags.Violent) == 0,
                "native work restriction leaves combat available");
            part.Notify_QuestSignalReceived(new Signal("Quest922.Passengers.Arrested", a.Named("SUBJECT")));
            part.Notify_QuestSignalReceived(new Signal("Quest921.Passengers.LeftMap", a.Named("SUBJECT")));
            Check(goodwillEvents == 0, "wrong quest signal and ordinary map transfer cannot penalize passengers");
            var doctor = Raw<Pawn>(); Set(doctor, "factionInt", Faction.OfPlayer);
            AccessTools.Method(typeof(RecipeWorker), "ReportViolation").Invoke(new RecipeWorker(), new object[] { a, doctor, issuer, -40, null });
            Check(run.passengers[0].result == PassengerResult.Failed && run.passengers[1].result == PassengerResult.Travelling,
                "surgery violation fails only the affected passenger");
            Check(run.Active && other.Active && other.passengers[0].result == PassengerResult.Travelling,
                "individual failure neither ends commission nor affects another commission");
            part.Notify_QuestSignalReceived(new Signal("Quest921.Passengers.Arrested", a.Named("SUBJECT")));
            issuer.Notify_MemberTookDamage(a, new DamageInfo(DamageDefOf.Cut, 50, instigator: doctor));
            part.Notify_PawnKilled(a, null);
            issuer.Notify_MemberCaptured(a, Faction.OfPlayer);
            issuer.Notify_MemberDied(a, null, false, false, null);
            Check(goodwillEvents == 1 && goodwillDelta == -5, "surgery then arrest then death charges the individual once");
            Check(b.IsQuestLodger() && b.Faction == Faction.OfPlayer && run.Active,
                "other passengers keep native quest control after a companion's violation");
            part.Notify_PawnDiscarded(b);
            Check(goodwillEvents == 2 && goodwillDelta == -10 && run.Active, "missing passenger is independent and does not end the quest");
            foreach (var p in run.passengers) p.pawn = null;
            run.quest = null; run.faction = null;
            var saved = new SpaceMissionState { nextId = 88, missions = new List<SpaceMission> { mission, run } };
            string raw = Scribe.saver.DebugOutputFor(saved), path = Path.GetTempFileName();
            try
            {
                var document = new XmlDocument(); document.LoadXml(raw);
                File.WriteAllText(path, "<root>" + document.DocumentElement.InnerXml + "</root>");
                Scribe.loader.InitLoading(path);
                var loaded = new SpaceMissionState(); loaded.ExposeData(); Scribe.loader.FinalizeLoading();
                Check(loaded.nextId == 88 && loaded.missions.Count == 2, "native Scribe preserves independent missions");
                Check(loaded.missions[0].Payment(1, 999) == 400 && loaded.missions[0].Payment(1, 1000) == 100,
                    "deadline and deposit rules survive reload");
                Check(loaded.missions[1].passengers.All(p => p.result == PassengerResult.Failed)
                    && loaded.missions[1].passengers[0].reason == "SurgeryViolation", "individual results survive reload");
            }
            finally { Scribe.ForceStop(); File.Delete(path); }
        }
        finally
        {
            universe.missionState.missions.Remove(run); universe.missionState.missions.Remove(other);
            game.signalManager.DeregisterReceiver(quest);
            game.questManager = previousQuests;
            queue = (List<Action>)AccessTools.Field(typeof(UniverseManager), "frameActions").GetValue(universe);
            if (queue != null && queue.Count > queueCount) queue.RemoveRange(queueCount, queue.Count - queueCount);
            harmony.Unpatch(goodwill, HarmonyPatchType.Prefix, harmony.Id);
        }
        MissionCombatChecks(game, harmony);
        MissionSettlementChecks(game, harmony);
        Console.WriteLine("Missions: tier gates, deposit/deadline economics, passenger signal isolation, native Scribe and Harmony signatures passed.");
    }

    sealed class MissionBayFixture : ISpaceCargoContainer, IThingHolder
    {
        internal ThingOwner<Thing> inventory;
        internal int limit = 8, refuseSilverAt, silverCalls;
        internal MissionBayFixture() { inventory = new ThingOwner<Thing>(this); }
        public ThingWithComps Thing => null;
        public bool Spawned => true;
        public Map Map => null;
        public IntVec3 Position => IntVec3.Zero;
        public bool IsUnlocked => true;
        public IReadOnlyList<Thing> Contents => inventory.ToList();
        public int CurrentSlotLimit => limit;
        public bool TryStore(Thing thing)
        {
            if (thing.def == ThingDefOf.Silver && ++silverCalls == refuseSilverAt) return false;
            return inventory.Count < limit && inventory.TryAdd(thing, canMergeWithExistingStacks: false);
        }
        public Thing Take(Thing thing, int count) => inventory.Take(thing, count);
        public string GetUniqueLoadID() => "MissionBayFixture";
        public IThingHolder ParentHolder => null;
        public ThingOwner GetDirectlyHeldThings() => inventory;
        public void GetChildHolders(List<IThingHolder> children) => ThingOwnerUtility.AppendThingHoldersFromThings(children, inventory);
    }
    static MissionBayFixture missionBay;
    static bool MissionBayBoundary(ref ISpaceCargoContainer __result) { __result = missionBay; return false; }
    static bool MissionSilverBoundary(ThingDef def, ref Thing __result)
    {
        if (def != ThingDefOf.Silver) return true;
        var thing = Raw<Thing>(); thing.def = def; thing.stackCount = 1; Set(thing, "mapIndexOrState", (sbyte)-1);
        __result = thing; return false;
    }
    static bool MissionDestroyBoundary(Thing __instance)
    {
        __instance.holdingOwner?.Remove(__instance);
        Set(__instance, "mapIndexOrState", (sbyte)-2); return false;
    }
    static bool MissionEndBoundary(Quest __instance) { Set(__instance, "ended", true); return false; }
    static bool MissionDesignationBoundary(IThingHolder holder, ref bool __result)
    { if (!(holder is MissionBayFixture)) return true; __result = false; return false; }
    static void MissionSettlementChecks(Game game, Harmony harmony)
    {
        // Native ThingOwner performs actual split/detach/reinsert. Unity spawn and quest letters are boundaries.
        var service = game.GetComponent<UniverseManager>().Missions;
        var silverBefore = ThingDefOf.Silver;
        ThingDefOf.Silver = Raw<ThingDef>(); ThingDefOf.Silver.defName = "MissionSilverFixture";
        ThingDefOf.Silver.category = ThingCategory.Item; ThingDefOf.Silver.stackLimit = 500;
        var cargoDef = Raw<ThingDef>(); cargoDef.defName = "MissionCargoFixture";
        cargoDef.category = ThingCategory.Item; cargoDef.stackLimit = 1;
        var terminal = Raw<Building_SpaceTradeTerminal>(); Set(terminal, "mapIndexOrState", (sbyte)-1);
        var methods = new[] {
            AccessTools.PropertyGetter(typeof(Building_SpaceTradeTerminal), "MissionCargo"),
            AccessTools.Method(typeof(ThingMaker), nameof(ThingMaker.MakeThing)),
            AccessTools.Method(typeof(Thing), nameof(Thing.Destroy)),
            AccessTools.Method(typeof(Quest), nameof(Quest.End)),
            AccessTools.Method(typeof(Faction), nameof(Faction.TryAffectGoodwillWith)),
            AccessTools.Method(typeof(ThingOwnerUtility), nameof(ThingOwnerUtility.ShouldRemoveDesignationsOnAddedThings)) };
        string[] patches = { nameof(MissionBayBoundary), nameof(MissionSilverBoundary), nameof(MissionDestroyBoundary),
            nameof(MissionEndBoundary), nameof(CaptureGoodwill), nameof(MissionDesignationBoundary) };
        for (int i = 0; i < methods.Length; i++) harmony.Patch(methods[i], prefix: new HarmonyMethod(typeof(Program), patches[i]));
        try
        {
            missionBay = new MissionBayFixture();
            var faction = new Faction { def = new FactionDef() };
            var mission = new SpaceMission { id = 7301, kind = SpaceMissionKind.Freight, faction = faction,
                quest = new Quest { id = 7302, acceptanceTick = 0 }, quantity = 2, rewardEach = 100,
                depositEach = 300, deadline = 500, expires = 900, successGoodwill = 3 };
            for (int i = 0; i < 2; i++)
            {
                var box = Raw<MissionCargo>(); box.def = cargoDef; box.stackCount = 1; box.thingIDNumber = 7310 + i;
                box.missionId = mission.id; box.issuer = faction; Set(box, "mapIndexOrState", (sbyte)-1);
                mission.cargoIds.Add(box.thingIDNumber); missionBay.inventory.TryAdd(box, false);
            }
            var settle = AccessTools.Method(typeof(SpaceMissionService), "Settle");
            goodwillEvents = goodwillDelta = 0;
            var reject = AccessTools.Method(typeof(SpaceMissionService), "Reject");
            harmony.Patch(reject, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMissionMessage)));
            int logs = errors.Count;
            try
            {
                missionBay.limit = 1;
                settle.Invoke(service, new object[] { terminal, mission, 2, 800 });
                Check(!mission.settled && missionBay.Contents.Count == 2 && goodwillEvents == 0,
                    "insufficient output capacity leaves contract and cargo untouched");
                missionBay.limit = 8; missionBay.refuseSilverAt = 2;
                settle.Invoke(service, new object[] { terminal, mission, 2, 800 });
                Check(!mission.settled && missionBay.Contents.Count == 2 && missionBay.Contents.All(t => t is MissionCargo),
                    "refused second payout stack restores delivered boxes and removes partial silver");
                Check(goodwillEvents == 0, "rolled-back delivery has no goodwill settlement");
                Check(errors.Count == logs + 1 && errors.Last().Contains("Delivery rolled back"), "expected rollback is diagnosed once");
                errors.RemoveAt(errors.Count - 1);
                missionBay.refuseSilverAt = 0;
                settle.Invoke(service, new object[] { terminal, mission, 2, 800 });
                Check(mission.settled && mission.paidSilver == 800 && mission.refundedDeposit == 600
                    && mission.deliveredCount == 2 && missionBay.Contents.Sum(t => t.stackCount) == 800,
                    "successful delivery writes exact physical payment and settlement record");
                settle.Invoke(service, new object[] { terminal, mission, 2, 800 });
                Check(missionBay.Contents.Sum(t => t.stackCount) == 800 && goodwillEvents == 1 && goodwillDelta == 3,
                    "repeated settlement cannot pay or reward goodwill twice");
                MissionOpeningChecks(game, harmony, cargoDef);
            }
            finally { harmony.Unpatch(reject, HarmonyPatchType.Prefix, harmony.Id); }
        }
        finally
        {
            foreach (var method in methods) harmony.Unpatch(method, HarmonyPatchType.Prefix, harmony.Id);
            ThingDefOf.Silver = silverBefore; missionBay = null;
        }
    }
    static bool SkipMissionMessage() => false;

    static int missionPlacementBudget, missionPlacedContents;
    static bool MissionPlacementBoundary(Thing thing, Action<Thing, int> placedAction, ref bool __result)
    {
        int amount = Math.Min(thing.stackCount, missionPlacementBudget);
        missionPlacementBudget -= amount; missionPlacedContents += amount;
        placedAction?.Invoke(thing, amount);
        __result = amount == thing.stackCount;
        if (!__result) thing.stackCount -= amount;
        return false;
    }
    static void MissionOpeningChecks(Game game, Harmony harmony, ThingDef cargoDef)
    {
        var place = typeof(GenPlace).GetMethods().Single(m => m.Name == nameof(GenPlace.TryPlaceThing)
            && m.GetParameters()[4].ParameterType == typeof(Action<Thing, int>));
        harmony.Patch(place, prefix: new HarmonyMethod(typeof(Program), nameof(MissionPlacementBoundary)));
        try
        {
            var box = Raw<MissionCargo>(); box.def = cargoDef; box.count = 100; box.contents = ThingDefOf.Silver;
            Set(box, "mapIndexOrState", (sbyte)0); Set(box, "positionInt", new IntVec3(2, 0, 2));
            var pawn = Raw<Pawn>(); Set(pawn, "mapIndexOrState", (sbyte)0); Set(pawn, "positionInt", box.Position);
            missionPlacedContents = 0; missionPlacementBudget = 20;
            Check(!box.Open(pawn) && box.count == 80 && !box.Sealed && missionPlacedContents == 20,
                "partial native placement consumes only the actual placed manifest and breaks the seal");
            missionPlacementBudget = 100;
            Check(box.Open(pawn) && box.Destroyed && missionPlacedContents == 100,
                "retry opening produces only remaining contents and removes the empty crate");
            Check(!box.Open(pawn) && missionPlacedContents == 100, "repeated opening cannot duplicate contents");
        }
        finally { harmony.Unpatch(place, HarmonyPatchType.Prefix, harmony.Id); }
    }

    static ShipBase missionFixtureShip;
    static bool MissionShipBoundary(ref ShipBase __result) { __result = missionFixtureShip; return false; }
    static void MissionCombatChecks(Game game, Harmony harmony)
    {
        var universe = game.GetComponent<UniverseManager>();
        var map = Raw<Map>(); map.info = new MapInfo { Size = new IntVec3(10, 1, 10) };
        game.Maps.Add(map);
        var obj = new MapSpaceObject(5960, map, default); universe.objects.Add(obj);
        var enemy = new Faction { def = new FactionDef() };
        var body = Raw<Building>(); body.def = Raw<ThingDef>(); body.def.category = ThingCategory.Building;
        Set(body, "mapIndexOrState", (sbyte)(game.Maps.Count - 1)); Set(body, "factionInt", enemy);
        var console = new CompDeltaVPilotConsole { parent = body, isMain = true, shipId = 5961 };
        missionFixtureShip = new FoundationShip(new Ship(5961, map, console));
        var m = new SpaceMission { id = 5962, kind = SpaceMissionKind.Combat, enemy = enemy,
            quest = new Quest { id = 5963, acceptanceTick = 0 }, enemyTarget = new MissionTarget { shipId = 5961, objectId = 5960 } };
        universe.missionState.missions.Add(m);
        var resolve = AccessTools.Method(typeof(ShipRegistry), nameof(ShipRegistry.ShipByIdentity));
        harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(Program), nameof(MissionShipBoundary)));
        var queue = (List<Action>)AccessTools.Field(typeof(UniverseManager), "frameActions").GetValue(universe);
        int queueCount = queue?.Count ?? 0;
        void Evaluate()
        {
            universe.Missions.ShipChanged(new DriveSubject(0, 5961));
            queue = (List<Action>)AccessTools.Field(typeof(UniverseManager), "frameActions").GetValue(universe);
            var action = queue[queue.Count - 1]; queue.RemoveAt(queue.Count - 1); action();
        }
        try
        {
            Evaluate(); Check(!m.combatComplete, "an enemy main console remains an objective without power/damage predicates");
            missionFixtureShip = ShipBase.Invalid;
            var archived = new ArchivedSpaceObject { objectId = 5964, globe = new GlobeDisplaySummary() };
            archived.globe.ships.Add(new GlobeShipSummary { key = 5961 }); universe.objects.Add(archived);
            Evaluate(); Check(!m.combatComplete, "archived ship identity prevents false console-loss victory");
            Check(universe.Missions.Protects(archived), "retirement protection follows archived ship identity across a changed container");
            universe.objects.Remove(archived);
            obj.map = null;
            Evaluate(); Check(!m.combatComplete, "temporarily unresolved container cannot establish victory");
            obj.map = map;
            Evaluate(); Check(m.combatComplete, "a loaded target without a main console completes once");
            Check(universe.Missions.Protects(obj), "completed objective remains protected until the contract ends");
            m.combatComplete = false;
            missionFixtureShip = new FoundationShip(new Ship(5961, map, console));
            Set(body, "factionInt", Faction.OfPlayer);
            Evaluate(); Check(m.combatComplete, "captured main console completes the designated ship objective");
            Set(body, "factionInt", enemy);
            Check(m.combatComplete, "later ownership changes cannot revoke recorded completion");
        }
        finally
        {
            harmony.Unpatch(resolve, HarmonyPatchType.Prefix, harmony.Id);
            universe.missionState.missions.Remove(m); universe.objects.Remove(obj); game.Maps.Remove(map);
            if (queue != null && queue.Count > queueCount) queue.RemoveRange(queueCount, queue.Count - queueCount);
            missionFixtureShip = null;
        }
    }
}
