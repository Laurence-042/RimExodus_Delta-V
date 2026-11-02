using RimWorld;
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
            BlowOffNearbyRoofs(map, cell);

            // 基架已移除，建筑爆炸等后效不能重复毁伤同一基架。
            // 快照覆盖同格电缆/插件；宿主回调可能提前销毁或移走其他建筑。
            Thing[] things = cell.GetThingList(map).ToArray();
            foreach (Thing thing in things)
            {
                if (thing is Building && thing.Spawned && thing.Map == map && !thing.Destroyed)
                    thing.Kill();
            }
            CleanInvalidFilth(map, cell);
            Current.Game?.GetComponent<UniverseManager>()?.Scatter.NotifyDestroyedCell(map, cell);
        }

        private static void BlowOffNearbyRoofs(Map map, IntVec3 origin)
        {
            var universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.FrameOf(map) == null) return;
            // Local structural shock only: no roof collapse damage, no recursion,
            // no removal of rock roofs or terrain-owned moon-pool force fields.
            for (int i = 0; i < GenAdj.AdjacentCells.Length; i++)
            {
                IntVec3 offset = GenAdj.AdjacentCells[i];
                IntVec3 cell = origin + offset;
                if (!SubstructureOwnership.HasFoundation(map, cell)) continue;
                RoofDef roof = map.roofGrid.RoofAt(cell);
                if (roof == null || roof.isNatural || MoonPoolUtility.IsFieldRoofAt(map, cell)
                    || RoofShockBlocked(map, cell)) continue;
                bool diagonal = offset.x != 0 && offset.z != 0;
                if (diagonal && RoofShockBlocked(map, origin + new IntVec3(offset.x, 0, 0))
                    && RoofShockBlocked(map, origin + new IntVec3(0, 0, offset.z))) continue;
                if (!Rand.Chance(diagonal ? 0.25f : 0.55f)) continue;
                map.roofGrid.SetRoof(cell, null);
                universe.Projectiles.NotifyRoofDestroyed(map, cell);
            }
        }

        private static bool RoofShockBlocked(Map map, IntVec3 cell)
        {
            Building building = cell.GetEdificeSafe(map);
            return building != null && (building.def.IsWall || building.def.IsDoor || building.IsAirtight);
        }

        /// <summary>仅供毁伤和搬运收尾；Space 的继承掩码可能接受碎石，须同时遵循生成时的真空限制。</summary>
        internal static void CleanInvalidFilth(Map map, IntVec3 cell)
        {
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
            foreach (Thing thing in cell.GetThingList(map).ToArray())
                if (thing is Filth && (terrain.exposesToVacuum
                    || !FilthMaker.TerrainAcceptsFilth(terrain, thing.def)))
                    thing.Destroy(DestroyMode.Vanish);
        }
    }
}
