using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeltaV
{
    public partial class Building_SpaceTradeTerminal : Building, ITrader
    {
        private List<Building_SpaceCargoContainer> supplyCargo = new List<Building_SpaceCargoContainer>();
        private Building_SpaceCargoContainer deliveryCargo;
        private TraderKindDef traderKind;
        private int restockDays = 15;
        private int lastRestockTick = -1;
        private bool stockGenerated;
        private int priceSeed;

        public TraderKindDef TraderKind => traderKind ?? Faction?.def?.baseTraderKinds?.FirstOrDefault()
            ?? DefDatabase<TraderKindDef>.GetNamedSilentFail("Orbital_BulkGoods");
        public IEnumerable<Thing> Goods => ValidContainers(unlocked: false).SelectMany(c => c.Contents.ToArray());
        public int RandomPriceFactorSeed => priceSeed == 0 ? thingIDNumber : priceSeed;
        public string TraderName => LabelCap;
        public bool CanTradeNow => TradeAvailability().Accepted;
        public float TradePriceImprovementOffsetForPlayer => 0f;
        public TradeCurrency TradeCurrency => TraderKind?.tradeCurrency ?? TradeCurrency.Silver;
        internal Map ShipTradeMap => TradingMap();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref supplyCargo, "supplyCargo", LookMode.Reference);
            Scribe_References.Look(ref deliveryCargo, "deliveryCargo");
            Scribe_Defs.Look(ref traderKind, "traderKind");
            Scribe_Values.Look(ref restockDays, "restockDays", 15);
            Scribe_Values.Look(ref lastRestockTick, "lastRestockTick", -1);
            Scribe_Values.Look(ref stockGenerated, "tradeStockGenerated");
            Scribe_Values.Look(ref priceSeed, "tradePriceSeed");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                supplyCargo ??= new List<Building_SpaceCargoContainer>();
                restockDays = Mathf.Clamp(restockDays, 1, 60);
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            Current.Game?.GetComponent<UniverseManager>()?.QueueFrameAction(() =>
            {
                if (!Spawned || Map != map || map.Disposed) return;
                CheckRestock();
            });
        }

        // Validate at the interaction boundary. A severed module keeps its own inventory.
        private IEnumerable<Building_SpaceCargoContainer> ValidContainers(bool unlocked)
        {
            if (!Spawned) yield break;
            var connected = new HashSet<IntVec3>();
            SubstructureOwnership.CollectConnected(Map, Position, connected, null);
            IEnumerable<Building_SpaceCargoContainer> candidates = unlocked
                ? new[] { deliveryCargo } : supplyCargo.Distinct().Where(c => c != deliveryCargo);
            foreach (Building_SpaceCargoContainer cargo in candidates)
                if (cargo?.Spawned == true && cargo.Map == Map && cargo.IsUnlocked == unlocked
                    && connected.Contains(cargo.Position))
                    yield return cargo;
        }

        public override void TickRare()
        {
            base.TickRare();
            CheckRestock();
        }

        private bool TradeWindowOpen => TradeSession.trader == this && Find.WindowStack.IsOpen<Dialog_Trade>();

        private void CheckRestock()
        {
            // Authoring player stations does not generate free goods during normal play.
            if (!Spawned || Faction == null || Faction.IsPlayer || TradeWindowOpen) return;
            if (stockGenerated && Find.TickManager.TicksGame < lastRestockTick + restockDays * GenDate.TicksPerDay) return;
            if (!StockConfigurationReport().Accepted) return;
            if (!stockGenerated && Goods.Any())
            {
                stockGenerated = true;
                lastRestockTick = Find.TickManager.TicksGame;
                return;
            }
            RegenerateStock();
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

        private void RegenerateStock()
        {
            if (!StockConfigurationReport().Accepted || TradeWindowOpen) return;
            List<Building_SpaceCargoContainer> cabinets = ValidContainers(unlocked: false).ToList();
            ThingSetMakerParams parms = default;
            parms.traderDef = TraderKind;
            parms.tile = Map.Tile;
            parms.makingFaction = Faction;
            // Finish native stock generation before replacing the existing physical stock.
            List<Thing> generated = ThingSetMakerDefOf.TraderStock.root.Generate(parms);
            foreach (Building_SpaceCargoContainer cargo in cabinets)
                foreach (Thing thing in cargo.Contents.ToArray()) thing.Destroy();
            foreach (Thing thing in generated)
            {
                // Cargo holds items, not pawns. Reserve sixteen stacks per supply bay for sales.
                if (thing.def.category != ThingCategory.Item
                    || !cabinets.Any(c => c.Contents.Count < c.CurrentSlotLimit - 16 && c.TryStore(thing)))
                    thing.Destroy();
            }
            stockGenerated = true;
            lastRestockTick = Find.TickManager.TicksGame;
            priceSeed = Rand.RangeInclusive(1, 10000000);
            ShipPhysics.InvalidateMass(Map);
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
            if (!pawn.IsColonistPlayerControlled) yield break;
            if (CanConfigure)
                yield return new FloatMenuOption("DeltaV_TradeConfigure".Translate(), OpenConfiguration);
            AcceptanceReport report = NegotiatorReport(pawn);
            string label = "DeltaV_TradeTerminalTrade".Translate();
            if (!report.Accepted)
            {
                yield return new FloatMenuOption(label + ": " + report.Reason, null);
                yield break;
            }
            yield return new FloatMenuOption(label, () =>
                pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(DeltaVDefOf.DeltaV_UseTradeTerminal, this), JobTag.Misc));
        }

        private AcceptanceReport NegotiatorReport(Pawn pawn)
        {
            if (pawn == null || pawn.Map != Map || !pawn.CanReach(this, PathEndMode.Touch, Danger.Deadly))
                return "NoPath".Translate();
            AcceptanceReport report = TradeAvailability();
            if (!report.Accepted) return report;
            report = pawn.CanTradeWith(Faction, TraderKind);
            return report.Accepted ? report : new AcceptanceReport(report.Reason.NullOrEmpty()
                ? "DeltaV_TradeRelationsBlocked".Translate().ToString() : report.Reason);
        }

        internal void OpenTrade(Pawn pawn)
        {
            CheckRestock();
            AcceptanceReport report = NegotiatorReport(pawn);
            if (!report.Accepted)
            {
                Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, false);
                return;
            }
            Find.WindowStack.Add(new Dialog_Trade(pawn, this));
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos()) yield return gizmo;
            if (!CanConfigure) yield break;
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_TradeConfigure".Translate(),
                defaultDesc = "DeltaV_TradeConfigureDesc".Translate(),
                icon = TexCommand.Install,
                action = OpenConfiguration
            };
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
