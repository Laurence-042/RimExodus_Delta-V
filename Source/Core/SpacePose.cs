using System;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 飞船/POI 在统一宇宙坐标系中的刚体姿态（AGENTS §5、§8）。
    ///
    /// 宇宙坐标是球面绝对坐标（轨道层球面）：
    /// - position：球面上的单位方向向量（Vector3，恒归一）；tile 投影由 OrbitSphere
    ///   纯函数完成，不存在任何需要对齐的持久锚；
    /// - velocity：position 处切空间速度（x = 东向、y = 北向，cells/s）；
    /// - angle：船体局部系到该处切空间基的旋转（弧度，0 = 东向，见 SpaceTransform）。
    ///
    /// 旧档迁移：平面坐标存档把位置存成 Vector2（键 "position"）。加载时若没有新键
    /// "dir"，则读入 legacy 字段并置位 legacyPlanar，由 UniverseManager.LoadedGame 用
    /// 旧 hostMap 锚统一换算成球面方向后清除。
    /// </summary>
    public struct SpacePose : IExposable
    {
        public Vector3 position;
        public Vector2 velocity;
        public float angle;
        public float angularVelocity;

        /// <summary>旧平面存档的原始位置（仅加载期有效，不序列化）。</summary>
        [NonSerialized] public bool legacyPlanar;
        [NonSerialized] public Vector2 legacyPosition;

        public SpacePose(Vector3 position, float angle)
        {
            this.position = position;
            this.angle = angle;
            velocity = Vector2.zero;
            angularVelocity = 0f;
            legacyPlanar = false;
            legacyPosition = Vector2.zero;
        }

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                Scribe_Values.Look(ref position, "dir");
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (Scribe.loader.curXmlParent?["dir"] == null)
                {
                    // 平面坐标旧档：读旧键，交给 UniverseManager.LoadedGame 换算。
                    Vector2 legacy = default;
                    Scribe_Values.Look(ref legacy, "position");
                    legacyPosition = legacy;
                    legacyPlanar = true;
                }
                else
                {
                    Scribe_Values.Look(ref position, "dir");
                }
            }
            Scribe_Values.Look(ref velocity, "velocity");
            Scribe_Values.Look(ref angle, "angle");
            Scribe_Values.Look(ref angularVelocity, "angularVelocity");
        }
    }
}
