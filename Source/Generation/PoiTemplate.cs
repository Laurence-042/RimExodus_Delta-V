using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 阶段3 最简太空 POI 模板：残骸飞船——**不对称外形的基架甲板**（2026-09-18 用户
    /// 决策：随机朝向主船体 + 侧向突出部 + 角部咬缺 + 边缘磨损 + 一片脱离主体的小碎块，
    /// 符合残骸定位，也便于提前识别剪影形状/朝向类问题）+ 破损边缘墙 + 散落物资，全部
    /// 由 seed 决定（同 seed 重生成结果一致；玩家改动则由 PoiSnapshot 恢复，优先级高于
    /// 模板）。阶段3 的 POI 不放 pawn（快照不保 pawn 的前提）。
    /// </summary>
    public static class PoiTemplate
    {
        public static void Stamp(Map map, int seed, int ownerId)
        {
            Rand.PushState(seed);
            try
            {
                List<IntVec3> deck = GenerateAsymmetricDeck(map);
                SubstructureOwnership.SetPendingStamp(map, deck, ownerId);
                try
                {
                    foreach (IntVec3 cell in deck)
                    {
                        map.terrainGrid.SetTerrain(cell, DeltaVDefOf.Substructure);
                    }
                }
                finally
                {
                    SubstructureOwnership.RemovePendingStamp(map);
                }
                var deckSet = new HashSet<IntVec3>(deck);
                // 破损边缘墙：甲板边界格（四邻存在非甲板格）约四成缺口，其余钢墙
                //（部分残血，表现为可拆的残骸结构）。
                foreach (IntVec3 cell in deck)
                {
                    if (!IsDeckEdge(cell, deckSet) || Rand.Value < 0.4f)
                    {
                        continue;
                    }
                    Building wall = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                    GenSpawn.Spawn(wall, cell, map, WipeMode.Vanish);
                    if (Rand.Value < 0.5f)
                    {
                        wall.HitPoints = Mathf.CeilToInt(wall.MaxHitPoints * Rand.Range(0.2f, 0.6f));
                    }
                }
                // 散落物资与一台残破推进器（外壳代理的视觉锚点）。
                SpawnItemStack(map, deck, ThingDefOf.Steel, Rand.RangeInclusive(40, 90), Rand.RangeInclusive(2, 4));
                SpawnItemStack(map, deck, ThingDefOf.ComponentIndustrial, Rand.RangeInclusive(5, 15), Rand.RangeInclusive(1, 2));
                IntVec3 thrusterCell = RandomStandableCell(deck, map);
                if (thrusterCell.IsValid)
                {
                    // 生成方式对齐 ShipStructureStamp.SpawnAnchored：MakeThing + 有效
                    // Rot4。直接以 def 重载 + Rot4.Invalid 生成会得到无效旋转的建筑，
                    // 贴图丢失（2026-09-17 实测）。
                    Thing thruster = ThingMaker.MakeThing(DeltaVDefOf.LargeThruster);
                    GenSpawn.Spawn(thruster, thrusterCell, map, new Rot4(Rand.RangeInclusive(0, 3)),
                        WipeMode.Vanish);
                }
            }
            finally
            {
                Rand.PopState();
            }
        }

        /// <summary>
        /// 不对称甲板：随机朝向主船体 + 单侧突出部 + 对角咬缺 + 边缘磨损 + 脱离主体的小
        /// 碎块。咬缺与磨损可能产生不连通区域——残骸本就可以断成几截，剪影上也正好
        /// 用来检验多块形状。全部随机量由外层 Rand.PushState(seed) 驱动，确定性生成。
        /// </summary>
        private static List<IntVec3> GenerateAsymmetricDeck(Map map)
        {
            IntVec3 center = map.Center;
            bool vertical = Rand.Value < 0.5f;
            int length = Rand.RangeInclusive(16, 22);
            int width = Rand.RangeInclusive(9, 13);
            int spanX = vertical ? width : length;
            int spanZ = vertical ? length : width;
            var body = new CellRect(center.x - spanX / 2, center.z - spanZ / 2, spanX, spanZ);

            // 突出部：贴在主船体随机一侧的短粗结构（对接舱段/货桥的残迹）。
            int side = Rand.RangeInclusive(0, 3); // 0=E 1=W 2=N 3=S
            int outLen = Rand.RangeInclusive(3, 6);
            int outWidth = Rand.RangeInclusive(3, Mathf.Min(6, width));
            CellRect protrusion;
            int anchor;
            switch (side)
            {
                case 0:
                    anchor = Rand.RangeInclusive(body.minZ, body.maxZ - outWidth + 1);
                    protrusion = new CellRect(body.maxX + 1, anchor, outLen, outWidth);
                    break;
                case 1:
                    anchor = Rand.RangeInclusive(body.minZ, body.maxZ - outWidth + 1);
                    protrusion = new CellRect(body.minX - outLen, anchor, outLen, outWidth);
                    break;
                case 2:
                    anchor = Rand.RangeInclusive(body.minX, body.maxX - outWidth + 1);
                    protrusion = new CellRect(anchor, body.maxZ + 1, outWidth, outLen);
                    break;
                default:
                    anchor = Rand.RangeInclusive(body.minX, body.maxX - outWidth + 1);
                    protrusion = new CellRect(anchor, body.minZ - outLen, outWidth, outLen);
                    break;
            }

            // 咬缺：从主船体一角切掉一块（爆炸断口的抽象）。
            bool biteEast = side != 0 || Rand.Value < 0.5f; // 与突出部同角时换一侧，保证外形不对称
            bool biteNorth = Rand.Value < 0.5f;
            int biteX = Rand.RangeInclusive(3, 5);
            int biteZ = Rand.RangeInclusive(2, 4);
            CellRect bite = new CellRect(
                biteEast ? body.maxX - biteX + 1 : body.minX,
                biteNorth ? body.maxZ - biteZ + 1 : body.minZ,
                biteX, biteZ);

            var deck = new List<IntVec3>();
            var candidates = new List<IntVec3>();
            candidates.AddRange(body.Cells);
            candidates.AddRange(protrusion.Cells);
            var biteSet = new HashSet<IntVec3>(bite.Cells);
            foreach (IntVec3 cell in candidates)
            {
                if (!cell.InBounds(map) || biteSet.Contains(cell))
                {
                    continue;
                }
                deck.Add(cell);
            }
            // 边缘磨损：边界格约 15% 脱落（可能断开连通——残骸允许断块）。
            var erosionSet = new HashSet<IntVec3>(deck);
            deck.RemoveAll(cell => IsDeckEdge(cell, erosionSet) && Rand.Value < 0.15f);

            // 脱离主体的小碎块：距主体 2-4 格的一小片甲板（剪影多块形状的检验样本）。
            IntVec3 fragmentBase = center + new IntVec3(
                (side == 0 ? -1 : 1) * (spanX / 2 + Rand.RangeInclusive(3, 5)), 0,
                Rand.RangeInclusive(-spanZ / 2, spanZ / 2));
            int fragW = Rand.RangeInclusive(2, 4);
            int fragH = Rand.RangeInclusive(2, 3);
            var fragment = new CellRect(fragmentBase.x, fragmentBase.z, fragW, fragH);
            foreach (IntVec3 cell in fragment)
            {
                if (cell.InBounds(map))
                {
                    deck.Add(cell);
                }
            }
            return deck;
        }

        /// <summary>甲板边界格：四邻存在非甲板格。</summary>
        private static bool IsDeckEdge(IntVec3 cell, HashSet<IntVec3> deck)
        {
            return !deck.Contains(cell + IntVec3.East)
                || !deck.Contains(cell + IntVec3.West)
                || !deck.Contains(cell + IntVec3.North)
                || !deck.Contains(cell + IntVec3.South);
        }

        private static void SpawnItemStack(Map map, List<IntVec3> deck, ThingDef def, int totalCount, int stacks)
        {
            for (int i = 0; i < stacks; i++)
            {
                int count = Mathf.Max(1, totalCount / stacks);
                IntVec3 cell = RandomStandableCell(deck, map);
                if (!cell.IsValid)
                {
                    return;
                }
                Thing item = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                item.stackCount = Mathf.Min(count, def.stackLimit);
                GenSpawn.Spawn(item, cell, map, WipeMode.Vanish);
            }
        }

        private static readonly List<IntVec3> CellBuffer = new List<IntVec3>(64);

        private static IntVec3 RandomStandableCell(List<IntVec3> deck, Map map)
        {
            CellBuffer.Clear();
            foreach (IntVec3 cell in deck)
            {
                if (cell.Standable(map))
                {
                    CellBuffer.Add(cell);
                }
            }
            return CellBuffer.Count == 0 ? IntVec3.Invalid : CellBuffer.RandomElement();
        }
    }
}
