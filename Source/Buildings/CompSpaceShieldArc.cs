using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 护盾弧太空侧参数（太空战架构 §3.3/§5 段5）：挂在护盾发生器 def 上（XML patch），
    /// 配置弧跨与朝向。容量事实驻留原版 comp（CompProjectileInterceptor.
    /// currentHitPoints/HitPointsMax）——同图战斗完全原版机制；太空弧几何是派生缓存
    ///（随容器姿态旋转），不是事实源。原版发生器不可旋转，弧朝向经本字段配置
    ///（地图帧角，0 = 东、90 = 北；缺省全圆环兜底）。
    /// </summary>
    public class CompProperties_SpaceShieldArc : CompProperties
    {
        /// <summary>弧跨（度，绕朝向对称；360 = 全圆环）。</summary>
        public float arcSpanDeg = 120f;

        /// <summary>弧朝向（度，地图帧；原版建筑不旋转，经此配置）。</summary>
        public float arcFacingDeg = 90f;

        public CompProperties_SpaceShieldArc()
        {
            compClass = typeof(CompSpaceShieldArc);
        }
    }

    public class CompSpaceShieldArc : ThingComp
    {
        public float SpanRad => ((CompProperties_SpaceShieldArc)props).arcSpanDeg * Mathf.Deg2Rad;

        public float FacingRad => ((CompProperties_SpaceShieldArc)props).arcFacingDeg * Mathf.Deg2Rad;

        /// <summary>建筑上的原版护盾容量 comp（CompGravshipShieldGenerator 经 is-T 命中）。</summary>
        public CompProjectileInterceptor Interceptor => parent.TryGetComp<CompProjectileInterceptor>();
    }
}
