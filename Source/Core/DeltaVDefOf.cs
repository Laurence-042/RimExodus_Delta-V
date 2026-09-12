using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 用到的原版/本 mod Def 引用。字段名必须与 defName 完全一致，
    /// DefOf 由游戏自动绑定。
    /// </summary>
    [DefOf]
    public static class DeltaVDefOf
    {
        public static TerrainDef DeltaV_Void;

        public static MapGeneratorDef DeltaV_ShipPocket;

        public static WorldObjectDef DeltaV_SpaceHostParent;

        public static WorldObjectDef DeltaV_ShipMapParent;

        // 原版 defs（defName 一致）
        // 逆重基架（gravship substructure）：飞船甲板。isFoundation=true，
        // "只有基架上的东西才能被逆重引擎抬起"——未来质量/结构判定都以它为准。
        public static TerrainDef Substructure;

        public static ThingDef Ship_Engine;
    }
}
