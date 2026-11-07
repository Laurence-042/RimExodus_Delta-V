using System;
using System.Collections.Generic;

namespace DeltaV
{
    // Clip native textured quads into disjoint power cells. This is display geometry only.
    internal static class GuardShieldOutline
    {
        internal struct Circle : IEquatable<Circle>
        {
            public double x, z, radius;
            public int id;
            public bool Equals(Circle b) => x == b.x && z == b.z && radius == b.radius && id == b.id;
        }
        internal struct Point
        {
            public double x, z;
            public Point(double x, double z) { this.x = x; this.z = z; }
        }
        // CompProjectileInterceptor.PostDraw's native texture-to-collision-radius ratio.
        internal const double TextureScale = 1.1601562;

        internal static List<Point> Clip(Circle self, IReadOnlyList<Circle> others)
        {
            double extent = self.radius * TextureScale;
            var visible = new List<Point> { new Point(-extent, -extent), new Point(-extent, extent),
                new Point(extent, extent), new Point(extent, -extent) };
            var scratch = new List<Point>();
            foreach (Circle other in others)
            {
                if (other.id == self.id) continue;
                double dx = other.x - self.x, dz = other.z - self.z;
                double distance = Math.Sqrt(dx * dx + dz * dz);
                if (distance < 1e-9 && Math.Abs(self.radius - other.radius) < 1e-9)
                {
                    if (other.id < self.id) { visible.Clear(); break; }
                    continue;
                }
                if (distance + self.radius <= other.radius) { visible.Clear(); break; }
                if (distance + other.radius <= self.radius) continue;
                // Entire native texture squares are disjoint: do not let an unrelated radius trim this glow.
                double reach = (self.radius + other.radius) * TextureScale;
                if (Math.Abs(dx) >= reach || Math.Abs(dz) >= reach) continue;
                // |p-ci|²-ri² <= |p-cj|²-rj². On the circle itself this removes exactly
                // the covered arc; equal-radius textures also have identical UV radius at the seam.
                double limit = (dx * dx + dz * dz + self.radius * self.radius - other.radius * other.radius) * 0.5;
                scratch.Clear();
                Point previous = visible[visible.Count - 1];
                double before = previous.x * dx + previous.z * dz - limit;
                foreach (Point current in visible)
                {
                    double after = current.x * dx + current.z * dz - limit;
                    if ((before <= 0) != (after <= 0))
                    {
                        double t = before / (before - after);
                        scratch.Add(new Point(previous.x + (current.x - previous.x) * t,
                            previous.z + (current.z - previous.z) * t));
                    }
                    if (after <= 0) scratch.Add(current);
                    previous = current; before = after;
                }
                var swap = visible; visible = scratch; scratch = swap;
                if (visible.Count < 3) { visible.Clear(); break; }
            }
            return visible;
        }
    }
}
