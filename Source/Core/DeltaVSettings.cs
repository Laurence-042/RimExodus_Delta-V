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

        /// <summary>POI 进入活跃段的距离（universe 格，阶段3）。</summary>
        public float poiActiveRadius = 300f;

        /// <summary>POI 超出此距离即删除收快照（universe 格，阶段3）。</summary>
        public float poiDeleteRadius = 1200f;

        /// <summary>NPC 战术决策间隔；自动驾驶积分和已选目标的武器射击仍逐 tick。</summary>
        public int npcDecisionIntervalTicks = 60;

        /// <summary>POI/普通容器生命周期与归档对象恢复、航行检查间隔。</summary>
        public int lifecycleScanIntervalTicks = 60;

        /// <summary>质量、控制域和碰撞面缓存的按需重扫间隔；结构变更立即失效。</summary>
        public int shipStructureCacheIntervalTicks = 120;

        /// <summary>分帧地图生成每个渲染帧的主线程预算（毫秒）。</summary>
        public float incrementalMapGenBudgetMs = 8f;

        /// <summary>
        /// 对接完成判据的保持时长（tick，2026-09-21 阶段4）：与预定落点位姿重合持续
        /// 这么久才完成对接——防高速掠过误触发；自动驾驶收敛后天然满足。
        /// </summary>
        public int dockingHoldTicks = 60;

        /// <summary>对齐容差-位置（格）：与预定落点距离在此内（且角度在容差内）视为
        /// 已对齐——幽灵船变色提示，保持 dockingHoldTicks 后完成对接。</summary>
        public float dockingPositionWindow = 0.35f;

        /// <summary>对齐容差-角度（度）：与预定落点朝向差在此内视为已对齐。</summary>
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
        /// 太空战命中音效（壳上特效的伴生音）：聚焦守方图位置化发声，非聚焦守方按
        /// 摄像机距离衰减。有人说宇宙中没有空气所以不能传声——超凡智能认为这是
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

        // ---- 星区内容生成（「生成」页；全局设置，只影响尚未首次生成的星区）----

        /// <summary>按类型的星区生成规则（SectorContentTypes 键；缺条目时读注册表默认）。</summary>
        public List<SectorTypeRule> sectorRules = new List<SectorTypeRule>();

        /// <summary>按 prefab 相对路径配置 NPC 战术；生成时复制到对象，不追改已生成对象。</summary>
        public List<PrefabDoctrineRule> prefabDoctrines = new List<PrefabDoctrineRule>();

        public List<NpcDoctrine> DoctrineForPrefab(string path)
        {
            PrefabDoctrineRule rule = prefabDoctrines?.FirstOrDefault(r => r.prefabPath == path);
            return rule?.priorities != null && rule.priorities.Count > 0
                ? new List<NpcDoctrine>(rule.priorities) : NpcDoctrineDefaults.Create();
        }

        /// <summary>
        /// 该叶子类型的生效规则：设置有条目用条目，否则用注册表默认（读路径不静默改写设置）。
        /// </summary>
        public SectorTypeRule EffectiveSectorRule(SectorContentType leaf)
        {
            SectorTypeRule existing = sectorRules?.FirstOrDefault(r => r.typeId == leaf.id);
            if (existing != null) return existing;
            return new SectorTypeRule { typeId = leaf.id, minCount = leaf.defaultMin };
        }

        /// <summary>为注册表中的每个叶子类型补齐缺失的设置条目（只补缺，不覆盖已有值）。</summary>
        public void EnsureSectorRulesSeeded()
        {
            if (sectorRules == null) sectorRules = new List<SectorTypeRule>();
            foreach (SectorContentType leaf in SectorContentTypes.AllLeaves())
            {
                if (sectorRules.Any(r => r.typeId == leaf.id)) continue;
                sectorRules.Add(new SectorTypeRule { typeId = leaf.id, minCount = leaf.defaultMin });
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref flightAssist, "flightAssist", true);
            Scribe_Values.Look(ref cellsPerTile, "cellsPerTile", (int)OrbitSphere.DefaultCellsPerTile);
            Scribe_Values.Look(ref universeZoomMax, "universeZoomMax", 400f);
            Scribe_Values.Look(ref poiActiveRadius, "poiActiveRadius", 300f);
            Scribe_Values.Look(ref poiDeleteRadius, "poiDeleteRadius", 1200f);
            Scribe_Values.Look(ref npcDecisionIntervalTicks, "npcDecisionIntervalTicks", 60);
            Scribe_Values.Look(ref lifecycleScanIntervalTicks, "lifecycleScanIntervalTicks", 60);
            Scribe_Values.Look(ref shipStructureCacheIntervalTicks, "shipStructureCacheIntervalTicks", 120);
            Scribe_Values.Look(ref incrementalMapGenBudgetMs, "incrementalMapGenBudgetMs", 8f);
            Scribe_Values.Look(ref dockingHoldTicks, "dockingHoldTicks", 60);
            Scribe_Values.Look(ref dockingPositionWindow, "dockingPositionWindow", 0.35f);
            Scribe_Values.Look(ref dockingAngleWindowDeg, "dockingAngleWindowDeg", 1.15f);
            Scribe_Values.Look(ref dockingCancelDistance, "dockingCancelDistance", 250f);
            Scribe_Values.Look(ref passiveDrag, "passiveDrag", true);
            Scribe_Values.Look(ref dragLinearHalfLifeTicks, "dragLinearHalfLifeTicks", 480);
            Scribe_Values.Look(ref dragAngularHalfLifeTicks, "dragAngularHalfLifeTicks", 180);
            Scribe_Values.Look(ref spaceImpactSound, "spaceImpactSound", true);
            Scribe_Values.Look(ref logCore, "logCore", false);
            Scribe_Values.Look(ref logInput, "logInput", false);
            Scribe_Values.Look(ref logPhysics, "logPhysics", false);
            Scribe_Values.Look(ref logHarmony, "logHarmony", false);
            Scribe_Values.Look(ref logLifecycle, "logLifecycle", false);
            Scribe_Values.Look(ref logRender, "logRender", false);
            Scribe_Values.Look(ref logCombat, "logCombat", false);
            Scribe_Collections.Look(ref sectorRules, "sectorRules", LookMode.Deep);
            Scribe_Collections.Look(ref prefabDoctrines, "prefabDoctrines", LookMode.Deep);
            base.ExposeData();
        }
    }
}
