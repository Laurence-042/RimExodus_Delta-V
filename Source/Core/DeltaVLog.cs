using Verse;

namespace DeltaV
{
    /// <summary>
    /// 诊断日志的功能模块划分（2026-09-13，模式照搬 RimExodus 分模块日志）。
    ///
    /// 模块与 DeltaVSettings.log* 一一对应：
    /// - Core：驾驶状态与场景（进入/退出为必要行为日志，**不受开关控制**，见 PilotController）
    /// - Input：驾驶输入采样与飞行辅助合成（门控）
    /// - Physics：质量扫描与积分（门控，节流输出）
    /// - Harmony：按键抑制决策（门控；抑制激活的一次性摘要常开）
    ///
    /// 纪律（与 RimExodus 相同，勿回退）：Warn/Error 不受开关控制；
    /// 关键状态迁移（驾驶进入/退出、抑制激活、补丁初始化结果）常开；
    /// 热路径成本 = 一次静态调用 + 字段读。
    /// </summary>
    public enum DeltaVLogModule
    {
        Core,
        Input,
        Physics,
        Harmony
    }

    /// <summary>
    /// 分模块日志入口。门控输出用 Message(module, text)（前缀 [DeltaV:{module}]）；
    /// 常开日志直接 Log.Message（[DeltaV] 前缀），不经此门控。
    /// </summary>
    public static class DeltaVLog
    {
        public static bool Enabled(DeltaVLogModule module)
        {
            var s = DeltaVMod.Settings;
            if (s == null)
            {
                return false;
            }
            switch (module)
            {
                case DeltaVLogModule.Core: return s.logCore;
                case DeltaVLogModule.Input: return s.logInput;
                case DeltaVLogModule.Physics: return s.logPhysics;
                case DeltaVLogModule.Harmony: return s.logHarmony;
                default: return false;
            }
        }

        public static void Message(DeltaVLogModule module, string text)
        {
            if (Enabled(module))
            {
                Log.Message("[DeltaV:" + module + "] " + text);
            }
        }
    }
}
