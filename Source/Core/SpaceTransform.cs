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
    ///   → universe (球面方向，OrbitSphere) → focused 参考系 (相对) → 显示。
    ///
    /// universe 位置的 API 形态是 Vector3 单位方向（球面绝对坐标）：
    /// - "偏移"一律以切空间格数（x = 东、y = 北）表达，球面移动/投影全部委托 OrbitSphere；
    /// - 角度约定沿用阶段1（写入 doc/阶段1-双船原型.md，改动必须同步文档）：
    ///   2D 旋转公式 u.x = p.x·cosθ − p.z·sinθ，u.z = p.x·sinθ + p.z·cosθ；
    ///   与 Unity 的对应关系 Quaternion.Euler(0, −θ·Rad2Deg, 0)。
    ///   ShellRenderer 的显示矩阵必须通过本类的 FocusedMeshRotation 取得，保证一致。
    ///
    /// 本类保持无状态：所有方法显式传入 SpaceObject / Map / 方向，不读取全局当前聚焦状态。
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

        public static Vector3 ShipLocalToUniverse(SpaceObject obj, Vector2 shipPoint)
        {
            return ShipLocalToUniverse(obj.pose, shipPoint);
        }

        public static Vector3 ShipLocalToUniverse(SpacePose pose, Vector2 shipPoint)
        {
            return OrbitSphere.Move(pose.position, Rotate(shipPoint, pose.angle));
        }

        public static Vector2 UniverseToShipLocal(SpaceObject obj, Vector3 universeDir)
        {
            return RotateInverse(OrbitSphere.Offset(obj.pose.position, universeDir), obj.pose.angle);
        }

        /// <summary>
        /// universe → 目标船的**地图坐标**（含中心偏移）。
        /// 专为消灭"中心系/地图系"混用 bug 家族而设（2026-09-12 三次同族事故：
        /// FocusedDrawPosition / ShellPicking / 相机跳转都把 UniverseToShipLocal 的
        /// 中心系输出当地图坐标用）。消费方要落格子/落相机一律用本方法，
        /// 直接拿 UniverseToShipLocal 输出当地图坐标 = 反模式。
        /// </summary>
        public static Vector2 UniverseToMapPoint(SpaceObject obj, Vector3 universeDir)
        {
            return ShipLocalToMap(obj.map, UniverseToShipLocal(obj, universeDir));
        }

        /// <summary>
        /// universe → 停靠船在停靠图（宿主内部）上的地图坐标。宿主对象承载帧
        /// （星区架构 §4 坐标桥）：宿主位姿 ⊕ 内部局部坐标的逆变换，对任意角度的
        /// 宿主（worldtile 画布 / POI 口袋内部）一致；宿主尚未纳管的瞬态回退到
        /// 「以 dockedOrigin 为原点的纯平移」（worldtile 帧角度恒 0 的旧特例）。
        /// </summary>
        public static Vector2 UniverseToDockedMapPoint(UniverseManager universe, ShipState ship, Vector3 universeDir)
        {
            SpaceObject host = universe?.FrameOf(ship.dockedMap);
            if (host != null && host.map == ship.dockedMap)
            {
                return UniverseToMapPoint(host, universeDir);
            }
            return DockController.DockedOriginMapPoint(ship) + OrbitSphere.Offset(ship.pose.position, universeDir);
        }

        // ---------- Universe ⇄ focused 参考系 ----------
        // DisplayTransform = inverse(FocusedObject.SpacePose)（AGENTS §6）。

        public static Vector2 UniverseToFocused(SpaceObject focused, Vector3 universeDir)
        {
            return RotateInverse(OrbitSphere.Offset(focused.pose.position, universeDir), focused.pose.angle);
        }

        public static Vector3 FocusedToUniverse(SpaceObject focused, Vector2 focusedPoint)
        {
            return OrbitSphere.Move(focused.pose.position, Rotate(focusedPoint, focused.pose.angle));
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
        /// 宇宙绝对姿态 → 星球视图球面切平面旋转。局部 X/Z 分别对齐当前位置的
        /// 东/北基，再叠加与 Map 外壳一致的 SpacePose.angle 手性。世界标记只能
        /// 消费该结果，不能从 tile 朝向或屏幕旋转反推宇宙姿态。
        /// </summary>
        public static Quaternion UniverseWorldMarkerRotation(Vector3 universeDir, float universeAngle)
        {
            Vector3 radial = universeDir.normalized;
            OrbitSphere.Basis(radial, out Vector3 east, out Vector3 north);
            Quaternion tangentFrame = Quaternion.LookRotation(north, radial);
            return tangentFrame * Quaternion.Euler(0, -universeAngle * Mathf.Rad2Deg, 0);
        }

        /// <summary>
        /// 船体 forward（局部 +X）的世界方向：与世界视图剪影（UniverseWorldMarkerRotation →
        /// plane10 局部 X 轴）同一条旋转链导出。星球仪剪影等一切需要“船头朝哪”的显示
        /// 必须经此入口，不得各写一套 east·cosθ+north·sinθ（2026-09-17：星球仪自算角度
        /// 与世界剪影分叉 45° 的事故后统一）。
        /// </summary>
        public static Vector3 MarkerForwardWorld(Vector3 universeDir, float universeAngle)
        {
            return UniverseWorldMarkerRotation(universeDir, universeAngle) * Vector3.right;
        }

        /// <summary>
        /// 代理显示位置：universe 方向 → 聚焦参考系 → 地图世界绘制坐标（y 为海拔）。
        /// focusedMap 是承载本次绘制的地图（聚焦对象地图，或承载帧的真实空间图）；
        /// focused 帧坐标以聚焦对象中心为原点，而地图渲染中该中心位于地图几何中心，
        /// 所以必须再加上 ShipLocalOrigin——漏加会让所有代理整体偏移半张地图。
        /// </summary>
        public static Vector3 FocusedDrawPosition(SpaceObject focused, Map focusedMap, Vector3 universeDir, float altitude)
        {
            Vector2 rel = UniverseToFocused(focused, universeDir);
            Vector2 mapPoint = ShipLocalToMap(focusedMap, rel);
            return new Vector3(mapPoint.x, altitude, mapPoint.y);
        }
    }
}
