using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 飞船在统一宇宙坐标系中的刚体姿态（AGENTS §5、§8）。
    /// 宇宙平面与地图 XZ 平面平行；angle 为弧度，0 = +X，正方向约定见 SpaceTransform。
    /// velocity / angularVelocity 是权威状态的一部分，但阶段1 尚无物理积分，
    /// 只有轨道测试动画直接驱动 position / angle。
    /// </summary>
    public struct SpacePose : IExposable
    {
        public Vector2 position;
        public Vector2 velocity;
        public float angle;
        public float angularVelocity;

        public SpacePose(Vector2 position, float angle)
        {
            this.position = position;
            this.angle = angle;
            velocity = Vector2.zero;
            angularVelocity = 0f;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref position, "position");
            Scribe_Values.Look(ref velocity, "velocity");
            Scribe_Values.Look(ref angle, "angle");
            Scribe_Values.Look(ref angularVelocity, "angularVelocity");
        }
    }
}
