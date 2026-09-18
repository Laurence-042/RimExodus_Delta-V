using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 把普通地图上新建的原版 Gravship 接入 Delta-V 移动模式。地面控制台仍显示入口，
    /// 但只给出重力环境说明；Orbit Layer 才允许把真实结构搬入独立飞行口袋图。
    /// </summary>
    public static class MobileShipBootstrap
    {
        public static bool IsZeroGravity(Map map)
        {
            return map != null && map.Tile.Valid && map.Tile.LayerDef.isSpace;
        }

        /// <summary>
        /// 图上全部玩家驾驶台（含未 bootstrap 的原版 Gravship 结构）。面板用它判断
        /// 固定模式入口：唯一驾驶台可直接进入移动模式；多艘船必须选中具体驾驶台
        /// （按其连接的引擎与相连基架连通域捕获准确船体，不能任选一台）。
        /// </summary>
        public static List<Building> PlayerConsoles(Map map)
        {
            var result = new List<Building>();
            if (map?.listerThings?.AllThings == null) return result;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is Building building && building.Spawned && !building.Destroyed
                    && building.Faction == Faction.OfPlayer
                    && building.TryGetComp<CompDeltaVPilotConsole>() != null)
                {
                    result.Add(building);
                }
            }
            return result;
        }

        public static void TryEnter(UniverseManager universe, Building console)
        {
            Map sourceMap = console?.Map;
            if (universe == null || sourceMap == null || console.Faction != Faction.OfPlayer) return;
            if (!IsZeroGravity(sourceMap))
            {
                Messages.Message("DeltaV_PilotGravityRestricted".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (Find.GravshipController == null || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            CompPilotConsole vanillaConsole = console.TryGetComp<CompPilotConsole>();
            Building_GravEngine engine = vanillaConsole?.engine;
            if (engine == null || engine.Map != sourceMap)
            {
                Messages.Message("DeltaV_DockFailNoEngine".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 圈定/锚定与转换捕获同口径（相连基架连通域）：bootstrap 的船可能已建得
            // 超出引擎力场，结构中心按实际船体范围取（停放锚点同族入口）。
            if (engine.AllConnectedSubstructure.Count == 0)
            {
                Messages.Message("CannotLaunchNoSubstructure".Translate().CapitalizeFirst(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            Vector2 structureCenter = SpaceTransform.BoundsCenterOfCells(engine.AllConnectedSubstructure);

            // 任意真实轨道图都能 bootstrap：宇宙坐标是球面绝对坐标，源图 tile 帧只是
            // 把"图内结构中心"接到球面方向的纯函数，不需要 Universe 预先存在任何锚。
            // 原版逆重飞船发射到任意 POI/orbit 图后起飞因此天然可用（该图只是普通来源图）。
            SpaceObject sourceFrame = universe.FrameOf(sourceMap);
            if (sourceFrame == null)
            {
                Log.Error($"[DeltaV] Bootstrap on '{sourceMap.Parent?.Label}': no tile frame for a valid space map.");
                return;
            }

            Vector2 sourceLocal = SpaceTransform.MapToShipLocal(sourceMap, structureCenter);
            Vector3 universeDir = SpaceTransform.FocusedToUniverse(sourceFrame, sourceLocal);
            // 基架归属域认领（2026-09-18 设计）：bootstrap 捕获的就是引擎所在的整个归属
            // 连通域——整域（含「未占有」域）重盖章为船 ID，剪影、捕获与再降落保持
            // 归属连续。裸 ID 域直接继承；域 ID 仍被宿主对象（如残骸 POI）持有时换新
            // ID（bootstrap 捕获的是整个归属域，残骸的全部基架随之成为船体，POI 只剩
            // 空图）；未占有域获得全新船 ID。
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(sourceMap);
            int shipId = universe.NextObjectId();
            if (ownership != null)
            {
                int domainOwner = ownership.OwnerAt(engine.Position);
                if (domainOwner != SubstructureOwnership.Unassigned
                    && !universe.objects.Any(o => o.objectId == domainOwner))
                {
                    shipId = domainOwner;
                }
                ownership.SetOwner(engine.AllConnectedSubstructure, shipId);
            }
            // 初始宇宙角 = 宿主帧角 ⊕ 主台在源图上的朝向（与降落落点反向映射互逆；
            // 真实轨道图的显示帧角恒 0，此收口为约定统一）。
            var ship = new ShipState(shipId, engine.RenamableLabel, null,
                new SpacePose(universeDir, SpaceTransform.WrapAngle(sourceFrame.pose.angle
                    + ShipOrientation.AngleFromRot4(console.Rotation))),
                isPlayerShip: true)
            {
                faction = Faction.OfPlayer,
                primaryConsole = console,
                docked = true,
                dockedMap = sourceMap,
                dockedOrigin = structureCenter,
                dockedRotation = Rot4.North
            };
            universe.RegisterObject(ship);
            if (universe.primaryShipId < 0 || !universe.Ships.Any(s => s.objectId == universe.primaryShipId))
            {
                universe.primaryShipId = ship.objectId;
            }
            HostColonistBarVisibility.NotifyDockingStateChanged();
            // 驾驶台只负责明确选择“哪一艘”离开共享地图进入移动模式。
            // 生成口袋图后保持未驾驶；玩家再通过该独占地图的 HUD 启用控制。
            DockController.TryLaunch(universe, ship);
        }
    }
}
