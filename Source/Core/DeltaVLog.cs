using Verse;

namespace DeltaV
{
    /// <summary>
    /// 诊断日志的功能模块划分（结构仿 RimExodus 分模块日志）：
    /// - Core：宇宙状态/聚焦切换/存读档；
    /// - Generation：宿主图与飞船图创建、轨道 tile 选择、船体铺设；
    /// - Rendering：外壳捕获与绘制（阶段1 重点调试链路）。
    /// 纪律：Warn/Error 不受开关控制；热路径埋点只在状态跃迁时输出，禁止逐帧刷屏。
    /// </summary>
    public enum DeltaVLogModule
    {
        Core,
        Generation,
        Rendering
    }

    /// <summary>
    /// 分模块日志入口。门控读取 DeltaVMod.Settings；Settings 为 null 时恒 false。
    /// 热路径成本 = 一次静态调用 + 字段读。
    /// </summary>
    public static class DeltaVLog
    {
        public static bool Enabled(DeltaVLogModule module)
        {
            DeltaVSettings s = DeltaVMod.Settings;
            if (s == null)
            {
                return false;
            }
            switch (module)
            {
                case DeltaVLogModule.Core: return s.logCore;
                case DeltaVLogModule.Generation: return s.logGeneration;
                case DeltaVLogModule.Rendering: return s.logRendering;
                default: return false;
            }
        }

        /// <summary>门控通过才输出，前缀 [DeltaV:{module}]。</summary>
        public static void Message(DeltaVLogModule module, string text)
        {
            if (Enabled(module))
            {
                Log.Message("[DeltaV:" + module + "] " + text);
            }
        }
    }
}
