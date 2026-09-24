using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 对接口（2026-09-18 基架归属设计）：门类建筑，常闭密封（ExchangeVacuum=false，
    /// 开门通行也不透真空——它就是船体与外界之间的气闸边界）。建造朝向决定对接通道
    /// 的延伸方向。完成停靠后自动沿朝向铺设对接通道（DeltaV_DockingChannel
    /// 地形 + 建造顶）直至遇到首个障碍（wreck 结构/非 Space 地形/建筑）；范围内无障碍
    /// 则保持收起。通道是「类基架」地形：可铺在 Space 上供通行，但不属于基架——不进
    /// 引擎连通域、不参与捕获与剪影。密封由原版房间机制原生承担：通道格有顶且地形
    /// 不 exposesToVacuum，Space 不可通行构成房间边界，通道自成一个不暴露真空的房间
    /// （Room.ExposedToSpace = 无顶 ∨ 地形 exposesToVacuum）。对接口被摧毁/移除（含随
    /// 脱离停靠开始自由飞行）时同步移除通道并还原为 Space。
    /// </summary>
    public class Building_DockingPort : Building_Door
    {
        /// <summary>本端口铺设的通道格（存档事实：多端口通道可能相邻，不可按地形反推）。</summary>
        private List<IntVec3> channelCells = new List<IntVec3>();

        public bool ChannelExtended => channelCells.Count > 0;

        private int ChannelRange => def.GetModExtension<DefModExtension_DockingPort>()?.channelRange ?? 8;

        /// <summary>密封门：开门通行仍不与相邻房间交换真空。</summary>
        public override bool ExchangeVacuum => false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref channelCells, "channelCells", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && channelCells == null)
            {
                channelCells = new List<IntVec3>();
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }
            yield return new Command_Action
            {
                defaultLabel = ChannelExtended
                    ? "DeltaV_DockRetractLabel".Translate()
                    : "DeltaV_DockExtendLabel".Translate(),
                defaultDesc = ChannelExtended
                    ? "DeltaV_DockRetractDesc".Translate()
                    : "DeltaV_DockExtendDesc".Translate(),
                // 1.6 无 UI/Commands/Cancel、UI/Commands/Claim；原版两图标均在 Designators 下。
                icon = ContentFinder<Texture2D>.Get(ChannelExtended ? "UI/Designators/Cancel" : "UI/Designators/Claim"),
                action = delegate
                {
                    if (ChannelExtended) RetractChannel();
                    else TryExtendChannel(silent: false);
                }
            };
        }

        /// <summary>
        /// 降落落定后的自动延伸（战术降落到达重放与原版战略到达统一经 LandingEnded）。
        /// 静默失败：口袋图/开阔太空上的端口范围内无障碍，保持收起即可。
        /// </summary>
        public static void NotifyLandingSettled()
        {
            foreach (Map map in Find.Maps)
            {
                foreach (Thing thing in map.listerThings.ThingsOfDef(DeltaVDefOf.DeltaV_DockingPort))
                {
                    if (thing is Building_DockingPort port && port.ChannelExtended == false)
                    {
                        port.TryExtendChannel(silent: true);
                    }
                }
            }
        }

        /// <summary>
        /// 沿建造朝向铺设通道：逐格推进，Space 空地铺通道格；遇到首个障碍（非 Space
        /// 地形或建筑）即视为对接面，止步于其前一格——0 格也成立（船壳直接贴靠）。
        /// 范围内无任何障碍视为失败（悬空通道无意义）。
        /// </summary>
        public bool TryExtendChannel(bool silent)
        {
            if (!Spawned || ChannelExtended)
            {
                return false;
            }
            Map map = Map;
            IntVec3 step = Rotation.FacingCell;
            var cells = new List<IntVec3>();
            IntVec3 probe = Position + step;
            bool docked = false;
            for (int i = 0; i < ChannelRange; i++)
            {
                if (!probe.InBounds(map))
                {
                    break;
                }
                if (map.terrainGrid.TerrainAt(probe) != TerrainDefOf.Space
                    || probe.GetFirstBuilding(map) != null)
                {
                    docked = true;
                    break;
                }
                cells.Add(probe);
                probe += step;
            }
            if (!docked)
            {
                if (!silent)
                {
                    Messages.Message("DeltaV_DockNoFace".Translate(), this, MessageTypeDefOf.RejectInput, false);
                }
                return false;
            }
            foreach (IntVec3 cell in cells)
            {
                map.terrainGrid.SetTerrain(cell, DeltaVDefOf.DeltaV_DockingChannel);
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            }
            channelCells = cells;
            if (!silent && cells.Count > 0)
            {
                Messages.Message("DeltaV_DockChannelLaid".Translate(cells.Count), this,
                    MessageTypeDefOf.PositiveEvent, false);
            }
            return true;
        }

        /// <summary>收起通道：还原为 Space 并移除随通道铺设的建造顶。</summary>
        public void RetractChannel()
        {
            if (!Spawned)
            {
                channelCells = new List<IntVec3>();
                return;
            }
            Map map = Map;
            foreach (IntVec3 cell in channelCells)
            {
                if (cell.InBounds(map) && map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.DeltaV_DockingChannel)
                {
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Space);
                }
                if (cell.InBounds(map) && map.roofGrid.RoofAt(cell) == RoofDefOf.RoofConstructed)
                {
                    map.roofGrid.SetRoof(cell, null);
                }
            }
            channelCells = new List<IntVec3>();
        }

        /// <summary>
        /// 移除同步（含摧毁、起飞搬运的 WillReplace despawn、脱离停靠）：先收通道再离场。
        /// Thing.Destroy 内部必经 DeSpawn，无需另挂销毁钩子。
        /// </summary>
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            RetractChannel();
            base.DeSpawn(mode);
        }

        // ---- 脱离事务的通道编排（解耦架构 §9）----

        /// <summary>
        /// 本端口的通道是否与指定脱离域相关：端口在脱离域上，或任一通道格与脱离域
        /// 四邻接。A、B、C 场景只收回与本次脱离相关的通道，B、C 之间无关通道保留。
        /// </summary>
        private bool ChannelRelatesTo(HashSet<IntVec3> departingDomain)
        {
            if (departingDomain.Contains(Position))
            {
                return true;
            }
            foreach (IntVec3 cell in channelCells)
            {
                foreach (IntVec3 delta in GenAdj.CardinalDirectionsAround)
                {
                    if (departingDomain.Contains(cell + delta))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 脱离前收集「本次脱离相关通道」上的滞留内容（§9 确认窗清单）：通道格上的
        /// Pawn 与物品——通道收回后它们原格留在未脱离方地图（不自动迁往安全格）。
        /// </summary>
        public static List<Thing> CollectChannelLeftBehind(Map map, HashSet<IntVec3> departingDomain)
        {
            var leftBehind = new List<Thing>();
            if (map == null || departingDomain == null) return leftBehind;
            foreach (Thing thing in map.listerThings.ThingsOfDef(DeltaVDefOf.DeltaV_DockingPort))
            {
                if (!(thing is Building_DockingPort port) || !port.Spawned
                    || !port.ChannelExtended || !port.ChannelRelatesTo(departingDomain))
                {
                    continue;
                }
                foreach (IntVec3 cell in port.channelCells)
                {
                    foreach (Thing content in map.thingGrid.ThingsListAt(cell))
                    {
                        if (content is Pawn || content.def.category == ThingCategory.Item)
                        {
                            leftBehind.Add(content);
                        }
                    }
                }
            }
            return leftBehind;
        }

        /// <summary>
        /// 脱离事务的统一收通道入口（§9：不能只依赖离开一方端口的 DeSpawn——另一侧
        /// 端口仍在原图，也必须通知收回）：收回本图上全部与脱离域相关的通道。
        /// 通道上的 Pawn 与物品原格留置（地形还原 Space 后由原版真空语义接管）。
        /// </summary>
        public static void RetractChannelsFor(Map map, HashSet<IntVec3> departingDomain)
        {
            if (map == null || departingDomain == null) return;
            foreach (Thing thing in map.listerThings.ThingsOfDef(DeltaVDefOf.DeltaV_DockingPort).ToList())
            {
                if (thing is Building_DockingPort port && port.Spawned && port.ChannelExtended
                    && port.ChannelRelatesTo(departingDomain))
                {
                    port.RetractChannel();
                }
            }
        }
    }

    /// <summary>对接口参数：通道最大延伸距离（格）。</summary>
    public class DefModExtension_DockingPort : DefModExtension
    {
        public int channelRange = 8;
    }

    /// <summary>
    /// 对接口放置预览：沿建造朝向绘制延伸带（Space 空地为通道格，首个障碍为对接面，
    /// 红色标记；类似推进器排气区的方向性空间占用可视化）。
    /// </summary>
    public class PlaceWorker_DockingPort : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol,
            Thing thing = null)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            int range = def.GetModExtension<DefModExtension_DockingPort>()?.channelRange ?? 8;
            IntVec3 step = rot.FacingCell;
            IntVec3 probe = center + step;
            var cells = new List<IntVec3>();
            for (int i = 0; i < range && probe.InBounds(map); i++)
            {
                if (map.terrainGrid.TerrainAt(probe) != TerrainDefOf.Space
                    || probe.GetFirstBuilding(map) != null)
                {
                    var face = new List<IntVec3> { probe };
                    GenDraw.DrawFieldEdges(face, new Color(1f, 0.3f, 0.3f, 0.6f));
                    GenDraw.DrawFieldEdges(cells, new Color(0.3f, 1f, 0.5f, 0.4f));
                    return;
                }
                cells.Add(probe);
                probe += step;
            }
            GenDraw.DrawFieldEdges(cells, new Color(0.3f, 1f, 0.5f, 0.4f));
        }
    }
}
