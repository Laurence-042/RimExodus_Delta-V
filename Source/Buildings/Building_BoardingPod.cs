using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 跳帮舱发射管（2026-09-21 阶段4）：铺在基架上的单程载人制导飞行器发射器。
    /// 用法：选中要投放的殖民者 → 点发射管 gizmo「发射跳帮舱」→ 经 Targeter 点击
    /// 目标船外壳（ShellPicking 在瞄准会话内解析被点对象——选择路由对 Targeter 的
    /// 让位契约自第一版即成立）→ 乘员 DeSpawn 装舱、BoardingPodFlight 导弹式追踪，
    /// 命中撞穿外壳开孔、密封舱落位堵孔、乘员在目标图落点旁生成。
    /// 单程：撤离 = 整机对接或占领目标。
    /// </summary>
    public class Building_BoardingPodLauncher : Building
    {
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }
            if (Faction != Faction.OfPlayer || !Spawned) yield break;
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_PodLaunchLabel".Translate(),
                defaultDesc = "DeltaV_PodLaunchDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack"),
                action = BeginLaunchTargeting
            };
        }

        private void BeginLaunchTargeting()
        {
            List<Pawn> crew = Find.Selector.SelectedPawns
                .Where(p => p.Faction == Faction.OfPlayer && p.Spawned && p.Map == Map && !p.Downed)
                .ToList();
            if (crew.Count == 0)
            {
                Messages.Message("DeltaV_PodNeedCrew".Translate(), this, MessageTypeDefOf.RejectInput, false);
                return;
            }
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            Map map = Map;
            var parameters = new TargetingParameters
            {
                canTargetPawns = false,
                canTargetBuildings = false,
                canTargetItems = false,
                canTargetLocations = true,
                validator = target => ResolveTargetShip(universe, map, target.Cell) != null
            };
            Find.Targeter.BeginTargeting(parameters,
                target => TryLaunchAt(universe, map, target.Cell, crew),
                null, null, null, false);
        }

        /// <summary>瞄准会话内解析被点外壳：限其他飞船（有活图的 ShipState）。</summary>
        private static ShipState ResolveTargetShip(UniverseManager universe, Map frameMap, IntVec3 cell)
        {
            if (universe == null || frameMap == null || !cell.InBounds(frameMap)) return null;
            SpaceObject picked = ShellPicking.ShellAtFramePoint(universe, frameMap,
                new Vector3(cell.x + 0.5f, 0f, cell.z + 0.5f));
            ShipState ship = picked as ShipState;
            if (ship == null || ship.map == null || ship.map.Disposed) return null;
            return ship;
        }

        private void TryLaunchAt(UniverseManager universe, Map frameMap, IntVec3 cell, List<Pawn> crew)
        {
            ShipState source = universe?.ShipOnMap(Map);
            ShipState target = ResolveTargetShip(universe, frameMap, cell);
            if (source == null || target == null || target == source)
            {
                Messages.Message("DeltaV_PodNoTarget".Translate(), this, MessageTypeDefOf.RejectInput, false);
                return;
            }
            universe.BoardingPods.Launch(source, target, crew);
        }
    }

    /// <summary>
    /// 落位跳帮舱（命中产物，非建造物）：门类建筑、常闭密封（ExchangeVacuum=false，
    /// 复用对接口气闸语义）——舱体堵住撞穿的破口，开门通行但不透真空，目标舰内部
    /// 不因破口失压。拆除/摧毁后破口重新敞开（墙体已在撞击中损毁）。
    /// </summary>
    public class Building_BoardingPodLanded : Building_Door
    {
        public override bool ExchangeVacuum => false;
    }
}
