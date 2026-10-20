using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public enum CombatVisualStyle { Kinetic, Heavy, Rail, Energy, Missile, Coupling, Mining }

    /// <summary>
    /// 弹体数据 def（太空战架构 §5.1）：挂在弹体 building def 上的唯一参数源。
    /// 弹体 def 同时是联合效果注册表的键与贴图来源（无图型贴图取自 def 图形）；
    /// 可拦截性由结构派生（有图 ⇔ 可拦截），本 comp 不设「可拦截」标记字段。
    /// </summary>
    public class CompProperties_SpaceProjectile : CompProperties
    {
        // Presentation only: never used by damage, guidance or collision.
        public CombatVisualStyle visualStyle;
        public float tracerLength;
        public float tracerWidth = 0.12f;
        public SoundDef fireSoundMap;
        public SoundDef fireSoundShell;
        public SoundDef impactSoundMap;
        public SoundDef impactSoundShell;

        /// <summary>巡航速度（格/游戏秒）。</summary>
        public float speed = 60f;

        /// <summary>超时自毁（tick，无后效）。</summary>
        public int timeoutTicks = 1800;

        /// <summary>导弹（纯追踪制导：每 tick 速度方向转向目标容器当前位姿）。</summary>
        public bool guided;

        /// <summary>转向率上限（度/秒；默认近似即时转向）。</summary>
        public float turnRateDegPerSec = 1080f;

        /// <summary>可选通用冷发射程序：点火前 tick 数；0 表示立即进入原有飞行阶段。</summary>
        public int coldLaunchTicks;

        /// <summary>冷发射速度相对巡航速度的倍率。</summary>
        public float coldLaunchSpeedFactor = 0.25f;

        /// <summary>离架后强转向阶段的最长 tick 数；对准或失去目标时提前点火，0 跳过。</summary>
        public int initialTurnTicks;
        public float initialTurnRateDegPerSec = 1080f;

        /// <summary>主机从离架速度过渡到巡航速度的 tick 数；不改变后续追踪能力。</summary>
        public int accelerationTicks;

        /// <summary>普通弹体喷口在朝前 +X、左侧 +Y 的弹体帧中定位；shuttle 读取真实推进布局。</summary>
        public Vector2 exhaustOffset = new Vector2(-0.55f, 0f);

        /// <summary>程序允许的锁定弧；360 表示可锁定任意方向目标。</summary>
        public float lockArcDeg = 360f;

        /// <summary>有图形态：弹体 building spawn 于自己的小图（可拦截；血量由 building 实例计算）。</summary>
        public bool hasMap = true;

        /// <summary>激光（>0 = 最大射程，即时链：发射 tick 内一次全射程扫掠，无图不可拦截）。</summary>
        public float laserRange = 0f;

        /// <summary>即时激光在前台层的线宽（地图格）。</summary>
        public float laserBeamWidth = 0.3f;

        /// <summary>持续束每游戏 tick 经同一发射链结算；表现保持恒定，只显示当前 tick 的射线。</summary>
        public bool continuousBeam;

        /// <summary>跳过全部盾面，仍与实体结构碰撞。</summary>
        public bool ignoresShields;

        /// <summary>此专用弹体对有图制导导弹跳过目标掩体命中概率。</summary>
        public bool guaranteedHitMissiles;

        /// <summary>飞行表现：点火后留下烟迹并在尾部绘制推进焰；不参与弹道。</summary>
        public bool exhaustTrail;
        public float exhaustSize = 1f;
        public int smokeLifetimeTicks = 120;

        /// <summary>将实弹碎裂的固定脉冲电费（W·d）。</summary>
        public float shieldFragmentCost = 2f;
        /// <summary>将该弹体减速到零的脉冲电费（W·d）。</summary>
        public float shieldStopEnergy = 80f;
        /// <summary>激光主动偏转 180 度的脉冲电费（W·d）。</summary>
        public float shieldLaserTurnEnergy = 2f;
        public ThingDef shieldFragmentDef;
        public int shieldFragmentCount;
        public float shieldFragmentScatterDeg = 12f;

        public CompProperties_SpaceProjectile()
        {
            compClass = typeof(CompSpaceProjectile);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string error in base.ConfigErrors(parentDef)) yield return error;
            if (continuousBeam && (laserRange <= 0f || hasMap))
                yield return $"{parentDef.defName}: continuous beams require a mapless laser";
        }
    }

    /// <summary>弹体 building 上的数据载体（参数读取一律经 def.GetCompProperties，实例仅标识）。</summary>
    public class CompSpaceProjectile : ThingComp
    {
    }
}
