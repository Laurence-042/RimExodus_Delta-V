using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 自由飞行 bootstrap 辅助查询（2026-09-24 解耦重构：原「固定船 bootstrap 认领」
    /// 流程已并入驾驶台入口——驾驶台 spawn 时自动认领（CompDeltaVPilotConsole.
    /// ClaimOnSpawn），分离走出力 gate 统一无引擎搬运）。本文件只保留重力门禁查询。
    /// </summary>
    public static class FreeFlightBootstrap
    {
        /// <summary>
        /// 图是否为无重力太空环境：真实 tile 看 space 层（轨道 = 零重力、地表 = 重力）；
        /// 口袋图无 tile——Delta-V 宇宙内部图（飞船/POI 内部，结构外是 Space 真空）按
        /// 「是否属于宇宙容器」判定，其他 Mod 的地表口袋图（如 RimExodus 地面图）恒重力。
        /// 注意：口袋图 Tile 恒 Invalid，旧实现只查 Tile.Valid 会把口袋宿主图（对接进
        /// 别船内部后的脱离场景）全部误判为重力图（2026-09-24 修正）。
        /// </summary>
        public static bool IsZeroGravity(UniverseManager universe, Map map)
        {
            if (map == null) return false;
            if (map.Tile.Valid) return map.Tile.LayerDef.isSpace;
            return universe != null && universe.FrameOf(map) != null;
        }
    }
}
