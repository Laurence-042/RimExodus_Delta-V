using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class PrefabClusterEntry : IExposable
    {
        public string prefabPath;
        public float weight = 1f;
        [Unsaved] internal string weightBuffer;

        public PrefabClusterEntry()
        {
        }

        public PrefabClusterEntry(string prefabPath, float weight = 1f)
        {
            this.prefabPath = prefabPath;
            this.weight = weight;
        }

        public PrefabClusterEntry Copy() => new PrefabClusterEntry(prefabPath, weight);

        public void ExposeData()
        {
            Scribe_Values.Look(ref prefabPath, "prefabPath");
            Scribe_Values.Look(ref weight, "weight", 1f);
        }
    }

    public sealed class CompProperties_PrefabObjectSpawner : CompProperties
    {
        public string prefabPath;
        public float distance = 100f;
        public float bearingDegrees;
        public float relativeAngleDegrees;
        public bool inheritFaction = true;

        public CompProperties_PrefabObjectSpawner()
        {
            compClass = typeof(CompPrefabObjectSpawner);
        }
    }

    public sealed class CompProperties_PrefabClusterSpawner : CompProperties
    {
        public float innerRadius = 80f;
        public float outerRadius = 300f;
        public float densityPerTenThousand = 0.35f;
        public float minimumSpacing = 20f;
        public int maxObjects = 128;
        public bool inheritFaction;

        public CompProperties_PrefabClusterSpawner()
        {
            compClass = typeof(CompPrefabClusterSpawner);
        }
    }

    internal struct PrefabObjectSpawnPlan
    {
        internal string prefabPath;
        internal Vector2 localOffset;
        internal float relativeAngle;
        internal bool inheritFaction;
        internal bool clusterChild;
        internal int seed;
    }

    internal static class PrefabObjectMarkerUtility
    {
        internal const float MaxRadius = 5000f;
        internal const float MaxDensity = 100f;

        internal static bool TryRegister(PrefabSpawnContext context, PrefabObjectSpawnPlan plan,
            out PoiState marker, out string error)
        {
            if (!TryLoadDocument(context, plan.prefabPath, out SpaceObject host,
                out MapPrefabDocument document, out error))
            {
                marker = null;
                return false;
            }
            return TryRegister(context, host, document, plan, out marker, out error);
        }

        internal static bool TryLoadDocument(PrefabSpawnContext context, string prefabPath,
            out SpaceObject host, out MapPrefabDocument document, out string error)
        {
            host = null;
            document = null;
            error = null;
            if (context?.Universe == null || context.Map == null)
            {
                error = "prefab was not spawned in a Delta-V universe map";
                return false;
            }
            host = context.Universe.SpaceObjectOnMap(context.Map);
            if (host == null)
            {
                error = $"{UniverseManager.MapTag(context.Map)} has no registered SpaceObject";
                return false;
            }
            try
            {
                document = MapPrefab.LoadRelative(prefabPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            if (string.Equals(host.sourcePrefabPath?.Replace('\\', '/'),
                prefabPath?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                error = "direct self-reference is not allowed";
                return false;
            }
            return true;
        }

        internal static bool TryRegister(PrefabSpawnContext context, SpaceObject host,
            MapPrefabDocument document, PrefabObjectSpawnPlan plan, out PoiState marker,
            out string error)
        {
            marker = null;
            error = null;
            int objectId = context.Universe.AllocateOwnerId();
            string name = document.name.NullOrEmpty()
                ? Path.GetFileNameWithoutExtension(plan.prefabPath)
                : document.name;
            SpacePose pose = SpaceTransform.RigidPointPose(host.pose, plan.localOffset,
                SpaceTransform.WrapAngle(plan.relativeAngle));
            marker = new PoiState(objectId, $"{name} #{objectId}", pose.position, pose.angle, plan.seed)
            {
                pose = pose,
                faction = plan.inheritFaction ? context.Faction : null,
                clusterChild = InheritsClusterChild(host.clusterChild, plan.clusterChild),
                sourcePrefabPath = plan.prefabPath?.Replace('\\', '/')
            };
            context.Universe.RegisterObject(marker);
            if (context.Universe.ObjectById(objectId) != marker)
            {
                error = $"failed to register SpaceObject #{objectId}";
                marker = null;
                return false;
            }
            return true;
        }

        internal static bool InheritsClusterChild(bool hostIsClusterChild, bool spawnedByCluster)
            => hostIsClusterChild || spawnedByCluster;

        internal static int ClusterCount(float innerRadius, float outerRadius,
            float densityPerTenThousand, int maxObjects)
        {
            float inner = Mathf.Clamp(innerRadius, 0f, MaxRadius);
            float outer = Mathf.Clamp(outerRadius, inner, MaxRadius);
            float density = Mathf.Clamp(densityPerTenThousand, 0f, MaxDensity);
            double area = Math.PI * (outer * (double)outer - inner * (double)inner);
            return Mathf.Clamp(Mathf.RoundToInt((float)(area * density / 10000d)), 0,
                Mathf.Max(0, maxObjects));
        }

        internal static List<PrefabObjectSpawnPlan> BuildClusterPlans(
            IReadOnlyList<PrefabClusterEntry> entries,
            float innerRadius, float outerRadius, float densityPerTenThousand,
            float minimumSpacing, int maxObjects, bool inheritFaction, int seed)
        {
            var usable = entries?.Where(entry => entry != null
                    && !entry.prefabPath.NullOrEmpty() && entry.weight > 0f)
                .Select(entry => entry.Copy()).ToList() ?? new List<PrefabClusterEntry>();
            var result = new List<PrefabObjectSpawnPlan>();
            if (usable.Count == 0) return result;

            float inner = Mathf.Clamp(innerRadius, 0f, MaxRadius);
            float outer = Mathf.Clamp(outerRadius, inner, MaxRadius);
            float spacing = Mathf.Max(0f, minimumSpacing);
            int count = ClusterCount(inner, outer, densityPerTenThousand, maxObjects);
            var random = new System.Random(seed);
            var offsets = new List<Vector2>(count);
            double weightTotal = usable.Sum(entry => (double)entry.weight);
            for (int index = 0; index < count; index++)
            {
                Vector2 best = Vector2.zero;
                float bestNearest = -1f;
                for (int attempt = 0; attempt < 24; attempt++)
                {
                    double radiusSquared = inner * (double)inner
                        + random.NextDouble() * (outer * (double)outer - inner * (double)inner);
                    float radius = (float)Math.Sqrt(radiusSquared);
                    float angle = (float)(random.NextDouble() * Math.PI * 2d);
                    Vector2 candidate = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                    float nearest = float.MaxValue;
                    foreach (Vector2 previous in offsets)
                        nearest = Mathf.Min(nearest, Vector2.Distance(candidate, previous));
                    if (offsets.Count == 0) nearest = float.MaxValue;
                    if (nearest > bestNearest) { bestNearest = nearest; best = candidate; }
                    if (nearest >= spacing) break;
                }
                // A dense request may not admit every derived object. Preserve the authored
                // spacing contract instead of silently placing an overlapping best candidate.
                if (offsets.Count > 0 && bestNearest < spacing) continue;
                offsets.Add(best);

                double pick = random.NextDouble() * weightTotal;
                PrefabClusterEntry selected = usable[usable.Count - 1];
                foreach (PrefabClusterEntry entry in usable)
                {
                    pick -= entry.weight;
                    if (pick <= 0d) { selected = entry; break; }
                }
                result.Add(new PrefabObjectSpawnPlan
                {
                    prefabPath = selected.prefabPath,
                    localOffset = best,
                    relativeAngle = (float)(random.NextDouble() * Math.PI * 2d),
                    inheritFaction = inheritFaction,
                    clusterChild = true,
                    // The SpacePose already owns the object's facing. Keep prefab-local placement
                    // north-up so lifecycle materialization cannot apply a second random rotation.
                    seed = random.Next() & ~3
                });
            }
            return result;
        }

        internal static void DestroyMarker(Thing parent)
        {
            if (parent != null && !parent.Destroyed) parent.Destroy(DestroyMode.Vanish);
        }
    }

    public sealed class CompPrefabObjectSpawner : CompPrefabMarker
    {
        internal string prefabPath;
        internal float distance;
        internal float bearingDegrees;
        internal float relativeAngleDegrees;
        internal bool inheritFaction;
        [Unsaved] internal PrefabObjectMarkerDraft previewDraft;

        private CompProperties_PrefabObjectSpawner Props => (CompProperties_PrefabObjectSpawner)props;

        public override void PostPostMake()
        {
            base.PostPostMake();
            prefabPath = Props.prefabPath;
            distance = Props.distance;
            bearingDegrees = Props.bearingDegrees;
            relativeAngleDegrees = Props.relativeAngleDegrees;
            inheritFaction = Props.inheritFaction;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref prefabPath, "prefabPath", Props.prefabPath);
            Scribe_Values.Look(ref distance, "distance", Props.distance);
            Scribe_Values.Look(ref bearingDegrees, "bearingDegrees", Props.bearingDegrees);
            Scribe_Values.Look(ref relativeAngleDegrees, "relativeAngleDegrees", Props.relativeAngleDegrees);
            Scribe_Values.Look(ref inheritFaction, "inheritFaction", Props.inheritFaction);
        }

        internal PrefabObjectMarkerDraft Draft() => new PrefabObjectMarkerDraft
        {
            prefabPath = prefabPath,
            distance = distance,
            bearingDegrees = bearingDegrees,
            relativeAngleDegrees = relativeAngleDegrees,
            inheritFaction = inheritFaction
        };

        internal void Apply(PrefabObjectMarkerDraft draft)
        {
            prefabPath = draft.prefabPath;
            distance = Mathf.Clamp(draft.distance, 0f, PrefabObjectMarkerUtility.MaxRadius);
            bearingDegrees = Mathf.Repeat(draft.bearingDegrees, 360f);
            relativeAngleDegrees = Mathf.Repeat(draft.relativeAngleDegrees, 360f);
            inheritFaction = draft.inheritFaction;
            PrefabMarkerPreviewCache.Refresh(prefabPath);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!DebugSettings.godMode) yield break;
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_PrefabMarkerConfigure".Translate(),
                defaultDesc = "DeltaV_PrefabMarkerConfigureDesc".Translate(),
                icon = TexCommand.Install,
                action = () => { if (DebugSettings.godMode) Find.WindowStack.Add(new Dialog_PrefabObjectMarker(this)); }
            };
        }

        public override string CompInspectStringExtra()
            => "DeltaV_PrefabExactInspect".Translate(prefabPath.NullOrEmpty() ? "—" : prefabPath,
                distance.ToString("0.#"), bearingDegrees.ToString("0.#"), relativeAngleDegrees.ToString("0.#"));

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            PrefabObjectMarkerDraft draft = previewDraft ?? Draft();
            if (draft.prefabPath.NullOrEmpty()) return;
            float angle = draft.bearingDegrees * Mathf.Deg2Rad;
            var plan = new PrefabObjectSpawnPlan
            {
                prefabPath = draft.prefabPath,
                localOffset = SpaceTransform.Rotate(Vector2.right * Mathf.Max(0f, draft.distance), angle),
                relativeAngle = draft.relativeAngleDegrees * Mathf.Deg2Rad
            };
            PrefabMarkerPreviewCache.Draw(parent, plan, new Color(0.25f, 0.9f, 1f, 0.42f));
        }

        public override void Expand(PrefabSpawnContext context)
        {
            if (prefabPath.NullOrEmpty())
            {
                Log.Error($"[DeltaV] Exact prefab object marker at {parent.Position} has no prefab path.");
                return;
            }
            float bearing = bearingDegrees * Mathf.Deg2Rad;
            var plan = new PrefabObjectSpawnPlan
            {
                prefabPath = prefabPath,
                localOffset = SpaceTransform.Rotate(Vector2.right * Mathf.Max(0f, distance), bearing),
                relativeAngle = relativeAngleDegrees * Mathf.Deg2Rad,
                inheritFaction = inheritFaction,
                seed = Gen.HashCombineInt(parent.thingIDNumber, context.OwnerId) & ~3
            };
            if (!PrefabObjectMarkerUtility.TryRegister(context, plan, out _, out string error))
            {
                Log.Error($"[DeltaV] Exact prefab object marker at {parent.Position} failed: {error}.");
                return;
            }
            PrefabObjectMarkerUtility.DestroyMarker(parent);
        }
    }

    public sealed class CompPrefabClusterSpawner : CompPrefabMarker
    {
        internal List<PrefabClusterEntry> entries = new List<PrefabClusterEntry>();
        internal float innerRadius;
        internal float outerRadius;
        internal float densityPerTenThousand;
        internal float minimumSpacing;
        internal bool inheritFaction;
        internal int seed;
        [Unsaved] internal PrefabClusterMarkerDraft previewDraft;

        private CompProperties_PrefabClusterSpawner Props => (CompProperties_PrefabClusterSpawner)props;

        public override void PostPostMake()
        {
            base.PostPostMake();
            innerRadius = Props.innerRadius;
            outerRadius = Props.outerRadius;
            densityPerTenThousand = Props.densityPerTenThousand;
            minimumSpacing = Props.minimumSpacing;
            inheritFaction = Props.inheritFaction;
            seed = Rand.Int;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref entries, "prefabs", LookMode.Deep);
            Scribe_Values.Look(ref innerRadius, "innerRadius", Props.innerRadius);
            Scribe_Values.Look(ref outerRadius, "outerRadius", Props.outerRadius);
            Scribe_Values.Look(ref densityPerTenThousand, "densityPerTenThousand", Props.densityPerTenThousand);
            Scribe_Values.Look(ref minimumSpacing, "minimumSpacing", Props.minimumSpacing);
            Scribe_Values.Look(ref inheritFaction, "inheritFaction", Props.inheritFaction);
            Scribe_Values.Look(ref seed, "seed", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                entries ??= new List<PrefabClusterEntry>();
                entries.RemoveAll(entry => entry == null);
                if (seed == 0) seed = Gen.HashCombineInt(parent.thingIDNumber, 7919);
            }
        }

        internal PrefabClusterMarkerDraft Draft() => new PrefabClusterMarkerDraft
        {
            entries = entries.Select(entry => entry.Copy()).ToList(),
            innerRadius = innerRadius,
            outerRadius = outerRadius,
            densityPerTenThousand = densityPerTenThousand,
            minimumSpacing = minimumSpacing,
            inheritFaction = inheritFaction,
            seed = seed
        };

        internal void Apply(PrefabClusterMarkerDraft draft)
        {
            entries = draft.entries.Where(entry => entry != null).Select(entry => entry.Copy()).ToList();
            innerRadius = Mathf.Clamp(draft.innerRadius, 0f, PrefabObjectMarkerUtility.MaxRadius);
            outerRadius = Mathf.Clamp(draft.outerRadius, innerRadius, PrefabObjectMarkerUtility.MaxRadius);
            densityPerTenThousand = Mathf.Clamp(draft.densityPerTenThousand, 0f,
                PrefabObjectMarkerUtility.MaxDensity);
            minimumSpacing = Mathf.Clamp(draft.minimumSpacing, 0f, outerRadius * 2f);
            inheritFaction = draft.inheritFaction;
            seed = draft.seed == 0 ? 1 : draft.seed;
            foreach (PrefabClusterEntry entry in entries) PrefabMarkerPreviewCache.Refresh(entry.prefabPath);
        }

        internal List<PrefabObjectSpawnPlan> Plans(PrefabClusterMarkerDraft draft = null)
        {
            PrefabClusterMarkerDraft source = draft ?? Draft();
            return PrefabObjectMarkerUtility.BuildClusterPlans(source.entries,
                source.innerRadius, source.outerRadius, source.densityPerTenThousand,
                source.minimumSpacing, Props.maxObjects, source.inheritFaction, source.seed);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!DebugSettings.godMode) yield break;
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_PrefabMarkerConfigure".Translate(),
                defaultDesc = "DeltaV_PrefabClusterConfigureDesc".Translate(),
                icon = TexCommand.Install,
                action = () => { if (DebugSettings.godMode) Find.WindowStack.Add(new Dialog_PrefabClusterMarker(this)); }
            };
        }

        public override string CompInspectStringExtra()
        {
            int count = PrefabObjectMarkerUtility.ClusterCount(innerRadius, outerRadius,
                densityPerTenThousand, Props.maxObjects);
            return "DeltaV_PrefabClusterInspect".Translate(entries.Count, count,
                innerRadius.ToString("0.#"), outerRadius.ToString("0.#"));
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            List<PrefabObjectSpawnPlan> plans = Plans(previewDraft);
            int limit = Mathf.Min(plans.Count, 64);
            for (int i = 0; i < limit; i++)
                PrefabMarkerPreviewCache.Draw(parent, plans[i], new Color(1f, 0.72f, 0.2f, 0.28f));
        }

        public override void Expand(PrefabSpawnContext context)
        {
            List<PrefabObjectSpawnPlan> plans = Plans();
            if (plans.Count == 0)
            {
                Log.Error($"[DeltaV] Prefab cluster marker at {parent.Position} has no generated objects; "
                    + "check its prefab weights and density.");
                return;
            }
            if (context?.Universe == null || context.Map == null)
            {
                Log.Error($"[DeltaV] Prefab cluster marker at {parent.Position} was not spawned "
                    + "in a Delta-V universe map.");
                return;
            }
            SpaceObject host = context.Universe.SpaceObjectOnMap(context.Map);
            if (host == null)
            {
                Log.Error($"[DeltaV] Prefab cluster marker at {parent.Position}: "
                    + $"{UniverseManager.MapTag(context.Map)} has no registered SpaceObject.");
                return;
            }
            if (host.clusterChild)
            {
                PrefabObjectMarkerUtility.DestroyMarker(parent);
                return;
            }
            var documents = new Dictionary<string, MapPrefabDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (PrefabObjectSpawnPlan plan in plans)
            {
                if (documents.ContainsKey(plan.prefabPath)) continue;
                if (!PrefabObjectMarkerUtility.TryLoadDocument(context, plan.prefabPath,
                    out SpaceObject checkedHost, out MapPrefabDocument document, out string error))
                {
                    Log.Error($"[DeltaV] Prefab cluster marker at {parent.Position} cannot expand "
                        + $"'{plan.prefabPath}': {error}.");
                    return;
                }
                host = checkedHost;
                documents.Add(plan.prefabPath, document);
            }
            var registered = new List<PoiState>(plans.Count);
            foreach (PrefabObjectSpawnPlan plan in plans)
            {
                if (PrefabObjectMarkerUtility.TryRegister(context, host, documents[plan.prefabPath],
                    plan, out PoiState child, out string error))
                {
                    registered.Add(child);
                    continue;
                }
                foreach (PoiState created in registered) context.Universe.UnregisterObject(created);
                Log.Error($"[DeltaV] Prefab cluster marker at {parent.Position} rolled back after "
                    + $"'{plan.prefabPath}' failed: {error}.");
                return;
            }
            PrefabObjectMarkerUtility.DestroyMarker(parent);
        }
    }
}
