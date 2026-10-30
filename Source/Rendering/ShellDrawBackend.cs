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

}
