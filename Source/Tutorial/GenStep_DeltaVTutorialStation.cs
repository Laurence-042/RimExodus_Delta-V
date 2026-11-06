using System;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    public sealed class ScenPart_ConfigPage_TutorialColonists : ScenPart_ConfigPage_ConfigureStartingPawns
    {
        public override void PostIdeoChosen()
        {
            // GravshipCrew's default CrewMember kind requires a vacsuit. Use the
            // vanilla civilian wardrobe for every initial candidate and reroll.
            Find.GameInitData.startingPawnKind = PawnKindDefOf.Colonist;
            base.PostIdeoChosen();
        }
    }

    public sealed class GenStep_DeltaVTutorialStation : GenStep
    {
        public override int SeedPart => 238495711;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (Find.GameInitData == null || !Find.Scenario.AllParts.Any(p => p is ScenPart_DeltaVTutorialArrival))
                throw new InvalidOperationException("Tutorial station generator is only valid for the tutorial new-game scenario.");
            var universe = Current.Game.GetComponent<UniverseManager>();
            if (universe.tutorialRun != null) throw new InvalidOperationException("Tutorial station was already initialized.");
            MapPrefabDocument prefab = MapPrefab.LoadRelative("ArrivalStation.xml");
            if (prefab.thingEntries.Count(e => e.def.defName == "Ship_CryptosleepCasket") < Find.GameInitData.startingAndOptionalPawns.Count + universe.Tutorial.UnselectedCrew.Count)
                throw new InvalidOperationException("Tutorial station does not have enough cryptosleep caskets for the selected colonists.");
            if (OrbitalPoiRegistry.EnsureForMap(universe, map)?.map != map)
                throw new InvalidOperationException("Tutorial initial map was not registered as an orbital POI before content generation.");
            var things = MapPrefab.Spawn(prefab, map, MapPrefab.CenteredPosition(prefab, map, Rot4.North),
                Faction.OfPlayer, universe.AllocateOwnerId(), Rot4.North, universe);
            if (things.Count != prefab.thingEntries.Count)
                throw new InvalidOperationException("Tutorial prefab was only partially spawned; check the preceding prefab errors.");
            var caskets = BindArrivalCaskets(things);
            foreach (var cell in things.SelectMany(t => t.OccupiedRect()).Distinct())
                map.areaManager.Home[cell] = true;
            foreach (var cell in map.AllCells)
                if (SubstructureOwnership.HasFoundation(map, cell)) map.areaManager.Home[cell] = true;

            Faction mentors = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(
                DefDatabase<FactionDef>.GetNamed("DeltaV_OrbitalPioneers"), hidden: true));
            // Skip the generator's surface settlement, then expose this real orbital ally in diplomacy.
            mentors.hidden = false;
            Find.FactionManager.Add(mentors);
            mentors.TryAffectGoodwillWith(Faction.OfPlayer, 100 - mentors.GoodwillWith(Faction.OfPlayer), false, false);
            mentors.Name = "DeltaV_Tutorial_Mentors".Translate();
            universe.tutorialRun = new TutorialRunState
            {
                station = map, caskets = caskets, mentors = mentors,
                berthDoor = things.OfType<Building_Door>().OrderByDescending(d => d.Position.x).First()
            };
            MapGenerator.PlayerStartSpot = caskets[0].InteractionCell;
            MapGenerator.rootsToUnfog.AddRange(caskets.Select(c => c.InteractionCell));
        }

        internal static System.Collections.Generic.List<Building_CryptosleepCasket> BindArrivalCaskets(
            System.Collections.Generic.List<Thing> things)
        {
            // The sole oxygen pump identifies the aid compartment from the placed prefab.
            // Only its captain wakes; the other crew stay in their original ship caskets.
            Room cargo = things.Single(t => t.def.defName == "OxygenPump").GetRoom();
            if (cargo == null || cargo.ExposedToSpace || cargo.UsesOutdoorTemperature || cargo.OpenRoofCount != 0)
                throw new InvalidOperationException("Tutorial cargo aid compartment is not sealed and roofed.");
            var caskets = things.OfType<Building_CryptosleepCasket>().ToList();
            var captain = caskets.Single(c => c.InteractionCell.GetRoom(c.Map) == cargo);
            if (caskets.Any(c => !c.InteractionCell.Standable(c.Map)))
                throw new InvalidOperationException("Tutorial cryosleep exit is not accessible.");
            return new[] { captain }.Concat(caskets.Where(c => c != captain)
                .OrderBy(c => c.Position.x).ThenBy(c => c.Position.z)).ToList();
        }
    }

    public sealed class ScenPart_DeltaVTutorialArrival : ScenPart
    {
        public override bool CanCoexistWith(ScenPart other)
            => !(other is ScenPart_PlayerPawnsArriveMethod) && !(other is ScenPart_DeltaVTutorialArrival);

        public override void GenerateIntoMap(Map map)
        {
            if (Find.GameInitData == null) return;
            var run = Current.Game.GetComponent<UniverseManager>().tutorialRun;
            if (run == null || run.station != map) throw new InvalidOperationException("Tutorial arrival requires the initial station generation step.");
            if (run.colonists.Count != 0) throw new InvalidOperationException("Tutorial starting pawns were already placed.");
            var selected = Find.GameInitData.startingAndOptionalPawns;
            if (selected.Count != 1) throw new InvalidOperationException("Tutorial arrival requires one selected captain.");
            var service = Current.Game.GetComponent<UniverseManager>().Tutorial;
            var pawns = selected.Concat(service.UnselectedCrew).ToList();
            if (run.caskets.Count < pawns.Count || run.caskets.Any(c => c.HasAnyContents))
                throw new InvalidOperationException("Tutorial cryosleep caskets are missing or already occupied.");
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (Find.WorldPawns.Contains(pawn)) Find.WorldPawns.RemovePawn(pawn);
                pawn.wasLeftBehindStartingPawn = false;
                pawn.SetFactionDirect(Faction.OfPlayer);
                PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn);
                pawn.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                if (!run.caskets[i].TryAcceptThing(pawn, allowSpecialEffects: false)
                    || !run.caskets[i].GetDirectlyHeldThings().Contains(pawn))
                    throw new InvalidOperationException($"Could not place starting pawn {pawn} into tutorial cryosleep.");
                if (i == 0) run.colonists.Add(pawn);
            }
            service.UnselectedCrew.Clear();
            // Match vanilla arrival enumeration, including scenario animals and personal possessions.
            var things = Find.Scenario.AllParts.SelectMany(p => p.PlayerStartingThings()).ToList();
            foreach (Pawn pawn in selected)
                foreach (ThingDefCount item in Find.GameInitData.startingPossessions[pawn])
                    things.Add(StartingPawnUtility.GenerateStartingPossession(item));
            foreach (Thing thing in things)
            {
                if (thing.def.CanHaveFaction) thing.SetFactionDirect(Faction.OfPlayer);
                int originalCount = thing.stackCount;
                while (originalCount > 0)
                {
                    Thing stack = thing.SplitOff(Math.Min(thing.def.stackLimit, originalCount));
                    originalCount -= stack.stackCount;
                    if (!GenPlace.TryPlaceThing(stack, run.caskets[0].InteractionCell, map, ThingPlaceMode.Near))
                        throw new InvalidOperationException($"No room for tutorial starting possession {stack.def.defName}.");
                }
            }
        }
    }
}
