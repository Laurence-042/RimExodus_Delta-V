using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
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

        /// <summary>漂移同步只匹配容器线速度和网格方向，不建立位置锚，也不附加 ω×offset。</summary>
        internal static SpacePose MatchingDriftPose(Vector3 position, SpacePose host, float relativeAngle)
            => new SpacePose(position, WrapAngle(host.angle + relativeAngle))
            {
                velocity = TransportVelocity(host, position),
                angularVelocity = host.angularVelocity
            };

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

        // ---------- 停放锚点（原版放置语义 ⇄ 船体帧原点；2026-09-17 收口）----------
        // 两类锚，调用方不得自行组合换算（含 GetRoot / GetSizeRotAdjustedCell /
        // engineToCenter / 半跨度偏移的散落拼装），只允许经本区段入口：
        // - 原版放置锚：root 格（脚印角格）+ Rot4——marker / designator /
        //   PlaceGravshipInMap 的语义；
        // - 船体帧原点：口袋图几何中心（SpacePose.position 的锚；停靠后 = 落点处的
        //   该中心位置，见 ShipOriginFromPlacementRoot）。
        // 连续坐标一律格角空间（格 c 占 [c, c+1)）；需要格心处由调用方显式 +0.5。

        /// <summary>船以 rot 停靠到 root 后，其口袋图几何中心（SpacePose.position 锚）
        /// 在宿主图的连续坐标（格角空间）——dockedOrigin 的唯一来源。</summary>
        public static Vector2 ShipOriginFromPlacementRoot(Map pocketMap, IntVec3 pocketRoot, IntVec3 root, Rot4 rot)
        {
            Vector2 pocketCenterFromRoot = ShipLocalOrigin(pocketMap) - new Vector2(pocketRoot.x, pocketRoot.z);
            // Vanilla rotates integer cell anchors; continuous positions rotate about cell centers.
            Vector2 halfCell = new Vector2(0.5f, 0.5f);
            return new Vector2(root.x, root.z) + halfCell + RotatePlacementOffset(pocketCenterFromRoot - halfCell, rot);
        }

        internal static SpacePose PoseAfterPlacement(SpacePose sourcePose, Map sourceMap, IntVec3 sourceRoot,
            Map targetMap, IntVec3 targetRoot, Rot4 rotation)
        {
            Vector2 oldOriginInTarget = ShipOriginFromPlacementRoot(sourceMap, sourceRoot, targetRoot, rotation);
            Rot4 inverse = new Rot4((4 - rotation.AsInt) % 4);
            Vector2 newOriginInSource = RotatePlacementOffset(ShipLocalOrigin(targetMap) - oldOriginInTarget, inverse);
            float angle = -rotation.AsInt * Mathf.PI * 0.5f;
            return RigidPointPose(sourcePose, newOriginInSource, -angle);
        }

        /// <summary>引擎 root 格 → gravship 脚印包围盒中心的连续坐标（原版 engineToCenter
        /// 语义；停靠图坐标系、不旋转）。起飞侧的「未来口袋图几何中心」经此入口：
        /// ShipPocketMapFactory 把船居中放入新口袋图，该点即新口袋图几何中心——
        /// SpacePose.position 锚在 dock/undock 间的连续性来源。</summary>
        public static Vector2 GravshipBoundsCenterFromEngineRoot(Gravship gravship, IntVec3 engineRoot)
        {
            return new Vector2(engineRoot.x + gravship.engineToCenter.x,
                engineRoot.z + gravship.engineToCenter.z);
        }

        /// <summary>格子集合脚印包围盒中心的连续坐标（格角空间；bootstrap 等尚无
        /// Gravship 数据时的同族锚——连通域包围盒中心）。</summary>
        public static Vector2 BoundsCenterOfCells(IEnumerable<IntVec3> cells)
        {
            Vector3 center = CellRect.FromCellList(new List<IntVec3>(cells)).CenterVector3;
            return new Vector2(center.x, center.z);
        }

        // ---------- 速度换基（唯一入口） ----------

        /// <summary>速度从一个位姿的锚点基平行输运到目标锚点基（x=东、y=北）。
        /// 自动驾驶相对速度/宿主前馈、对接完成的宿主速度继承共用本入口，不得各写一份
        /// 基变换点乘（2026-09-21 去重收口，不变量 #21）。</summary>
        public static Vector2 TransportVelocity(SpacePose pose, Vector3 frameDirection)
            => TransportTangent(pose.velocity, pose.position, frameDirection);

        /// <summary>速度、冲量等切向量共用同一换基，调用方不得自行重建球面基。</summary>
        public static Vector2 TransportTangent(Vector2 vector, Vector3 sourceDirection, Vector3 frameDirection)
        {
            OrbitSphere.Basis(sourceDirection, out Vector3 east, out Vector3 north);
            Vector3 world = east * vector.x + north * vector.y;
            OrbitSphere.Basis(frameDirection, out east, out north);
            return new Vector2(Vector3.Dot(world, east), Vector3.Dot(world, north));
        }

        /// <summary>从质心到共同作用点的力臂，表达在该作用点的切空间。</summary>
        public static Vector2 MomentArmAt(SpacePose pose, Vector2 comLocal, Vector3 point)
        {
            Vector3 center = ShipLocalToUniverse(pose, comLocal);
            return -OrbitSphere.Offset(point, center);
        }

        /// <summary>
        /// 刚体上 ship-local 点在该点球面切基中的速度。先在对象锚点切基中合成锚点
        /// 平移与 ω×r，再统一平行输运到目标点；随动对接和未来硬点制导只能走本入口，
        /// 不得各自遗漏旋转目标偏置点的切向速度。
        /// </summary>
        public static Vector2 VelocityAtLocalPoint(SpacePose pose, Vector2 localPoint, Vector3 pointDirection)
        {
            return VelocityAtMovingLocalPoint(pose, localPoint, Vector2.zero, pointDirection);
        }

        /// <summary>随宿主移动且在宿主局部系内自行运动的目标点速度；战斗环绕与随动制导共用。</summary>
        public static Vector2 VelocityAtMovingLocalPoint(SpacePose pose, Vector2 localPoint,
            Vector2 localVelocity, Vector3 pointDirection)
        {
            Vector2 rotatedOffset = Rotate(localPoint, pose.angle);
            Vector2 anchorVelocity = pose.velocity + pose.angularVelocity
                * new Vector2(-rotatedOffset.y, rotatedOffset.x) + Rotate(localVelocity, pose.angle);
            var pointPose = new SpacePose
            {
                position = pose.position,
                velocity = anchorVelocity,
                angle = pose.angle,
                angularVelocity = pose.angularVelocity
            };
            return TransportVelocity(pointPose, pointDirection);
        }

        /// <summary>
        /// 宿主刚体上一个局部点的完整位姿。已停靠船同步、脱离停靠与未来硬点载具
        /// 只允许经此入口合成位置、v+ω×r、朝向和角速度。
        /// </summary>
        public static SpacePose RigidPointPose(SpacePose hostPose, Vector2 hostLocalPoint,
            float localAngle)
        {
            OrbitSphere.Direction direction = OrbitSphere.Move(hostPose.direction, Rotate(hostLocalPoint, hostPose.angle));
            return new SpacePose
            {
                direction = direction,
                velocity = VelocityAtLocalPoint(hostPose, hostLocalPoint, direction.Vector),
                angle = WrapAngle(hostPose.angle + localAngle),
                angularVelocity = hostPose.angularVelocity
            };
        }

        /// <summary>已知双方局部对齐点与相对方向，反求本船容器帧的唯一目标位姿。</summary>
        internal static SpacePose AlignLocalPoint(SpacePose host, Vector2 hostPoint, Vector2 ownPoint, float relativeAngle)
        {
            SpacePose target = RigidPointPose(host, hostPoint, 0f);
            SpacePose own = RigidPointPose(host, hostPoint - Rotate(ownPoint, relativeAngle), relativeAngle);
            // The planar inverse is only a starting estimate: the two origins have
            // different sphere tangent bases. Correct against the actual displayed point.
            for (int i = 0; i < 3; i++)
            {
                OrbitSphere.Direction point = OrbitSphere.Move(own.direction, Rotate(ownPoint, own.angle));
                Vector2 error = OrbitSphere.Offset(point, target.direction);
                if (error.sqrMagnitude < .000001f) break;
                own.direction = OrbitSphere.Move(own.direction, TransportTangent(error, point.Vector, own.position));
            }
            own.velocity = Vector2.zero;
            own.velocity = TransportVelocity(target, own.position) - VelocityAtLocalPoint(own, ownPoint, own.position);
            return own;
        }

        /// <summary>
        /// 两容器的相对速度（太空战架构 §3.3：v_攻 − v_守，表达在守方位置切空间基；
        /// 碰撞事件的相对速度标量/矢量唯一入口，调用方禁止自行做基变换点乘）。
        /// 内部复用 TransportVelocity 的平行输运。
        /// </summary>
        public static Vector2 RelativeVelocity(SpacePose attackerPose, SpacePose defenderPose)
        {
            return TransportVelocity(attackerPose, defenderPose.position) - defenderPose.velocity;
        }

        /// <summary>
        /// 入射角（弧度，0 = 正对被撞面法线，π/2 = 掠射）：入射方向（守方局部、单位、
        /// 指向行进方向）与被撞面法线（守方局部、单位、指向入射来侧）的夹角。
        /// 法线由调用方按命中面语义给出（碰撞核心按扫掠进入面推导；基架格按同规则
        /// 的格面法线抽象——与实心建筑同一规则，不另设第二套角度约定）。
        /// </summary>
        public static float IncidenceAngle(Vector2 incidentDir, Vector2 surfaceNormal)
        {
            float cos = Vector2.Dot(incidentDir.normalized, surfaceNormal.normalized);
            return Mathf.Acos(Mathf.Clamp(-cos, -1f, 1f));
        }

        /// <summary>
        /// 截击解算（切平面近似，预瞄指示器与 PD 提前量同一纯函数，太空战架构 §6.3）：
        /// 相对位置 p（发射点切基下目标 − 发射点）、相对漂移 w（目标速度 − 发射点速度，
        /// 同基），求最小正根 t 使 |p + w·t| = speed·t。无解（追不上/同速绕圈）返回 false。
        /// 战术距离内球面曲率可忽略；跨层换算仍由调用方经 OrbitSphere/TransportVelocity。
        /// </summary>
        public static bool TrySolveInterceptPlanar(Vector2 p, Vector2 w, float speed, out float time)
        {
            time = 0f;
            if (speed <= 0f) return false;
            float a = Vector2.Dot(w, w) - speed * speed;
            float b = 2f * Vector2.Dot(p, w);
            float c = Vector2.Dot(p, p);
            if (Mathf.Abs(a) < 1e-6f)
            {
                if (Mathf.Abs(b) < 1e-6f) return false;
                float linear = -c / b;
                if (linear > 0f)
                {
                    time = linear;
                    return true;
                }
                return false;
            }
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f) return false;
            float root = Mathf.Sqrt(discriminant);
            float t1 = (-b - root) / (2f * a);
            float t2 = (-b + root) / (2f * a);
            // 取最小正根（最早截击点）。
            if (t1 > 0f && (t2 <= 0f || t1 <= t2)) time = t1;
            else if (t2 > 0f) time = t2;
            else return false;
            return time > 0f && float.IsFinite(time);
        }

        // ---------- Ship local ⇄ universe ----------

        public static Vector3 ShipLocalToUniverse(SpaceObject obj, Vector2 shipPoint)
        {
            return ShipLocalToUniverse(obj.pose, shipPoint);
        }

        public static Vector3 ShipLocalToUniverse(SpacePose pose, Vector2 shipPoint)
        {
            return OrbitSphere.Move(pose.direction, Rotate(shipPoint, pose.angle)).Vector;
        }

        /// <summary>所属对象内部图的连续地图点 → 唯一球面轨道位置。</summary>
        public static Vector3 MapPointToUniverse(SpaceObject owner, Map map, Vector2 mapPoint)
        {
            return ShipLocalToUniverse(owner, MapToShipLocal(map, mapPoint));
        }

        /// <summary>地图帧方向在指定地图点换算为该 universe 点的切空间方向。</summary>
        public static Vector2 MapDirectionToUniverseTangent(SpaceObject owner, Map map,
            Vector2 mapPoint, Vector2 mapDirection)
        {
            if (mapDirection.sqrMagnitude < 1e-8f) return Vector2.zero;
            Vector3 from = MapPointToUniverse(owner, map, mapPoint);
            Vector3 to = MapPointToUniverse(owner, map, mapPoint + mapDirection.normalized);
            Vector2 tangent = OrbitSphere.Offset(from, to);
            return tangent.sqrMagnitude > 1e-8f ? tangent.normalized : Vector2.zero;
        }

        public static Vector2 UniverseToShipLocal(SpaceObject obj, Vector3 universeDir)
        {
            return UniverseToShipLocal(obj.pose, universeDir);
        }

        public static Vector2 UniverseToShipLocal(SpacePose pose, Vector3 universeDir)
        {
            return RotateInverse(OrbitSphere.Offset(pose.position, universeDir), pose.angle);
        }

        /// <summary>
        /// universe → 所属对象内部图的**地图坐标**（含中心偏移）。
        /// 专为消灭"中心系/地图系"混用 bug 家族而设（2026-09-12 三次同族事故：
        /// FocusedDrawPosition / ShellPicking / 相机跳转都把 UniverseToShipLocal 的
        /// 中心系输出当地图坐标用）。消费方要落格子/落相机一律用本方法，
        /// 直接拿 UniverseToShipLocal 输出当地图坐标 = 反模式。
        /// </summary>
        /// <summary>球面轨道位置 → 所属对象内部图的连续地图点。</summary>
        public static Vector2 UniverseToMapPoint(SpaceObject owner, Map map, Vector3 universeDir)
        {
            return ShipLocalToMap(map, UniverseToShipLocal(owner, universeDir));
        }

        // ---------- Universe ⇄ focused 参考系 ----------
        // DisplayTransform = inverse(FocusedObject.SpacePose)（AGENTS §6）。

        public static Vector2 UniverseToFocused(SpaceObject focused, Vector3 universeDir)
        {
            return UniverseToShipLocal(focused, universeDir);
        }

        public static Vector3 FocusedToUniverse(SpaceObject focused, Vector2 focusedPoint)
        {
            return ShipLocalToUniverse(focused, focusedPoint);
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
        /// 轴向约定（前向 = 局部 +X）：地图几何壳、世界剪影（UniverseWorldMarkerRotation/
        /// MarkerForwardWorld 同链）与 X 向拉伸的四边形（激光束）。
        /// </summary>
        public static Quaternion FocusedMeshRotation(SpaceObject focused, float universeAngle)
        {
            return Quaternion.Euler(0f, -UniverseAngleToFocused(focused, universeAngle) * Mathf.Rad2Deg, 0f);
        }

        /// <summary>
        /// 「UV 上方 = 图案朝向」贴图的显示旋转（唯一入口，2026-09-27 弹头 90° 事故后
        /// 收敛）：原版 Projectile/Graphic 平面贴图的图案朝局部 +Z（MeshMakerPlanes
        /// 的 V 轴沿 +Z；原版弹体以 LookRotation 把 +Z 对准飞行方向），与
        /// <see cref="FocusedMeshRotation"/> 的 +X 前向约定不同源。一切「带朝向的原版
        /// 贴图」渲染必须经此入口，禁止调用点手写 π/2 轴向修正。
        /// </summary>
        public static Quaternion FocusedSpriteRotation(SpaceObject focused, float universeAngle)
        {
            return FocusedMeshRotation(focused, universeAngle - Mathf.PI / 2f);
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
            Vector2 mapPoint = UniverseToMapPoint(focused, focusedMap, universeDir);
            return new Vector3(mapPoint.x, altitude, mapPoint.y);
        }
    }
}
