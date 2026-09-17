using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 外壳命中测试：聚焦帧地图坐标 → 宇宙 → 各对象局部坐标 → 船脚印（非 Void 格）命中判定。
    ///
    /// 这是外壳点击的**唯一命中入口**，选择点击与未来的武器瞄准（阶段5）共用：
    /// - 选择路由（UniverseManager.HandleShellClick）在瞄准会话（Targeter 激活）或
    ///   建造设计器选中时完全让位——那类点击属于瞄准/建造流程，不进入选择路由；
    /// - 瞄准层届时在自己的 Targeter 流程内调用本类解析被点的对象，不经选择路由。
    /// "选取点击 vs 瞄准点击"的分界 = 是否处于瞄准会话，从第一版起即成立（勿回退）。
    ///
    /// 命中精度说明：本测试是**选择级**（脚印格子命中，terrain != DeltaV_Void）；
    /// 武器碰撞将来需要**碰撞级**几何（AGENTS §14：渲染外壳/碰撞体/建筑格三者不等价），
    /// 届时另行实现，不复用本判定。
    /// </summary>
    public static class ShellPicking
    {
        public static SpaceObject ShellAtFramePoint(UniverseManager universe, Map frameMap, Vector3 frameMapPoint)
        {
            SpaceObject frame = universe.FrameOf(frameMap);
            if (frame == null)
            {
                return null;
            }
            Vector2 frameLocal = SpaceTransform.MapToShipLocal(frameMap, new Vector2(frameMapPoint.x, frameMapPoint.z));
            Vector3 universeDir = SpaceTransform.FocusedToUniverse(frame, frameLocal);
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == frame)
                {
                    continue;
                }
                ShipState ship = obj as ShipState;
                if (ship != null && ship.docking)
                {
                    continue; // 选点船由原版 marker 处理
                }
                bool docked = ship != null && ship.docked;
                Map objectMap = docked ? ship.dockedMap : obj.map;
                if (objectMap == null || (docked && objectMap == frameMap)) continue;
                // UniverseToMapPoint：universe → 地图坐标（含中心偏移）——
                // 直接拿 UniverseToShipLocal 的中心系输出当地图坐标是已记录三次的反模式。
                Vector2 mapPoint = docked
                    ? SpaceTransform.UniverseToDockedMapPoint(universe, ship, universeDir)
                    : SpaceTransform.UniverseToMapPoint(obj, universeDir);
                var cell = new IntVec3(Mathf.FloorToInt(mapPoint.x), 0, Mathf.FloorToInt(mapPoint.y));
                if (!cell.InBounds(objectMap))
                {
                    continue;
                }
                if (docked)
                {
                    Building_GravEngine engine = DockController.FindEngineForShip(objectMap, ship.primaryConsole);
                    if (engine != null && engine.AllConnectedSubstructure.Contains(cell)) return obj;
                }
                else if (MapOverlayRenderer.ContainsVisibleContent(objectMap, cell,
                    obj is PoiState ? ShellRenderer.PoiConfig : ShellRenderer.ShipConfig))
                {
                    return obj;
                }
            }
            return null;
        }
    }
}
