using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>唯一对接收尾：船体落地钩子同步执行，源图处置和聚焦交接在帧边界执行。</summary>
    internal sealed class DockingCompletion
    {
        private readonly UniverseManager universe;
        private readonly Map targetMap;
        private readonly Map sourceMap;
        private readonly bool impactLanding;
        private readonly ShipFlightTransitionService.DockingFocusHandoff handoff;

        private readonly ShipBase vessel;
        private readonly string label;

        internal DockingCompletion(UniverseManager universe, ShipBase vessel, Map targetMap, Map sourceMap,
            ShipFlightTransitionService.DockingFocusHandoff handoff, bool impactLanding = false)
        {
            this.universe = universe;
            this.vessel = vessel;
            label = vessel.Label;
            this.targetMap = targetMap;
            this.sourceMap = sourceMap;
            this.impactLanding = impactLanding;
            this.handoff = handoff;
        }

        public void OnTransferedIntoHost() => vessel.NotifyLanded();

        /// <summary>
        /// 帧边界公共收尾：源图处置 + 聚焦交接 + 通知 +
        /// 消息 + pending 清理。聚焦 handoff 在完成 tick 同步捕获（host 取计划快照），
        /// 此处只消费；玩家完成时不在源图则捕获为 default，不打扰。
        /// </summary>
        public void Finish()
        {
            try { FinishCore(); }
            finally { universe.ShipTransitions.FinishDockingCleanup(sourceMap); }
        }

        private void FinishCore()
        {
            ShipFlightTransitionService transitions = universe.ShipTransitions;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"docking finish: '{label}' {GetType().Name}, source={UniverseManager.MapTag(sourceMap)}, currentMap={UniverseManager.MapTag(Find.CurrentMap)}");
            if (sourceMap == null) return;
            // 统一失效宿主放置后的结构查询；与建筑/归属事件共享同一版本。
            SubstructureOwnership.BumpStructureVersion();
            if (targetMap != null && !targetMap.Disposed)
            {
                PlanetGlobeRenderer.InvalidateSilhouette(targetMap);
            }
            if (sourceMap.Disposed || !Find.Maps.Contains(sourceMap))
            {
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"docking finish source disposal skipped: source={UniverseManager.MapTag(sourceMap)}");
            }
            else
            {
                SpaceObject pocketContainer = universe.SpaceObjectOnMap(sourceMap);
                SpacePose sourcePose = pocketContainer?.pose ?? new SpacePose(Vector3.right, 0f);
                ShipFlightTransitionService.AbandonPawnsOn(sourceMap);
                transitions.ConvertLeftoverSubstructureToWreck(universe, sourceMap, sourcePose);
                MapOverlayRenderer.Release(sourceMap);
                PlanetGlobeRenderer.InvalidateSilhouette(sourceMap);
                if (pocketContainer != null)
                {
                    universe.UnregisterObject(pocketContainer);
                }
                // 先交接再销毁：CurrentMap 已在宿主图上，maps[0] 回退不会触发。
                if (handoff.IsValid)
                {
                    transitions.ApplyDockingFocusHandoff(universe, handoff);
                    DeltaVLog.Message(DeltaVLogModule.Core,
                        $"docking finish focus handoff -> {UniverseManager.MapTag(handoff.host.InteriorMap)}; currentMap now {UniverseManager.MapTag(Find.CurrentMap)}");
                }
                else
                {
                    DeltaVLog.Message(DeltaVLogModule.Core,
                        $"docking finish destroying source with no handoff; currentMap={UniverseManager.MapTag(Find.CurrentMap)}");
                }
                universe.Lifecycle.DestroyPocketMap(sourceMap);
                ShipPhysics.ClearCache();
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    "docking source pocket map removed after merge (leftovers disposed per confirmation)");
            }
            HostColonistBarVisibility.NotifyDockingStateChanged();
            Building_DockingPort.NotifyLandingSettled(targetMap);
            Messages.Message("DeltaV_DockSuccess".Translate(label),
                MessageTypeDefOf.PositiveEvent, false);
            if (!impactLanding) transitions.DiscardPendingDocking(universe);
        }

    }
}
