using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 自由飞行 bootstrap 辅助查询（2026-09-24 解耦重构：原「固定船 bootstrap 认领」
    /// 流程已并入 ShipFlightTransitionService.BeginFreeFlightFromConsole——驾驶台 spawn
    /// 时自动认领（CompDeltaVPilotConsole.ClaimOnSpawn），脱离走统一无引擎搬运）。
    /// 本文件只保留重力门禁查询。
    /// </summary>
    public static class FreeFlightBootstrap
    {
        /// <summary>图是否为无重力太空环境（有效空间层 tile = 战术自由飞行允许；地表恒否）。</summary>
        public static bool IsZeroGravity(Map map)
        {
            return map != null && map.Tile.Valid && map.Tile.LayerDef.isSpace;
        }
    }
}
