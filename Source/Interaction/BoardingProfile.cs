using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// shuttle 船体帧中的一台虚拟推进器。position/force 都以舱体中心为原点，
    /// +X = 舱头（建筑朝向），+Y = 舱体左侧；force 的方向与长度分别是推力方向与
    /// 最大推力。注入 Map 时统一按舱体 Rot4 旋转，调用方不得再组合角点/方向。
    /// </summary>
    public class ShuttleThrusterConfig
    {
        public Vector2 position;
        public Vector2 force;
        public ThrusterVisualProfile visuals;
    }

    /// <summary>
    /// 跳帮 shuttle 的数值档位（modExtension，2026-09-27）：canBoard 与推进器布局放
    /// modExtensions 而非 comps——comps li 追加会与原版 PassengerShuttle patch 的
    /// CompSpaceShuttle 实例重复（TryGetComp 取首个），modExtensions 是允许追加的
    /// 字段族且原版无此项。读取唯一入口 = CompSpaceShuttle.CanBoard/ThrusterLayout。
    /// </summary>
    public class DefModExtension_BoardingProfile : DefModExtension
    {
        /// <summary>具备侵入式对接能力（跳帮特化）。</summary>
        public bool canBoard;
        public float boostMultiplier = 2f;
        public int boostTicks = 600;

        /// <summary>
        /// 可选的完整虚拟推进器布局；非空时替换 CompProperties_SpaceShuttle 的默认表。
        /// 每项显式给出船体帧位置与推力矢量，数量和方向均不受限制。
        /// </summary>
        public List<ShuttleThrusterConfig> virtualThrusters;
    }
}
