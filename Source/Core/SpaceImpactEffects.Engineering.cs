using RimWorld;
using UnityEngine;
using Verse;
using System.Linq;

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

        private static void CutStructure(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part)
        {
            if (evt.damageScale <= 0f || evt.defender?.map == null) return;
            Map map = evt.defender.map;
            if (evt.defenderBuilding is Building building && building.Spawned && !building.Destroyed)
            {
                if (building is Mineable rock && building.def.mineable)
                {
                    MineRock(evt, part, rock);
                    return;
                }
                if (!TryFinishCut(evt, part, building, DestroyMode.Deconstruct)) return;
            }
            else if (evt.defenderIsSubstructure && SubstructureOwnership.HasFoundation(map, evt.hitCell)
                && part.damage * evt.damageScale >= part.substructureBreakThreshold)
            {
                TerrainDef foundation = SubstructureOwnership.FoundationAt(map, evt.hitCell);
                ClearCuttingRoofs(map, evt.hitCell);
                if (foundation != null && SubstructureDamageUtility.TryDestroyHitFoundation(map, evt.hitCell, scatterNearbyRoofs: false, instigator: evt.launcher))
                    GenLeaving.DoLeavingsFor(foundation, evt.hitCell, map);
            }
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject owner = evt.defender;
            int version = owner.RepresentationVersion;
            universe?.QueueFrameAction(() =>
            {
                if (!map.Disposed && universe.FrameOf(map) == owner && owner.RepresentationVersion == version)
                    universe.Scatter.ScatterLoose(map);
            });
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

        private static void Salvage(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part)
        {
            Thing launcher = evt.launcher;
            if (launcher?.Spawned != true || launcher.Destroyed || evt.defender.IsActiveCollider
                || evt.damageScale <= 0f) return;
            Map targetMap = evt.defender.map;
            Map receiverMap = launcher.Map;
            if (receiverMap == targetMap || receiverMap.Disposed) return;
            ShipBase receiver = ShipRegistry.ShipOf(launcher);
            if (receiver?.IsValid != true) return;
            // 与深钻井同走原版 Near 放置：合堆、找位与失败由原版处理，不用空格数阻止切割。
            IntVec3 output = launcher.Position;

            if (evt.defenderBuilding is Building building && building.Spawned && !building.Destroyed)
            {
                if (building is Mineable rock && building.def.mineable)
                {
                    MineRock(evt, part, rock);
                    return;
                }
                if (!TryFinishCut(evt, part, building, DestroyMode.Vanish)) return;
                using (new SalvageOutputScope(launcher))
                    GenLeaving.DoLeavingsFor(building, receiverMap, DestroyMode.Deconstruct,
                        CellRect.SingleCell(output));
                return;
            }

            IntVec3 cell = evt.hitCell;
            if (!evt.defenderIsSubstructure || !cell.IsValid || !cell.InBounds(targetMap)
                || !SubstructureOwnership.HasFoundation(targetMap, cell)) return;
            TerrainDef foundation = SubstructureOwnership.FoundationAt(targetMap, cell);
            if (foundation == null) return;
            // 基架没有耐久；达到共同毁伤阈值后调用统一毁伤机制，再结算材料。
            if (part.damage * evt.damageScale < part.substructureBreakThreshold) return;
            ClearCuttingRoofs(targetMap, cell);
            if (!SubstructureDamageUtility.TryDestroyHitFoundation(targetMap, cell, scatterNearbyRoofs: false, instigator: evt.launcher)) return;
            using (new SalvageOutputScope(launcher))
                GenLeaving.DoLeavingsFor(foundation, output, receiverMap);
        }

        private static bool TryFinishCut(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part,
            Building building, DestroyMode mode)
        {
            if (!building.def.destroyable || building is Frame
                || (!building.def.building.canBeDamagedByAttacks && !(building is Mineable && building.def.mineable))) return false;
            int cut = Mathf.Max(1, Mathf.RoundToInt(part.damage * evt.damageScale));
            PublicFacility.NotifyCut(building, evt.launcher, cut);
            if (building.def.useHitPoints && building.HitPoints > cut)
            {
                building.HitPoints -= cut;
                return false;
            }
            building.Destroy(mode);
            return building.Destroyed;
        }

        private static void MineRock(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part, Mineable rock)
        {
            Thing launcher = evt.launcher;
            if (launcher?.Spawned != true || evt.defender.IsActiveCollider
                || !TryFinishCut(evt, part, rock, DestroyMode.Vanish)) return;
            BuildingProperties mining = rock.def.building;
            ThingDef mineral = mining.mineableThing;
            if (mineral == null || part.miningYieldFactor <= 0f || !Rand.Chance(mining.mineableDropChance)) return;

            // 原版只按采矿效率扣减可损耗矿产，岩块等单件产物只掷掉落概率。
            int count = Mathf.Max(1, mining.EffectiveMineableYield);
            if (mining.mineableYieldWasteable)
                count = Mathf.Max(1, GenMath.RoundRandom(count * part.miningYieldFactor));
            using var output = part.kind == ImpactEffectKind.Salvage ? new SalvageOutputScope(launcher) : null;
            while (count > 0)
            {
                Thing ore = ThingMaker.MakeThing(mineral);
                ore.stackCount = Mathf.Min(count, mineral.stackLimit);
                count -= ore.stackCount;
                if (part.kind == ImpactEffectKind.Salvage)
                    GenPlace.TryPlaceThing(ore, launcher.Position, launcher.Map, ThingPlaceMode.Near);
                else
                    Current.Game.GetComponent<UniverseManager>().Scatter.QueueMiningEjection(
                        ore, evt.defender, evt.hitCell, evt.hitDefenderLocal, -evt.incidentDir);
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
