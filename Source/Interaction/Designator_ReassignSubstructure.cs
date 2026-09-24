using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 「修改基架归属」命令（Orders 分类，与拆除/开采同组；2026-09-18 基架归属设计）：
    /// 选中后先弹归属选择浮窗——候选 = 当前地图全部基架归属 ID 并集 +「新建独立 ID」
    /// （用于把一个连通域拆成两个互相独立的归属）——再拖选基架格改写归属。改写会
    /// 联动引擎连通域重算与星球仪剪影失效。仅 Delta-V 参考系图有效；RimExodus/原版
    /// 地表图不提供此命令（FrameOf 门禁）。
    /// </summary>
    public class Designator_ReassignSubstructure : Designator_Cells
    {
        private int targetOwnerId = SubstructureOwnership.Unassigned;

        /// <summary>
        /// 区域/计划类格子选择机制（存储区同款 Default2D 样式组：填充矩形/椭圆/线，
        /// 可经原版切样式键换）：拖拽期间实时显示格子区域，松开批量指定。不覆写时
        /// 拖拽器无样式，退化为单击单格。
        /// </summary>
        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Zones;

        public Designator_ReassignSubstructure()
        {
            defaultLabel = "DeltaV_ReassignSubstructure".Translate();
            defaultDesc = "DeltaV_ReassignSubstructureDesc".Translate();
            icon = ContentFinder<Texture2D>.Get("UI/Designators/Claim");
            useMouseIcon = true;
            soundDragSustain = SoundDefOf.Designate_DragStandard;
            soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
            soundSucceeded = SoundDefOf.Designate_Claim;
            tutorTag = "DeltaV_ReassignSubstructure";
        }

        public override void ProcessInput(Event ev)
        {
            // 无可改写基架（地表图/无归属数据/图上没有基架）直接拒绝，不进入选点。
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SubstructureOwnership ownership = OwnershipOnCurrentMap(universe);
            if (ownership == null || ownership.OwnersOnMap().Count == 0)
            {
                Messages.Message("DeltaV_ReassignNoTarget".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            base.ProcessInput(ev);
            OpenOwnerChooser(universe, ownership);
        }

        /// <summary>
        /// 只要求当前图仍是可改写的 Delta-V 图；目标未选（浮窗打开中）也保持选中——
        /// 态势图描边应在选 gizmo 后立即显示。取消浮窗时显式 Deselect。
        /// </summary>
        public override bool CanRemainSelected()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return OwnershipOnCurrentMap(universe) != null;
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SubstructureOwnership ownership = OwnershipOnCurrentMap(universe);
            if (ownership == null)
            {
                return false;
            }
            if (targetOwnerId == SubstructureOwnership.Unassigned)
            {
                return "DeltaV_ReassignPickFirst".Translate();
            }
            if (!c.InBounds(Map) || Map.terrainGrid.TerrainAt(c) != DeltaVDefOf.Substructure)
            {
                return false;
            }
            if (ownership.OwnerAt(c) == targetOwnerId)
            {
                return false; // 已是目标归属：不重复计数
            }
            return AcceptanceReport.WasAccepted;
        }

        public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SubstructureOwnership ownership = OwnershipOnCurrentMap(universe);
            if (ownership == null || targetOwnerId == SubstructureOwnership.Unassigned)
            {
                FinalizeDesignationFailed();
                return;
            }
            // 主台脚下格保护（解耦架构 §3.2 已定）：不写入、其余合法格正常修改、
            // 一次汇总提示——不通过强行改主台归属制造多个主台竞争。
            var protectedCells = new HashSet<IntVec3>();
            foreach (Ship ship in ShipRegistry.ShipsOnMap(Find.CurrentMap))
            {
                if (ship.console?.parent != null && ship.console.parent.Spawned)
                {
                    protectedCells.Add(ship.console.parent.Position);
                }
            }
            int protectedSkipped = 0;
            // 批量一次写入：SetOwner 收尾统一做引擎 dirty 与剪影失效，避免逐格通知。
            var targets = new List<IntVec3>();
            foreach (IntVec3 cell in cells)
            {
                if (!CanDesignateCell(cell).Accepted)
                {
                    continue;
                }
                if (protectedCells.Contains(cell))
                {
                    protectedSkipped++;
                    continue;
                }
                targets.Add(cell);
            }
            if (protectedSkipped > 0)
            {
                Messages.Message("DeltaV_ReassignProtectedMainConsole".Translate(protectedSkipped),
                    MessageTypeDefOf.RejectInput, false);
            }
            if (targets.Count == 0)
            {
                FinalizeDesignationFailed();
                return;
            }
            ownership.SetOwner(targets, targetOwnerId);
            FinalizeDesignationSucceeded();
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            DesignateMultiCell(Gen.YieldSingle(c));
        }

        /// <summary>
        /// 选中期间的归属态势图（类计划/存储区标记的视觉提示）：本图全部基架按当前
        /// 归属着色描边（归属显示色见 SubstructureOwnership.OwnerColor）。名称标签经
        /// pendingLabels 缓存、由 GameComponentOnGUI 在 OnGUI 相位绘制——本方法跑在
        /// Update 相位（DesignatorManagerUpdate），DrawThingLabel/GUI.* 只能在 OnGUI 调用。
        /// </summary>
        public override void SelectedUpdate()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SubstructureOwnership ownership = OwnershipOnCurrentMap(universe);
            if (ownership == null)
            {
                return;
            }
            pendingLabels.Clear();
            pendingLabelFrame = Time.frameCount;
            foreach (KeyValuePair<int, List<IntVec3>> domain in ownership.OwnerDomains())
            {
                Color color = SubstructureOwnership.OwnerColor(domain.Key);
                GenDraw.DrawFieldEdges(domain.Value, color);
                IntVec3 centroid = CentroidOf(domain.Value);
                if (centroid.InBounds(Map) && !centroid.Fogged(Map))
                {
                    pendingLabels.Add((GenMapUI.LabelDrawPosFor(centroid),
                        universe.LabelForOwner(domain.Key), color));
                }
            }
        }

        // Update 相期计算、OnGUI 相期绘制的名称标签缓存（frameCount 守卫防止过期帧残留）。
        private static readonly List<(Vector2 pos, string label, Color color)> pendingLabels =
            new List<(Vector2, string, Color)>();
        private static int pendingLabelFrame = -1;

        /// <summary>GameComponentOnGUI 调用：绘制本命令选中期间的归属名称标签。</summary>
        public static void DrawPendingLabels()
        {
            if (pendingLabels.Count == 0 || pendingLabelFrame != Time.frameCount)
            {
                return;
            }
            foreach ((Vector2 pos, string label, Color color) in pendingLabels)
            {
                GenMapUI.DrawThingLabel(pos, label, color);
            }
        }

        /// <summary>拖选反馈：将被改写的格子按**目标归属色**亮色描边（与态势图的当前色对照）。</summary>
        public override void RenderHighlight(List<IntVec3> dragCells)
        {
            if (targetOwnerId == SubstructureOwnership.Unassigned || dragCells == null)
            {
                return;
            }
            var eligible = new List<IntVec3>();
            foreach (IntVec3 cell in dragCells)
            {
                if (CanDesignateCell(cell).Accepted)
                {
                    eligible.Add(cell);
                }
            }
            if (eligible.Count > 0)
            {
                Color target = SubstructureOwnership.OwnerColor(targetOwnerId);
                target.a = 0.9f;
                GenDraw.DrawFieldEdges(eligible, target);
            }
        }

        public override void DrawMouseAttachments()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (targetOwnerId == SubstructureOwnership.Unassigned)
            {
                GenUI.DrawMouseAttachment(null, "DeltaV_ReassignPickFirst".Translate(), 0f);
                return;
            }
            string targetLabel = universe != null ? universe.LabelForOwner(targetOwnerId) : targetOwnerId.ToString();
            string text = "DeltaV_ReassignTarget".Translate(targetLabel);
            IntVec3 hover = UI.MouseMapPosition().ToIntVec3();
            SubstructureOwnership ownership = OwnershipOnCurrentMap(universe);
            if (ownership != null && hover.InBounds(Map)
                && Map.terrainGrid.TerrainAt(hover) == DeltaVDefOf.Substructure)
            {
                int hoverOwner = ownership.OwnerAt(hover);
                text += "\n" + "DeltaV_ReassignHoverOwner".Translate(
                    hoverOwner == SubstructureOwnership.Unassigned
                        ? "DeltaV_OwnerUnassigned".Translate()
                        : universe.LabelForOwner(hoverOwner));
            }
            GenUI.DrawMouseAttachment(null, text, 0f);
        }

        private static IntVec3 CentroidOf(List<IntVec3> cells)
        {
            long x = 0, z = 0;
            foreach (IntVec3 cell in cells)
            {
                x += cell.x;
                z += cell.z;
            }
            return new IntVec3((int)(x / cells.Count), 0, (int)(z / cells.Count));
        }

        private static SubstructureOwnership OwnershipOnCurrentMap(UniverseManager universe)
        {
            Map map = Find.CurrentMap;
            if (universe?.FrameOf(map) == null)
            {
                return null;
            }
            return map != null ? SubstructureOwnership.TryGet(map) : null;
        }

        private void OpenOwnerChooser(UniverseManager universe, SubstructureOwnership ownership)
        {
            var options = new List<FloatMenuOption>();
            foreach (int ownerId in ownership.OwnersOnMap())
            {
                int captured = ownerId;
                options.Add(new FloatMenuOption(
                    "DeltaV_ReassignOwnerOption".Translate(universe.LabelForOwner(ownerId)),
                    delegate { SelectTarget(captured); }));
            }
            options.Add(new FloatMenuOption(
                "DeltaV_ReassignNewOwnerOption".Translate(),
                delegate { SelectTarget(universe.AllocateOwnerId()); }));
            options.Add(new FloatMenuOption(
                "DeltaV_ReassignCancelOption".Translate(),
                delegate
                {
                    targetOwnerId = SubstructureOwnership.Unassigned;
                    Find.DesignatorManager.Deselect();
                }));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void SelectTarget(int ownerId)
        {
            targetOwnerId = ownerId;
            Messages.Message(
                "DeltaV_ReassignSelected".Translate(
                    Current.Game?.GetComponent<UniverseManager>()?.LabelForOwner(ownerId)),
                MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
