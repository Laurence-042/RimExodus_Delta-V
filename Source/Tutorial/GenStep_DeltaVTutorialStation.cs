using System;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
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
            // GenStep_Space disables regions. Oxygen pumps query their room in PostSpawnSetup,
            // so region queries must already work while the prefab places its facilities.
            map.regionAndRoomUpdater.Enabled = true;
            var things = MapPrefab.Spawn(prefab, map, MapPrefab.CenteredPosition(prefab, map, Rot4.North),
                Faction.OfPlayer, universe.AllocateOwnerId(), Rot4.North, universe);
            if (things.Count != prefab.thingEntries.Count)
                throw new InvalidOperationException("Tutorial prefab was only partially spawned; check the preceding prefab errors.");
            var caskets = things.OfType<Building_CryptosleepCasket>().OrderBy(t => t.Position.x).ToList();
            Thing table = things.Single(t => t.def.defName == "Table2x2c");
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            foreach (var casket in caskets)
            {
                Room room = casket.InteractionCell.GetRoom(map);
                if (room == null || room.ExposedToSpace || room.UsesOutdoorTemperature || room.OpenRoofCount != 0
                    || !casket.InteractionCell.Standable(map))
                    throw new InvalidOperationException("Tutorial cryosleep exit is not inside a sealed, roofed, accessible room.");
            }
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
                station = map, caskets = caskets, suppliesTable = table, mentors = mentors,
                berthDoor = things.OfType<Building_Door>().OrderByDescending(d => d.Position.x).First()
            };
            MapGenerator.PlayerStartSpot = caskets[0].InteractionCell;
            MapGenerator.rootsToUnfog.AddRange(caskets.Select(c => c.InteractionCell));
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
                    if (!GenPlace.TryPlaceThing(stack, run.suppliesTable.Position + IntVec3.South * 3, map, ThingPlaceMode.Near))
                        throw new InvalidOperationException($"No room for tutorial starting possession {stack.def.defName}.");
                }
            }
        }
    }
}
