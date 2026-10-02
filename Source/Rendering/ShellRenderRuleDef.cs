using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 船壳 Thing 的显式规则；无规则时仍自动纳入实心结构。
    /// 绘制来源和表面层级收在同一配置中，不改变碰撞分类。
    /// </summary>
    public class ShellRenderRuleDef : Def
    {
        public ThingDef thingDef;
        public bool exclude;
        public ShellRenderConfig render;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (thingDef == null) yield return $"{defName}: thingDef must be set";
            if (render?.alternateGraphic != null && render.alternateGraphic.graphicClass == null)
            {
                yield return $"{defName}: render.alternateGraphic requires graphicClass";
            }
            if (render?.source == ShellGraphicSource.TurretTop && thingDef?.thingClass != null
                && !typeof(IShellTurretTopSource).IsAssignableFrom(thingDef.thingClass))
                yield return $"{defName}: TurretTop source requires IShellTurretTopSource";
            if (render?.source == ShellGraphicSource.TurretTop && render.alternateGraphic?.graphicClass != null
                && !typeof(Graphic_Single).IsAssignableFrom(render.alternateGraphic.graphicClass))
                yield return $"{defName}: TurretTop alternateGraphic must use Graphic_Single";
        }
    }

    public enum ShellGraphicSource : byte
    {
        Thing,
        TurretTop
    }

    public enum ShellSurfaceLayer : byte
    {
        Hull,
        Exposed,
        Effects
    }

    public class ShellRenderConfig
    {
        public ShellGraphicSource source;
        public ShellSurfaceLayer layer;
        public GraphicData alternateGraphic;
        public float zIndex;

        public Graphic GraphicFor(Thing thing) => alternateGraphic?.GraphicColoredFor(thing) ?? thing.Graphic;
    }

    /// <summary>把原版旋转炮身状态提供给船壳；其他炮塔可按需实现此接口。</summary>
    public interface IShellTurretTopSource
    {
        Material ShellTopMaterial { get; }
        float ShellTopAngle { get; }
    }
}
