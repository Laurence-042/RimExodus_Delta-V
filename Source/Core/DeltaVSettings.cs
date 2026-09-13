using Verse;

namespace DeltaV
{
    /// <summary>
    /// Mod 设置容器（阶段2 首批正式设置项）。hostMapSize 只影响下一次创建测试场景，
    /// 是显示/性能参数而非宇宙坐标协议（AGENTS §10）。
    /// </summary>
    public class DeltaVSettings : ModSettings
    {
        /// <summary>飞行辅助：无输入的轴按推进器真实出力反向减速（不引入魔法衰减）。默认开。</summary>
        public bool flightAssist = true;

        /// <summary>宿主/显示地图边长（100–1000），下次场景创建生效。</summary>
        public int hostMapSize = 150;

        /// <summary>
        /// 宇宙视野缩放宽上限（阶段3）：聚焦 Delta-V 图（船图/宿主图）时相机可缩放到此格数，
        /// 远处飞船/POI 的外壳代理画在世界空间、本不受地图矩形限制，唯一约束就是这个缩放上限。
        /// </summary>
        public float universeZoomMax = 400f;

        /// <summary>
        /// floating origin 平移阈值（阶段3）：主船离宇宙原点超过此距离时全体 SpaceObject
        /// pose 一次性平移回原点附近（相对位置与背景视角都不变），保证长途航行坐标有界。
        /// </summary>
        public float originShiftThreshold = 1000f;

        /// <summary>POI 进入活跃段的距离（universe 格，阶段3）。</summary>
        public float poiActiveRadius = 300f;

        /// <summary>POI 超出此距离即删除收快照（universe 格，阶段3）。</summary>
        public float poiDeleteRadius = 1200f;

        // ---- 分模块诊断日志开关（默认全关；Warn/Error 与必要状态日志不受开关控制）----

        public bool logCore;

        public bool logInput;

        public bool logPhysics;

        public bool logHarmony;

        public bool logLifecycle;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref flightAssist, "flightAssist", true);
            Scribe_Values.Look(ref hostMapSize, "hostMapSize", 150);
            Scribe_Values.Look(ref universeZoomMax, "universeZoomMax", 400f);
            Scribe_Values.Look(ref originShiftThreshold, "originShiftThreshold", 1000f);
            Scribe_Values.Look(ref poiActiveRadius, "poiActiveRadius", 300f);
            Scribe_Values.Look(ref poiDeleteRadius, "poiDeleteRadius", 1200f);
            Scribe_Values.Look(ref logCore, "logCore", false);
            Scribe_Values.Look(ref logInput, "logInput", false);
            Scribe_Values.Look(ref logPhysics, "logPhysics", false);
            Scribe_Values.Look(ref logHarmony, "logHarmony", false);
            Scribe_Values.Look(ref logLifecycle, "logLifecycle", false);
            base.ExposeData();
        }
    }
}
