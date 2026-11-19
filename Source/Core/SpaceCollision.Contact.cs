using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceCollision
    {
        // Workspace reused by one confirmed contact. The maps still own all structural state.
        internal sealed class HullImpactBatch
        {
            internal HullMotion source, target;
            internal bool allowCrush, crushed, continues, sourceIsB;
            internal float closing;
            internal List<(IntVec3 a, IntVec3 b)> layer;
            internal readonly HashSet<(IntVec3 a, IntVec3 b)> pairs = new HashSet<(IntVec3, IntVec3)>();
            internal readonly Dictionary<Thing, (TargetView receiver, IntVec3 cell, int damage, Thing instigator)> walls =
                new Dictionary<Thing, (TargetView, IntVec3, int, Thing)>();
            internal readonly Dictionary<(Map map, IntVec3 cell), (TargetView receiver, Thing cover, int damage, Thing instigator)> foundations =
                new Dictionary<(Map, IntVec3), (TargetView, Thing, int, Thing)>();

            internal void Prepare(HullMotion source, HullMotion target, bool allowCrush,
                List<(IntVec3 a, IntVec3 b)> layer, bool sourceIsB)
            {
                this.source = source;
                this.target = target;
                this.allowCrush = allowCrush;
                this.layer = layer;
                this.sourceIsB = sourceIsB;
            }

            internal void Clear()
            {
                source = target = default;
                allowCrush = crushed = continues = sourceIsB = false;
                closing = 0f;
                layer = null;
                pairs.Clear();
                walls.Clear();
                foundations.Clear();
            }
        }

        internal static void ResolveHullContact(HullImpactBatch batch, IntVec3 sourceCell, IntVec3 targetCell,
            HullContact hit, ThingDef def, SweptBody body, Thing launcher, Action<VisualContact> visual)
        {
            var source = batch.source.view.AtPose(batch.source.body.pose);
            var target = batch.target.view.AtPose(batch.target.body.pose);
            if (def == null || !source.CanHullContact || !target.CanHullContact
                || !target.TryStructure(targetCell, out SpaceCellKind kind, out Thing thing)) return;

            body.attacker = source.owner;
            body.projectileDef = def;
            body.hullSource = source.owner;
            body.hullSourceMap = source.map;
            body.hullSourceVersion = source.owner.RepresentationVersion;
            body.hullSourceCell = sourceCell;
            body.launcher = launcher;
            body.weapon = null;
            body.attackerBuilding = null;
            body.mass = body.damageScale = body.armorPenetrationScale = body.beamEnergyScale = 1f;
            body.dead = body.resolvedHit = body.continued = body.didContinue = body.redirected = body.didRedirect = false;
            body.onSelfDestruct = null;
            body.onVisualContact = visual;
            body.from = source.pose.position;
            body.hullBatch = batch;
            var contact = new SweepContact
            {
                target = target, defender = target.owner,
                ray = new RayContact(hit.localPoint, hit.localNormal, targetCell, kind, thing),
                fraction = hit.time, universePoint = target.ToUniverse(hit.localPoint), direction = -hit.localNormal,
                props = def.GetCompProperties<CompProperties_SpaceProjectile>(), effect = SpaceImpactEffects.Resolve(def, thing?.def)
            };
            try { ResolveContact(body, contact); }
            finally { body.hullBatch = null; SharedEvent.hullBatch = null; }
        }
    }
}
