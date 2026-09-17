using System.Collections.Generic;
using UnityEngine;

namespace DeltaV
{
    /// <summary>
    /// 2D 平面几何工具：凸包（Andrew 单调链）与凸包间最近距离。供降落目标选择的
    /// 「可见船壳最近距离」判定使用——两组格子集合各自取凸包后求距离，
    /// 相交记 0，否则取双向「顶点到对边」的最小值（分离凸包的正确最近距离）。
    /// </summary>
    public static class PlanarGeometry
    {
        /// <summary>点集凸包（逆时针）；去重后少于 3 点时原样返回共线/单点结果。</summary>
        public static List<Vector2> ConvexHull(List<Vector2> points)
        {
            var sorted = new List<Vector2>(points);
            sorted.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            int n = sorted.Count;
            var hull = new List<Vector2>(Mathf.Min(n * 2, 256));
            for (int i = 0; i < n; i++)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], sorted[i]) <= 0f)
                {
                    hull.RemoveAt(hull.Count - 1);
                }
                hull.Add(sorted[i]);
            }
            int lower = hull.Count + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], sorted[i]) <= 0f)
                {
                    hull.RemoveAt(hull.Count - 1);
                }
                hull.Add(sorted[i]);
            }
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        /// <summary>两凸包最近距离：任一为空返回无穷大；相交（含包含）记 0。</summary>
        public static float HullDistance(List<Vector2> hullA, List<Vector2> hullB)
        {
            if (hullA == null || hullB == null || hullA.Count == 0 || hullB.Count == 0)
            {
                return float.MaxValue;
            }
            if (HullsOverlap(hullA, hullB)) return 0f;
            float distance = float.MaxValue;
            for (int i = 0; i < hullA.Count; i++)
            {
                distance = Mathf.Min(distance, PointToHullDistance(hullA[i], hullB));
            }
            for (int i = 0; i < hullB.Count; i++)
            {
                distance = Mathf.Min(distance, PointToHullDistance(hullB[i], hullA));
            }
            return distance;
        }

        private static bool HullsOverlap(List<Vector2> a, List<Vector2> b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (PointInHull(a[i], b)) return true;
            }
            for (int i = 0; i < b.Count; i++)
            {
                if (PointInHull(b[i], a)) return true;
            }
            // 无互相包含的相交必伴随边相交。
            for (int i = 0; i < a.Count; i++)
            {
                Vector2 a1 = a[i];
                Vector2 a2 = a[(i + 1) % a.Count];
                for (int j = 0; j < b.Count; j++)
                {
                    Vector2 b1 = b[j];
                    Vector2 b2 = b[(j + 1) % b.Count];
                    if (SegmentsIntersect(a1, a2, b1, b2)) return true;
                }
            }
            return false;
        }

        private static bool PointInHull(Vector2 p, List<Vector2> hull)
        {
            for (int i = 0; i < hull.Count; i++)
            {
                if (Cross(hull[i], hull[(i + 1) % hull.Count], p) < 0f) return false;
            }
            return true;
        }

        private static float PointToHullDistance(Vector2 p, List<Vector2> hull)
        {
            float distance = float.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                distance = Mathf.Min(distance, PointToSegmentDistance(p, hull[i], hull[(i + 1) % hull.Count]));
            }
            return distance;
        }

        private static float PointToSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float sqrLength = ab.sqrMagnitude;
            float t = sqrLength < 1e-10f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / sqrLength);
            return Vector2.Distance(p, a + ab * t);
        }

        private static bool SegmentsIntersect(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
        {
            return Cross(a1, a2, b1) * Cross(a1, a2, b2) < 0f
                && Cross(b1, b2, a1) * Cross(b1, b2, a2) < 0f;
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }
    }
}
