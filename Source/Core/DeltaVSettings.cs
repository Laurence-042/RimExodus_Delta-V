using System.Collections.Generic;
using System.Linq;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Mod 设置容器。尺度类设置（cellsPerTile）只是"格 ↔ 弧度"换算系数，
    /// 不是宇宙坐标协议（AGENTS §10）；改值由 UniverseManager 执行位姿重标定。
    /// </summary>
    public class DeltaVSettings : ModSettings
    {
        /// <summary>飞行辅助：无输入的轴按推进器真实出力反向减速（不引入魔法衰减）。默认开。</summary>
        public bool flightAssist = true;

        public bool hideSpaceMapsWithoutPlayerPawns = true;

        // Empty paths preserve the test scene's automatic latest-export selection.
        public string testScenePlayerPrefab = "";
        public string testSceneEnemyPrefab = "";
        public bool testSceneKeepPrefabMarkers;
        public float testSceneDistance = ShipTestScene.DefaultEnemyDistance;
        public float testSceneBearingDeg = ShipTestScene.DefaultEnemyBearingDeg;
        public float testScenePlayerAngleDeg;
        public float testSceneEnemyAngleDeg = ShipTestScene.DefaultEnemyAngleDeg;

        /// <summary>
        /// 1 个星区（worldtile）弧长 ≈ 多少宇宙格（星区架构 §8，默认 20000）。
        /// 只影响尺度换算，不影响 tile 划分；改值即时触发位姿重标定（停靠/起飞中禁止）。
        /// </summary>
        public int cellsPerTile = (int)OrbitSphere.DefaultCellsPerTile;

        /// <summary>
        /// 宇宙视野缩放宽上限（阶段3）：聚焦 Delta-V 图（船图/真实空间图）时相机可缩放到此格数，
        /// 远处飞船/POI 的外壳代理画在世界空间、本不受地图矩形限制，唯一约束就是这个缩放上限。
        /// </summary>
        public float universeZoomMax = 400f;
        public float coordinateGridOpacity = 0.22f;

        /// <summary>POI 进入活跃段的距离（universe 格，阶段3）。</summary>
        public float poiActiveRadius = 300f;

        /// <summary>POI 超出此距离即删除收快照（universe 格，阶段3）。</summary>
        public float poiDeleteRadius = 1200f;

        /// <summary>NPC 战术决策间隔；自动驾驶积分和已选目标的武器射击仍逐 tick。</summary>
        public int npcDecisionIntervalTicks = 60;

        /// <summary>POI/普通容器的距离生命周期扫描间隔；宇宙运动仍逐游戏 tick 推进。</summary>
        public int lifecycleScanIntervalTicks = 60;

        /// <summary>访问过的空间站现场精确保留天数；到期后可丢弃现场并从冻结 prefab 重建。</summary>
        public int exactMapRetentionDays = 60;

        /// <summary>分帧地图生成每个渲染帧的主线程预算（毫秒）。</summary>
        public float incrementalMapGenBudgetMs = 8f;

        public const int MaxLoadedMapLimit = sbyte.MaxValue + 1;
        public int loadedMapLimit = MaxLoadedMapLimit;

        /// <summary>
        /// 漂移同步就绪的保持时长（tick）：线速度、网格角度与角速度
        /// 持续满足窗口后允许开始作业；不检查位置。
        /// </summary>
        public int dockingHoldTicks = 60;
        public int worksiteMargin = 24;

        /// <summary>同步容差-角度（度）：与所选网格方向差在此内视为已对齐。</summary>
        public float dockingAngleWindowDeg = 1.15f;

        /// <summary>
        /// 对接计划自动取消距离（universe 格，阶段4）：计划船与目标中心距离超出即取消
        /// （通常小于休眠距离，目标图不应在计划期休眠）。
        /// </summary>
        public float dockingCancelDistance = 250f;

        // ---- 被动阻力（解耦架构 §11.1，默认开启）----

        /// <summary>
        /// 「牛顿在棺材里代替飞船旋转」：为平移和旋转增加被动阻力（指数半衰期衰减），
        /// 提高游戏性；与飞行辅助的主动反推并存。不是推进器指令——不消耗油门、
        /// 不生成火焰、不触发拆船。实际积分与 MPC 预测使用同一阻力定义。
        /// </summary>
        public bool passiveDrag = true;

        /// <summary>线速度半衰期（tick；默认 480 = 8 秒）。</summary>
        public int dragLinearHalfLifeTicks = 480;

        /// <summary>角速度半衰期（tick；默认 180 = 3 秒）。</summary>
        public int dragAngularHalfLifeTicks = 180;

        // ---- 分模块诊断日志开关（默认全关；Warn/Error 与必要状态日志不受开关控制）----

        /// <summary>
        /// 太空战开火与补充命中音效：聚焦图位置化发声，非聚焦对象按
        /// 摄像机平面距离衰减。有人说宇宙中没有空气所以不能传声——超凡智能认为这是
        /// 凡人的思维。默认开。
        /// </summary>
        public bool spaceImpactSound = true;

        public bool logCore;

        public bool logInput;

        public bool logPhysics;

        public bool logHarmony;

        public bool logLifecycle;

        public bool logRender;

        /// <summary>战斗链诊断（发射/命中/拦截/跳弹/放行/tag 切换等用户事件级，边沿触发）。</summary>
        public bool logCombat;

        public bool logAI;

        /// <summary>通用临时排障日志开关，默认关闭；具体埋点随问题清理。</summary>
        public bool logDevelopment;
        public bool showShipPlanner;

        /// <summary>每五秒输出帧率、实际 TPS 与 Delta-V 阶段计时；默认关闭。</summary>
        public bool logPerformance;

        // ---- 星区内容生成（「生成」页；全局设置，只影响尚未首次生成的星区）----

        /// <summary>Pure generation recipes. They never classify runtime SpaceObjects.</summary>
        public List<SectorGenerationNode> sectorGenerationRoots = new List<SectorGenerationNode>();
        public SectorGenerationPreset sectorGenerationPreset = SectorGenerationPresets.Default;

        public void EnsureSectorGenerationTable()
        {
            if (sectorGenerationRoots == null || sectorGenerationRoots.Count == 0)
                sectorGenerationRoots = SectorGenerationTable.Defaults();
            if (!SectorGenerationTable.Validate(sectorGenerationRoots, out string error))
            {
                Log.Warning("[DeltaV] Invalid sector generation table was replaced with defaults: " + error);
                sectorGenerationRoots = SectorGenerationTable.Defaults();
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref flightAssist, "flightAssist", true);
            Scribe_Values.Look(ref hideSpaceMapsWithoutPlayerPawns, "hideSpaceMapsWithoutPlayerPawns", true);
            Scribe_Values.Look(ref testScenePlayerPrefab, "testScenePlayerPrefab", "");
            Scribe_Values.Look(ref testSceneEnemyPrefab, "testSceneEnemyPrefab", "");
            Scribe_Values.Look(ref testSceneKeepPrefabMarkers, "testSceneKeepPrefabMarkers", false);
            Scribe_Values.Look(ref testSceneDistance, "testSceneDistance", ShipTestScene.DefaultEnemyDistance);
            Scribe_Values.Look(ref testSceneBearingDeg, "testSceneBearingDeg", ShipTestScene.DefaultEnemyBearingDeg);
            Scribe_Values.Look(ref testScenePlayerAngleDeg, "testScenePlayerAngleDeg", 0f);
            Scribe_Values.Look(ref testSceneEnemyAngleDeg, "testSceneEnemyAngleDeg", ShipTestScene.DefaultEnemyAngleDeg);
            Scribe_Values.Look(ref cellsPerTile, "cellsPerTile", (int)OrbitSphere.DefaultCellsPerTile);
            Scribe_Values.Look(ref universeZoomMax, "universeZoomMax", 400f);
            Scribe_Values.Look(ref poiActiveRadius, "poiActiveRadius", 300f);
            Scribe_Values.Look(ref poiDeleteRadius, "poiDeleteRadius", 1200f);
            Scribe_Values.Look(ref npcDecisionIntervalTicks, "npcDecisionIntervalTicks", 60);
            Scribe_Values.Look(ref lifecycleScanIntervalTicks, "lifecycleScanIntervalTicks", 60);
            Scribe_Values.Look(ref exactMapRetentionDays, "exactMapRetentionDays", 60);
            Scribe_Values.Look(ref incrementalMapGenBudgetMs, "incrementalMapGenBudgetMs", 8f);
            Scribe_Values.Look(ref loadedMapLimit, "loadedMapLimit", MaxLoadedMapLimit);
            Scribe_Values.Look(ref dockingHoldTicks, "dockingHoldTicks", 60);
            Scribe_Values.Look(ref worksiteMargin, "worksiteMargin", 24);
            Scribe_Values.Look(ref dockingAngleWindowDeg, "dockingAngleWindowDeg", 1.15f);
            Scribe_Values.Look(ref dockingCancelDistance, "dockingCancelDistance", 250f);
            Scribe_Values.Look(ref passiveDrag, "passiveDrag", true);
            Scribe_Values.Look(ref dragLinearHalfLifeTicks, "dragLinearHalfLifeTicks", 480);
            Scribe_Values.Look(ref dragAngularHalfLifeTicks, "dragAngularHalfLifeTicks", 180);
            Scribe_Values.Look(ref spaceImpactSound, "spaceImpactSound", true);
            Scribe_Values.Look(ref coordinateGridOpacity, "coordinateGridOpacity", 0.22f);
            Scribe_Values.Look(ref logCore, "logCore", false);
            Scribe_Values.Look(ref logInput, "logInput", false);
            Scribe_Values.Look(ref logPhysics, "logPhysics", false);
            Scribe_Values.Look(ref logHarmony, "logHarmony", false);
            Scribe_Values.Look(ref logLifecycle, "logLifecycle", false);
            Scribe_Values.Look(ref logRender, "logRender", false);
            Scribe_Values.Look(ref logCombat, "logCombat", false);
            Scribe_Values.Look(ref logAI, "logAI", false);
            Scribe_Values.Look(ref logDevelopment, "logDevelopment", false);
            Scribe_Values.Look(ref showShipPlanner, "showShipPlanner", false);
            Scribe_Values.Look(ref logPerformance, "logPerformance", false);
            Scribe_Collections.Look(ref sectorGenerationRoots, "sectorGenerationRoots", LookMode.Deep);
            Scribe_Values.Look(ref sectorGenerationPreset, "sectorGenerationPreset", SectorGenerationPresets.Default);
            base.ExposeData();
        }
    }
}
