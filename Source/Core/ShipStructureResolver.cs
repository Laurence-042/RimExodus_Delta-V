using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 飞船当前承载图、逆重引擎、连通船体域与成员判定的唯一查询入口。
    /// 查询本身无状态；归属过滤仍由 Building_GravEngine 的统一 Harmony 入口执行。
    /// </summary>
    public static class ShipStructureResolver
    {
        public static Map MapOf(ShipState ship) => ship?.InteriorMap;

        public static Building_GravEngine EngineOf(ShipState ship)
        {
            return EngineOn(MapOf(ship), ship?.primaryConsole);
        }

        public static Building_GravEngine EngineOn(Map map, Building primaryConsole)
        {
            if (map == null || map.Disposed) return null;
            CompPilotConsole consoleComp = primaryConsole?.TryGetComp<CompPilotConsole>();
            if (consoleComp?.engine != null && consoleComp.engine.Spawned && consoleComp.engine.Map == map)
            {
                return consoleComp.engine;
            }

            Building_GravEngine fallback = null;
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.GravEngine))
            {
                if (!(thing is Building_GravEngine engine)) continue;
                if (fallback == null) fallback = engine;
                if (primaryConsole != null && primaryConsole.Spawned && primaryConsole.Map == map
                    && engine.AllConnectedSubstructure.Contains(primaryConsole.Position))
                {
                    return engine;
                }
            }
            return fallback;
        }

        public static HashSet<IntVec3> ConnectedCells(ShipState ship, bool noRegen = false)
        {
            Building_GravEngine engine = EngineOf(ship);
            return engine == null
                ? null
                : noRegen ? engine.AllConnectedSubstructureNoRegen : engine.AllConnectedSubstructure;
        }

        public static bool Contains(ShipState ship, Thing thing)
        {
            Map map = MapOf(ship);
            if (ship == null || thing == null || map == null || thing.MapHeld != map) return false;
            Building_GravEngine engine = EngineOn(map, ship.primaryConsole);
            return engine != null && engine.AllConnectedSubstructure.Contains(thing.PositionHeld);
        }

        /// <summary>停靠图中代表该船口袋图几何中心的 Map 坐标；旧存档缺字段时重建。</summary>
        public static Vector2 DockedOrigin(ShipState ship)
        {
            if (ship?.dockedMap == null) return Vector2.zero;
            if (ship.dockedOrigin != Vector2.zero) return ship.dockedOrigin;
            Building_GravEngine engine = EngineOf(ship);
            if (engine == null) return SpaceTransform.ShipLocalOrigin(ship.dockedMap);
            if (ship.map != null && ship.pocketRoot.IsValid)
            {
                ship.dockedOrigin = SpaceTransform.ShipOriginFromPlacementRoot(
                    ship.map, ship.pocketRoot, engine.Position, ship.dockedRotation);
            }
            else
            {
                Vector3 center = CellRect.FromCellList(engine.ValidSubstructure).CenterVector3;
                ship.dockedOrigin = new Vector2(center.x, center.z);
            }
            return ship.dockedOrigin;
        }
    }
}
