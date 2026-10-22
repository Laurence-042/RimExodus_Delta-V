using System.Collections.Generic;
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
        private static readonly List<SpaceObject> PickOrderScratch = new List<SpaceObject>();

        public static SpaceObject ShellAtFramePoint(UniverseManager universe, Map frameMap, Vector3 frameMapPoint)
        {
            SpaceObject frame = universe.FrameOf(frameMap);
            if (frame == null)
            {
                return null;
            }
            Vector3 universeDir = SpaceTransform.MapPointToUniverse(frame, frameMap,
                new Vector2(frameMapPoint.x, frameMapPoint.z));
            // 与 ShellRenderer 绘制同一排序倒序遍历：整壳分层后 ID 大者在视觉最上，
            // 重叠处优先命中玩家看得见的壳；任务状态不改变拾取顺序。
            ShellRenderer.OrderShellsById(universe, frame, PickOrderScratch);
            for (int idx = PickOrderScratch.Count - 1; idx >= 0; idx--)
            {
                SpaceObject obj = PickOrderScratch[idx];
                // 每容器一壳（解耦重构：容器图即整壳；多船图不再按船分壳）。
                Map objectMap = obj.map;
                if (objectMap == null || objectMap == frameMap) continue;
                // UniverseToMapPoint：universe → 地图坐标（含中心偏移）——
                // 直接拿 UniverseToShipLocal 的中心系输出当地图坐标是已记录三次的反模式。
                SpaceObject objectFrame = universe.FrameOf(objectMap);
                if (objectFrame == null) continue;
                Vector2 mapPoint = SpaceTransform.UniverseToMapPoint(objectFrame, objectMap, universeDir);
                var cell = new IntVec3(Mathf.FloorToInt(mapPoint.x), 0, Mathf.FloorToInt(mapPoint.y));
                if (!cell.InBounds(objectMap))
                {
                    continue;
                }
                if (MapOverlayRenderer.ContainsVisibleContent(objectMap, cell))
                {
                    return obj;
                }
            }
            return null;
        }
    }
}
