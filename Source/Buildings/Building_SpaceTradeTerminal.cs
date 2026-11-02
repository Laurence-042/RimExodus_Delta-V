using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeltaV
{
    public class Building_SpaceTradeTerminal : Building, ITrader
    {
        private List<Building_SpaceCargoContainer> linkedCargo = new List<Building_SpaceCargoContainer>();
        private bool stockGenerated;
        private bool linksInitialized;
        private int priceSeed;

        public TraderKindDef TraderKind => Faction?.def?.baseTraderKinds?.FirstOrDefault()
            ?? DefDatabase<TraderKindDef>.GetNamedSilentFail("Orbital_BulkGoods");
        public IEnumerable<Thing> Goods => ValidContainers(unlocked: false).SelectMany(c => c.Contents.ToArray());
        public int RandomPriceFactorSeed => priceSeed == 0 ? thingIDNumber : priceSeed;
        public string TraderName => LabelCap;
        public bool CanTradeNow => Spawned && Faction != null && TraderKind != null && TradingMap() != null
            && ValidContainers(unlocked: false).Any() && ValidContainers(unlocked: true).Any();
        public float TradePriceImprovementOffsetForPlayer => 0f;
        public TradeCurrency TradeCurrency => TraderKind?.tradeCurrency ?? TradeCurrency.Silver;
        internal Map ShipTradeMap => TradingMap();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref linkedCargo, "tradeCargo", LookMode.Reference);
            Scribe_Values.Look(ref stockGenerated, "tradeStockGenerated");
            Scribe_Values.Look(ref linksInitialized, "tradeLinksInitialized");
            Scribe_Values.Look(ref priceSeed, "tradePriceSeed");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                linkedCargo ??= new List<Building_SpaceCargoContainer>();
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            Current.Game?.GetComponent<UniverseManager>()?.QueueFrameAction(() =>
            {
                if (!Spawned || Map != map || map.Disposed || stockGenerated) return;
                AutoLinkIfEmpty();
                GenerateStock();
            });
        }

        // Validate at the interaction boundary. A severed module keeps its own inventory.
        private IEnumerable<Building_SpaceCargoContainer> ValidContainers(bool unlocked)
        {
            if (!Spawned) yield break;
            var connected = new HashSet<IntVec3>();
            SubstructureOwnership.CollectConnected(Map, Position, connected, null);
            foreach (Building_SpaceCargoContainer cargo in linkedCargo.Distinct())
                if (cargo?.Spawned == true && cargo.Map == Map && cargo.IsUnlocked == unlocked
                    && connected.Contains(cargo.Position))
                    yield return cargo;
        }

        private void AutoLinkIfEmpty()
        {
            if (linksInitialized || !Spawned) return;
            linkedCargo.Clear();
            var connected = new HashSet<IntVec3>();
            SubstructureOwnership.CollectConnected(Map, Position, connected, null);
            linkedCargo.AddRange(Map.listerThings.AllThings.OfType<Building_SpaceCargoContainer>()
                .Where(c => connected.Contains(c.Position))
                .OrderBy(c => c.Position.x).ThenBy(c => c.Position.z));
            // A station provides one already-unlocked receiving bay. All other
            // bays remain locked stock until a pawn hacks them.
            if (Faction != Faction.OfPlayer)
            {
                if (linkedCargo.Count >= 2 && linkedCargo.All(c => !c.IsUnlocked))
                    linkedCargo[0].GetComp<CompHackable>()?.HackNow();
                foreach (Building_SpaceCargoContainer cargo in linkedCargo.Where(c => c.IsUnlocked))
                    if (cargo.Faction != Faction.OfPlayer) cargo.SetFaction(Faction.OfPlayer);
            }
            linksInitialized = linkedCargo.Any(c => c.IsUnlocked)
                && linkedCargo.Any(c => !c.IsUnlocked);
        }

        private Map TradingMap()
        {
            if (!Spawned) return null;
            var connected = new HashSet<IntVec3>();
            SubstructureOwnership.CollectConnected(Map, Position, connected, null);
            foreach (Building_DockingPort port in Map.listerThings.AllThings.OfType<Building_DockingPort>())
            {
                Building_DockingPort peer = port.Docking?.Peer;
                if (connected.Contains(port.Position) && port.Docking?.Connected == true
                    && peer?.Spawned == true && peer.Faction == Faction.OfPlayer && peer.Map != Map)
                    return peer.Map;
            }
            return null;
        }

        private void GenerateStock(bool force = false)
        {
            if ((!force && (stockGenerated || Faction == null || Faction == Faction.OfPlayer))
                || TraderKind == null) return;
            var connected = new HashSet<IntVec3>();
            SubstructureOwnership.CollectConnected(Map, Position, connected, null);
            List<Building_SpaceCargoContainer> cabinets = ValidContainers(unlocked: false).ToList();
            if (cabinets.Count == 0) return;
            // Authored prefab stock is already physical stock; never add random goods on top.
            bool authoredStock = cabinets.Any(c => c.Contents.Count > 0);
            ThingSetMakerParams parms = default;
            parms.traderDef = TraderKind;
            parms.tile = Map.Tile;
            parms.makingFaction = Faction;
            if (force || !authoredStock)
            {
                List<Thing> generated = ThingSetMakerDefOf.TraderStock.root.Generate(parms);
                foreach (Thing thing in generated)
                {
                    // Keep sixteen slots per bay free for goods sold to the station.
                    if (thing.def.category != ThingCategory.Item
                        || !cabinets.Any(c => c.Contents.Count < c.CurrentSlotLimit - 16 && c.TryStore(thing)))
                        thing.Destroy();
                }
            }
            int seed = Rand.RangeInclusive(1, 10000000);
            foreach (Building_SpaceTradeTerminal terminal in Map.listerThings.AllThings.OfType<Building_SpaceTradeTerminal>())
                if (connected.Contains(terminal.Position))
                {
                    terminal.stockGenerated = true;
                    terminal.priceSeed = seed;
                }
        }

        public IEnumerable<Thing> ColonyThingsWillingToBuy(Pawn negotiator)
        {
            Map shipMap = TradingMap();
            if (shipMap == null) yield break;
            foreach (Thing thing in shipMap.listerThings.AllThings)
                if (thing.def.category == ThingCategory.Item && !thing.Destroyed)
                    yield return thing;
        }

        internal static bool IsStillOnShip(Thing thing, Map ship)
        {
            if (thing == null || thing.Destroyed) return false;
            return thing.Spawned && thing.Map == ship;
        }

        internal bool DealStillValid(TradeDeal deal, out bool noRoom)
        {
            noRoom = false;
            if (!CanTradeNow || TradeSession.playerNegotiator?.Map != Map) return false;
            var goods = new HashSet<Thing>(Goods);
            Map ship = TradingMap();
            foreach (Tradeable row in deal.AllTradeables)
            {
                if (row.ActionToDo == TradeAction.PlayerBuys
                    && row.thingsTrader.Where(goods.Contains).Sum(t => t.stackCount)
                        < row.CountToTransferToSource) return false;
                if (row.ActionToDo == TradeAction.PlayerSells
                    && row.thingsColony.Where(t => IsStillOnShip(t, ship))
                        .Sum(t => t.stackCount) < row.CountToTransferToDestination) return false;
            }
            int boughtStacks = deal.AllTradeables.Sum(row => row.ActionToDo == TradeAction.PlayerBuys
                ? CountTransferredStacks(row.thingsTrader, row.CountToTransferToSource) : 0);
            int soldStacks = deal.AllTradeables.Sum(row => row.ActionToDo == TradeAction.PlayerSells
                ? CountTransferredStacks(row.thingsColony, row.CountToTransferToDestination) : 0);
            if (boughtStacks > ValidContainers(unlocked: true).Sum(c => c.CurrentSlotLimit - c.Contents.Count)
                || soldStacks > ValidContainers(unlocked: false).Sum(c => c.CurrentSlotLimit - c.Contents.Count))
            {
                noRoom = true;
                return false;
            }
            return true;
        }

        private static int CountTransferredStacks(IEnumerable<Thing> things, int count)
        {
            int stacks = 0;
            foreach (Thing thing in things)
            {
                if (count <= 0) break;
                count -= thing.stackCount;
                stacks++;
            }
            return stacks;
        }

        public void GiveSoldThingToTrader(Thing toGive, int countToGive, Pawn negotiator)
        {
            Thing transferred = toGive.SplitOff(countToGive);
            transferred.PreTraded(TradeAction.PlayerSells, negotiator, this);
            if (!ValidContainers(unlocked: false).Any(c => c.TryStore(transferred)))
            {
                // A late capacity change must not silently destroy a completed sale.
                GenPlace.TryPlaceThing(transferred, Position, Map, ThingPlaceMode.Near);
                Log.Error("[DeltaV] Station trader had no room for sold item " + transferred);
            }
        }

        public void GiveSoldThingToPlayer(Thing toGive, int countToGive, Pawn negotiator)
        {
            Building_SpaceCargoContainer source = ValidContainers(unlocked: false).FirstOrDefault(c => c.Contents.Contains(toGive));
            if (source == null) return;
            Thing transferred = source.Take(toGive, countToGive);
            if (transferred == null) return;
            transferred.PreTraded(TradeAction.PlayerBuys, negotiator, this);
            if (!ValidContainers(unlocked: true).Any(c => c.TryStore(transferred)))
            {
                GenPlace.TryPlaceThing(transferred, Position, Map, ThingPlaceMode.Near);
                Log.Error("[DeltaV] Station trader had no room to deliver bought item " + transferred);
            }
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn pawn)
        {
            foreach (FloatMenuOption option in base.GetFloatMenuOptions(pawn)) yield return option;
            if (!pawn.IsColonistPlayerControlled || !pawn.CanReach(this, PathEndMode.Touch, Danger.Deadly))
                yield break;
            AutoLinkIfEmpty();
            if (CanTradeNow && pawn.CanTradeWith(Faction, TraderKind).Accepted)
                yield return new FloatMenuOption("DeltaV_TradeTerminalTrade".Translate(), () =>
                    pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(DeltaVDefOf.DeltaV_UseTradeTerminal, this), JobTag.Misc));
        }

        internal void OpenTrade(Pawn pawn)
        {
            AutoLinkIfEmpty();
            if (!CanTradeNow || !pawn.CanTradeWith(Faction, TraderKind).Accepted) return;
            GenerateStock();
            Find.WindowStack.Add(new Dialog_Trade(pawn, this));
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos()) yield return gizmo;
            if (!Spawned) yield break;
            if (DebugSettings.godMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DeltaV_DevTradeAddStock".Translate(),
                    action = () => { AutoLinkIfEmpty(); GenerateStock(force: true); }
                };
                yield return new Command_Action
                {
                    defaultLabel = "DeltaV_DevTradeRelink".Translate(),
                    action = () => { linksInitialized = false; AutoLinkIfEmpty(); }
                };
                yield return new Command_Action
                {
                    defaultLabel = "DeltaV_DevTradeInspect".Translate(),
                    action = InspectDebugState
                };
            }
            if (Faction?.IsPlayer != true && !DebugSettings.godMode) yield break;
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_TradeTerminalLink".Translate(),
                defaultDesc = "DeltaV_TradeTerminalLinkDesc".Translate(),
                icon = TexCommand.Install,
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    foreach (Building_SpaceCargoContainer cargo in Map.listerThings.AllThings.OfType<Building_SpaceCargoContainer>())
                    {
                        Building_SpaceCargoContainer selected = cargo;
                        string label = (linkedCargo.Contains(cargo) ? "✓ " : "") + cargo.LabelCap + " (" + cargo.Position + ")";
                        options.Add(new FloatMenuOption(label, () =>
                        {
                            linksInitialized = true;
                            if (!linkedCargo.Remove(selected)) linkedCargo.Add(selected);
                        }));
                    }
                    if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
                }
            };
        }

        private void InspectDebugState()
        {
            var validStorage = new HashSet<Building_SpaceCargoContainer>(ValidContainers(unlocked: false));
            var validDelivery = new HashSet<Building_SpaceCargoContainer>(ValidContainers(unlocked: true));
            Map ship = TradingMap();
            var report = new StringBuilder("DeltaV_DevTradeReport".Translate(
                stockGenerated ? "DeltaV_DevYes".Translate() : "DeltaV_DevNo".Translate(),
                linksInitialized ? "DeltaV_DevYes".Translate() : "DeltaV_DevNo".Translate(),
                TraderKind?.defName ?? "—", priceSeed,
                ship == null ? "—" : UniverseManager.MapTag(ship),
                CanTradeNow ? "DeltaV_DevYes".Translate() : "DeltaV_DevNo".Translate()));
            foreach (Building_SpaceCargoContainer cargo in linkedCargo.Distinct())
                report.Append('\n').Append("DeltaV_DevTradeCargoLine".Translate(
                    cargo?.Position.ToString() ?? "—",
                    cargo?.IsUnlocked == true ? "DeltaV_DevTradeUnlocked".Translate() : "DeltaV_DevTradeLocked".Translate(),
                    validStorage.Contains(cargo) || validDelivery.Contains(cargo)
                        ? "DeltaV_DevYes".Translate() : "DeltaV_DevNo".Translate(),
                    cargo?.Contents.Count ?? 0));
            Find.WindowStack.Add(new Dialog_MessageBox(report.ToString()));
        }
    }

    public class JobDriver_UseSpaceTradeTerminal : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Do(() =>
            {
                if (TargetThingA is Building_SpaceTradeTerminal terminal && terminal.Spawned)
                    terminal.OpenTrade(pawn);
            });
        }
    }
}
