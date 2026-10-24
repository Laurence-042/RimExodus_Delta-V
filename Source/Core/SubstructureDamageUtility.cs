using Verse;

namespace DeltaV
{
    /// <summary>基架毁伤机制；通用地形移除及跨图搬运不调用此入口。</summary>
    public static class SubstructureDamageUtility
    {
        /// <summary>建筑先承伤，多格建筑保护全部占用格；电缆不构成保护。</summary>
        public static bool CanDamage(Map map, IntVec3 cell) =>
            SpaceStructureRules.ClassifyCell(map, cell, out _) == SpaceCellKind.Substructure;

        public static bool TryDestroy(Map map, IntVec3 cell)
        {
            if (!CanDamage(map, cell)) return false;
            map.terrainGrid.RemoveFoundation(cell, doLeavings: false);
            if (SubstructureOwnership.HasFoundation(map, cell)) return false;
            CompleteDestruction(map, cell);
            return true;
        }

        /// <summary>仅在明确的毁伤流程已移除基架后调用，原版毁伤适配共用。</summary>
        internal static void CompleteDestruction(Map map, IntVec3 cell)
        {
            if (SubstructureOwnership.HasFoundation(map, cell)) return;
            Current.Game?.GetComponent<UniverseManager>()?.Projectiles.NotifyFoundationDestroyed(map, cell);
            // 保留真实底层太空/天然地面；原版毁伤可能只清掉了最上层临时地形。
            map.terrainGrid.RemoveTempTerrain(cell, doLeavings: false, preventDestroyEffects: true);
            if (map.terrainGrid.UnderTerrainAt(cell) != null)
                map.terrainGrid.RemoveTopLayer(cell, doLeavings: false);
            map.roofGrid.SetRoof(cell, null);

            // 基架已移除，建筑爆炸等后效不能重复毁伤同一基架。
            // 快照覆盖同格电缆/插件；宿主回调可能提前销毁或移走其他建筑。
            Thing[] things = cell.GetThingList(map).ToArray();
            foreach (Thing thing in things)
            {
                if (thing is Building && thing.Spawned && thing.Map == map && !thing.Destroyed)
                    thing.Kill();
            }
            Current.Game?.GetComponent<UniverseManager>()?.Scatter.NotifyDestroyedCell(map, cell);
        }
    }
}
