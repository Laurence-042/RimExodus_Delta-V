using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 飞船地图姿态与主驾驶台（船艏）之间的唯一换算入口。
    /// SpacePose.angle 仍表示稳定 Map 局部系到宇宙系的旋转；玩家所见“航向”则是
    /// 该旋转再叠加主驾驶台在 Map 内的朝向。没有有效主台时沿用历史 +X 船艏。
    /// </summary>
    public static class ShipOrientation
    {
        public static float LocalForwardAngle(ShipState ship)
        {
            Building console = ship?.primaryConsole;
            if (console == null || console.Destroyed)
            {
                return 0f;
            }
            return AngleFromRot4(console.Rotation);
        }

        public static float HeadingAngle(ShipState ship)
        {
            return ship == null
                ? 0f
                : SpaceTransform.WrapAngle(ship.pose.angle + LocalForwardAngle(ship));
        }

        /// <summary>RimWorld 格向（East=+X, North=+Z）转为 Delta-V 平面角。</summary>
        public static float AngleFromRot4(Rot4 rot)
        {
            if (rot == Rot4.East) return 0f;
            if (rot == Rot4.West) return Mathf.PI;
            if (rot == Rot4.South) return -Mathf.PI * 0.5f;
            return Mathf.PI * 0.5f;
        }

        public static Rot4 Rot4FromAngle(float angle)
        {
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            if (dir.x > 0.5f) return Rot4.East;
            if (dir.x < -0.5f) return Rot4.West;
            if (dir.y > 0.5f) return Rot4.North;
            return Rot4.South;
        }

        /// <summary>
        /// 求原版 Gravship.Rotation，使指定源朝向经原版 PositionData 旋转后成为目标朝向。
        /// 枚举四向并调用原版 Rot4.Rotated，避免另写一套旋转手性公式。
        /// </summary>
        public static Rot4 PlacementRotation(Rot4 sourceFacing, Rot4 targetFacing)
        {
            foreach (Rot4 candidate in Rot4.AllRotations)
            {
                if (candidate.Rotated((RotationDirection)sourceFacing.AsByte) == targetFacing)
                {
                    return candidate;
                }
            }
            return Rot4.North;
        }
    }
}
