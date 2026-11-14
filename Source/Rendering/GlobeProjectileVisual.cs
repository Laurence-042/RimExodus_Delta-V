using UnityEngine;
using Verse;

namespace DeltaV
{
    // Borrow the same Def texture as the in-flight sprite. No texture is copied per shot.
    internal sealed class GlobeProjectileVisual
    {
        internal readonly ThingDef Def;
        internal Vector2 Size => Def.graphicData?.drawSize ?? Vector2.one;
        private Texture texture;
        internal Texture Texture => texture ?? (texture = Def.graphicData?.Graphic?.MatAt(Rot4.North)?.mainTexture);
        internal GlobeProjectileVisual(ThingDef def) { Def = def; }
    }
}
