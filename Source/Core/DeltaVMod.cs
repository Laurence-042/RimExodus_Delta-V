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

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Gap(6f);
            Text.Font = GameFont.Medium;
            listing.Label("Delta-V debug settings");
            Text.Font = GameFont.Small;
            listing.GapLine();
            listing.Label("Diagnostic log modules:");
            listing.CheckboxLabeled("Core (universe state, focus, save/load)", ref Settings.logCore);
            listing.CheckboxLabeled("Generation (maps, orbit tiles, hull stamping)", ref Settings.logGeneration);
            listing.CheckboxLabeled("Rendering (shell replay)", ref Settings.logRendering);
            listing.GapLine();
            listing.Label("Shell replay draw families (stale-bisect diagnostics):");
            listing.CheckboxLabeled("Draw terrain layer in shell replay", ref Settings.shellDrawTerrain);
            listing.CheckboxLabeled("Draw things layer in shell replay", ref Settings.shellDrawThings);
            listing.Gap(6f);
            if (listing.ButtonText("Reset to defaults"))
            {
                Settings = new DeltaVSettings();
                WriteSettings();
            }
            listing.End();
        }

        public override string SettingsCategory()
        {
            return "RimExodus: Delta-V";
        }
    }
}
