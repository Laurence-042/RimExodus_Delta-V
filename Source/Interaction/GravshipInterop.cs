using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 对原版 Gravship 私有构造/字段与放置收尾的唯一镜像。这里只翻译原版
    /// 数据形状，不持有模式流程；选点、捕获时机与生命周期仍由模式服务编排。
    ///
    /// 无引擎搬运（解耦架构 §3.1/§15.2）：战术对接完成、脱离与断连拆分不要求逆重引擎，
    /// <see cref="CaptureDomain"/> 按**任意 cells 集合**镜像原版 <c>Gravship(engine)</c>
    /// 构造器的捕获段（TransferZones/CopyCellContents/CopyAreas/CopyStorageGroups/
    /// UpdateBoundingBoxes），<see cref="StripDomain"/> 镜像原版 GenerateGravship 的移除段；
    /// 二者与 <see cref="PlaceIntoMap"/>（原版 PlaceGravshipInMap 全量收尾）构成完整
    /// 搬运链——不另造一套 Pawn/区域/建筑 comp 搬运算法。与 engine 的差异点：
    /// 捕获域参数化（不再取 engine.ValidSubstructure）、包含判定用 cells.Contains
    /// （替代 engine.OnValidSubstructure）、设施枚举扫域内 CompGravshipFacility
    /// （替代 engine.GravshipComponents）、goodwill 用船派系（替代 engine.Faction）。
    /// </summary>
    internal static class GravshipInterop
    {
        private static readonly ConstructorInfo EmptyCtor =
            AccessTools.Constructor(typeof(Gravship), Type.EmptyTypes);

        private static readonly FieldInfo EngineField = AccessTools.Field(typeof(Gravship), "engine");
        private static readonly FieldInfo ThingsField = AccessTools.Field(typeof(Gravship), "things");
        private static readonly FieldInfo PawnsField = AccessTools.Field(typeof(Gravship), "pawns");
        private static readonly FieldInfo PowerOnField = AccessTools.Field(typeof(Gravship), "powerOn");
        private static readonly FieldInfo ConnectParentsField = AccessTools.Field(typeof(Gravship), "connectParents");
        private static readonly FieldInfo RoofsField = AccessTools.Field(typeof(Gravship), "roofs");
        private static readonly FieldInfo FoundationsField = AccessTools.Field(typeof(Gravship), "foundations");
        private static readonly FieldInfo TerrainsField = AccessTools.Field(typeof(Gravship), "terrains");
        private static readonly FieldInfo TerrainColorsField = AccessTools.Field(typeof(Gravship), "terrainColors");
        private static readonly FieldInfo TerrainDesignationsField = AccessTools.Field(typeof(Gravship), "terrainDesignations");
        private static readonly FieldInfo GasesField = AccessTools.Field(typeof(Gravship), "gases");
        private static readonly FieldInfo OccupiedRectsField = AccessTools.Field(typeof(Gravship), "occupiedRects");
        private static readonly FieldInfo RoomTemperaturesField = AccessTools.Field(typeof(Gravship), "roomTemperatures");
        private static readonly FieldInfo ExtenderPositionsField = AccessTools.Field(typeof(Gravship), "gravFieldExtenderPositions");
        private static readonly FieldInfo StoryStateField = AccessTools.Field(typeof(Gravship), "storyState");
        private static readonly FieldInfo ThrustersField = AccessTools.Field(typeof(Gravship), "thrusters");
        private static readonly FieldInfo ExteriorDoorsField = AccessTools.Field(typeof(Gravship), "exteriorDoors");
        private static readonly FieldInfo BoundsField = AccessTools.Field(typeof(Gravship), "bounds");
        private static readonly FieldInfo ConsoleField = AccessTools.Field(typeof(Gravship), "pilotConsole");

        /// <summary>只接入已捕获的组件驾驶台；显式主台与备用台不可互相覆盖。</summary>
        internal static void BindPilotConsole(Gravship gravship, Building console)
        {
            if (console?.TryGetComp<CompDeltaVPilotConsole>() == null
                || gravship?.ContainsThing(console) != true) return;
            ConsoleField.SetValue(gravship, console);
        }

        internal static void RefreshPilotConsole(Gravship gravship)
        {
            Building console = CompDeltaVPilotConsole.IdentityConsoleOf(gravship)?.parent as Building;
            if (console == null) ConsoleField.SetValue(gravship, null);
            else BindPilotConsole(gravship, console);
        }
        private static readonly FieldInfo ControllerTimeLeft =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "timeLeft");

        /// <summary>
        /// 原版 Designator_MoveGravship.GetSizeRotAdjustedCell 与
        /// ValidGravshipLocation 的 GetRoot 组合（references/RimWorldDecompiled/RimWorld/
        /// Designator_MoveGravship.cs）。输入是鼠标/CanDesignateCell 的格，不是放置 root。
        /// 偶数尺寸时二者可能相差一格，不能把 designator 输入当轨道坐标锚。
        /// </summary>
        internal static IntVec3 DesignatorInputToRoot(IntVec3 input, IntVec2 size, Rot4 rot)
        {
            IntVec3 halfX = new IntVec3(size.x / 2, 0, 0);
            IntVec3 halfZ = new IntVec3(0, 0, size.z / 2);
            IntVec3 adjusted = input;
            if (rot == Rot4.North) adjusted += halfX + halfZ;
            else if (rot == Rot4.East) adjusted += halfX - halfZ;
            else if (rot == Rot4.South) adjusted -= halfX + halfZ;
            else if (rot == Rot4.West) adjusted += -halfX + halfZ;
            return PrefabUtility.GetRoot(adjusted, size, rot);
        }

        internal static IntVec3 RootToDesignatorInput(IntVec3 root, IntVec2 size, Rot4 rot)
        {
            // GetRoot 与调整均为整数平移；测一次原点即可取得精确逆平移。
            return root - DesignatorInputToRoot(IntVec3.Zero, size, rot);
        }

        internal static IntVec3 CenterToDesignatorInput(IntVec3 center, IntVec2 size, Rot4 rot)
        {
            return RootToDesignatorInput(PrefabUtility.GetRoot(center, size, rot), size, rot);
        }

        internal static AcceptanceReport CanDesignateRoot(Designator_MoveGravship designator,
            IntVec3 root, IntVec2 size, Rot4 rot)
        {
            return designator.CanDesignateCell(RootToDesignatorInput(root, size, rot));
        }

        /// <summary>
        /// 选点/合法性复核共用的零消费预览：脚印取船的控制域（捕获同口径，不要求引擎），
        /// 字段只读填充。选点期与完成期复核各建一次（复核要读**当前**脚印——对接期间的
        /// 扩建会改变它）。mainConsole 可为 null；previewBody 用于 shuttle，把真实舱体
        /// 作为只读 PositionData 放进同一个原版 ThingPlacements 旋转链，禁止调用方另算朝向。
        /// </summary>
        internal static Gravship BuildPreview(PilotTarget target)
        {
            if (!target.IsValid) return null;
            ShipBase ship = target.Vessel;
            ShipTransferSpec transfer = ship.Transfer;
            var topology = Current.Game.GetComponent<UniverseManager>().FoundationTopology;
            var cells = transfer.body == null ? topology.DomainAt(target.Map, ship.Anchor.Position)
                : new HashSet<IntVec3>(ship.HullCells);
            if (cells == null) return null;
            var buildings = transfer.body == null ? topology.BuildingsIn(target.Map, cells)
                : new HashSet<Building> { transfer.body };
            return BuildPreview(cells, transfer.console, target.Map, transfer.body, ship.MoveColocatedItems, buildings);
        }

        internal static Gravship BuildPreview(HashSet<IntVec3> cells, Building mainConsole,
            Map sourceMap = null, Building previewBody = null, bool includeItems = true,
            HashSet<Building> buildings = null)
        {
            if (EmptyCtor == null || cells == null || cells.Count == 0) return null;
            Map map = sourceMap ?? mainConsole?.Map;
            if (mainConsole != null && (!mainConsole.Spawned || mainConsole.Map == null)) return null;
            if (map == null) return null;

            var hollow = (Gravship)EmptyCtor.Invoke(null);
            Gravship gravship = WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Gravship, hollow) as Gravship;
            if (gravship == null) return null;

            CellRect domainBounds = CellRect.FromCellList(cells.ToList());
            IntVec3 origin = new IntVec3(domainBounds.minX, 0, domainBounds.minZ);
            gravship.originalPosition = origin;
            var foundations = (Dictionary<IntVec3, TerrainDef>)FoundationsField?.GetValue(gravship);
            var localCells = new List<IntVec3>(cells.Count);
            foreach (IntVec3 cell in cells)
            {
                IntVec3 local = cell - origin;
                localCells.Add(local);
                // 预览只消费脚印形状；无基架层（shuttle 口袋图）时以原版基架 def 占位，
                // 避免 null 地形 def 流入原版 Foundations 视图（marker 缓存/旋转/放置链）。
                foundations[local] = map.terrainGrid.FoundationAt(cell) ?? DeltaVDefOf.Substructure;
            }
            BoundsField?.SetValue(gravship, CellRect.FromCellList(localCells));

            if (mainConsole != null && (buildings?.Contains(mainConsole) ?? cells.Contains(mainConsole.Position)))
            {
                var things = (Dictionary<Thing, PositionData>)ThingsField?.GetValue(gravship);
                things[mainConsole] = new PositionData(mainConsole.Position - origin, mainConsole.Rotation);
                BindPilotConsole(gravship, mainConsole);
            }
            if (previewBody != null && previewBody != mainConsole && previewBody.Spawned
                && previewBody.Map == map && cells.Contains(previewBody.Position))
            {
                var things = (Dictionary<Thing, PositionData>)ThingsField?.GetValue(gravship);
                things[previewBody] = new PositionData(previewBody.Position - origin, previewBody.Rotation);
            }

            var thrusters = (Dictionary<Thing, PositionData>)ThrustersField?.GetValue(gravship);
            var previewThings = (Dictionary<Thing, PositionData>)ThingsField.GetValue(gravship);
            foreach (Thing thing in ShipStructureIndex.Read(map).Things)
            {
                bool selected = thing is Building body && buildings != null
                    ? buildings.Contains(body) : cells.Contains(thing.Position);
                if (!thing.Spawned || !selected) continue;
                if (ShouldBringOnGravship(thing, thing.Position, includeItems))
                    previewThings[thing] = new PositionData(thing.Position - origin, thing.Rotation);
                // 对接口与舱体共用原版 PositionData 旋转链，选点显示不得另算门的位置和朝向。
                if (thing is Building_DockingPort port)
                    previewThings[port] = new PositionData(port.Position - origin, port.Rotation);
                if (thing is Building building
                    && building.TryGetComp<CompGravshipThruster>() is CompGravshipThruster thruster
                    && thruster.CanBeActive)
                {
                    thrusters[building] = new PositionData(building.Position - origin, building.Rotation);
                }
            }
            return gravship;
        }

        /// <summary>读取原版 Gravship.ThingPlacements 得到旋转后的最终 Thing 朝向。</summary>
        internal static bool TryPlacedRotation(Gravship gravship, Thing thing, out Rot4 rotation)
        {
            if (gravship != null && thing != null
                && gravship.ThingPlacements.TryGetValue(thing, out PositionData.Data placement))
            {
                rotation = placement.rotation;
                return true;
            }
            rotation = Rot4.Invalid;
            return false;
        }

        // ---- 无引擎搬运镜像（本 mod 对 Gravship(engine) 构造器与 GenerateGravship
        // 移除段的唯一显式镜像；来源 references/RimWorldDecompiled/RimWorld.Planet/
        // Gravship.cs 与 RimWorld/GravshipUtility.cs）----

        /// <summary>
        /// 按 cells 集合捕获一个可搬运的 Gravship（镜像 <c>Gravship(engine)</c> 构造器；
        /// origin = 域包围盒 min，局部坐标非负）。mainConsole 决定 PilotConsole；
        /// ownerFaction 用于镜像 CheckAffectedGoodwill（无主台残片传 null 跳过）。
        /// 捕获会消费区域、指令、储存组和携带物；所有可失败的前置检查必须先完成。
        /// </summary>
        internal static Gravship CaptureDomain(Map map, HashSet<IntVec3> cells, Building mainConsole,
            Faction ownerFaction, bool includeTerrain = true, bool includeItems = true,
            HashSet<Building> buildings = null)
        {
            if (EmptyCtor == null || map == null || cells == null || cells.Count == 0) return null;
            var hollow = (Gravship)EmptyCtor.Invoke(null);
            Gravship gravship = WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Gravship, hollow) as Gravship;
            if (gravship == null) return null;

            CellRect captureBounds = CellRect.FromCellList(cells.ToList());
            IntVec3 origin = new IntVec3(captureBounds.minX, 0, captureBounds.minZ);
            gravship.originalPosition = origin;
            var storyState = new StoryState(gravship);
            map.storyState.CopyTo(storyState);
            StoryStateField?.SetValue(gravship, storyState);
            gravship.GetType().GetField(nameof(Gravship.autoSlaughterConfigs),
                BindingFlags.Instance | BindingFlags.Public)?.SetValue(gravship, map.autoSlaughterManager.configs);

            var things = (Dictionary<Thing, PositionData>)ThingsField.GetValue(gravship);
            var pawns = (Dictionary<Pawn, PositionData>)PawnsField.GetValue(gravship);
            var powerOn = (Dictionary<Thing, bool>)PowerOnField.GetValue(gravship);
            var connectParents = (Dictionary<Thing, Thing>)ConnectParentsField.GetValue(gravship);
            var thrusters = (Dictionary<Thing, PositionData>)ThrustersField.GetValue(gravship);
            var exteriorDoors = (Dictionary<Thing, PositionData>)ExteriorDoorsField.GetValue(gravship);
            var foundations = (Dictionary<IntVec3, TerrainDef>)FoundationsField.GetValue(gravship);
            var terrains = (Dictionary<IntVec3, TerrainDef>)TerrainsField.GetValue(gravship);
            var terrainColors = (Dictionary<IntVec3, ColorDef>)TerrainColorsField.GetValue(gravship);
            var terrainDesignations = (Dictionary<IntVec3, Designation>)TerrainDesignationsField.GetValue(gravship);
            var roofs = (Dictionary<IntVec3, RoofDef>)RoofsField.GetValue(gravship);
            var gases = (Dictionary<IntVec3, uint>)GasesField.GetValue(gravship);
            var roomTemperatures = (List<RoomTemperatureVacuum>)RoomTemperaturesField.GetValue(gravship);
            var extenderPositions = (List<IntVec3>)ExtenderPositionsField.GetValue(gravship);

            // 设施先行（镜像 ctor 经 engine.GravshipComponents 的加法）：域内全部
            // CompGravshipFacility 建筑。
            IEnumerable<Thing> facilities = buildings != null ? buildings : ShipStructureIndex.Read(map).Things.ToList();
            foreach (Thing thing in facilities)
            {
                if (thing is Building facilityBuilding && facilityBuilding.Spawned
                    && (buildings != null || cells.Contains(facilityBuilding.Position))
                    && facilityBuilding.TryGetComp<CompGravshipFacility>() != null)
                {
                    AddThing(gravship, cells, facilityBuilding, facilityBuilding.Position - origin,
                        things, pawns, powerOn, connectParents, thrusters, exteriorDoors, extenderPositions,
                        explicitMember: buildings != null);
                }
            }

            if (buildings != null)
                foreach (Building building in buildings)
                    AddThing(gravship, cells, building, building.Position - origin, things, pawns,
                        powerOn, connectParents, thrusters, exteriorDoors, extenderPositions, explicitMember: true);

            var occupied = new HashSet<IntVec3>();
            var seenRooms = new HashSet<Room>();
            foreach (IntVec3 cell in cells)
            {
                IntVec3 local = cell - origin;
                List<Thing> list = map.thingGrid.ThingsListAt(cell);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Thing thing = list[i];
                    if (buildings != null && thing is Building) continue;
                    if (ShouldBringOnGravship(thing, cell, includeItems)
                        && (!(thing is Building building) || !building.def.building.isAttachment))
                    {
                        foreach (Thing attached in GenConstruct.GetAttachedBuildings(thing))
                        {
                            if (buildings != null && !buildings.Contains(attached)) continue;
                            AddThing(gravship, cells, attached, local + attached.Rotation.Opposite.FacingCell,
                                things, pawns, powerOn, connectParents, thrusters, exteriorDoors, extenderPositions);
                        }
                        AddThing(gravship, cells, thing, local,
                            things, pawns, powerOn, connectParents, thrusters, exteriorDoors, extenderPositions);
                    }
                }
                occupied.Add(local);
                if (includeTerrain)
                {
                    TerrainDef foundation = map.terrainGrid.FoundationAt(cell);
                    // Vanilla SpawnTerrain dereferences each value; blank cells have no foundation entry.
                    if (foundation != null) foundations[local] = foundation;
                    terrains[local] = map.terrainGrid.TerrainAt(cell);
                    terrainColors[local] = map.terrainGrid.ColorAt(cell);
                    roofs[local] = map.roofGrid.RoofAt(cell);
                    foreach (Designation designation in map.designationManager.AllDesignationsAt(cell))
                    {
                        if (designation.def == DesignationDefOf.RemoveFloor
                            || designation.def == DesignationDefOf.PaintFloor
                            || designation.def == DesignationDefOf.RemovePaintFloor)
                        {
                            terrainDesignations[local] = designation;
                            map.designationManager.RemoveDesignation(designation);
                        }
                    }
                    gases[local] = map.gasGrid.GetDirect(cell);
                    Room room = cell.GetRoom(map);
                    if (room != null && seenRooms.Add(room) && !room.UsesOutdoorTemperature)
                    {
                        roomTemperatures.Add(new RoomTemperatureVacuum
                        {
                            roomCell = local,
                            temperature = room.Temperature,
                            vacuum = room.Vacuum,
                        });
                    }
                }
            }

            TransferZones(map, gravship, origin, cells);
            CopyAreas(map, gravship, origin, cells, things, pawns);
            CopyStorageGroups(map, gravship, things);
            // 与预览使用同一显式主台。捕获备用台或残骸不产生新的主台身份。
            BindPilotConsole(gravship, mainConsole);
            UpdateBoundingBoxes(gravship, occupied);
            gravship.engineToCenter = CellRect.FromCellList(occupied.ToList()).CenterVector3 - origin.ToVector3();
            CheckAffectedGoodwill(pawns.Keys, ownerFaction);
            return gravship;
        }

        /// <summary>镜像 Gravship.ShouldBringOnGravship（静态谓词）。</summary>
        private static bool ShouldBringOnGravship(Thing thing, IntVec3 cell, bool includeItems)
        {
            if (!includeItems && thing.def.category == ThingCategory.Item) return false;
            if (thing is Mote) return false;
            if (thing is Skyfaller) return false;
            if (!thing.def.bringAlongOnGravship) return false;
            if (cell != thing.Position) return false;
            if (thing.def.category == ThingCategory.Ethereal && !(thing is Blueprint) && !(thing is Frame))
            {
                return false;
            }
            return true;
        }

        /// <summary>镜像 Gravship.AddThing（包含判定换成 cells.Contains）。</summary>
        private static void AddThing(Gravship gravship, HashSet<IntVec3> cells, Thing thing, IntVec3 offset,
            Dictionary<Thing, PositionData> things, Dictionary<Pawn, PositionData> pawns,
            Dictionary<Thing, bool> powerOn, Dictionary<Thing, Thing> connectParents,
            Dictionary<Thing, PositionData> thrusters, Dictionary<Thing, PositionData> exteriorDoors,
            List<IntVec3> extenderPositions, bool explicitMember = false)
        {
            if (thing is Mote || thing is Skyfaller || !thing.def.bringAlongOnGravship)
            {
                return;
            }
            if (thing is Pawn pawn)
            {
                if (pawns.ContainsKey(pawn))
                {
                    return;
                }
                pawns.Add(pawn, new PositionData(offset, pawn.Rotation, pawn.Drafted));
                if (pawn.carryTracker.CarriedThing != null
                    && pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out Thing dropped)
                    && !dropped.Destroyed)
                {
                    AddThing(gravship, cells, dropped, offset,
                        things, pawns, powerOn, connectParents, thrusters, exteriorDoors, extenderPositions);
                }
            }
            else
            {
                if (things.ContainsKey(thing) || !thing.Spawned || (!explicitMember && !cells.Contains(thing.Position)))
                {
                    return;
                }
                things.Add(thing, new PositionData(offset, thing.Rotation));
            }
            if (thing.TryGetComp(out CompPowerTrader comp))
            {
                powerOn[thing] = comp.PowerOn;
            }
            if (thing.TryGetComp(out CompPower compPower))
            {
                connectParents[thing] = compPower.connectParent?.parent;
            }
            if (thing.TryGetComp(out CompGravshipThruster thruster) && thruster.CanBeActive)
            {
                thrusters[thing] = new PositionData(offset, thing.Rotation);
            }
            if (thing is Building_Door door)
            {
                foreach (IntVec3 item in GenAdj.CellsAdjacentCardinal(door))
                {
                    if (!cells.Contains(item))
                    {
                        exteriorDoors[thing] = new PositionData(offset, thing.Rotation);
                        break;
                    }
                }
            }
            if (thing.def == ThingDefOf.GravFieldExtender)
            {
                extenderPositions.Add(offset);
            }
        }

        /// <summary>镜像 Gravship.TransferZones：域内储物/种植区收缴为可搬运数据并删源区。</summary>
        private static void TransferZones(Map oldMap, Gravship gravship, IntVec3 origin, HashSet<IntVec3> domain)
        {
            var areas = gravship.areas;
            for (int i = oldMap.zoneManager.AllZones.Count - 1; i >= 0; i--)
            {
                Zone zone = oldMap.zoneManager.AllZones[i];
                if (zone is Zone_Stockpile stockpile)
                {
                    MoveableStockpile moveable = null;
                    foreach (IntVec3 cell in stockpile.Cells)
                    {
                        if (domain.Contains(cell))
                        {
                            if (moveable == null) moveable = new MoveableStockpile(gravship, stockpile);
                            moveable.Add(cell - origin);
                        }
                    }
                    if (moveable != null)
                    {
                        areas.stockpiles.Add(moveable);
                        stockpile.Delete();
                    }
                }
                else if (zone is Zone_Growing growing)
                {
                    MoveableGrowZone moveable = null;
                    foreach (IntVec3 cell in growing.Cells)
                    {
                        if (domain.Contains(cell))
                        {
                            if (moveable == null) moveable = new MoveableGrowZone(gravship, growing);
                            moveable.Add(cell - origin);
                        }
                    }
                    if (moveable != null)
                    {
                        areas.growZones.Add(moveable);
                        growing.Delete();
                    }
                }
            }
        }

        /// <summary>镜像 Gravship.CopyAreas：家区/允许区/屋顶区/清雪区收缴为可搬运数据。</summary>
        private static void CopyAreas(Map oldMap, Gravship gravship, IntVec3 origin, HashSet<IntVec3> domain,
            Dictionary<Thing, PositionData> things, Dictionary<Pawn, PositionData> pawns)
        {
            var areas = gravship.areas;
            foreach (Area area in oldMap.areaManager.AllAreas)
            {
                if (area is Area_Home)
                {
                    areas.homeArea = new MoveableArea_Allowed(gravship, area);
                    foreach (IntVec3 cell in area.ActiveCells)
                    {
                        if (domain.Contains(cell))
                        {
                            areas.homeArea.Add(cell - origin);
                        }
                    }
                    areas.homeArea.assignedPawns.AddRange(pawns.Keys.Where(pawn =>
                        pawn.playerSettings?.AreaRestrictionInPawnCurrentMap == area));
                    foreach (Pawn unspawned in oldMap.mapPawns.AllPawnsUnspawned)
                    {
                        if (unspawned.SpawnedParentOrMe != null && things.ContainsKey(unspawned.SpawnedParentOrMe)
                            && unspawned.playerSettings?.AreaRestrictionInPawnCurrentMap == area)
                        {
                            areas.homeArea.assignedPawns.Add(unspawned);
                        }
                    }
                    foreach (Pawn assignedPawn in areas.homeArea.assignedPawns)
                    {
                        assignedPawn.playerSettings?.Notify_MapRemoved(assignedPawn.MapHeld);
                    }
                    continue;
                }
                if (area is Area_Allowed allowed)
                {
                    MoveableArea_Allowed moveable = new MoveableArea_Allowed(gravship, allowed);
                    foreach (IntVec3 cell in area.ActiveCells)
                    {
                        if (domain.Contains(cell))
                        {
                            moveable.Add(cell - origin);
                        }
                    }
                    moveable.assignedPawns.AddRange(pawns.Keys.Where(pawn =>
                        pawn.playerSettings?.AreaRestrictionInPawnCurrentMap == area));
                    areas.allowedAreas.Add(moveable);
                    foreach (Pawn assignedPawn in moveable.assignedPawns)
                    {
                        assignedPawn.playerSettings?.Notify_MapRemoved(assignedPawn.Map);
                    }
                    continue;
                }
                MoveableArea generic = null;
                foreach (IntVec3 cell in area.ActiveCells)
                {
                    if (domain.Contains(cell))
                    {
                        if (generic == null)
                        {
                            generic = new MoveableArea(gravship, area.Label, area.RenamableLabel, area.Color, area.ID);
                        }
                        generic.Add(cell - origin);
                    }
                }
                if (generic != null)
                {
                    if (area is Area_NoRoof) areas.noRoofArea = generic;
                    else if (area is Area_BuildRoof) areas.buildRoofArea = generic;
                    else if (area is Area_SnowOrSandClear) areas.snowClearArea = generic;
                    else if (ModsConfig.BiotechActive && area is Area_PollutionClear) areas.pollutionClearArea = generic;
                }
            }
        }

        /// <summary>镜像 Gravship.CopyStorageGroups：储物组收缴并在源图解散。</summary>
        private static void CopyStorageGroups(Map oldMap, Gravship gravship, Dictionary<Thing, PositionData> things)
        {
            for (int i = oldMap.storageGroups.StorageGroupsForReading.Count - 1; i >= 0; i--)
            {
                StorageGroup storageGroup = oldMap.storageGroups.StorageGroupsForReading[i];
                MoveableStorageGroup moveable = null;
                foreach (IStorageGroupMember member in storageGroup.members)
                {
                    if (member is Thing key && things.ContainsKey(key))
                    {
                        if (moveable == null) moveable = new MoveableStorageGroup(storageGroup);
                        moveable.members.Add(member);
                    }
                }
                if (moveable != null)
                {
                    if (moveable.members.Any())
                    {
                        gravship.areas.storageGroups.Add(moveable);
                    }
                    foreach (IStorageGroupMember member in moveable.members)
                    {
                        member.SetStorageGroup(null);
                    }
                }
            }
        }

        /// <summary>镜像 Gravship.UpdateBoundingBoxes（bounds + 矩形分解 occupiedRects）。</summary>
        private static void UpdateBoundingBoxes(Gravship gravship, HashSet<IntVec3> occupiedCells)
        {
            var occupiedRects = (List<CellRect>)OccupiedRectsField.GetValue(gravship);
            var covered = new HashSet<IntVec3>();
            CellRect bounds = CellRect.FromCellList(occupiedCells.ToList());
            BoundsField.SetValue(gravship, bounds);
            for (int i = bounds.minX; i <= bounds.maxX; i++)
            {
                for (int j = bounds.minZ; j <= bounds.maxZ; j++)
                {
                    var corner = new IntVec3(i, 0, j);
                    if (!occupiedCells.Contains(corner) || covered.Contains(corner)) continue;
                    int width = 1;
                    int height = 1;
                    for (int k = i + 1; k <= bounds.maxX && occupiedCells.Contains(new IntVec3(k, 0, j)); k++)
                    {
                        width++;
                    }
                    bool growing = true;
                    while (j + height <= bounds.maxZ && growing)
                    {
                        for (int l = 0; l < width; l++)
                        {
                            if (!occupiedCells.Contains(new IntVec3(i + l, 0, j + height))
                                || covered.Contains(new IntVec3(i + 1, 0, j + height)))
                            {
                                growing = false;
                                break;
                            }
                        }
                        if (growing) height++;
                    }
                    for (int m = 0; m < width; m++)
                    {
                        for (int n = 0; n < height; n++)
                        {
                            covered.Add(new IntVec3(i + m, 0, j + n));
                        }
                    }
                    occupiedRects.Add(new CellRect(i, j, width, height));
                }
            }
        }

        /// <summary>镜像 Gravship.CheckAffectedGoodwill（域内判定由捕获保证；此处只做派系语义）。</summary>
        private static void CheckAffectedGoodwill(IEnumerable<Pawn> pawns, Faction ownerFaction)
        {
            if (ownerFaction == null) return;
            var kidnappedByFaction = new Dictionary<Faction, int>();
            foreach (Pawn pawn in pawns)
            {
                if (pawn.Faction == Faction.OfPlayer || pawn.IsPlayerControlled
                    || pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony) continue;
                if (pawn.Faction == null || pawn.Faction.HostileTo(Faction.OfPlayer)) continue;
                kidnappedByFaction.TryAdd(pawn.Faction, 0);
                kidnappedByFaction[pawn.Faction]++;
            }
            foreach (KeyValuePair<Faction, int> pair in kidnappedByFaction)
            {
                pair.Key.TryAffectGoodwillWith(Faction.OfPlayer, -10 * pair.Value, canSendMessage: true,
                    canSendHostilityLetter: true, reason: HistoryEventDefOf.PawnKidnappedOnGravShip);
            }
        }

        /// <summary>
        /// 镜像原版 GenerateGravship 的移除段：按**捕获时的绝对格集合** cells 移除清单
        /// 内容并剥除地形/屋顶/区域标志。不要求 engine。
        ///
        /// cells 必须与 <see cref="CaptureDomain"/> 传入的集合同源（同调用栈现查或直接
        /// 传参）——**禁止**从 <c>gravship.Foundations.Keys</c> 反推源图格：原版
        /// <c>Foundations</c> 属性是按当前 Rotation 旋转后的视图（Gravship.cs
        /// GetRotatedValues），对接完成/脱离都会在 strip 前设置 Rotation，旋转视图反推
        /// 的格集会绕 origin 错位（2026-09-24 事故：South 旋转下 960 格 hull 只剥中 1 格，
        /// 其余全部残留源图）。原版镜子本尊用绝对坐标 engine.ValidSubstructure 剥离。
        /// </summary>
        internal static void StripDomain(Gravship gravship, Map map, HashSet<IntVec3> cells,
            bool includeTerrain = true)
        {
            if (gravship == null || map == null || cells == null || cells.Count == 0) return;
            using var changes = Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.BeginChanges();
            IntVec3[] stripCells = new IntVec3[cells.Count];
            int i = 0;
            foreach (IntVec3 cell in cells)
            {
                stripCells[i++] = cell;
            }

            // 移除清单内容（与原版同序：先全体 PreSwapMap，再 DeSpawn）。
            List<Thing> things = gravship.Things.ToList();
            things.SortByDescending(GravshipUtility.ThingSpawnPriority);
            foreach (Thing thing in things)
            {
                thing.PreSwapMap();
            }
            foreach (Thing thing in things)
            {
                if (thing.Spawned)
                {
                    thing.DeSpawn(DestroyMode.WillReplace);
                }
            }
            foreach (Pawn pawn in gravship.Pawns)
            {
                pawn.PreSwapMap();
                if (pawn.Spawned)
                {
                    pawn.DeSpawn(DestroyMode.WillReplace);
                }
            }

            // 原版起飞后由源图 MapUpdate/Deinit 在降落放置前冲刷 delayed 电网队列
            // （DeregisterTransmitter → ResetPowerVars 清空 transmitter.connectChildren）。
            // 本流程 strip 与 place 同栈执行，必须在此手动补上同一冲刷，否则
            // PowerBuildings 会向 connectChildren 重复注册 connector。
            map.powerNetManager.UpdatePowerNetsAndConnections_First();

            // 剥除 Substructure 地形（镜像原版 GenerateGravship 的逐格清理）。
            // includeTerrain=false（shuttle 起飞，§7.2「不搬基架」）：只移除清单内容，
            // 宿主的甲板/屋顶/气/温度原样保留——舱体起飞不在宿主图开洞。
            ulong dirtyFlags = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Roofs
                | (ulong)MapMeshFlagDefOf.Snow | (ulong)MapMeshFlagDefOf.Sand | (ulong)MapMeshFlagDefOf.Gas;
            for (int c = 0; includeTerrain && c < stripCells.Length; c++)
            {
                IntVec3 cell = stripCells[c];
                int index = map.cellIndices.CellToIndex(cell);
                map.terrainGrid.RemoveGravshipTerrainUnsafe(cell, index);
                SubstructureDamageUtility.CleanInvalidFilth(map, cell);
                map.roofGrid.RemoveRoofUnsafe(index);
                map.areaManager.BuildRoof[index] = false;
                map.areaManager.NoRoof[index] = false;
                map.glowGrid.DirtyCell(cell);
                map.snowGrid.SetDepth(cell, 0f);
                map.sandGrid.SetDepth(cell, 0f);
                map.gasGrid.ClearCellUnsafe(cell);
                foreach (Designation designation in map.designationManager.AllDesignationsAt(cell))
                {
                    map.designationManager.RemoveDesignation(designation);
                }
                IntVec2 cell2D = cell.ToIntVec2;
                foreach (FleckSystem system in map.flecks.Systems)
                {
                    system.RemoveAllFlecks(fleck => fleck.GetPosition().ToIntVec2() == cell2D);
                }
                map.mapDrawer.MapMeshDirty(cell, dirtyFlags, regenAdjacentCells: true, regenAdjacentSections: false);
                map.pathing.RecalculatePerceivedPathCostAt(cell);
            }
            map.roofGrid.Drawer.SetDirty();
        }

        internal static void ForceCutsceneTimeZero(WorldComponent_GravshipController controller)
        {
            if (controller != null && ControllerTimeLeft != null) ControllerTimeLeft.SetValue(controller, 0f);
        }

        internal static void PlaceIntoMap(Gravship gravship, IntVec3 root, Map map, int ownerId,
            Dictionary<IntVec3, int> placedOwners = null)
        {
            using var changes = Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.BeginChanges();
            var stampCells = new HashSet<IntVec3>(gravship.Foundations.Count);
            foreach (IntVec3 cell in gravship.Foundations.Keys) stampCells.Add(cell + root);
            if (placedOwners != null) SubstructureOwnership.For(map).SetPendingStamp(placedOwners);
            else SubstructureOwnership.For(map).SetPendingStamp(stampCells, ownerId);
            List<Thing> spawned;
            try
            {
                GravshipPlacementUtility.PlaceGravshipInMap(gravship, root, map, out spawned);
            }
            finally
            {
                SubstructureOwnership.For(map).ClearPendingStamp();
            }
            GravshipPlacementUtility.ApplyTemperatureVacuumFromBase(gravship, root, map);
            map.listerFilthInHomeArea.RebuildAll();
            map.resourceCounter.UpdateResourceCounts();
            map.wealthWatcher.ForceRecount(allowDuringInit: true);
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            foreach (Thing thing in spawned)
                if (thing != null && !thing.Destroyed) thing.PostSwapMap();
            foreach (Pawn pawn in gravship.Pawns)
                if (pawn != null && !pawn.Destroyed) pawn.PostSwapMap();
        }
    }
}
