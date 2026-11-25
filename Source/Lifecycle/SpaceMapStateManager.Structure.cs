using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed partial class SpaceMapStateManager
    {
        /// <summary>建筑/地形事件合并到帧边界；复核搬运、生成后的最终内容。</summary>
        internal void QueueStructureCheck(Map map)
        {
            if (map == null) return;
            SpaceObject owner = universe.FrameOf(map);
            // Do not discover members/roles while publishing a structural mutation.
            // The full eligibility query belongs at the coalesced frame boundary.
            if (!IsOrdinaryStructureMap(owner)) return;
            int version = owner.RepresentationVersion;
            if (!TryBeginTransition(owner, MapTransitionKind.StructureReview, map)) return;
            universe.QueueFrameAction(() =>
            {
                if (!TransitionStillCurrent(owner.objectId, MapTransitionKind.StructureReview, version, map)) return;
                try
                {
                    if (universe.ObjectById(owner.objectId) == owner && owner.RepresentationVersion == version
                        && owner.map == map && IsOrdinaryStructureMap(owner))
                    {
                        if (!map.Disposed && universe.Generation?.IsGenerating(map) != true)
                            universe.ShipTransitions.FinishEmptyWorksite(map);
                        RecycleUnsupportedMap(map);
                    }
                }
                finally { CompleteTransition(owner.objectId, MapTransitionKind.StructureReview, version); }
            });
        }

        private static bool IsOrdinaryStructureMap(SpaceObject owner) => owner is MapSpaceObject
            && owner.map?.Parent is ShipPocketParent_DeltaV
            && !UsesSeparateLifecycle(owner);

        private static bool UsesSeparateLifecycle(SpaceObject owner) => owner.projectileRoleOverride
            || ((owner.baseTag | (owner.Payload?.Role ?? 0))
                & (SpaceObjectTag.Projectile | SpaceObjectTag.Scattered | SpaceObjectTag.Personnel)) != 0;

        internal static bool HasMapSupport(SpaceObject owner)
        {
            if (owner?.map == null || owner.map.Disposed) return false;
            if (UsesSeparateLifecycle(owner)) return true;
            var ownership = SubstructureOwnership.For(owner.map);
            return ownership.SpawnedBuildingCount != 0 || ownership.SupportCellCount != 0;
        }

        /// <summary>只在安全帧上下文调用；当前聚焦、玩家 Pawn 或物品均不阻止无承载图回收。</summary>
        internal bool RecycleUnsupportedMap(Map map)
        {
            SpaceObject owner = universe.FrameOf(map);
            if (!IsOrdinaryStructureMap(owner) || map.Disposed || !Find.Maps.Contains(map)
                || universe.IsResettingScene || universe.Generation?.IsGenerating(map) == true
                || universe.ShipTransitions.IsAwaitingStrategicTakeoff(map)
                || universe.ShipTransitions.IsAwaitingDockingCleanup(map) || HasMapSupport(owner)) return false;

            bool wasFocused = Find.CurrentMap == map;
            if (!universe.Scatter.ScatterLoose(map)) return false;
            SpaceObject fallback = null;
            if (wasFocused)
            {
                fallback = universe.objects.Where(candidate => candidate != owner
                        && candidate.map != null && !candidate.map.Disposed && Find.Maps.Contains(candidate.map)
                        && !IsUniverseHost(candidate.map) && !candidate.HasTag(SpaceObjectTag.Projectile)
                        && universe.Generation?.IsGenerating(candidate.map) != true
                        && (!IsOrdinaryStructureMap(candidate) || HasMapSupport(candidate)))
                    .OrderByDescending(candidate => candidate.HasFaction(Faction.OfPlayerSilentFail))
                    .ThenBy(candidate => OrbitSphere.ArcCells(owner.pose.position, candidate.pose.position))
                    .ThenBy(candidate => candidate.objectId).FirstOrDefault();
                if (fallback != null)
                {
                    Vector3 center = Find.CameraDriver.CurrentViewRect.CenterVector3;
                    Vector3 point = SpaceTransform.MapPointToUniverse(owner, map, new Vector2(center.x, center.z));
                    universe.SetFocusedWithCamera(fallback,
                        SpaceTransform.UniverseToMapPoint(fallback, fallback.map, point), Find.CameraDriver.RootSize);
                }
                else Current.Game.CurrentMap = null;
            }

            // Cargo now belongs to independent SpaceObjects. Native teardown handles the map and its references.
            DestroyPocketMap(map);
            universe.UnregisterObject(owner);
            if (wasFocused && fallback == null) Find.World.renderer.wantedMode = WorldRenderMode.Planet;
            return true;
        }
    }
}
