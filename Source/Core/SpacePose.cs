using System;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 飞船/POI 在统一宇宙坐标系中的刚体姿态（AGENTS §5、§8）。
    ///
    /// 宇宙坐标是球面绝对坐标（轨道层球面）：
    /// - position：球面上的单位方向向量（双精度保存，Vector3 只作即时视图）；tile 投影由 OrbitSphere
    ///   纯函数完成，不存在任何需要对齐的持久锚；
    /// - velocity：position 锚点的位置导数（x = 东向、y = 北向，cells/s），不是偏心质心速度；
    /// - angle：船体局部系到该处切空间基的旋转（弧度，0 = 东向，见 SpaceTransform）；
    ///   angularVelocity 是这个角度的时间导数。质心状态由共同刚体点变换派生，不另存。
    ///
    /// </summary>
    public struct SpacePose : IExposable
    {
        internal OrbitSphere.Direction direction;
        public Vector3 position
        {
            get => direction.Vector;
            set => direction = new OrbitSphere.Direction(value).Normalized;
        }
        public Vector2 velocity;
        public float angle;
        public float angularVelocity;

        public SpacePose(Vector3 position, float angle)
        {
            direction = new OrbitSphere.Direction(position).Normalized;
            this.angle = angle;
            velocity = Vector2.zero;
            angularVelocity = 0f;


        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref direction.x, "dirX");
            Scribe_Values.Look(ref direction.y, "dirY");
            Scribe_Values.Look(ref direction.z, "dirZ");
            Scribe_Values.Look(ref velocity, "velocity");
            Scribe_Values.Look(ref angle, "angle");
            Scribe_Values.Look(ref angularVelocity, "angularVelocity");
        }
    }
}
