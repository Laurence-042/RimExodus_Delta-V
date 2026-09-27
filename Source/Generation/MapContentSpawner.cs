using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 地图工具包公共回放内核（单实现，2026-09-27 收敛）：「一条 Thing 记录 → 图上一个
    /// 实体」的统一入口与基架归属盖章包裹器。PoiSnapshot（存档事实回放）、
    /// ShipStructureStamp / PoiTemplate（程序化 stamp）、HomePrefab（居住区预制回放）
    /// 一律经此落物，不再各写 MakeThing/SetFaction/GenSpawn 变体（AGENTS §21）。
    /// pawn 不属于记录词汇（PoiSnapshot 不保 pawn；殖民者等生成走各自入口）。
    /// </summary>
    internal static class MapContentSpawner
    {
        /// <summary>
        /// 按记录生成并放置一个 Thing：MakeThing(def, stuff) → hp/堆数/品质/派系恢复 →
        /// GenSpawn.Spawn(WipeMode.Vanish)。hitPoints/stackCount ≤ 0 = 保持默认（满血/单件）；
        /// quality 为 null 或目标无 CompQuality 时跳过品质；派系仅在 def 可持有时写入。
        /// </summary>
        internal static Thing SpawnRecord(Map map, ThingDef def, ThingDef stuff, IntVec3 cell,
            Rot4 rot, int hitPoints, int stackCount, Faction faction, QualityCategory? quality = null)
        {
            Thing thing = ThingMaker.MakeThing(def, stuff);
            if (thing == null)
            {
                return null;
            }
            if (def.useHitPoints && hitPoints > 0)
            {
                thing.HitPoints = Mathf.Clamp(hitPoints, 1, thing.MaxHitPoints);
            }
            if (stackCount > 1 && thing.def.stackLimit > 1)
            {
                thing.stackCount = Mathf.Min(stackCount, thing.def.stackLimit);
            }
            if (quality.HasValue && thing.TryGetComp<CompQuality>() is { } compQuality)
            {
                compQuality.SetQuality(quality.Value, ArtGenerationContext.Outsider);
            }
            if (faction != null && thing.def.CanHaveFaction)
            {
                thing.SetFactionDirect(faction);
            }
            return GenSpawn.Spawn(thing, cell, map, rot, WipeMode.Vanish);
        }

        /// <summary>
        /// 基架归属盖章包裹器（SetPendingStamp 的登记-消费-清除三件套）：管线预先登记
        /// (cells, ownerId)，SetFoundation postfix 消费，无论写入路径如何退出都统一清除。
        /// </summary>
        internal static void WithOwnershipStamp(Map map, IEnumerable<IntVec3> cells, int ownerId, Action write)
        {
            SubstructureOwnership.For(map).SetPendingStamp(cells, ownerId);
            try
            {
                write();
            }
            finally
            {
                SubstructureOwnership.For(map).ClearPendingStamp();
            }
        }
    }
}
