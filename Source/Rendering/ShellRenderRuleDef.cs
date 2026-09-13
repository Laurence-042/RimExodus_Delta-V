using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 非墙体 Thing 进入外壳代理的显式白名单。alternateGraphic 省略时沿用 Thing 自身
    /// Graphic；zIndex 以一个 RimWorld AltInc 为单位调整前后遮挡，可为负数或小数。
    /// </summary>
    public class ShellRenderRuleDef : Def
    {
        public ThingDef thingDef;

        public GraphicData alternateGraphic;

        public float zIndex;

        public Graphic GraphicFor(Thing thing)
        {
            return alternateGraphic?.GraphicColoredFor(thing) ?? thing.Graphic;
        }

        public override System.Collections.Generic.IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (thingDef == null) yield return $"{defName}: thingDef must be set";
            if (alternateGraphic != null && alternateGraphic.graphicClass == null)
            {
                yield return $"{defName}: alternateGraphic requires graphicClass";
            }
        }
    }
}
