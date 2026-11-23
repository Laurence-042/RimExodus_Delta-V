using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Admission and deferred capacity release for real maps.</summary>
    public sealed class SpaceMapPool
    {
        private readonly UniverseManager universe;
        private int requestedSlots;
        private float admissionScore;

        public SpaceMapPool(UniverseManager universe) { this.universe = universe; }

        private static int LoadedMapLimit => Mathf.Clamp(
            DeltaVMod.CurrentSettings?.loadedMapLimit ?? DeltaVSettings.MaxLoadedMapLimit,
            1, DeltaVSettings.MaxLoadedMapLimit);

        internal bool TryMakeRoom(int slots = 1, float score = float.PositiveInfinity)
        {
            int excess = Find.Maps.Count + slots - LoadedMapLimit;
            if (excess <= 0) return true;
            if (slots > LoadedMapLimit || score <= 0f) return false;
            // A request can arrive inside the game tick. Only dispose maps at a frame boundary.
            if (requestedSlots == 0) universe.QueueFrameAction(ReleaseForCapacity);
            if (requestedSlots == 0 || score > admissionScore || (score == admissionScore && slots > requestedSlots))
            {
                requestedSlots = slots;
                admissionScore = score;
            }
            return false;
        }

        private void ReleaseForCapacity()
        {
            int targetCount = LoadedMapLimit - requestedSlots;
            float score = admissionScore;
            requestedSlots = 0;
            admissionScore = 0f;
            if (targetCount < 0 || Find.Maps.Count <= targetCount || universe.IsResettingScene
                || LongEventHandler.AnyEventNowOrWaiting) return;
            var renderer = Find.World.renderer;
            WorldRenderMode view = renderer.wantedMode;
            try
            {
                universe.MapStates.ReleaseForCapacity(targetCount, score);
            }
            finally { renderer.wantedMode = view; }
        }

    }
}
