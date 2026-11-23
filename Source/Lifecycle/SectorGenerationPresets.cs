using System;
using System.Collections.Generic;
using System.Linq;

namespace DeltaV
{
    /// <summary>全部星区生成叶子共用的面积密度预设。</summary>
    public enum SectorGenerationPreset
    {
        Disabled,
        Sparse,
        Standard,
        Dense
    }

    public static class SectorGenerationPresets
    {
        public const SectorGenerationPreset Default = SectorGenerationPreset.Standard;

        /// <summary>设置值的面积单位：一亿平方宇宙格。</summary>
        public const double DensityAreaUnitCellsSquared = 100_000_000d;

        /// <summary>
        /// 默认 20000 格尺度下规则六边形的参考面积。真实生成始终使用原版 tile
        /// 多边形面积；这里只供无世界实例的设置页与离线检查显示参考期望数。
        /// </summary>
        public const double DefaultReferenceSectorAreaCellsSquared = 346_410_161.5137755d;

        public static readonly IReadOnlyList<SectorGenerationPreset> All = new[]
        {
            SectorGenerationPreset.Disabled,
            SectorGenerationPreset.Sparse,
            SectorGenerationPreset.Standard,
            SectorGenerationPreset.Dense
        };

        public static float Scale(SectorGenerationPreset preset) => preset switch
        {
            SectorGenerationPreset.Disabled => 0f,
            SectorGenerationPreset.Sparse => .4f,
            SectorGenerationPreset.Standard => 1f,
            SectorGenerationPreset.Dense => 2f,
            _ => 1f
        };

        public static string LabelKey(SectorGenerationPreset preset)
        {
            switch (preset)
            {
                case SectorGenerationPreset.Disabled: return "DeltaV_SettingsSectorPresetDisabled";
                case SectorGenerationPreset.Sparse: return "DeltaV_SettingsSectorPresetSparse";
                case SectorGenerationPreset.Standard: return "DeltaV_SettingsSectorPresetStandard";
                case SectorGenerationPreset.Dense: return "DeltaV_SettingsSectorPresetDense";
                default: throw new ArgumentOutOfRangeException(nameof(preset), preset, null);
            }
        }

        public static double ReferenceSectorAreaCellsSquared()
        {
            double liveAverage = OrbitSphere.AverageTileAreaCellsSquared(OrbitSphere.Layer);
            if (liveAverage > 0d) return liveAverage;
            double scale = OrbitSphere.CellsPerTile / OrbitSphere.DefaultCellsPerTile;
            return DefaultReferenceSectorAreaCellsSquared * scale * scale;
        }

        public static double ExpectedObjectCount(DeltaVSettings settings)
        {
            return ExpectedObjectCount(settings, ReferenceSectorAreaCellsSquared());
        }

        public static double ExpectedObjectCount(DeltaVSettings settings, double sectorAreaCellsSquared)
        {
            if (settings == null) return 0d;
            double totalDensity = SectorGenerationTable.Leaves(settings.sectorGenerationRoots)
                .Sum(leaf => Math.Max(0f, leaf.standardDensityPer100MCells2))
                * Scale(settings.sectorGenerationPreset);
            return totalDensity * Math.Max(0d, sectorAreaCellsSquared) / DensityAreaUnitCellsSquared;
        }
    }
}
