using System;
using System.Collections.Generic;
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
    /// </summary>
    internal static class GravshipInterop
    {
        private static readonly ConstructorInfo EmptyCtor =
            AccessTools.Constructor(typeof(Gravship), Type.EmptyTypes);
        private static readonly FieldInfo EngineField = AccessTools.Field(typeof(Gravship), "engine");
        private static readonly FieldInfo ThingsField = AccessTools.Field(typeof(Gravship), "things");
        private static readonly FieldInfo ThrustersField = AccessTools.Field(typeof(Gravship), "thrusters");
        private static readonly FieldInfo FoundationsField = AccessTools.Field(typeof(Gravship), "foundations");
        private static readonly FieldInfo BoundsField = AccessTools.Field(typeof(Gravship), "bounds");
        private static readonly FieldInfo ConsoleField = AccessTools.Field(typeof(Gravship), "pilotConsole");
        private static readonly FieldInfo ControllerTimeLeft =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "timeLeft");

        internal static Gravship BuildPreview(ShipState ship, Building_GravEngine engine)
        {
            if (EmptyCtor == null || engine == null || !engine.Spawned || engine.Map == null) return null;
            Map map = engine.Map;
            HashSet<IntVec3> cells = engine.AllConnectedSubstructure;
            if (cells == null || cells.Count == 0) return null;

            var hollow = (Gravship)EmptyCtor.Invoke(null);
            Gravship gravship = WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Gravship, hollow) as Gravship;
            if (gravship == null) return null;

            IntVec3 origin = engine.Position;
            EngineField?.SetValue(gravship, engine);
            var foundations = (Dictionary<IntVec3, TerrainDef>)FoundationsField?.GetValue(gravship);
            var localCells = new List<IntVec3>(cells.Count);
            foreach (IntVec3 cell in cells)
            {
                IntVec3 local = cell - origin;
                localCells.Add(local);
                foundations[local] = map.terrainGrid.FoundationAt(cell);
            }
            BoundsField?.SetValue(gravship, CellRect.FromCellList(localCells));

            Building console = ship.primaryConsole;
            if (console != null && console.Spawned && console.Map == map && cells.Contains(console.Position))
            {
                var things = (Dictionary<Thing, PositionData>)ThingsField?.GetValue(gravship);
                things[console] = new PositionData(console.Position - origin, console.Rotation);
                ConsoleField?.SetValue(gravship, console);
            }

            var thrusters = (Dictionary<Thing, PositionData>)ThrustersField?.GetValue(gravship);
            foreach (CompGravshipFacility component in engine.GravshipComponents)
            {
                if (component is CompGravshipThruster thruster && thruster.CanBeActive)
                {
                    thrusters[thruster.parent] = new PositionData(
                        thruster.parent.Position - origin, thruster.parent.Rotation);
                }
            }
            return gravship;
        }

        internal static void ForceCutsceneTimeZero(WorldComponent_GravshipController controller)
        {
            if (controller != null && ControllerTimeLeft != null) ControllerTimeLeft.SetValue(controller, 0f);
        }

        internal static void PlaceIntoMap(Gravship gravship, IntVec3 root, Map map, int ownerId)
        {
            var stampCells = new HashSet<IntVec3>(gravship.Foundations.Count);
            foreach (IntVec3 cell in gravship.Foundations.Keys) stampCells.Add(cell + root);
            SubstructureOwnership.For(map).SetPendingStamp(stampCells, ownerId);
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
