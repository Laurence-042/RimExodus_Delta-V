using System.Collections.Generic;
using RimExodus.Maps;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeltaV
{
    internal sealed class SharedShellDrawBackend : IShellDrawBackend
    {
        private readonly MapDrawBackend backend;
        internal SharedShellDrawBackend(CameraEvent cameraEvent, string name)
        { backend = new MapDrawBackend(cameraEvent == CameraEvent.AfterForwardAlpha, name); }
        public void EnsureAttached(Camera camera) => backend.Attach(camera);
        public void BeginFrame() => backend.BeginFrame();
        public void DrawSorted(List<MapOverlayRenderer.OverlayCommand> commands)
        {
            ShellSurfaceLayer surface = (ShellSurfaceLayer)byte.MaxValue;
            foreach (var command in commands)
            {
                if (surface != command.surface) { backend.BeginGroup(); surface = command.surface; }
                backend.Draw(command.shared ?? new MapDrawCommand(command.mesh, command.matrix, command.material,
                    command.submeshIndex, command.props));
            }
        }
        public void FinishLayer() => backend.Finish();
        public void Detach() => backend.Dispose();
    }
}
