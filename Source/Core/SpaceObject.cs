using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 容器类别 tag（太空战架构 §2/§3.1）：**行为分派维度，不是类型分叉**——
    /// 「弹体」「飞船」都是抽象角色。存储值只有 Container/Marker/Projectile/Shuttle；
    /// Ship 是派生值（图上有有效主台的 Container，见 <see cref="SpaceObject.Tag"/>）。
    /// tag 只驱动单点分派属性（见 SpaceObject 上的分派区），调用方禁止散写 tag 比较；
    /// POI 显示等既有逻辑仍按 PoiState 类型判断，不经 tag。
    /// </summary>
    public enum SpaceObjectTag : byte
    {
        /// <summary>有图普通容器（船图/残骸/物化 POI/空画布锚）。</summary>
        Container = 0,

        /// <summary>未物化 POI 触发器（PoiState marker，恒无图）。</summary>
        Marker = 1,

        /// <summary>弹体（纯弹体是瞬态成员不落盘；侵入飞控的 shuttle 经运行时覆盖派发为本值）。</summary>
        Projectile = 2,

        /// <summary>在飞 shuttle（MiniShip；停靠后回落普通建筑，不再存在本 tag 的对象）。</summary>
        Shuttle = 3,

        /// <summary>派生值：承载有效飞船的容器（图上有主台）。永不落盘、不得手写。</summary>
        Ship = 4
    }

    /// <summary>
    /// 宇宙承载对象（解耦架构 §2）：**一张内部地图与星球轨道坐标之间的中间层**，
    /// 保存该地图整体的轨道运动状态。它不是某艘飞船的子类型或永久所有者——飞船 =
    /// 主驾驶台 + 同归属四连通基架（见 ShipRegistry），同图可承载多艘船与残骸归属。
    ///
    /// **物化容器 = 本类（普通 SpaceObject）**（2026-09-25 用户定案）：有图容器一律
    /// 没有独立的图标/POI 身份——显示名 = 图上全部结构名拼接（UTF-8 字节序），
    /// 图标/剪影按图上实际内容（船→船剪影；基架无船→残骸剪影；纯环境→点+环境名）。
    /// 未物化 POI（PoiState marker）才是无图的触发器特例，按设置的类型显示。
    /// faction 字段保留为环境派系（仅 POI 赋值并持久化；多船图的驾驶授权逐船从主台
    /// 派系判定，不得把容器 faction 当作全部船的授权）。
    ///
    /// 「每个可进入地图都属于一个 SpaceObject」：口袋图 = Delta-V 自建对象的内部，
    /// worldtile 地图 = 原版站点/空画布锚对象的内部；内部地图坐标 ⇄ 轨道坐标的唯一
    /// 通道是所属对象的位姿（对象位姿 ⊕ 内部局部坐标），不存在图级坐标锚。
    ///
    /// "map == null" 只对 PoiState marker 有意义（未物化触发器）；普通容器 map 恒在
    /// （丢失即注销，见 UniverseManager.LoadedGame 清扫）。map 之外的姿态数据是存档
    /// 事实；外壳显示以 Map 的 SectionLayer 为事实源。
    /// </summary>
    public class SpaceObject : IExposable
    {
        public int objectId;

        public SpacePose pose;

        /// <summary>存储 tag（存档事实；默认 Container，PoiState 构造置 Marker，弹体/shuttle 由服务设置）。</summary>
        public SpaceObjectTag baseTag;

        /// <summary>
        /// 侵入式对接的运行时角色覆盖（太空战架构 §7.5）：shuttle 进入侵入飞控时置 true，
        /// <see cref="Tag"/> 随之分派为 Projectile（碰撞主动参与/阻力豁免/可拦截随之生效）。
        /// 属性不经 RimWorld 序列化（只序列化公共字段）——读档后侵入计划丢弃、
        /// shuttle 自动回 shuttle tag 按存档速度漂移（§4.4 口径）。
        /// </summary>
        internal bool projectileRoleOverride { get; set; }

        /// <summary>对应的内部地图（已加载时非空）。Scribe_References 在 cross-ref 阶段解析。</summary>
        public Map map;

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

        [Unsaved] private string cachedLabel;
        [Unsaved] private int cachedLabelVersion = -1;

        // ---- tag 派生与单点分派（太空战架构 §3.1；调用方禁止散写 tag 判断）----

        /// <summary>读档无 tag 键时的缺省值（PoiState 覆盖为 Marker，兼容旧档）。</summary>
        protected virtual SpaceObjectTag DefaultTag => SpaceObjectTag.Container;

        /// <summary>
        /// 有效 tag（派生）：侵入 shuttle → Projectile；有图容器（Container 或带图的原生
        /// 镜像 Marker）且图上有有效主台 → Ship（敌舰 AI 将来按此寻路/索敌的统一入口；
        /// 单点派生、不落盘——船身份的唯一事实源是驾驶台，不变量 #22，不得另存一份）。
        /// 其余 = <see cref="baseTag"/>。
        /// </summary>
        public SpaceObjectTag Tag
        {
            get
            {
                if (baseTag == SpaceObjectTag.Shuttle && projectileRoleOverride) return SpaceObjectTag.Projectile;
                if (map != null && baseTag != SpaceObjectTag.Projectile
                    && baseTag != SpaceObjectTag.Shuttle
                    && ShipRegistry.MapHasMainConsole(map))
                {
                    return SpaceObjectTag.Ship;
                }
                return baseTag;
            }
        }

        /// <summary>瞬态成员（纯弹体）：写档剔除、读档无残留（§4.4）。侵入 shuttle 不算（baseTag=Shuttle 照常落盘）。</summary>
        public bool IsTransient => baseTag == SpaceObjectTag.Projectile;

        /// <summary>碰撞主动参与（弹体恒为主动方；侵入 shuttle 经 Tag 覆盖同样命中，§3.1）。</summary>
        public bool IsActiveCollider => Tag == SpaceObjectTag.Projectile;

        /// <summary>被动阻力适用（弹体豁免——无推进器的飞行体不受阻力减速；shuttle 与船照常）。</summary>
        public bool AppliesPassiveDrag => Tag != SpaceObjectTag.Projectile;

        /// <summary>剪影/星球仪/世界标记/音效/星区记账参与（弹体不进，§3.1；将来弹体上星球仪只改这里）。</summary>
        public bool ParticipatesInOverlays => baseTag != SpaceObjectTag.Projectile;

        /// <summary>点防御可拦截性（结构派生，§3.2：有图 ∧ tag ∈ {projectile, shuttle}；不是 def 标记字段）。</summary>
        public bool InterceptionEligible =>
            map != null && (Tag == SpaceObjectTag.Projectile || baseTag == SpaceObjectTag.Shuttle);

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

        public SpaceObject(int objectId, Map map, SpacePose pose)
        {
            this.objectId = objectId;
            this.map = map;
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

        public virtual void ExposeData()
        {
            Scribe_Values.Look(ref objectId, "objectId", -1);
            Scribe_Values.Look(ref baseTag, "tag", DefaultTag);
            Scribe_References.Look(ref faction, "faction");
            Scribe_Deep.Look(ref pose, "pose");
            Scribe_References.Look(ref map, "map");
            Scribe_Values.Look(ref ambientLabel, "ambientLabel", null);
        }
    }
}
