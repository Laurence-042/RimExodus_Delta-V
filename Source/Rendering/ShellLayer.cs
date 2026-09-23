using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 船壳挂载层（2026-09-20 用户决策的结构收口）：一「层」= 一个相机事件上的
    /// 命令缓冲 + 该层的收尾语义。调用方经 <see cref="Slot"/>（哪一层 + 层内哪个
    /// 槽位）显式声明绘制目的地；层序由发射顺序保证，不依赖 3D 深度（机制与
    /// 「勿回退海拔/深度排序」约束见 MapOverlayRenderer.EmitShellCommands 与
    /// AGENTS §9 整壳分层机制）。
    /// </summary>
    internal abstract class ShellLayer
    {
        private CommandBuffer buffer;
        private Camera attachedCamera;
        private int lastEmittedSlot = -1;

        protected abstract CameraEvent Event { get; }

        protected abstract string BufferName { get; }

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

        /// <summary>挂到地图相机（RimExodus SeamlessTileRenderer.EnsureCommandBuffer
        /// 同款）。RimWorld 全程复用同一相机对象；相机身份变化时重挂，防跨相机残留。</summary>
        public void EnsureAttached(Camera camera)
        {
            if (buffer != null && attachedCamera == camera) return;
            Detach();
            buffer = new CommandBuffer { name = BufferName };
            attachedCamera = camera;
            attachedCamera.AddCommandBuffer(Event, buffer);
        }

        public void Detach()
        {
            if (attachedCamera != null && buffer != null)
                attachedCamera.RemoveCommandBuffer(Event, buffer);
            buffer?.Release();
            buffer = null;
            attachedCamera = null;
            lastEmittedSlot = -1;
        }

        /// <summary>每帧开始（含不画的帧）：清空旧命令——不清会在相机上逐帧重放
        /// （RimExodus 同款教训）；同时重置槽位自检基线。</summary>
        public void BeginFrame()
        {
            buffer?.Clear();
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
            MapOverlayRenderer.EmitShellCommands(buffer);
        }

        /// <summary>本帧全部槽位发射完的收尾；语义由子类定义（两层语义不同，勿合并）。</summary>
        public abstract void Finish(bool anyDrawn);

        protected void ClearDepthOnly()
        {
            buffer?.ClearRenderTarget(true, false, Color.clear, 1f);
        }
    }

    /// <summary>背景层：非进场壳按 objectId 升序整组绘制，先于聚焦图渲染
    /// （BeforeForwardOpaque）——聚焦图自身内容随后无条件覆盖全部背景壳。</summary>
    internal sealed class ShellBackgroundLayer : ShellLayer
    {
        protected override CameraEvent Event => CameraEvent.BeforeForwardOpaque;

        protected override string BufferName => "DeltaV Shell Layering";

        /// <summary>必要语义的收尾：只清深度、保留颜色——聚焦图内容无条件覆盖
        /// 全部背景壳（玩家关注聚焦图内部，2026-09-20 用户决策）。</summary>
        public override void Finish(bool anyDrawn)
        {
            if (anyDrawn) ClearDepthOnly();
        }
    }

    /// <summary>前台层：对接进场/选点悬停壳在聚焦图画完之后再绘制。</summary>
    internal sealed class ShellForegroundLayer : ShellLayer
    {
        protected override CameraEvent Event => CameraEvent.AfterForwardAlpha;

        protected override string BufferName => "DeltaV Shell Layering (approach)";

        /// <summary>防御性收尾：不向前台层之后的其他缓冲遗留本组深度。</summary>
        public override void Finish(bool anyDrawn)
        {
            if (anyDrawn) ClearDepthOnly();
        }
    }
}
