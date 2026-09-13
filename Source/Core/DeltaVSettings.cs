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

        // ---- 分模块诊断日志开关（默认全关；Warn/Error 与必要状态日志不受开关控制）----

        public bool logCore;

        public bool logInput;

        public bool logPhysics;

        public bool logHarmony;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref flightAssist, "flightAssist", true);
            Scribe_Values.Look(ref hostMapSize, "hostMapSize", 150);
            Scribe_Values.Look(ref logCore, "logCore", false);
            Scribe_Values.Look(ref logInput, "logInput", false);
            Scribe_Values.Look(ref logPhysics, "logPhysics", false);
            Scribe_Values.Look(ref logHarmony, "logHarmony", false);
            base.ExposeData();
        }
    }
}
