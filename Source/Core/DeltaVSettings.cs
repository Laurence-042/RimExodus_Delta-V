using Verse;

namespace DeltaV
{
    /// <summary>
    /// Mod 设置（结构仿 RimExodusSettings：分模块日志开关）。
    /// </summary>
    public class DeltaVSettings : ModSettings
    {
        // ---- 诊断日志分模块 ----
        public bool logCore = false;

        public bool logGeneration = false;

        public bool logRendering = true;

        // ---- 外壳重放绘制家族（残留二分定位用）----
        public bool shellDrawTerrain = true;

        public bool shellDrawThings = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref logCore, "logCore", false);
            Scribe_Values.Look(ref logGeneration, "logGeneration", false);
            Scribe_Values.Look(ref logRendering, "logRendering", true);
            Scribe_Values.Look(ref shellDrawTerrain, "shellDrawTerrain", true);
            Scribe_Values.Look(ref shellDrawThings, "shellDrawThings", true);
        }
    }
}
