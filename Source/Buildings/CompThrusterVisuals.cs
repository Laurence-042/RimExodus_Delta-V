using UnityEngine;
using Verse;

namespace DeltaV
{
    // Def 配置；只影响表现。普通推进器与 shuttle 虚拟喷口共享这一契约。
    public class ThrusterVisualProfile
    {
        public bool smoke = true;
        public int smokeInterval = 3, smokeLifetime = 150;
        public float smokeSize = 0.65f, smokeGrowth = 3f, exhaustSpeed = 9f, spread = 1.1f;
        public float smokeOpacity = 0.65f;
        public Color smokeColor = new Color(0.85f, 0.88f, 0.92f);
        public bool flame;
        public int flameLifetime = 18;
        public float flameSize = 0.7f, flameSpeed = 22f;
    }

    public class CompProperties_ThrusterVisuals : CompProperties
    {
        public ThrusterVisualProfile exhaust = new ThrusterVisualProfile();
        public CompProperties_ThrusterVisuals() { compClass = typeof(CompThrusterVisuals); }
    }

    public class CompThrusterVisuals : ThingComp
    {
        public ThrusterVisualProfile Profile => ((CompProperties_ThrusterVisuals)props).exhaust;
    }
}
