using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 月池地形自带可透光的力场屋顶。普通实体屋顶优先保留，拆除后才露出力场顶。
    /// 力场顶使用原版屋顶格，使房间、温度、天气和真空共用原版语义。
    /// </summary>
    public static class MoonPoolUtility
    {
        private static TerrainDef moonPoolDef;
        private static RoofDef fieldRoofDef;
        [System.ThreadStatic] private static bool reconcilingRoof;
        [System.ThreadStatic] private static int shuttlePassageDepth;
        [System.ThreadStatic] private static int lightingDepth;

        /// <summary>月池地形 def（加载后解析）。</summary>
        public static TerrainDef MoonPoolTerrain =>
            moonPoolDef ?? (moonPoolDef = DefDatabase<TerrainDef>.GetNamed("DeltaV_MoonPool", false));

        public static RoofDef FieldRoof =>
            fieldRoofDef ?? (fieldRoofDef = DefDatabase<RoofDef>.GetNamed("DeltaV_MoonPoolFieldRoof", false));

        public static bool IsMoonPoolAt(Map map, IntVec3 cell)
        {
            return MoonPoolTerrain != null && map?.terrainGrid != null && cell.InBounds(map)
                && map.terrainGrid.TerrainAt(cell) == MoonPoolTerrain;
        }

        public static bool IsFieldRoofAt(Map map, IntVec3 cell)
            => FieldRoof != null && map?.roofGrid != null && cell.InBounds(map)
                && map.roofGrid.RoofAt(cell) == FieldRoof;

        // These scopes only change roof queries inside one vanilla validation/render call stack.
        internal static bool ShuttlePassageActive => shuttlePassageDepth > 0;
        internal static bool LightingActive => lightingDepth > 0;
        internal static void BeginShuttlePassage() => shuttlePassageDepth++;
        internal static void EndShuttlePassage() => shuttlePassageDepth--;
        internal static void BeginLighting() => lightingDepth++;
        internal static void EndLighting() => lightingDepth--;

        internal static bool IgnoreRoofForCurrentCall(Map map, IntVec3 cell)
            => (ShuttlePassageActive && IsMoonPoolAt(map, cell))
                || (LightingActive && IsFieldRoofAt(map, cell));

        /// <summary>Event-driven reconciliation. The terrain is authoritative for field roofs.</summary>
        public static void SyncRoofAt(Map map, IntVec3 cell)
        {
            if (reconcilingRoof || map?.terrainGrid == null || map.roofGrid == null
                || !cell.InBounds(map) || FieldRoof == null) return;
            reconcilingRoof = true;
            try
            {
                RoofDef roof = map.roofGrid.RoofAt(cell);
                if (IsMoonPoolAt(map, cell))
                {
                    if (roof == null) map.roofGrid.SetRoof(cell, FieldRoof);
                    if (map.areaManager != null)
                    {
                        map.areaManager.BuildRoof[cell] = false;
                        if (map.roofGrid.RoofAt(cell) == FieldRoof)
                            map.areaManager.NoRoof[cell] = false;
                    }
                }
                else if (roof == FieldRoof)
                {
                    map.roofGrid.SetRoof(cell, null);
                }
            }
            finally { reconcilingRoof = false; }
        }

        public static void SyncRoofsOnLoad(Map map)
        {
            if (map?.terrainGrid == null || map.roofGrid == null) return;
            foreach (IntVec3 cell in CellRect.WholeMap(map).Cells)
                if (IsMoonPoolAt(map, cell) || IsFieldRoofAt(map, cell)) SyncRoofAt(map, cell);
        }
    }
}
