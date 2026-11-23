using System.Collections.Generic;
using RimExodus.Maps;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Optional REF native-map path for nonfocused map containers. REF owns the scoped
    /// Find.CurrentMap/view substitution and invokes each source map's original draw callbacks;
    /// Delta-V only supplies visible sources and their universe-to-focus display transforms.
    /// </summary>
    internal static class NativeMapShellRenderer
    {
        private const string ProviderOwner = SharedMapBackend.Owner + ".NativeMapShells";

        internal static void Configure()
        {
            MapRuntime.Current.Rendering.SetProvider(ProviderOwner, CollectViews);
        }

        internal static void NotifyModeChanged()
        {
            // Clear releases REF's per-path render resources but deliberately keeps providers.
            MapRuntime.Current?.Rendering.Clear();
            ShellRenderer.ClearRuntimeCaches();
        }

        private static void CollectViews(Map host, List<MapRenderPlan> output)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.UseNativeMapShellRendering != true || host == null
                || !WorldRendererUtility.DrawingMap || LongEventHandler.ShouldWaitForEvent) return;
            SpaceObject frame = universe.FrameOf(host);
            if (frame == null || Find.CameraDriver == null) return;

            CellRect displayView = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == frame || !obj.ParticipatesInOverlays || obj.Payload != null
                    || !ShellRenderer.HasDrawableMap(universe, obj)
                    || !ShellRenderer.TryGetMapDisplayTransform(frame, host, obj,
                        out Matrix4x4 matrix, out _)) continue;

                CellRect localView = ShellRenderer.TransformRectInverse(matrix, displayView).ExpandedBy(4);
                if (!localView.Overlaps(CellRect.WholeMap(obj.map))) continue;
                output.Add(new MapRenderPlan(obj.map, matrix, displayView));
            }
        }
    }
}
