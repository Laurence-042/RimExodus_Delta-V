using Verse;

namespace DeltaV
{
    /// <summary>
    /// Mod 设置容器。尺度类设置（cellsPerTile）只是"格 ↔ 弧度"换算系数，
    /// 不是宇宙坐标协议（AGENTS §10）；改值由 UniverseManager 执行位姿重标定。
    /// </summary>
    public class DeltaVSettings : ModSettings
    {
        /// <summary>飞行辅助：无输入的轴按推进器真实出力反向减速（不引入魔法衰减）。默认开。</summary>
        public bool flightAssist = true;

        /// <summary>
        /// 1 个星区（worldtile）弧长 ≈ 多少宇宙格（星区架构 §8，默认 20000）。
        /// 只影响尺度换算，不影响 tile 划分；改值即时触发位姿重标定（停靠/起飞中禁止）。
        /// </summary>
        public int cellsPerTile = (int)OrbitSphere.DefaultCellsPerTile;

        /// <summary>
        /// 宇宙视野缩放宽上限（阶段3）：聚焦 Delta-V 图（船图/真实空间图）时相机可缩放到此格数，
        /// 远处飞船/POI 的外壳代理画在世界空间、本不受地图矩形限制，唯一约束就是这个缩放上限。
        /// </summary>
        public float universeZoomMax = 400f;

        /// <summary>POI 进入活跃段的距离（universe 格，阶段3）。</summary>
        public float poiActiveRadius = 300f;

        /// <summary>POI 超出此距离即删除收快照（universe 格，阶段3）。</summary>
        public float poiDeleteRadius = 1200f;

        /// <summary>分帧地图生成每个渲染帧的主线程预算（毫秒）。</summary>
        public float incrementalMapGenBudgetMs = 8f;

        // ---- 分模块诊断日志开关（默认全关；Warn/Error 与必要状态日志不受开关控制）----

        public bool logCore;

        public bool logInput;

        public bool logPhysics;

        public bool logHarmony;

        public bool logLifecycle;

        public bool logRender;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref flightAssist, "flightAssist", true);
            Scribe_Values.Look(ref cellsPerTile, "cellsPerTile", (int)OrbitSphere.DefaultCellsPerTile);
            Scribe_Values.Look(ref universeZoomMax, "universeZoomMax", 400f);
            Scribe_Values.Look(ref poiActiveRadius, "poiActiveRadius", 300f);
            Scribe_Values.Look(ref poiDeleteRadius, "poiDeleteRadius", 1200f);
            Scribe_Values.Look(ref incrementalMapGenBudgetMs, "incrementalMapGenBudgetMs", 8f);
            Scribe_Values.Look(ref logCore, "logCore", false);
            Scribe_Values.Look(ref logInput, "logInput", false);
            Scribe_Values.Look(ref logPhysics, "logPhysics", false);
            Scribe_Values.Look(ref logHarmony, "logHarmony", false);
            Scribe_Values.Look(ref logLifecycle, "logLifecycle", false);
            Scribe_Values.Look(ref logRender, "logRender", false);
            base.ExposeData();
        }
    }
}
