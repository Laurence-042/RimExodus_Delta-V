using Verse;

namespace DeltaV
{
    /// <summary>
    /// Mod 设置容器。阶段1 的调试开关验证后已移除；保留设置框架，
    /// 后续正式设置项（阶段2+）在此挂载并经 ExposeData 持久化。
    /// </summary>
    public class DeltaVSettings : ModSettings
    {
    }
}
