using RimWorld;
using Verse;

namespace DeltaV
{
    public class CompProperties_PrefabPawnSpawner : CompProperties
    {
        public PawnKindDef pawnKind;
        public PawnKindDef fallbackPawnKind;
        public float fallbackRadius = 3f;

        public CompProperties_PrefabPawnSpawner()
        {
            compClass = typeof(CompPrefabPawnSpawner);
        }
    }

    /// <summary>
    /// 一个标记生成一名 pawn。优先原格；只有原格不可站立时才在附近找落点。
    /// 未指定 pawnKind 时先取目标派系基础成员，再走原版派系 PawnKind 池；
    /// 都没有时使用 Def 的后备种类。
    /// </summary>
    public class CompPrefabPawnSpawner : CompPrefabMarker
    {
        public CompProperties_PrefabPawnSpawner Props => (CompProperties_PrefabPawnSpawner)props;

        public override void Expand(PrefabSpawnContext context)
        {
            PawnKindDef kind = Props.pawnKind
                ?? context.Faction?.def.basicMemberKind
                ?? context.Faction?.RandomPawnKind()
                ?? Props.fallbackPawnKind;
            if (kind == null)
            {
                Log.Warning($"[DeltaV] Pawn prefab marker at {parent.Position} has no usable PawnKind "
                    + $"(faction={context.Faction?.def.defName ?? "none"}).");
                return;
            }
            IntVec3 spawnCell = IntVec3.Invalid;
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(parent.Position, Props.fallbackRadius, useCenter: true))
            {
                if (cell.InBounds(context.Map) && cell.Standable(context.Map)
                    && cell.GetFirstPawn(context.Map) == null
                    && (cell == parent.Position || cell.GetFirstBuilding(context.Map) == null))
                {
                    spawnCell = cell;
                    break;
                }
            }
            if (!spawnCell.IsValid)
            {
                Log.Warning($"[DeltaV] Pawn prefab marker at {parent.Position} has no standable spawn cell.");
                return;
            }
            // Pocket maps have no world tile; use the same tile-free request as
            // procedural ship crew generation.
            Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, context.Faction));
            SpacePawnEquipment.EnsureVacuumSuit(pawn);
            if (GenSpawn.Spawn(pawn, spawnCell, context.Map, WipeMode.Vanish) != pawn || !pawn.Spawned)
            {
                Log.Warning($"[DeltaV] Pawn prefab marker at {parent.Position} failed to spawn {kind.defName} at {spawnCell}.");
                return;
            }
            if (!parent.Destroyed)
            {
                parent.Destroy(DestroyMode.Vanish);
            }
        }
    }
}
