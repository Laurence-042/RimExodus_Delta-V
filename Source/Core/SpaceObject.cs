using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 容器类别 tag（太空战架构 §2/§3.1）：**行为分派维度，不是类型分叉**——
    /// 「弹体」「飞船」都是可叠加的抽象角色。Ship 从共同船体接口派生；
    /// 独立 shuttle 由船体贡献 Shuttle 角色；侵入时在原有角色上追加 Projectile。
    /// tag 只驱动单点分派属性（见 SpaceObject 上的分派区），调用方禁止散写 tag 比较；
    /// POI 显示等既有逻辑仍按 PoiState 类型判断，不经 tag。
    /// </summary>
    [System.Flags]
    public enum SpaceObjectTag : byte
    {
        /// <summary>有图普通容器（船图/残骸/物化 POI/空画布锚）。</summary>
        Container = 1,

        /// <summary>未物化 POI 触发器（PoiState marker，恒无图）。</summary>
        Marker = 2,

        /// <summary>弹体（纯弹体是瞬态成员不落盘；侵入飞控的 shuttle 追加此角色）。</summary>
        Projectile = 4,

        /// <summary>独立飞行的单建筑船角色，由船体提供；供点防在对象层快速过滤。</summary>
        Shuttle = 8,

        /// <summary>派生角色：承载任意 ShipBase 的容器。永不手写。</summary>
        Ship = 16,
        Scattered = 32
    }

    /// <summary>
    /// 宇宙刚体身份、唯一位姿和公共元数据。驾驶信息、预览、执行、清理与漂移由本契约统一暴露。
    /// MapSpaceObject 承载真实图；ArchivedSpaceObject 承载整图快照及必要数值摘要。
    /// 普通船由主台和归属域定义，单建筑船实现同一接口；驾驶主体身份不等于 objectId。
    /// 无主动驾驶能力的基类对象只参与被动物理运动；弹体角色仍由 tag 决定。
    /// </summary>
    public class SpaceObject : IExposable
    {
        public int objectId;

        public SpacePose pose;

        /// <summary>存储 tag（存档事实；默认 Container，PoiState 构造置 Marker，弹体由服务设置）。</summary>
        public SpaceObjectTag baseTag = SpaceObjectTag.Container;

        /// <summary>
        /// 侵入式对接的运行时附加角色（太空战架构 §7.5）：shuttle 进入侵入飞控时置 true，
        /// <see cref="Tag"/> 随之追加 Projectile（碰撞主动参与/阻力豁免/可拦截随之生效）。
        /// 属性不经 RimWorld 序列化（只序列化公共字段）——读档后侵入计划丢弃、
        /// 船体恢复普通容器角色，按存档速度漂移（§4.4 口径）。
        /// </summary>
        internal bool projectileRoleOverride { get; set; }

        /// <summary>对应的内部地图（已加载时非空）。Scribe_References 在 cross-ref 阶段解析。</summary>
        public virtual Map map { get => null; internal set { if (value != null) throw new System.InvalidOperationException("A passive object cannot own a map"); } }
        public int RepresentationVersion { get; internal set; }
        public bool IsArchived => this is ArchivedSpaceObject;

        /// <summary>
        /// 环境派系（POI 的归属事实；纯容器恒为 null）。多船混合时显示层应作组合标记，
        /// 驾驶授权逐船经 ShipRegistry.FactionOf 判定（解耦架构 §10）。
        /// </summary>
        public Faction faction;

        /// <summary>
        /// 环境兜底名（小行星等纯环境内部——图上无任何基架归属时的显示名；从物化
        /// marker 的 envLabel 继承，归档时随拼接名一起带回 marker）。可空；为空且图上
        /// 无结构时显示「未注册对象」。
        /// </summary>
        public string ambientLabel;

        /// <summary>生成时固定的内容类型和 prefab 身份；归档与母港查询沿用。</summary>
        public string contentTypeId;
        public string sourcePrefabPath;
        public List<NpcDoctrine> npcDoctrines;

        /// <summary>NPC 初始能力及最近决策；有图时重新评估战损，无图时使用该摘要。</summary>
        public bool npcBaselineRecorded;
        public float npcInitialThrust;
        public float npcInitialFirepower;
        public int npcInitialMainConsoleId;
        public bool npcRetreating;
        public int npcHomeObjectId;
        public Faction npcFaction;

        [Unsaved] private string cachedLabel;
        [Unsaved] private int cachedLabelVersion = -1;

        // ---- tag 派生与单点分派（太空战架构 §3.1；调用方禁止散写 tag 判断）----

        /// <summary>读档无 tag 键时的缺省值（PoiState 覆盖为 Marker）。</summary>
        protected virtual SpaceObjectTag DefaultTag => SpaceObjectTag.Container;

        /// <summary>
        /// 有效角色集合：船体成员派生 Ship，侵入驱动追加 Projectile，不抹掉原有船体身份。
        /// </summary>
        public SpaceObjectTag Tag
        {
            get
            {
                SpaceObjectTag tags = baseTag | MemberTags;
                if (projectileRoleOverride) tags |= SpaceObjectTag.Projectile;
                return tags;
            }
        }

        public bool HasTag(SpaceObjectTag tag) => (Tag & tag) == tag;
        public bool IsScattered => HasTag(SpaceObjectTag.Scattered);
        public bool IsWorksite => map?.GetComponent<DeltaVPocketMapMetadata>()?.worksite == true;
        public bool HasGravityRegion => map != null && (!(map.Parent is ShipPocketParent_DeltaV)
            || contentTypeId == "Asteroid");
        public bool CanRelocateMap => map != null && !HasGravityRegion && !IsScattered
            && !HasTag(SpaceObjectTag.Projectile);

        /// <summary>有图表示枚举真实船体；无图表示不伪造实体成员。</summary>
        public virtual IEnumerable<ShipBase> Ships => Enumerable.Empty<ShipBase>();
        public IEnumerable<CompSpaceWeapon> Weapons => Ships.SelectMany(ship => ship.Weapons).Distinct();
        public virtual IEnumerable<ShipPhysics.Thruster> BuildThrusters() => Enumerable.Empty<ShipPhysics.Thruster>();
        public virtual IEnumerable<SpaceCollisionCell> CollisionCells => Enumerable.Empty<SpaceCollisionCell>();
        protected virtual SpaceObjectTag MemberTags => ShipFactions.Any() ? SpaceObjectTag.Ship : 0;

        /// <summary>承载船体的派系成员；有图表示读取 ShipBase，无图表示读取卸载时保存的成员事实。</summary>
        protected virtual IEnumerable<Faction> ShipFactions => Enumerable.Empty<Faction>();

        /// <summary>混合派系容器按成员查询；没有船体时才使用环境派系。</summary>
        public bool HasFaction(Faction candidate)
            => candidate != null && Factions.Contains(candidate);

        /// <summary>对象成员派系的共同来源；无船才回退环境归属。</summary>
        public IEnumerable<Faction> Factions
        {
            get
            {
                bool hasShips = false;
                foreach (Faction member in ShipFactions)
                {
                    hasShips = true;
                    yield return member;
                }
                if (!hasShips) yield return faction;
            }
        }

        public bool HostileTo(Faction other) => other != null
            && Factions.Any(member => member != null && member.HostileTo(other));

        /// <summary>瞬态成员（纯弹体）：写档剔除、读档无残留（§4.4）。侵入船体不算（baseTag=Container 照常落盘）。</summary>
        public bool IsTransient => baseTag == SpaceObjectTag.Projectile;

        /// <summary>碰撞主动参与（弹体恒为主动方；侵入 shuttle 追加同一角色，§3.1）。</summary>
        public bool IsActiveCollider => HasTag(SpaceObjectTag.Projectile);

        /// <summary>被动阻力适用（弹体豁免——无推进器的飞行体不受阻力减速；shuttle 与船照常）。</summary>
        public bool AppliesPassiveDrag => baseTag != SpaceObjectTag.Projectile;

        /// <summary>剪影/星球仪/世界标记/音效/星区记账参与（弹体不进，§3.1；将来弹体上星球仪只改这里）。</summary>
        public bool ParticipatesInOverlays => baseTag != SpaceObjectTag.Projectile;

        /// <summary>先按缓存的对象角色筛选点防目标，不在索敌时遍历地图查找船体。</summary>
        public bool InterceptionEligible =>
            map != null && (Tag & (SpaceObjectTag.Projectile | SpaceObjectTag.Shuttle)) != 0;

        /// <summary>显示朝向策略：无姿态角的点状弹体按速度方向，其余按位姿角（§3.1）。</summary>
        public bool UsesVelocityHeading => baseTag == SpaceObjectTag.Projectile;

        /// <summary>
        /// 显示名 = 内部全部结构名拼接（可重建派生，不落盘）；图上无结构时退回
        /// ambientLabel。名称只用于显示，不参与身份与定位（解耦架构 §10）。
        /// </summary>
        public virtual string label
        {
            get
            {
                int version = UniverseManager.StructureNameVersion;
                if (cachedLabelVersion == version && cachedLabel != null) return cachedLabel;
                cachedLabel = ComputeContainerLabel();
                cachedLabelVersion = version;
                return cachedLabel;
            }
        }

        /// <summary>对象的当前内部地图（唯一逻辑内部图，无第二语义）。</summary>
        public virtual Map InteriorMap => map;

        public SpaceObject()
        {
        }

        public SpaceObject(int objectId, SpacePose pose)
        {
            this.objectId = objectId;
            this.pose = pose;
        }

        /// <summary>
        /// 容器拼接名：图上现存基架的归属名按 UTF-8 字节序排序、同名按结构 ID 稳定排序，
        /// 以本地化分隔符连接；同归属的多个未分离断片合为一个条目（一个 ID 只出现一次）。
        /// 图上无任何结构 → ambientLabel（纯环境容器）→「未注册对象」兜底。
        /// </summary>
        private string ComputeContainerLabel()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            Map interior = InteriorMap;
            if (universe == null || interior == null || interior.Disposed)
            {
                return !string.IsNullOrEmpty(ambientLabel)
                    ? ambientLabel
                    : "DeltaV_OwnerUnregistered".Translate(objectId);
            }
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(interior);
            List<int> owners = ownership?.OwnersOnMap();
            if (owners == null || owners.Count == 0)
            {
                return !string.IsNullOrEmpty(ambientLabel)
                    ? ambientLabel
                    : "DeltaV_OwnerUnregistered".Translate(objectId);
            }
            owners.Sort(CompareOwners);
            var parts = new System.Text.StringBuilder();
            foreach (int owner in owners)
            {
                if (parts.Length > 0)
                {
                    parts.Append("DeltaV_NameSeparator".Translate());
                }
                parts.Append(universe.LabelForOwner(owner));
            }
            return parts.ToString();
        }

        /// <summary>排序：名称的 UTF-8 字节序；同名按结构 ID（确定性，与当前语言文化无关）。</summary>
        private int CompareOwners(int a, int b)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            int byName = string.CompareOrdinal(
                universe?.LabelForOwner(a) ?? "", universe?.LabelForOwner(b) ?? "");
            // string.CompareOrdinal 即 UTF-16 码元序；对补充平面外字符与 UTF-8 字节序一致，
            // 本项目名称均为常规字符，二者等价。不同名按名称，同名按 ID。
            return byName != 0 ? byName : a.CompareTo(b);
        }

        protected virtual ShipPhysics.MassReport MotionReport => ShipPhysics.PointMassReport;
        public virtual DriveSubject PrimaryDriver => default;
        public virtual bool TryGetDrive(DriveSubject subject, out DriveInfo info) { info = null; return false; }
        public DriveInfo MotionInfo => ReadDriveInfo();

        /// <summary>命中脉冲交换动量；不推进位置、不触发推进器分离、不另作 tick 积分。</summary>
        internal void CoupleMotionWith(SpaceObject other, Vector3 contact,
            float fraction, float maxImpulse, float maxAngularImpulse)
        {
            if (other == null || other == this || map == null || other.map == null
                || map.Disposed || other.map.Disposed || IsActiveCollider || other.IsActiveCollider) return;
            ShipPhysics.CoupleMotion(this, MotionReport, other, other.MotionReport,
                contact, fraction, maxImpulse, maxAngularImpulse);
        }
        private readonly Dictionary<float, DriveInfo> driveViews = new Dictionary<float, DriveInfo>();
        private ShipPhysics.MassReport driveViewReport;
        protected DriveInfo ReadDriveInfo(float frameAngle = 0f)
        {
            var report = MotionReport;
            if (driveViewReport != report) { driveViewReport = report; driveViews.Clear(); }
            if (!driveViews.TryGetValue(frameAngle, out DriveInfo view))
                driveViews[frameAngle] = view = new DriveInfo(report, frameAngle);
            view.pose = pose;
            return view;
        }
        public virtual DriveResult ApplyDrive(DriveSubject subject, DriveControl control, float dt,
            out float allocatedTorque) { allocatedTorque = 0f; return DriveResult.InvalidSubject; }
        public virtual void ClearDrive() { cachedAllocationReport = null; }
        private int lastMotionTick = -1;
        private int lastThrustTick = -1;
        internal bool PropulsionUsedThisTick => lastThrustTick == Find.TickManager.TicksGame;
        protected bool BeginMotion(float dt)
        {
            if (dt <= 0f) return false;
            int tick = Find.TickManager.TicksGame;
            if (lastMotionTick == tick) return false;
            lastMotionTick = tick;
            return true;
        }
        public void AdvanceDrift(float dt)
        {
            if (!BeginMotion(dt)) return;
            ClearDrive();
            if (pose.velocity.sqrMagnitude < 1e-10f && Mathf.Abs(pose.angularVelocity) < 1e-10f) return;
            ShipPhysics.Integrate(this, MotionReport, Vector2.zero, 0f, dt, false, false, false);
        }
        private ShipPhysics.MassReport cachedAllocationReport;
        private int cachedAllocationVersion, cachedAllocationStep;
        private Vector2 cachedForceRequest, cachedForce;
        private float cachedTorqueRequest, cachedTorque;
        protected virtual DriveResult AuthorizeDrive(DriveSubject subject, ShipPhysics.Command command, bool playerInitiated)
            => DriveResult.Executed;
        protected virtual void AllocateDrive(ShipPhysics.MassReport report, ShipPhysics.Command command,
            out Vector2 force, out float torque) => ShipPhysics.PreviewAllocation(report, command, out force, out torque);
        protected DriveResult ExecuteDrive(ShipPhysics.MassReport report, float frameAngle,
            DriveSubject subject, DriveControl control, float dt, out float allocatedTorque)
        {
            allocatedTorque = 0f;
            if (dt <= 0f || lastMotionTick == Find.TickManager.TicksGame) return DriveResult.AlreadyAdvanced;
            var command = ShipPhysics.ForceToCommand(report, control.force, control.torque);
            DriveResult permission = AuthorizeDrive(subject, command, control.playerInitiated);
            if (permission != DriveResult.Executed) { ClearDrive(); return permission; }
            if (!BeginMotion(dt)) return DriveResult.AlreadyAdvanced;
            int step = Find.TickManager.TicksGame / 6;
            if (cachedAllocationReport != report || cachedAllocationVersion != report.version
                || cachedAllocationStep != step || cachedForceRequest != control.force || cachedTorqueRequest != control.torque)
            {
                AllocateDrive(report, command, out cachedForce, out cachedTorque);
                cachedAllocationReport = report; cachedAllocationVersion = report.version; cachedAllocationStep = step;
                cachedForceRequest = control.force; cachedTorqueRequest = control.torque;
            }
            Vector2 force = cachedForce;
            allocatedTorque = cachedTorque;
            foreach (ShipPhysics.Thruster thruster in report.thrusters)
            {
                if (!thruster.operational || thruster.thrust <= 0f || thruster.throttle < .0001f) continue;
                lastThrustTick = Find.TickManager.TicksGame;
                break;
            }
            ShipPhysics.Integrate(this, report, Vector2.Lerp(force, control.force, control.voidBlend),
                Mathf.Lerp(allocatedTorque, control.torque, control.voidBlend), dt,
                control.assistX, control.assistZ, control.assistW, frameAngle);
            return DriveResult.Executed;
        }
        public void SettleDrive(Vector3 direction, float angle)
        {
            ClearDrive();
            if (!BeginMotion(1f / 60f)) return;
            pose.position = direction; pose.angle = angle;
            pose.velocity = Vector2.zero; pose.angularVelocity = 0f;
        }
        internal void CopyFactsTo(SpaceObject other)
        {
            other.objectId = objectId; other.pose = pose; other.baseTag = baseTag;
            other.faction = faction; other.ambientLabel = ambientLabel;
            other.contentTypeId = contentTypeId; other.sourcePrefabPath = sourcePrefabPath;
            other.npcDoctrines = npcDoctrines;
            other.npcBaselineRecorded = npcBaselineRecorded;
            other.npcInitialThrust = npcInitialThrust; other.npcInitialFirepower = npcInitialFirepower;
            other.npcInitialMainConsoleId = npcInitialMainConsoleId;
            other.npcRetreating = npcRetreating; other.npcHomeObjectId = npcHomeObjectId;
            other.npcFaction = npcFaction;
            other.lastMotionTick = lastMotionTick;
        }

        public virtual void ExposeData()
        {
            Scribe_Values.Look(ref objectId, "objectId", -1);
            Scribe_Values.Look(ref baseTag, "tag", DefaultTag);
            Scribe_References.Look(ref faction, "faction");
            Scribe_Deep.Look(ref pose, "pose");
            Scribe_Values.Look(ref ambientLabel, "ambientLabel", null);
            Scribe_Values.Look(ref contentTypeId, "contentTypeId");
            Scribe_Values.Look(ref sourcePrefabPath, "sourcePrefabPath");
            Scribe_Collections.Look(ref npcDoctrines, "npcDoctrines", LookMode.Value);
            Scribe_Values.Look(ref npcBaselineRecorded, "npcBaselineRecorded");
            Scribe_Values.Look(ref npcInitialThrust, "npcInitialThrust");
            Scribe_Values.Look(ref npcInitialFirepower, "npcInitialFirepower");
            Scribe_Values.Look(ref npcInitialMainConsoleId, "npcInitialMainConsoleId");
            Scribe_Values.Look(ref npcRetreating, "npcRetreating");
            Scribe_Values.Look(ref npcHomeObjectId, "npcHomeObjectId");
            Scribe_References.Look(ref npcFaction, "npcFaction");

        }
    }
}
