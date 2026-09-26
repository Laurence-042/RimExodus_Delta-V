using Verse;

namespace DeltaV
{
    /// <summary>
    /// 弹体数据 def（太空战架构 §5.1）：挂在弹体 building def 上的唯一参数源。
    /// 弹体 def 同时是联合效果注册表的键与贴图来源（无图型贴图取自 def 图形）；
    /// 可拦截性由结构派生（有图 ⇔ 可拦截），本 comp 不设「可拦截」标记字段。
    /// </summary>
    public class CompProperties_SpaceProjectile : CompProperties
    {
        /// <summary>巡航速度（格/游戏秒）。</summary>
        public float speed = 60f;

        /// <summary>超时自毁（tick，无后效）。</summary>
        public int timeoutTicks = 1800;

        /// <summary>导弹（纯追踪制导：每 tick 速度方向转向目标容器当前位姿）。</summary>
        public bool guided;

        /// <summary>转向率上限（度/秒；默认近似即时转向）。</summary>
        public float turnRateDegPerSec = 1080f;

        /// <summary>有图形态：弹体 building spawn 于自己的小图（可拦截；血量由 building 实例计算）。</summary>
        public bool hasMap = true;

        /// <summary>激光（>0 = 最大射程，即时链：发射 tick 内一次全射程扫掠，无图不可拦截）。</summary>
        public float laserRange = 0f;

        public CompProperties_SpaceProjectile()
        {
            compClass = typeof(CompSpaceProjectile);
        }
    }

    /// <summary>弹体 building 上的数据载体（参数读取一律经 def.GetCompProperties，实例仅标识）。</summary>
    public class CompSpaceProjectile : ThingComp
    {
    }
}
