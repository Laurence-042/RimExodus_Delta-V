using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 左侧面板星球仪（星区架构 §11.1）：带南北轴与经纬网格的星球线框（背侧网格不显示），
    /// 摄像机沿 飞船—球心 连线设置位置与角度、永远北轴向上；玩家飞船渲染为底边内凹
    /// 三角（类似鼠标光标，标注当前位置与朝向），并以箭头显示当前速度矢量。
    ///
    /// 数据同源 SpacePose / OrbitSphere.Basis（世界视图剪影同一事实源），不引入第二套
    /// 坐标：飞船状态无欧拉锁（位置是球面方向、移动是 Rodrigues 旋转），只有「北轴向上」
    /// 的显示 roll 在极点附近不适定——保持上次稳定 roll 作 fallback（缓存上帧屏幕基）。
    /// 显示层只是投影，不是宇宙状态的事实源。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PlanetGlobeRenderer
    {
        private static readonly Material lineMaterial;

        static PlanetGlobeRenderer()
        {
            // 材质必须在主线程创建（StaticConstructorOnStartup 时机 = play data 载入后）。
            lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
        }

        /// <summary>极点退化时的显示 roll fallback：上一次稳定的屏幕基。</summary>
        private static Vector3 stableUp = Vector3.up;

        private static Vector3 stableRight = Vector3.right;

        /// <summary>正面判定阈值：掠过球缘的线段一并隐藏，避免边缘锯齿。</summary>
        private const float FrontEpsilon = 0.04f;

        public static void Draw(Rect rect, UniverseManager universe, SpaceObject contextFrame)
        {
            if (universe == null || !OrbitSphere.Valid) return;
            ShipState reference = universe.Focused ?? universe.PrimaryShip;
            // 首次进入太空（尚未 bootstrap 出任何 ShipState）时以当前图帧位置为相机锚点：
            // 球心—当前所在位置连线，仍是"飞船（所在处）—球心"语义的合理退化。
            Vector3 referenceDir = reference != null
                ? reference.pose.position
                : contextFrame?.pose.position ?? Vector3.up;
            if (referenceDir.sqrMagnitude < 1e-6f) return;

            Vector2 center = rect.center;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f - 4f;

            // 视图基：相机位于飞船—球心连线的船侧、指向球心，屏幕向上 = 世界 up 的投影。
            Vector3 shipDir = referenceDir.normalized;
            Vector3 forward = -shipDir;
            Vector3 up = Vector3.up - forward * Vector3.Dot(Vector3.up, forward);
            if (up.sqrMagnitude < 1e-4f)
            {
                up = stableUp; // 极点：roll 不适定，保持上次稳定 roll
            }
            else
            {
                up.Normalize();
                stableUp = up;
            }
            Vector3 right = Vector3.Cross(up, forward);
            if (right.sqrMagnitude < 1e-4f)
            {
                right = stableRight;
            }
            else
            {
                right.Normalize();
                stableRight = right;
            }

            lineMaterial.SetPass(0);
            GL.PushMatrix();
            // GUI 空间是左上原点、y 向下；无参 LoadPixelMatrix 是左下原点、y 向上，
            // 直接用会把整幅 GL 绘制垂直镜像到面板外的镜像位置。显式翻转底/顶对齐 GUI 坐标。
            GL.LoadPixelMatrix(0f, UI.screenWidth, UI.screenHeight, 0f);

            // 世界方向 → GUI 屏幕偏移：垂直分量取负（GUI y 向下，"上"是 -y）。
            Vector2 Project(Vector3 dir)
            {
                return center + new Vector2(Vector3.Dot(dir, right), -Vector3.Dot(dir, up)) * radius;
            }

            // ---- 经纬网格（每 30° 一条；背面线段剔除）----
            GL.Begin(GL.LINES);
            GL.Color(new Color(1f, 1f, 1f, 0.28f));
            for (int lat = -60; lat <= 60; lat += 30)
            {
                Vector3 prev = Spherical(lat, 0f);
                for (int lon = 6; lon <= 360; lon += 6)
                {
                    Vector3 next = Spherical(lat, lon);
                    if (Vector3.Dot((prev + next).normalized, shipDir) > FrontEpsilon)
                    {
                        Vertex(Project(prev));
                        Vertex(Project(next));
                    }
                    prev = next;
                }
            }
            for (int lon = 0; lon < 360; lon += 30)
            {
                Vector3 prev2 = Spherical(-84f, lon);
                for (int lat = -78; lat <= 84; lat += 6)
                {
                    Vector3 next = Spherical(lat, lon);
                    if (Vector3.Dot((prev2 + next).normalized, shipDir) > FrontEpsilon)
                    {
                        Vertex(Project(prev2));
                        Vertex(Project(next));
                    }
                    prev2 = next;
                }
            }

            // ---- 南北轴：极点向外延长的可见段 ----
            GL.Color(new Color(0.7f, 0.9f, 1f, 0.75f));
            if (Vector3.Dot(Vector3.up, shipDir) > -0.2f)
            {
                Vertex(center);
                Vertex(Project(Vector3.up * 1.18f));
            }
            if (Vector3.Dot(-Vector3.up, shipDir) > -0.2f)
            {
                Vertex(center);
                Vertex(Project(-Vector3.up * 1.18f));
            }
            GL.End();

            // ---- 玩家飞船标记（底边内凹三角；聚焦船带朝向与速度矢量）----
            foreach (ShipState ship in universe.Ships)
            {
                if (!ship.isPlayerShip || ship.docking) continue;
                bool focused = ReferenceEquals(ship, reference);
                Vector2 pos = Project(ship.pose.position);
                bool front = Vector3.Dot(ship.pose.position, shipDir) > 0f;
                float alpha = front ? 1f : 0.35f;
                Color color = focused
                    ? new Color(1f, 0.95f, 0.4f, alpha)
                    : new Color(1f, 1f, 1f, alpha * 0.8f);
                if (ship.docked)
                {
                    DrawDot(pos, 3f, new Color(1f, 0.95f, 0.4f, front ? 0.8f : 0.25f));
                    continue;
                }
                // 朝向：切空间 (cosθ, sinθ)（东/北）→ 世界向量 → 屏幕基投影。
                OrbitSphere.Basis(ship.pose.position, out Vector3 east, out Vector3 north);
                Vector2 heading = ScreenDir(east, north, ship.pose.angle, right, up);
                DrawCursorTriangle(pos, focused ? 9f : 6.5f, heading, color);
                if (ship.pose.velocity.sqrMagnitude > 0.01f)
                {
                    Vector2 velocity = ScreenDir(east, north,
                        Mathf.Atan2(ship.pose.velocity.y, ship.pose.velocity.x), right, up);
                    DrawArrow(pos + heading * 12f, velocity, focused ? 26f : 14f,
                        new Color(0.5f, 1f, 0.6f, alpha));
                }
            }

            GL.PopMatrix();

            // 北极标注（GUI 文本，GL 块之后绘制）。
            if (Vector3.Dot(Vector3.up, shipDir) > -0.2f)
            {
                Vector2 n = Project(Vector3.up * 1.24f);
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.9f, 1f, 0.9f);
                Widgets.Label(new Rect(n.x - 10f, n.y - 8f, 20f, 16f), "N");
                GUI.color = Color.white;
            }
        }

        private static Vector2 ScreenDir(Vector3 east, Vector3 north, float angle, Vector3 right, Vector3 up)
        {
            Vector2 tangent = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector3 world = east * tangent.x + north * tangent.y;
            return new Vector2(Vector3.Dot(world, right), -Vector3.Dot(world, up)).normalized;
        }

        private static Vector3 Spherical(float latDeg, float lonDeg)
        {
            float lat = latDeg * Mathf.Deg2Rad;
            float lon = lonDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));
        }

        private static void Vertex(Vector2 p)
        {
            GL.Vertex3(p.x, p.y, 0f);
        }

        /// <summary>底边内凹三角（类似鼠标光标）：尖朝 heading 方向。</summary>
        private static void DrawCursorTriangle(Vector2 c, float size, Vector2 heading, Color color)
        {
            Vector2 dir = heading.sqrMagnitude < 1e-6f ? Vector2.up : heading.normalized;
            Vector2 side = new Vector2(-dir.y, dir.x);
            Vector2 tip = c + dir * size;
            Vector2 baseRight = c - dir * (size * 0.62f) + side * (size * 0.72f);
            Vector2 baseLeft = c - dir * (size * 0.62f) - side * (size * 0.72f);
            Vector2 notch = c - dir * (size * 0.18f);
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            Vertex(tip);
            Vertex(baseRight);
            Vertex(notch);
            Vertex(tip);
            Vertex(notch);
            Vertex(baseLeft);
            GL.End();
        }

        private static void DrawDot(Vector2 c, float radiusPx, Color color)
        {
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            const int segments = 8;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i / (float)segments * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)segments * Mathf.PI * 2f;
                Vertex(c);
                Vertex(c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radiusPx);
                Vertex(c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radiusPx);
            }
            GL.End();
        }

        private static void DrawArrow(Vector2 from, Vector2 dir, float length, Color color)
        {
            if (dir.sqrMagnitude < 1e-6f) return;
            dir.Normalize();
            Vector2 tip = from + dir * length;
            Vector2 side = new Vector2(-dir.y, dir.x) * 5f;
            GL.Begin(GL.LINES);
            GL.Color(color);
            Vertex(from);
            Vertex(tip);
            Vertex(tip);
            Vertex(tip - dir * 6f + side);
            Vertex(tip);
            Vertex(tip - dir * 6f - side);
            GL.End();
        }
    }
}
