using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 全项目唯一坐标变换入口（AGENTS 不变量 #8：所有跨层坐标转换必须收口到统一 Transform API，
    /// 调用方禁止手写散落的旋转 + 平移公式）。
    ///
    /// 坐标链（story.md「预期技术路线-坐标转换」）：
    ///   Map local (cell / Thing) → ship local (连续，原点 = 地图几何中心)
    ///   → universe (绝对) → focused 参考系 (相对) → 显示。
    ///
    /// 角度约定（阶段1，写入 doc/阶段1-双船原型.md，改动必须同步文档）：
    ///   2D 旋转公式 u.x = p.x·cosθ − p.z·sinθ，u.z = p.x·sinθ + p.z·cosθ
    ///   （把 (x,z) 当作平面直角坐标逆时针为正）。
    ///   它与 Unity 的对应关系：Quaternion.Euler(0, −θ·Rad2Deg, 0)。
    ///   ShellRenderer 的显示矩阵必须通过本类的 FocusedMeshRotation 取得，保证与 2D 公式一致。
    ///
    /// 本类保持无状态：所有方法显式传入 SpaceObject / Map，不读取全局当前聚焦状态。
    /// </summary>
    public static class SpaceTransform
    {
        // ---------- 2D 核心 ----------

        public static Vector2 Rotate(Vector2 v, float angle)
        {
            float s = Mathf.Sin(angle);
            float c = Mathf.Cos(angle);
            return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
        }

        public static Vector2 RotateInverse(Vector2 v, float angle)
        {
            return Rotate(v, -angle);
        }

        /// <summary>规范化到 (−π, π]。HUD 与漂移核对用。</summary>
        public static float WrapAngle(float angle)
        {
            float twoPi = 2f * Mathf.PI;
            angle %= twoPi;
            if (angle <= -Mathf.PI) angle += twoPi;
            if (angle > Mathf.PI) angle -= twoPi;
            return angle;
        }

        // ---------- Map local ⇄ ship local ----------
        // 阶段1 约定：ship local 原点 = 地图几何中心 (Size.x/2, Size.z/2)，连续坐标。

        public static Vector2 ShipLocalOrigin(Map map)
        {
            return new Vector2(map.Size.x * 0.5f, map.Size.z * 0.5f);
        }

        public static Vector2 MapToShipLocal(Map map, Vector2 mapPoint)
        {
            return mapPoint - ShipLocalOrigin(map);
        }

        public static Vector2 ShipLocalToMap(Map map, Vector2 shipPoint)
        {
            return shipPoint + ShipLocalOrigin(map);
        }

        /// <summary>
        /// 原版 Gravship/Prefab 的 Rot4 局部偏移旋转，连续坐标版本。
        /// 手性严格对应 PrefabUtility.GetAdjustedLocalPosition，固定轨道坐标只能经此入口换算。
        /// </summary>
        public static Vector2 RotatePlacementOffset(Vector2 local, Rot4 rotation)
        {
            if (rotation == Rot4.East) return new Vector2(local.y, -local.x);
            if (rotation == Rot4.South) return new Vector2(-local.x, -local.y);
            if (rotation == Rot4.West) return new Vector2(-local.y, local.x);
            return local;
        }

        // ---------- Ship local ⇄ universe ----------

        public static Vector2 ShipLocalToUniverse(SpaceObject obj, Vector2 shipPoint)
        {
            return obj.pose.position + Rotate(shipPoint, obj.pose.angle);
        }

        public static Vector2 UniverseToShipLocal(SpaceObject obj, Vector2 universePoint)
        {
            return RotateInverse(universePoint - obj.pose.position, obj.pose.angle);
        }

        /// <summary>
        /// universe → 目标船的**地图坐标**（含中心偏移）。
        /// 专为消灭"中心系/地图系"混用 bug 家族而设（2026-09-12 三次同族事故：
        /// FocusedDrawPosition / ShellPicking / 相机跳转都把 UniverseToShipLocal 的
        /// 中心系输出当地图坐标用）。消费方要落格子/落相机一律用本方法，
        /// 直接拿 UniverseToShipLocal 输出当地图坐标 = 反模式。
        /// </summary>
        public static Vector2 UniverseToMapPoint(SpaceObject obj, Vector2 universePoint)
        {
            return ShipLocalToMap(obj.map, UniverseToShipLocal(obj, universePoint));
        }

        // ---------- Universe ⇄ focused 参考系 ----------
        // DisplayTransform = inverse(FocusedObject.SpacePose)（AGENTS §6）。

        public static Vector2 UniverseToFocused(SpaceObject focused, Vector2 universePoint)
        {
            return RotateInverse(universePoint - focused.pose.position, focused.pose.angle);
        }

        public static Vector2 FocusedToUniverse(SpaceObject focused, Vector2 focusedPoint)
        {
            return focused.pose.position + Rotate(focusedPoint, focused.pose.angle);
        }

        public static float UniverseAngleToFocused(SpaceObject focused, float universeAngle)
        {
            return WrapAngle(universeAngle - focused.pose.angle);
        }

        // ---------- 显示层专用 ----------

        /// <summary>
        /// 非聚焦船外壳代理网格的显示旋转。proxy 网格顶点在 ship local（y = 0 平面），
        /// 传入该船的宇宙角度，得到聚焦参考系下的网格旋转四元数。
        /// 必须与 2D 公式成对使用，禁止在渲染代码里另行手写 Euler。
        /// </summary>
        public static Quaternion FocusedMeshRotation(SpaceObject focused, float universeAngle)
        {
            return Quaternion.Euler(0f, -UniverseAngleToFocused(focused, universeAngle) * Mathf.Rad2Deg, 0f);
        }

        /// <summary>
        /// 代理显示位置：universe 坐标 → 聚焦参考系 → 地图世界绘制坐标（y 为海拔）。
        /// focusedMap 是承载本次绘制的地图（聚焦船地图，或宿主图）；
        /// focused 帧坐标以聚焦对象中心为原点，而地图渲染中该中心位于地图几何中心，
        /// 所以必须再加上 ShipLocalOrigin——漏加会让所有代理整体偏移半张地图。
        /// </summary>
        public static Vector3 FocusedDrawPosition(SpaceObject focused, Map focusedMap, Vector2 universePoint, float altitude)
        {
            Vector2 rel = UniverseToFocused(focused, universePoint);
            Vector2 mapPoint = ShipLocalToMap(focusedMap, rel);
            return new Vector3(mapPoint.x, altitude, mapPoint.y);
        }
    }
}
