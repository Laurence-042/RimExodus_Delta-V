using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Mod 入口。阶段1 不打任何 Harmony 补丁——地图生成、GameComponent 注册、
    /// 聚焦切换、逐帧绘制全部走原版 API。引入补丁时再初始化 Harmony 实例。
    /// </summary>
    public class DeltaVMod : Mod
    {
        public static DeltaVSettings Settings { get; private set; }

        public DeltaVMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DeltaVSettings>();
            Log.Message("[DeltaV] Phase 1 prototype loaded (no Harmony patches).");
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(inRect, "No configurable settings yet.");
        }

        public override string SettingsCategory()
        {
            return "RimExodus: Delta-V";
        }
    }
}
