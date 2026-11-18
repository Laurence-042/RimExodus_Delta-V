using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public abstract class AbstractThing : IExposable, IThingHolder
    {
        private ThingOwner<Thing> contents;

        protected AbstractThing() => contents = new ThingOwner<Thing>(this);
        public Thing Entity => contents?.Count > 0 ? contents[0] : null;
        public IThingHolder ParentHolder => null;
        public ThingOwner GetDirectlyHeldThings() => contents;
        public void GetChildHolders(List<IThingHolder> children) =>
            ThingOwnerUtility.AppendThingHoldersFromThings(children, contents);
        public void ExposeData() => Scribe_Deep.Look(ref contents, "contents", this);

        internal virtual SpaceObjectTag Role => SpaceObjectTag.Scattered;
        internal virtual bool Recoverable => Entity is Thing thing && !thing.Destroyed;
        internal virtual Building InterceptionBody => null;

        internal static AbstractThing Create(Thing entity) => entity switch
        {
            Pawn => new AbstractPawn(),
            Building building when building.def.GetCompProperties<CompProperties_SpaceProjectile>()?.guided == true
                => new AbstractMissile(),
            _ when entity?.def.category == ThingCategory.Item => new AbstractItem(),
            _ => null
        };

        internal bool Capture(Thing entity, Map source, IntVec3 cell, ThingOwner holder)
        {
            if (entity.Destroyed || entity.stackCount <= 0 || contents.Count != 0) return false;
            bool onMap = entity.Spawned;
            if (onMap)
            {
                if (holder != null || source == null || source.Disposed
                    || entity.Map != source || entity.Position != cell) return false;
            }
            else if (entity.holdingOwner != holder
                || (holder != null ? !holder.Contains(entity) : source != null)) return false;

            Rot4 rotation = entity.Rotation;
            try
            {
                BeforeTransfer(entity);
                if (holder != null && !holder.Remove(entity)) return false;
                if (onMap) entity.DeSpawn(DestroyMode.WillReplace);
                if (contents.TryAdd(entity, canMergeWithExistingStacks: false)) return true;
            }
            catch
            {
                if (contents.Contains(entity)) contents.Remove(entity);
                throw;
            }
            finally
            {
                if (!contents.Contains(entity) && !entity.Destroyed && !entity.Spawned && entity.holdingOwner == null)
                {
                    if (holder != null)
                    {
                        if (!holder.TryAdd(entity, canMergeWithExistingStacks: false))
                            throw new InvalidOperationException("Could not restore the source holder.");
                    }
                    else if (source != null && !source.Disposed)
                    {
                        entity.Position = cell;
                        entity.Rotation = rotation;
                        entity.SpawnSetup(source, respawningAfterLoad: false);
                    }
                }
            }
            return false;
        }

        internal TractorReceiveResult Handoff(Action<Thing> receive)
        {
            if (!Recoverable) return TractorReceiveResult.Rejected;
            Thing entity = Entity;
            int originalCount = entity.stackCount;
            BeforeTransfer(entity);
            contents.Remove(entity);
            try
            {
                receive(entity);
                if (entity.Spawned) entity.PostSwapMap();
            }
            finally
            {
                if (!entity.Destroyed && !entity.Spawned && entity.holdingOwner == null)
                    contents.TryAdd(entity, canMergeWithExistingStacks: false);
            }
            if (Entity == null) return TractorReceiveResult.Complete;
            return entity.stackCount < originalCount ? TractorReceiveResult.Partial : TractorReceiveResult.Rejected;
        }

        internal TractorReceiveResult Handoff(ThingOwner receiver, int count)
        {
            if (!Recoverable || count <= 0) return TractorReceiveResult.Rejected;
            Thing entity = Entity;
            if (!receiver.CanAcceptAnyOf(entity)) return TractorReceiveResult.Rejected;
            if (count >= entity.stackCount)
                return Handoff(thing => receiver.TryAdd(thing));

            int before = entity.stackCount;
            Thing part = contents.Take(entity, count);
            try { receiver.TryAdd(part); }
            finally
            {
                if (!part.Destroyed && part.holdingOwner == null)
                {
                    if (!entity.TryAbsorbStack(part, respectStackLimit: false))
                        throw new InvalidOperationException("Could not reunite the unreceived portion of a split stack.");
                }
            }
            return entity.stackCount < before
                ? TractorReceiveResult.Partial : TractorReceiveResult.Rejected;
        }

        protected virtual void BeforeTransfer(Thing entity) { }

        internal void DestroyContents()
        {
            Thing entity = Entity;
            if (entity == null) return;
            bool previous = Thing.allowDestroyNonDestroyable;
            try
            {
                Thing.allowDestroyNonDestroyable = true;
                if (!entity.Destroyed) entity.Destroy(DestroyMode.Vanish);
                contents.Remove(entity);
            }
            finally { Thing.allowDestroyNonDestroyable = previous; }
        }

        internal virtual void Draw(Vector3 point) =>
            Entity.Graphic.Draw(point, Entity.Rotation, Entity);

        protected static void DrawDynamic(Thing entity, Vector3 point)
        {
            entity.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, point);
            entity.DynamicDrawPhaseAt(DrawPhase.Draw, point);
        }
    }

    public sealed class AbstractItem : AbstractThing
    {
        internal override void Draw(Vector3 point)
        {
            if (Entity is Corpse corpse)
            {
                if (corpse.InnerPawn != null) DrawDynamic(corpse, point);
                return;
            }
            base.Draw(point);
        }
    }

    public sealed class AbstractPawn : AbstractThing, IThingHolderWithDrawnPawn
    {
        public float HeldPawnDrawPos_Y => AltitudeLayer.Pawn.AltitudeFor();
        public float HeldPawnBodyAngle => 0f;
        public PawnPosture HeldPawnPosture => Entity is Pawn { Downed: true }
            ? PawnPosture.LayingOnGroundNormal : PawnPosture.Standing;

        protected override void BeforeTransfer(Thing entity) => SpaceScatterService.ClearPawnTransferJob(entity);

        internal override void Draw(Vector3 point) => DrawDynamic(Entity, point);
    }

    public sealed class AbstractMissile : AbstractThing
    {
        internal override SpaceObjectTag Role => SpaceObjectTag.Projectile;
        internal override bool Recoverable => false;
        internal override Building InterceptionBody => Entity is Building { Destroyed: false } building ? building : null;
    }
}
