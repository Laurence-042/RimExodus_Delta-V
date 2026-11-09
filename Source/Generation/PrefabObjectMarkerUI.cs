using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class PrefabObjectMarkerDraft
    {
        internal string prefabPath;
        internal string contentTypeId;
        internal float distance;
        internal float bearingDegrees;
        internal float relativeAngleDegrees;
        internal bool inheritFaction;
    }

    internal sealed class PrefabClusterMarkerDraft
    {
        internal List<PrefabClusterEntry> entries = new List<PrefabClusterEntry>();
        internal string contentTypeId;
        internal float innerRadius;
        internal float outerRadius;
        internal float densityPerTenThousand;
        internal float minimumSpacing;
        internal bool inheritFaction;
        internal int seed;
    }

    internal static class PrefabMarkerDialogUtility
    {
        internal static void ChoosePrefab(Action<string> selected)
        {
            List<string> paths;
            try { paths = MapPrefab.ListPrefabFiles(); }
            catch (Exception ex)
            {
                Messages.Message(ex.Message, MessageTypeDefOf.RejectInput, false);
                return;
            }
            var options = paths.Select(path =>
            {
                string captured = path;
                return new FloatMenuOption(captured, () => selected(captured));
            }).ToList();
            if (options.Count == 0)
                options.Add(new FloatMenuOption("DeltaV_PrefabMarkerNoPrefabs".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        internal static void ChooseContentType(string current, Action<string> selected)
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption((current.NullOrEmpty() ? "✓ " : "")
                    + "DeltaV_PrefabMarkerNoType".Translate(), () => selected(null))
            };
            foreach (SectorContentType type in SectorContentTypes.AllLeaves())
            {
                SectorContentType captured = type;
                options.Add(new FloatMenuOption((current == type.id ? "✓ " : "") + type.Label,
                    () => selected(captured.id)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        internal static void NumericRow(Rect rect, string label, ref float value, ref string buffer,
            float min, float max)
        {
            Widgets.Label(rect.LeftPart(0.57f), label);
            Widgets.TextFieldNumeric(rect.RightPart(0.4f), ref value, ref buffer, min, max);
        }

        internal static void BottomButtons(Rect rect, Action apply, Action cancel)
        {
            float width = 140f;
            if (Widgets.ButtonText(new Rect(rect.width - width * 2f - 12f, rect.height - 38f, width, 34f),
                "CancelButton".Translate())) cancel();
            if (Widgets.ButtonText(new Rect(rect.width - width, rect.height - 38f, width, 34f),
                "OK".Translate())) apply();
        }
    }

    internal sealed class Dialog_PrefabObjectMarker : Window
    {
        private readonly CompPrefabObjectSpawner marker;
        private readonly PrefabObjectMarkerDraft draft;
        private string distanceBuffer;
        private string bearingBuffer;
        private string facingBuffer;

        public override Vector2 InitialSize => new Vector2(680f, 430f);

        internal Dialog_PrefabObjectMarker(CompPrefabObjectSpawner marker)
        {
            this.marker = marker;
            draft = marker.Draft();
            marker.previewDraft = draft;
            distanceBuffer = draft.distance.ToString();
            bearingBuffer = draft.bearingDegrees.ToString();
            facingBuffer = draft.relativeAngleDegrees.ToString();
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
        }

        public override void DoWindowContents(Rect rect)
        {
            if (marker.parent == null || marker.parent.Destroyed) { Close(); return; }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, rect.width, 32f), "DeltaV_PrefabExactTitle".Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 38f, rect.width, 46f), "DeltaV_PrefabExactHelp".Translate());

            if (Widgets.ButtonText(new Rect(0f, 92f, rect.width, 34f),
                "DeltaV_PrefabMarkerPrefab".Translate(draft.prefabPath.NullOrEmpty() ? "—" : draft.prefabPath)))
                PrefabMarkerDialogUtility.ChoosePrefab(path =>
                {
                    draft.prefabPath = path;
                    PrefabMarkerPreviewCache.Refresh(path);
                });
            if (Widgets.ButtonText(new Rect(0f, 134f, rect.width, 34f),
                "DeltaV_PrefabMarkerContentType".Translate(TypeLabel(draft.contentTypeId))))
                PrefabMarkerDialogUtility.ChooseContentType(draft.contentTypeId, id => draft.contentTypeId = id);

            PrefabMarkerDialogUtility.NumericRow(new Rect(0f, 184f, rect.width, 30f),
                "DeltaV_PrefabMarkerDistance".Translate(), ref draft.distance, ref distanceBuffer,
                0f, PrefabObjectMarkerUtility.MaxRadius);
            PrefabMarkerDialogUtility.NumericRow(new Rect(0f, 220f, rect.width, 30f),
                "DeltaV_PrefabMarkerBearing".Translate(), ref draft.bearingDegrees, ref bearingBuffer,
                -3600f, 3600f);
            PrefabMarkerDialogUtility.NumericRow(new Rect(0f, 256f, rect.width, 30f),
                "DeltaV_PrefabMarkerFacing".Translate(), ref draft.relativeAngleDegrees, ref facingBuffer,
                -3600f, 3600f);
            Widgets.CheckboxLabeled(new Rect(0f, 300f, rect.width, 30f),
                "DeltaV_PrefabMarkerInheritFaction".Translate(), ref draft.inheritFaction);
            Widgets.Label(new Rect(0f, 336f, rect.width, 38f), "DeltaV_PrefabMarkerPreviewHelp".Translate());

            PrefabMarkerDialogUtility.BottomButtons(rect, () =>
            {
                marker.Apply(draft);
                Close();
            }, () => Close());
        }

        private static string TypeLabel(string id)
            => SectorContentTypes.ById(id)?.Label ?? "DeltaV_PrefabMarkerNoType".Translate();

        public override void PreClose()
        {
            marker.previewDraft = null;
            base.PreClose();
        }
    }

    internal sealed class Dialog_PrefabClusterMarker : Window
    {
        private readonly CompPrefabClusterSpawner marker;
        private readonly PrefabClusterMarkerDraft draft;
        private Vector2 scroll;
        private string innerBuffer, outerBuffer, densityBuffer, spacingBuffer, seedBuffer;

        public override Vector2 InitialSize => new Vector2(820f, 690f);

        internal Dialog_PrefabClusterMarker(CompPrefabClusterSpawner marker)
        {
            this.marker = marker;
            draft = marker.Draft();
            marker.previewDraft = draft;
            innerBuffer = draft.innerRadius.ToString();
            outerBuffer = draft.outerRadius.ToString();
            densityBuffer = draft.densityPerTenThousand.ToString();
            spacingBuffer = draft.minimumSpacing.ToString();
            seedBuffer = draft.seed.ToString();
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
        }

        public override void DoWindowContents(Rect rect)
        {
            if (marker.parent == null || marker.parent.Destroyed) { Close(); return; }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, rect.width, 32f), "DeltaV_PrefabClusterTitle".Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 36f, rect.width, 42f), "DeltaV_PrefabClusterHelp".Translate());

            if (Widgets.ButtonText(new Rect(0f, 82f, rect.width, 32f),
                "DeltaV_PrefabMarkerContentType".Translate(TypeLabel(draft.contentTypeId))))
                PrefabMarkerDialogUtility.ChooseContentType(draft.contentTypeId, id => draft.contentTypeId = id);

            float half = (rect.width - 16f) * 0.5f;
            PrefabMarkerDialogUtility.NumericRow(new Rect(0f, 122f, half, 28f),
                "DeltaV_PrefabClusterInner".Translate(), ref draft.innerRadius, ref innerBuffer,
                0f, PrefabObjectMarkerUtility.MaxRadius);
            PrefabMarkerDialogUtility.NumericRow(new Rect(half + 16f, 122f, half, 28f),
                "DeltaV_PrefabClusterOuter".Translate(), ref draft.outerRadius, ref outerBuffer,
                0f, PrefabObjectMarkerUtility.MaxRadius);
            PrefabMarkerDialogUtility.NumericRow(new Rect(0f, 156f, half, 28f),
                "DeltaV_PrefabClusterDensity".Translate(), ref draft.densityPerTenThousand,
                ref densityBuffer, 0f, PrefabObjectMarkerUtility.MaxDensity);
            PrefabMarkerDialogUtility.NumericRow(new Rect(half + 16f, 156f, half, 28f),
                "DeltaV_PrefabClusterSpacing".Translate(), ref draft.minimumSpacing, ref spacingBuffer,
                0f, PrefabObjectMarkerUtility.MaxRadius);
            int seed = draft.seed;
            Widgets.Label(new Rect(0f, 190f, half * .57f, 28f), "DeltaV_PrefabClusterSeed".Translate());
            Widgets.TextFieldNumeric(new Rect(half * .6f, 190f, half * .4f, 28f), ref seed,
                ref seedBuffer, 1, int.MaxValue);
            draft.seed = seed;
            if (Widgets.ButtonText(new Rect(half + 16f, 190f, half, 28f),
                "DeltaV_PrefabClusterReroll".Translate()))
            {
                draft.seed = Rand.Int & int.MaxValue;
                if (draft.seed == 0) draft.seed = 1;
                seedBuffer = draft.seed.ToString();
            }
            Widgets.CheckboxLabeled(new Rect(0f, 224f, rect.width, 28f),
                "DeltaV_PrefabMarkerInheritFaction".Translate(), ref draft.inheritFaction);

            int count = PrefabObjectMarkerUtility.ClusterCount(draft.innerRadius, draft.outerRadius,
                draft.densityPerTenThousand, 128);
            Widgets.Label(new Rect(0f, 254f, rect.width, 30f),
                "DeltaV_PrefabClusterCount".Translate(count));
            Widgets.Label(new Rect(0f, 286f, rect.width - 150f, 28f),
                "DeltaV_PrefabClusterPool".Translate());
            if (Widgets.ButtonText(new Rect(rect.width - 140f, 282f, 140f, 30f),
                "DeltaV_PrefabClusterAdd".Translate()))
                PrefabMarkerDialogUtility.ChoosePrefab(path => draft.entries.Add(new PrefabClusterEntry(path)));

            Rect viewport = new Rect(0f, 318f, rect.width, rect.height - 414f);
            Rect view = new Rect(0f, 0f, viewport.width - 18f,
                Mathf.Max(viewport.height, draft.entries.Count * 42f));
            Widgets.BeginScrollView(viewport, ref scroll, view);
            for (int i = 0; i < draft.entries.Count; i++)
            {
                PrefabClusterEntry entry = draft.entries[i];
                float y = i * 42f;
                if (Widgets.ButtonText(new Rect(0f, y, view.width - 205f, 34f),
                    entry.prefabPath.NullOrEmpty() ? "—" : entry.prefabPath))
                {
                    int captured = i;
                    PrefabMarkerDialogUtility.ChoosePrefab(path =>
                    {
                        draft.entries[captured].prefabPath = path;
                        PrefabMarkerPreviewCache.Refresh(path);
                    });
                }
                Widgets.Label(new Rect(view.width - 198f, y + 5f, 48f, 28f),
                    "DeltaV_PrefabClusterWeight".Translate());
                entry.weightBuffer ??= entry.weight.ToString();
                Widgets.TextFieldNumeric(new Rect(view.width - 150f, y, 82f, 34f), ref entry.weight,
                    ref entry.weightBuffer, 0.01f, 1000f);
                if (Widgets.ButtonText(new Rect(view.width - 60f, y, 60f, 34f), "Delete".Translate()))
                {
                    draft.entries.RemoveAt(i--);
                }
            }
            Widgets.EndScrollView();

            Widgets.Label(new Rect(0f, rect.height - 88f, rect.width - 300f, 42f),
                "DeltaV_PrefabMarkerPreviewHelp".Translate());
            PrefabMarkerDialogUtility.BottomButtons(rect, () =>
            {
                marker.Apply(draft);
                Close();
            }, () => Close());
        }

        private static string TypeLabel(string id)
            => SectorContentTypes.ById(id)?.Label ?? "DeltaV_PrefabMarkerNoType".Translate();

        public override void PreClose()
        {
            marker.previewDraft = null;
            base.PreClose();
        }
    }

    [StaticConstructorOnStartup]
    internal static class PrefabMarkerPreviewCache
    {
        private sealed class Asset
        {
            internal Texture2D texture;
            internal readonly Dictionary<Color32, Material> materials = new Dictionary<Color32, Material>();
        }

        private static readonly Dictionary<string, Asset> Assets =
            new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);

        internal static void Refresh(string path)
        {
            if (path.NullOrEmpty()) return;
            string key = path.Replace('\\', '/');
            if (Assets.TryGetValue(key, out Asset asset)) Destroy(asset);
            Assets.Remove(key);
        }

        private static Asset Get(string path)
        {
            if (path.NullOrEmpty()) return null;
            string key = path.Replace('\\', '/');
            if (Assets.TryGetValue(key, out Asset cached)) return cached;
            try
            {
                MapPrefabDocument doc = MapPrefab.LoadRelative(key);
                var cells = new HashSet<IntVec3>(doc.foundations.Select(entry => entry.pos));
                if (cells.Count == 0)
                {
                    foreach (var terrain in doc.terrain)
                        if (terrain.def != TerrainDefOf.Space)
                            foreach (CellRect rect in terrain.rects)
                                foreach (IntVec3 cell in rect) cells.Add(cell);
                    foreach (var entry in doc.thingEntries)
                        foreach (IntVec3 cell in GenAdj.OccupiedRect(entry.pos, entry.rotation,
                            entry.def.Size)) cells.Add(cell);
                }
                Texture2D texture = ShipSilhouette.BuildTexture(cells,
                    "DeltaV_PrefabMarkerPreview_" + key.Replace('/', '_'));
                cached = new Asset { texture = texture };
            }
            catch (Exception ex)
            {
                Log.Warning($"[DeltaV] Cannot build prefab marker preview for '{key}': {ex.Message}");
                cached = new Asset();
            }
            Assets[key] = cached;
            return cached;
        }

        internal static void Draw(Thing marker, PrefabObjectSpawnPlan plan, Color color)
        {
            if (marker?.Map == null || !marker.Spawned) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject frame = universe?.SpaceObjectOnMap(marker.Map);
            if (frame == null) return;
            Asset asset = Get(plan.prefabPath);
            if (asset?.texture == null) return;
            Color32 key = color;
            if (!asset.materials.TryGetValue(key, out Material material))
            {
                material = new Material(MaterialPool.MatFrom(asset.texture,
                    ShaderDatabase.Transparent, color, 3105))
                {
                    name = "DeltaV_PrefabMarkerPreviewMaterial"
                };
                asset.materials.Add(key, material);
            }
            SpacePose target = SpaceTransform.RigidPointPose(frame.pose, plan.localOffset,
                plan.relativeAngle);
            Vector3 position = SpaceTransform.FocusedDrawPosition(frame, marker.Map, target.position,
                AltitudeLayer.MetaOverlays.AltitudeFor() + Altitudes.AltInc * 4f);
            Quaternion rotation = SpaceTransform.FocusedMeshRotation(frame, target.angle);
            float side = asset.texture.width;
            Matrix4x4 matrix = Matrix4x4.TRS(position, rotation, new Vector3(side, 1f, side));
            Graphics.DrawMesh(MeshPool.plane10, matrix, material, 0);
        }

        internal static void ClearRuntimeCaches()
        {
            foreach (Asset asset in Assets.Values) Destroy(asset);
            Assets.Clear();
        }

        private static void Destroy(Asset asset)
        {
            if (asset == null) return;
            foreach (Material material in asset.materials.Values)
                if (material != null) UnityEngine.Object.Destroy(material);
            if (asset.texture != null) UnityEngine.Object.Destroy(asset.texture);
        }
    }
}
