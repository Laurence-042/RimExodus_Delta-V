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

        // 专用宿主口袋图：所有 Delta-V 口袋图 sourceMap 的统一锚（无 tile，仅 pocketMaps）。
        public static WorldObjectDef DeltaV_UniverseHostParent;

        // 原版 defs（defName 一致）
        // 逆重基架（gravship substructure）：飞船甲板。isFoundation=true，
        // "只有基架上的东西才能被逆重引擎抬起"——未来质量/结构判定都以它为准。
        public static TerrainDef Substructure;

        // 对接口（2026-09-18 基架归属设计）与其铺设的对接通道地形；解锁科技真空屏障。
        public static ThingDef DeltaV_DockingPort;
        public static ThingDef DeltaV_DockingPassage;
        public static JobDef DeltaV_HackSoftDock;
        public static ThingDef DeltaV_SoftDockPortal;

        public static TerrainDef DeltaV_DockingChannel;

        public static ResearchProjectDef DeltaV_VacuumBarrier;

        public static JobDef DeltaV_LoadMissileLauncher;
        public static ThingDef DeltaV_Weapon_MissileLauncher;
        public static ThingDef DeltaV_Building_MissileMagazine;
        public static ThingDef DeltaV_CargoContainer;
        public static ThingDef DeltaV_TradeTerminal;
        public static JobDef DeltaV_UseTradeTerminal;

        // 阶段5 测试弹体（Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml；正式武器 def 随段6）。
        public static ThingDef DeltaV_Projectile_Cannon;
        public static ThingDef DeltaV_Projectile_HullContact;

        public static ThingDef DeltaV_Projectile_Laser;

        public static ThingDef DeltaV_Projectile_Missile;

        public static ThingDef DeltaV_Projectile_PDLaser;

        // 逆重飞船大型推进器（Odyssey，2×2 方形脚印、rotatable、需 Substructure）。
        // 注意：Ship_Engine 是星际飞船（结局飞船）引擎，与逆重飞船无关，勿混用。
        public static ThingDef LargeThruster;

        // 阶段2 驾驶按键（1.6/Defs/Misc/DeltaV_KeyBindings.xml；可在原版改键界面修改）
        public static Verse.KeyBindingCategoryDef DeltaV;

        public static Verse.KeyBindingDef DeltaV_ThrustForward;

        public static Verse.KeyBindingDef DeltaV_ThrustBackward;

        public static Verse.KeyBindingDef DeltaV_StrafeLeft;

        public static Verse.KeyBindingDef DeltaV_StrafeRight;

        public static Verse.KeyBindingDef DeltaV_RotateLeft;

        public static Verse.KeyBindingDef DeltaV_RotateRight;

        public static Verse.KeyBindingDef DeltaV_TogglePilotMode;

        public static Verse.KeyBindingDef DeltaV_AssistBrake;

        public static Verse.KeyBindingDef DeltaV_NavigationReferenceModifier;

        // 玩家火控模式切换（默认 B；驾驶期抑制其原版功能）。
        public static Verse.KeyBindingDef DeltaV_AimMode;

        // 星区 POI 命名器（1.6/Defs/RulePackDefs/RulePacks_Namers_SpacePoi.xml）：
        // 只产名字主干，类型名经 [poi_type] 由调用方注入；尾缀「(派系名) #ID」由
        // SectorContentService 在代码侧拼接。
        public static Verse.RulePackDef DeltaV_NamerSpacePoi;
    }
}
