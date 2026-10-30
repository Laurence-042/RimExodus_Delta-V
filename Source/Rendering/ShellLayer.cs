using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 船壳编排层：调用方经 <see cref="Slot"/>（哪一层 + 层内哪个槽位）显式声明
    /// 绘制目的地；层序由发射顺序保证。相机挂载、网格提交与清深度由后端负责。
    /// </summary>
    internal abstract class ShellLayer
    {
        private IShellDrawBackend backend;
        private int lastEmittedSlot = -1;

        protected abstract CameraEvent Event { get; }

        protected abstract string BufferName { get; }

        private IShellDrawBackend Backend => backend ??= new SharedShellDrawBackend(Event, BufferName);

        /// <summary>绘制目的地：哪一层 + 层内槽位（自下而上的整组序号）。
        /// 由层的 At(i) 构造，防止调用方手工拼装。</summary>
        public readonly struct Slot
        {
            public readonly ShellLayer layer;
            public readonly int index;

            public Slot(ShellLayer layer, int index)
            {
                this.layer = layer;
                this.index = index;
            }
        }

        public Slot At(int index)
        {
            return new Slot(this, index);
        }

        /// <summary>确保当前绘制后端绑定地图相机。</summary>
        public void EnsureAttached(Camera camera)
        {
            Backend.EnsureAttached(camera);
        }

        public void Detach()
        {
            backend?.Detach();
            backend = null;
            lastEmittedSlot = -1;
        }

        /// <summary>每帧开始（含不画的帧）：清理后端帧状态并重置槽位自检基线。</summary>
        public void BeginFrame()
        {
            backend?.BeginFrame();
            lastEmittedSlot = -1;
        }

        /// <summary>
        /// 发射本帧已收集的整壳命令（MapOverlayRenderer 收集管线的发射端）。
        /// 槽位契约：同层槽位自下而上单调递增、槽位属于本层；违反 = 调用方破坏
        /// 层序，WarningOnce 报警（仍照常发射，不吞画面）。
        /// </summary>
        public void Emit(Slot slot)
        {
            if (slot.layer == this)
            {
                if (slot.index > lastEmittedSlot) lastEmittedSlot = slot.index;
                else Log.WarningOnce($"[DeltaV] ShellLayer 槽位回退：{BufferName} slot {slot.index} <= {lastEmittedSlot}，层序契约被破坏", 0x4D31A01 + (int)Event * 2);
            }
            else
            {
                Log.WarningOnce($"[DeltaV] ShellLayer 收到外层槽位：{BufferName} <- {slot.layer.BufferName}", 0x4D31A02 + (int)Event * 2);
            }
            MapOverlayRenderer.EmitShellCommands(Backend);
        }

        /// <summary>本帧全部槽位发射完的收尾；语义由子类定义（两层语义不同，勿合并）。</summary>
        public abstract void Finish(bool anyDrawn);

        protected void FinishLayer()
        {
            backend?.FinishLayer();
        }
    }

    /// <summary>背景层：所有非聚焦壳按 objectId 升序整组绘制，先于聚焦图渲染
    /// （BeforeForwardOpaque）——聚焦图自身内容随后无条件覆盖全部背景壳。</summary>
    internal sealed class ShellBackgroundLayer : ShellLayer
    {
        protected override CameraEvent Event => CameraEvent.BeforeForwardOpaque;

        protected override string BufferName => "DeltaV Shell Layering";

        /// <summary>必要语义的收尾：只清深度、保留颜色——聚焦图内容无条件覆盖
        /// 全部背景壳（玩家关注聚焦图内部，2026-09-20 用户决策）。</summary>
        public override void Finish(bool anyDrawn)
        {
            if (anyDrawn) FinishLayer();
        }
    }

    /// <summary>前台层：弹体与瞄准效果在聚焦图画完之后绘制，不承载对象壳。</summary>
    internal sealed class ShellForegroundLayer : ShellLayer
    {
        protected override CameraEvent Event => CameraEvent.AfterForwardAlpha;

        protected override string BufferName => "DeltaV Foreground Effects";

        /// <summary>防御性收尾：不向前台层之后的其他缓冲遗留本组深度。</summary>
        public override void Finish(bool anyDrawn)
        {
            if (anyDrawn) FinishLayer();
        }
    }
}
