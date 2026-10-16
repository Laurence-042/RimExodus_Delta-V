using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 对接完成流程基类（飞船与 shuttle 公共收尾的模板方法）：公共部分实现宿主图
    /// 失效、源口袋残留处置、聚焦交接、殖民者条/对接口通知、成功消息与 pending
    /// 清理；子类只经 OnTransferedIntoHost 注入专属增量。
    /// 生命周期：完成 tick（CompleteDocking 调用栈）调 OnTransferedIntoHost，帧边界
    /// （QueueFrameAction）调 Finish——源图销毁与聚焦交接必须离开 tick 调用栈。
    /// shuttle 口袋无域外内容，公共残留处置（遗弃/残骸）对其自动空转，不设特例。
    /// </summary>
    internal abstract class DockingCompletion
    {
        protected readonly UniverseManager universe;
        protected readonly Map targetMap;
        private readonly Map sourceMap;
        private readonly bool impactLanding;
        private readonly ShipFlightTransitionService.DockingFocusHandoff handoff;

        private string labelCache;

        private DockingCompletion(UniverseManager universe, Map targetMap, Map sourceMap,
            ShipFlightTransitionService.DockingFocusHandoff handoff, bool impactLanding = false)
        {
            this.universe = universe;
            this.targetMap = targetMap;
            this.sourceMap = sourceMap;
            this.impactLanding = impactLanding;
            this.handoff = handoff;
        }

        /// <summary>成功消息/日志标签（船名或舱体名）。</summary>
        protected abstract string BuildLabel();

        /// <summary>转移成功后、帧边界前的专属增量（完成 tick 调用栈内）。</summary>
        public virtual void OnTransferedIntoHost()
        {
        }

        /// <summary>
        /// 帧边界公共收尾（模板方法，子类不得覆写）：源图处置 + 聚焦交接 + 通知 +
        /// 消息 + pending 清理。聚焦 handoff 在完成 tick 同步捕获（host 取计划快照），
        /// 此处只消费；玩家完成时不在源图则捕获为 default，不打扰。
        /// </summary>
        public void Finish()
        {
            ShipFlightTransitionService transitions = universe.ShipTransitions;
            if (!impactLanding) transitions.landedShipAwaitingCleanup = Ship.Invalid;
            string label = labelCache ?? (labelCache = BuildLabel());
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"docking finish: '{label}' {GetType().Name}, source={UniverseManager.MapTag(sourceMap)}, currentMap={UniverseManager.MapTag(Find.CurrentMap)}");
            if (sourceMap == null) return;
            // 宿主图放置失效（公共）。船路径的归属盖章链（SetPendingStamp →
            // NotifyOwnershipChanged）已顺带 bump+失效，此处幂等重复；shuttle 盖不了
            // 盖（ownerId=0，SetPendingStamp 不生效），这两行是它唯一的失效来源。
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

        /// <summary>普通飞船完成流程：无 tick 期增量，标签取结构归属名。</summary>
        internal sealed class ShipDockingCompletion : DockingCompletion
        {
            private readonly int ownerId;

            public ShipDockingCompletion(UniverseManager universe, int ownerId, Map targetMap,
                Map sourceMap,
                ShipFlightTransitionService.DockingFocusHandoff handoff)
                : base(universe, targetMap, sourceMap, handoff)
            {
                this.ownerId = ownerId;
            }

            protected override string BuildLabel()
            {
                return universe.LabelForOwner(ownerId);
            }
        }

        /// <summary>
        /// shuttle 完成流程：落地后启动原版发射冷却；容器注销时统一清理运行时键。
        /// </summary>
        internal sealed class ShuttleDockingCompletion : DockingCompletion
        {
            private readonly Building hull;

            public ShuttleDockingCompletion(UniverseManager universe, Building hull, Map targetMap,
                Map sourceMap,
                ShipFlightTransitionService.DockingFocusHandoff handoff, bool impactLanding = false)
                : base(universe, targetMap, sourceMap, handoff, impactLanding)
            {
                this.hull = hull;
            }

            protected override string BuildLabel()
            {
                return hull.LabelCap;
            }

            public override void OnTransferedIntoHost()
            {
                SpaceShuttleService.StartLaunchCooldown(hull.TryGetComp<CompLaunchable>());
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"shuttle '{hull.LabelCap}' docked into '{universe.FrameOf(targetMap)?.label}' via shared Gravship placement chain");
            }

        }
    }
}
