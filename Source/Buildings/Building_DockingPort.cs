using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 对接口沿玩家指定朝向生成非基架通道；每格由独立气密的常开门单元承接原版
    /// Region/寻路。密封根入口见 Patch_DockingPort；不铺实体屋顶、不改普通 Space。
    /// </summary>
    public partial class Building_DockingPort : Building_SealedDockingDoor, IHackable
    {
        /// <summary>本端口铺设的通道格（存档事实：多端口通道可能相邻，不可按地形反推）。</summary>
        private List<IntVec3> channelCells = new List<IntVec3>();
        private IntVec3 channelFace = IntVec3.Invalid;
        private bool changingChannel;

        public bool ChannelExtended => channelFace.IsValid;

        public void RebasePrefabLocalState(Map map, IntVec3 sourceMin, IntVec3 root, Rot4 rotation)
        {
            if (channelFace.IsValid)
                channelFace = root + PrefabUtility.GetAdjustedLocalPosition(channelFace - sourceMin, rotation);
            channelCells = channelCells
                .Select(cell => root + PrefabUtility.GetAdjustedLocalPosition(cell - sourceMin, rotation))
                .Where(cell => cell.InBounds(map)
                    && map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.DeltaV_DockingChannel)
                .ToList();
        }

        private int ChannelRange => def.GetModExtension<DefModExtension_DockingPort>()?.channelRange ?? 8;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref channelCells, "channelCells", LookMode.Value);
            Scribe_Values.Look(ref channelFace, "channelFace", IntVec3.Invalid);
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
            if (Faction != Faction.OfPlayer && !PublicFacility.PlayerMayUse(this)) yield break;
            foreach (Gizmo gizmo in SoftDockGizmos()) yield return gizmo;
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
                    if (ChannelExtended) RequestRetraction();
                    else TryExtendChannel(silent: false);
                }
            };
        }

        /// <summary>
        /// 放置落定后的自动延伸（战术对接由 DockingCompletion 通知，原版战略到达由补丁通知）。
        /// 静默失败：口袋图/开阔太空上的端口范围内无障碍，保持收起即可。
        /// </summary>
        public static void NotifyLandingSettled(Map map)
        {
            if (map == null || map.Disposed) return;
            foreach (Thing thing in map.listerThings.ThingsOfDef(DeltaVDefOf.DeltaV_DockingPort).ToList())
            {
                if (thing is Building_DockingPort port && !port.ChannelExtended)
                {
                    port.TryExtendChannel(silent: true);
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
            var cells = new List<IntVec3>();
            if (!TryFindChannel(map, Position, Rotation, ChannelRange, cells, out IntVec3 face))
            {
                if (!silent)
                {
                    Messages.Message("DeltaV_DockNoFace".Translate(), this, MessageTypeDefOf.RejectInput, false);
                }
                return false;
            }
            changingChannel = true;
            channelCells = cells;
            channelFace = face;
            try
            {
                foreach (IntVec3 cell in cells)
                {
                    map.terrainGrid.SetTerrain(cell, DeltaVDefOf.DeltaV_DockingChannel);
                    var segment = (Building_DockingChannel)ThingMaker.MakeThing(DeltaVDefOf.DeltaV_DockingPassage);
                    segment.port = this;
                    segment.SetFaction(Faction);
                    GenSpawn.Spawn(segment, cell, map, Rotation);
                    if (!segment.Spawned)
                        throw new System.InvalidOperationException($"Docking passage did not spawn at {cell}.");
                }
            }
            catch (System.Exception ex)
            {
                changingChannel = false;
                RetractChannel();
                Log.Error($"[DeltaV] Docking passage extension failed on {UniverseManager.MapTag(map)}: {ex}");
                return false;
            }
            finally { changingChannel = false; }
            if (!silent && cells.Count > 0)
            {
                Messages.Message("DeltaV_DockChannelLaid".Translate(cells.Count), this,
                    MessageTypeDefOf.PositiveEvent, false);
            }
            return true;
        }

        /// <summary>延伸与建造预览共用的几何入口；cells 即实际待铺设格，face 不铺设。</summary>
        internal static bool TryFindChannel(Map map, IntVec3 root, Rot4 rotation, int range,
            List<IntVec3> cells, out IntVec3 face, Building_DockingPort existing = null,
            ISet<IntVec3> landingStructure = null)
        {
            cells.Clear();
            face = IntVec3.Invalid;
            IntVec3 step = rotation.FacingCell;
            IntVec3 probe = root + step;
            for (int i = 0; i < range && probe.InBounds(map); i++, probe += step)
            {
                if (existing != null && map.terrainGrid.TerrainAt(probe) == DeltaVDefOf.DeltaV_DockingChannel
                    && probe.GetFirstThing<Building_DockingChannel>(map)?.port == existing)
                {
                    cells.Add(probe);
                    continue;
                }
                // 临时通道不是对接面；禁止串接，避免收回一段时牵连未确认留置内容的另一段。
                if (map.terrainGrid.TerrainAt(probe) == DeltaVDefOf.DeltaV_DockingChannel
                    || probe.GetFirstThing<Building_DockingChannel>(map) != null) return false;
                // 选点时只叠加尚未落位的结构；其余阻断、长度和接触面规则与实物完全相同。
                if (landingStructure?.Contains(probe) == true
                    || map.terrainGrid.TerrainAt(probe) != TerrainDefOf.Space
                    || probe.GetFirstBuilding(map) != null)
                {
                    face = probe;
                    return true;
                }
                cells.Add(probe);
            }
            return false;
        }

        /// <summary>收起通道：只移除本端口生成的单元与地形，内容物原格保留。</summary>
        public void RetractChannel()
        {
            if (changingChannel) return;
            if (!Spawned)
            {
                return;
            }
            Map map = Map;
            changingChannel = true;
            try
            {
                foreach (IntVec3 cell in channelCells)
                {
                    if (!cell.InBounds(map)) continue;
                    var segment = cell.GetFirstThing<Building_DockingChannel>(map);
                    if (segment?.port == this) segment.Destroy(DestroyMode.Vanish);
                    if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.DeltaV_DockingChannel)
                        map.terrainGrid.SetTerrain(cell, TerrainDefOf.Space);
                }
                channelCells.Clear();
                channelFace = IntVec3.Invalid;
            }
            finally { changingChannel = false; }
        }

        private void RequestRetraction()
        {
            List<Thing> contents = ChannelContents().Distinct().ToList();
            if (contents.Count == 0) { RetractChannel(); return; }
            string names = string.Join("\n", contents.Select(t => "- " + t.LabelCap));
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "DeltaV_DockRetractConfirm".Translate(names), () => RetractChannel()));
        }

        private IEnumerable<Thing> ChannelContents()
        {
            if (!Spawned) yield break;
            foreach (IntVec3 cell in channelCells)
                if (cell.InBounds(Map))
                    foreach (Thing content in Map.thingGrid.ThingsListAt(cell))
                        if (content is Pawn || content.def.category == ThingCategory.Item)
                            yield return content;
        }

        public override string GetInspectString()
        {
            string baseText = base.GetInspectString()?.TrimEnd();
            string channelText = Docking?.Peer != null
                ? "DeltaV_SoftDockProgress".Translate(Docking.Peer.LabelCap, Docking.Progress.ToStringPercent("F1")).ToString()
                : (ChannelExtended
                ? "DeltaV_DockConnected".Translate(channelCells.Count)
                : "DeltaV_DockFolded".Translate(ChannelRange)).ToString();
            return baseText.NullOrEmpty() ? channelText : baseText + "\n" + channelText;
        }

        // 结构/地形事件只复核相关端口；没有逐 tick 轮询或第二套通道几何。
        internal static void NotifyCellChanged(Map map, IntVec3 cell)
        {
            if (map == null || map.Disposed) return;
            List<Thing> ports = map.listerThings.ThingsOfDef(DeltaVDefOf.DeltaV_DockingPort);
            if (ports.Count == 0) return;
            foreach (Thing thing in ports.ToList())
            {
                if (!(thing is Building_DockingPort port) || !port.Spawned || port.changingChannel
                    || !port.ChannelExtended || (port.channelFace != cell && !port.channelCells.Contains(cell))) continue;
                var cells = new List<IntVec3>();
                bool valid = TryFindChannel(map, port.Position, port.Rotation, port.ChannelRange,
                    cells, out IntVec3 face, port) && face == port.channelFace
                    && cells.SequenceEqual(port.channelCells);
                if (!valid) port.RetractChannel();
            }
        }

        /// <summary>
        /// 移除同步（含摧毁、起飞搬运的 WillReplace despawn、脱离停靠）：先收通道再离场。
        /// Thing.Destroy 内部必经 DeSpawn，无需另挂销毁钩子。
        /// </summary>
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Current.Game?.GetComponent<UniverseManager>()?.SoftDock.Remove(this);
            RetractChannel();
            base.DeSpawn(mode);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            Current.Game?.GetComponent<UniverseManager>()?.SoftDock.Register(this);
        }

        public void OnHacked(Pawn hacker = null) { SetFaction(hacker?.Faction ?? Faction.OfPlayer); }
        public void OnLockedOut(Pawn hacker = null) { }

        // ---- 脱离事务的通道编排（解耦架构 §9）----

        /// <summary>
        /// 只按本端口与实际对接面判断脱离关系；路过第三艘船旁边不建立连接关系。
        /// </summary>
        private bool ChannelRelatesTo(HashSet<IntVec3> departingDomain)
        {
            if (departingDomain.Contains(Position))
            {
                return true;
            }
            return channelFace.IsValid && departingDomain.Contains(channelFace);
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
                leftBehind.AddRange(port.ChannelContents());
            }
            return leftBehind.Distinct().ToList();
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
            var cells = new List<IntVec3>();
            if (Building_DockingPort.TryFindChannel(map, center, rot, range, cells, out IntVec3 face))
            {
                GenDraw.DrawFieldEdges(new List<IntVec3> { face }, new Color(1f, 0.3f, 0.3f, 0.6f));
            }
            GenDraw.DrawFieldEdges(cells, new Color(0.3f, 1f, 0.5f, 0.4f));
        }
    }
}
