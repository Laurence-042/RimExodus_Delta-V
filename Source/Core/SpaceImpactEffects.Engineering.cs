using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceImpactEffects
    {
        private static void Weld(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part)
        {
            if (evt.damageScale <= 0f || evt.defender?.map == null) return;
            if (evt.defenderBuilding is Building building && building.Spawned && !building.Destroyed)
            {
                if (building.def.useHitPoints)
                    building.HitPoints = Mathf.Min(building.MaxHitPoints, building.HitPoints + Mathf.Max(0, Mathf.RoundToInt(part.damage * evt.damageScale)));
            }
            else if (evt.defenderIsSubstructure && evt.hitCell.InBounds(evt.defender.map))
                foreach (Filth filth in evt.hitCell.GetThingList(evt.defender.map).OfType<Filth>().ToList())
                    filth.ThinFilth();
        }

        private static void CutStructure(SpaceCollision.ImpactEvent evt)
        {
            Thing launcher = evt.launcher;
            if (evt.damageScale <= 0f || evt.defender?.map == null || launcher?.Spawned != true
                || (evt.weapon ?? launcher.TryGetComp<CompSpaceWeapon>())?.WeaponProps
                    is not CompProperties_MiningBeam cutter) return;
            Map map = evt.defender.map;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null || universe.IsResettingScene || map.Disposed || evt.defender.IsActiveCollider
                || SpaceMapStateManager.IsUniverseHost(map) || !FreeFlightBootstrap.IsZeroGravity(universe, map)
                || universe.SpaceObjectOnMap(map) != evt.defender) return;
            Map outputMap = cutter.deliverToEmitter ? launcher.Map : map;
            if (cutter.deliverToEmitter && (map == outputMap || outputMap.Disposed
                || ShipRegistry.ShipOf(launcher)?.IsValid != true)) return;
            Thing lossReporter = cutter.deliverToEmitter ? launcher : null;
            float damage = cutter.cutDamage * evt.damageScale;
            if (evt.defenderBuilding is Building building && building.Spawned && !building.Destroyed)
            {
                if (building is Mineable rock && building.def.mineable)
                {
                    MineRock(universe, evt, cutter, damage, rock);
                    return;
                }
                if (!ApplyCutDamage(evt, damage, building)) return;
                using var output = new SalvageOutputScope(lossReporter, map, building, cutter.dismantleYieldFactor);
                var returns = output.CollectReturns();
                output.RemoveBuilding(building);
                if (!building.Destroyed) return;
                DeliverCutReturns(universe, evt, returns, lossReporter);
            }
            else
            {
                IntVec3 cell = evt.hitCell;
                if (!evt.defenderIsSubstructure || !cell.IsValid || !cell.InBounds(map)
                    || damage < cutter.foundationBreakThreshold) return;
                TerrainDef foundation = SubstructureOwnership.FoundationAt(map, cell);
                if (foundation == null) return;
                using var output = new SalvageOutputScope(lossReporter, map, foundation, cell, cutter.dismantleYieldFactor);
                var returns = output.CollectReturns();
                ClearCuttingRoofs(map, cell);
                if (!SubstructureDamageUtility.TryDestroyHitFoundation(map, cell,
                    scatterNearbyRoofs: false, instigator: launcher, removeAttachment: output.RemoveBuilding)) return;
                DeliverCutReturns(universe, evt, returns, lossReporter);
            }
            if (cutter.deliverToEmitter) return;
            SpaceObject owner = evt.defender;
            int version = owner.RepresentationVersion;
            universe.QueueFrameAction(() =>
            {
                if (!map.Disposed && universe.FrameOf(map) == owner && owner.RepresentationVersion == version)
                    universe.Scatter.ScatterLoose(map);
            });
        }

        private static void DeliverCutReturns(UniverseManager universe, SpaceCollision.ImpactEvent evt,
            IReadOnlyList<Thing> returns, Thing receiver)
        {
            foreach (Thing item in returns)
                MiningBeamOutputRouter.Deliver(universe, evt, receiver, item, cutReturn: true);
        }

        private static void ApplyEngineeringForce(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part)
        {
            Thing launcher = evt.launcher;
            if (evt.damageScale <= 0f || launcher?.Spawned != true || launcher.Destroyed) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject receiver = universe?.FrameOf(launcher.Map);
            if (receiver == null || receiver == evt.defender || !evt.defender.CanRelocateMap) return;
            ShipBase ship = ShipRegistry.ShipOf(launcher);
            if (ship?.IsValid == true && !universe.ShipTransitions.RequestThrust(universe, PilotTarget.Of(ship),
                    playerInitiated: true)) return;
            CompSpaceWeapon weapon = evt.weapon ?? launcher.TryGetComp<CompSpaceWeapon>();
            if (!universe.ShipTransitions.PrepareExternalForceTarget(evt.defender, evt.hitCell, weapon,
                    evt.defenderIsSubstructure ? null : evt.defenderBuilding as Building)) return;
            if (part.kind == ImpactEffectKind.CoupleMotion)
                receiver.CoupleMotionWith(evt.defender, evt.hitUniverse, part.couplingFraction,
                    part.maxCouplingImpulse * evt.damageScale, part.maxCouplingAngularImpulse * evt.damageScale);
            else
            {
                Vector2 muzzle = SpaceWeaponGeometry.MuzzleMapPoint(weapon);
                ShipPhysics.ApplyTractorImpulse(receiver, SpaceTransform.MapToShipLocal(receiver.map, muzzle),
                    evt.defender, SpaceTransform.UniverseToShipLocal(evt.defender.pose, evt.hitUniverse),
                    Mathf.Max(0f, part.tractorForce) * evt.damageScale / 60f);
            }
        }

        private static bool ApplyCutDamage(SpaceCollision.ImpactEvent evt, float damage, Building building)
        {
            if (!building.def.destroyable || building is Frame
                || (!building.def.building.canBeDamagedByAttacks && !(building is Mineable && building.def.mineable))) return false;
            int cut = Mathf.Max(1, Mathf.RoundToInt(damage));
            PublicFacility.NotifyCut(building, evt.launcher, cut);
            if (building.def.useHitPoints && building.HitPoints > cut)
            {
                building.HitPoints -= cut;
                return false;
            }
            return true;
        }

        private static void MineRock(UniverseManager universe, SpaceCollision.ImpactEvent evt,
            CompProperties_MiningBeam cutter, float damage, Mineable rock)
        {
            Thing launcher = evt.launcher;
            if (!ApplyCutDamage(evt, damage, rock)) return;
            rock.Destroy(DestroyMode.Vanish);
            if (!rock.Destroyed) return;
            BuildingProperties mining = rock.def.building;
            ThingDef mineral = mining.mineableThing;
            if (mineral == null || cutter.miningYieldFactor <= 0f || !Rand.Chance(mining.mineableDropChance)) return;

            // 原版只按采矿效率扣减可损耗矿产，岩块等单件产物只掷掉落概率。
            int count = Mathf.Max(1, mining.EffectiveMineableYield);
            if (mining.mineableYieldWasteable)
                count = Mathf.Max(1, GenMath.RoundRandom(count * cutter.miningYieldFactor));
            using var output = cutter.deliverToEmitter ? new SalvageOutputScope(launcher) : null;
            while (count > 0)
            {
                Thing ore = ThingMaker.MakeThing(mineral);
                ore.stackCount = Mathf.Min(count, mineral.stackLimit);
                count -= ore.stackCount;
                MiningBeamOutputRouter.Deliver(universe, evt,
                    cutter.deliverToEmitter ? launcher : null, ore, cutReturn: false);
            }
        }

        private static void ClearCuttingRoofs(Map map, IntVec3 origin)
        {
            foreach (IntVec3 offset in GenAdj.CardinalDirections.Concat(new[] { IntVec3.Zero }))
            {
                IntVec3 cell = origin + offset;
                if (!cell.InBounds(map) || cell.GetThingList(map).Any(t => t is Building && !t.Destroyed)
                    || MoonPoolUtility.IsFieldRoofAt(map, cell) || map.roofGrid.RoofAt(cell) == null) continue;
                map.roofGrid.SetRoof(cell, null);
                Current.Game?.GetComponent<UniverseManager>()?.Projectiles.NotifyRoofDestroyed(map, cell);
            }
        }
    }
}
