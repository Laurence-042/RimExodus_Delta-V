using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 驾驶台 Rot4 ⇄ Delta-V 平面角的唯一换算入口（纯函数，无状态）。
    /// 玩家所见「航向」= 容器角 ⊕ 主台朝向（ShipRegistry.ShipPose 的派生组成）；
    /// 船帧相对地图帧的角 σ = AngleFromRot4(主台.Rotation)——主台 East ⇔ 船帧对齐
    /// 地图帧（与原自由飞行口袋图 East 规范一致）。没有有效主台时沿用历史 +X 船艏。
    /// </summary>
    public static class ShipOrientation
    {
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
            // Same nearest-quarter rule for docking previews and impact placement.
            // Midpoints use the existing Mathf.Round tie-to-even rule.
            int quarter = Mathf.RoundToInt(angle / (Mathf.PI * 0.5f));
            switch ((quarter % 4 + 4) % 4)
            {
                case 0: return Rot4.East;
                case 1: return Rot4.North;
                case 2: return Rot4.West;
                default: return Rot4.South;
            }
        }

        public static float SnapAngle(float angle) => AngleFromRot4(Rot4FromAngle(angle));

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
