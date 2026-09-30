using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public class CompProperties_PrefabPawnSpawner : CompProperties
    {
        public PawnKindDef pawnKind;
        public int count = 1;
        public float radius = 6f;

        public CompProperties_PrefabPawnSpawner()
        {
            compClass = typeof(CompPrefabPawnSpawner);
        }
    }

    /// <summary>
    /// marker 展开器：在标记建筑附近生成指定数量的 pawn。生成即携带目标派系
    /// （PawnGenerationRequest 的 faction 参数），不做事后 SetFaction。
    /// </summary>
    public class CompPrefabPawnSpawner : CompPrefabMarker
    {
        public CompProperties_PrefabPawnSpawner Props => (CompProperties_PrefabPawnSpawner)props;

        public override void Expand(PrefabSpawnContext context)
        {
            if (Props.pawnKind == null || Props.count <= 0)
            {
                return;
            }
            var candidates = new List<IntVec3>();
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(parent.Position, Props.radius, useCenter: true))
            {
                if (cell.InBounds(context.Map) && cell.Standable(context.Map))
                {
                    candidates.Add(cell);
                }
            }
            for (int i = 0; i < Props.count && candidates.Count > 0; i++)
            {
                IntVec3 cell = candidates[Rand.Range(0, candidates.Count)];
                candidates.Remove(cell);
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(Props.pawnKind, context.Faction));
                GenSpawn.Spawn(pawn, cell, context.Map, WipeMode.Vanish);
            }
        }
    }
}
