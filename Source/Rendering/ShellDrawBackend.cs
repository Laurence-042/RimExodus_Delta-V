using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeltaV
{
    /// <summary>同步消费已排序的整壳命令；层内槽位与整壳顺序仍由 ShellLayer 管理。</summary>
    internal interface IShellDrawBackend
    {
        void EnsureAttached(Camera camera);
        void BeginFrame();
        void DrawSorted(List<MapOverlayRenderer.OverlayCommand> commands);
        void FinishLayer();
        void Detach();
    }

    /// <summary>RimWorld 1.6 的相机命令缓冲实现，负责提交及保色清深度。</summary>
    internal sealed class CommandBufferShellDrawBackend : IShellDrawBackend
    {
        private readonly CameraEvent cameraEvent;
        private readonly string bufferName;
        private CommandBuffer buffer;
        private Camera attachedCamera;

        internal CommandBufferShellDrawBackend(CameraEvent cameraEvent, string bufferName)
        {
            this.cameraEvent = cameraEvent;
            this.bufferName = bufferName;
        }

        public void EnsureAttached(Camera camera)
        {
            if (buffer != null && attachedCamera == camera) return;
            Detach();
            buffer = new CommandBuffer { name = bufferName };
            attachedCamera = camera;
            attachedCamera.AddCommandBuffer(cameraEvent, buffer);
        }

        public void BeginFrame()
        {
            buffer?.Clear();
        }

        public void DrawSorted(List<MapOverlayRenderer.OverlayCommand> commands)
        {
            ShellSurfaceLayer currentSurface = (ShellSurfaceLayer)byte.MaxValue;
            for (int i = 0; i < commands.Count; i++)
            {
                MapOverlayRenderer.OverlayCommand command = commands[i];
                if (command.surface != currentSurface)
                {
                    ClearDepthOnly();
                    currentSurface = command.surface;
                }
                buffer.DrawMesh(command.mesh, command.matrix, command.material,
                    command.submeshIndex, -1, command.props);
            }
        }

        public void FinishLayer()
        {
            ClearDepthOnly();
        }

        public void Detach()
        {
            if (attachedCamera != null && buffer != null)
                attachedCamera.RemoveCommandBuffer(cameraEvent, buffer);
            buffer?.Release();
            buffer = null;
            attachedCamera = null;
        }

        private void ClearDepthOnly()
        {
            buffer?.ClearRenderTarget(true, false, Color.clear, 1f);
        }
    }
}
