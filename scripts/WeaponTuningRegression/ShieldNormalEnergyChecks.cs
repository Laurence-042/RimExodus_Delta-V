using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    static void CheckShieldNormalEnergy()
    {
        Check(Near(SpaceKinetics.ShieldCost(1, -60, 360, 2), 42)
            && SpaceKinetics.ShieldCost(1, -60, 0, 2) == 0, "reflection pays positive work; zero impulse has no charge");
        foreach (float mass in new[] { 1f, 2f, 20f, 48f, 96f, 144f })
        foreach (float impulse in new[] { 0f, 36f, 180f, 360f })
        {
            var incoming = new Vector2(17, -60);
            Vector2 outgoing = SpaceKinetics.ApplyShieldImpulse(mass, incoming, Vector2.up, impulse);
            Check(Near(outgoing.x, 17) && Near((outgoing.y + 60) * mass, impulse), "normal momentum changes by paid impulse; tangent preserved");
            foreach (float angle in new[] { -.7f, 1.1f, 2.6f })
            {
                Vector2 rotated = SpaceKinetics.ApplyShieldImpulse(mass, SpaceTransform.Rotate(incoming, angle),
                    SpaceTransform.Rotate(Vector2.up, angle), impulse);
                Check((rotated - SpaceTransform.Rotate(outgoing, angle)).magnitude < .001f, "impulse response is invariant under shared rotation");
            }
            foreach (float available in new[] { 0f, 1f, 2f, 3f, 10f, 100f })
            {
                float paid = SpaceKinetics.FundedShieldImpulse(mass, -60, impulse, 2, available);
                Check(paid <= impulse && SpaceKinetics.ShieldCost(mass, -60, paid, 2) <= available + .00001f,
                    "partially funded pulse never borrows energy or exceeds selected impulse");
            }
        }
        var normal = new CompProperties_SpaceProjectile { shieldFragmentCount = 3 };
        var rod = new CompProperties_SpaceProjectile { shieldFragmentCount = 4,
            shieldTumbleDeltaV = .15f, shieldBreakDeltaV = .5f };
        foreach (float mass in new[] { 1f, 1.5f, 2f, 3f, 20f, 48f, 96f, 144f })
        foreach (float angle in new[] { 0f, 1f, 5f, 15f, 30f, 60f, 85f })
        foreach (int strength in new[] { 0, 5, 10, 20, 50, 100 })
        foreach (var props in new[] { normal, rod })
        {
            var incoming = new Vector2(Mathf.Sin(angle*Mathf.Deg2Rad), -Mathf.Cos(angle*Mathf.Deg2Rad))*60;
            float impulse = SpaceKinetics.ShieldImpulse(strength);
            double lateral = impulse*Math.Sin(angle*Math.PI/180)/mass;
            int expected = lateral > props.shieldBreakDeltaV + 1e-5 ? props.shieldFragmentCount
                : lateral > props.shieldTumbleDeltaV + 1e-5 ? 1 : 0;
            int actual = SpaceKinetics.ShieldFragmentCount(mass,incoming,Vector2.up,impulse,props);
            Check(actual == expected, $"lateral response: m={mass}, angle={angle}, strength={strength}, count={actual}");
            Check(SpaceKinetics.ShieldFragmentCount(mass,SpaceTransform.Rotate(incoming,.7f),
                SpaceTransform.Rotate(Vector2.up,.7f),impulse,props)==actual, "response invariant under shared rotation");
        }
        Check(SpaceKinetics.ShieldFragmentCount(1,new Vector2(0,-60),Vector2.up,360,normal)==0
            && SpaceKinetics.ShieldFragmentCount(20,new Vector2(0,-300),Vector2.up,360,rod)==0,
            "normal incidence preserves both rapid rounds and long rods at full strength");
        normal.shieldTumbling=true;
        Check(SpaceKinetics.ShieldFragmentCount(48,new Vector2(60,-60),Vector2.up,360,normal)==0
            && SpaceKinetics.ShieldFragmentCount(1,new Vector2(60,-60),Vector2.up,360,normal)==3,
            "full-mass tumbling shell can tear later without repeating the tumble conversion");
        normal.shieldFragmentCount=1;
        Check(SpaceKinetics.ShieldFragmentCount(1,new Vector2(60,-60),Vector2.up,360,normal)==0,
            "small fragments are not recursively split");
        Check(SpaceKinetics.FundedShieldImpulse(1, 60, 360, 2, 100) == 0, "outgoing shots receive no pulse");
        Check(SpaceKinetics.ApplyShieldImpulse(1, new Vector2(0, -60), Vector2.up, 60).sqrMagnitude == 0,
            "exact stopping impulse creates no artificial velocity");
    }
    static void CheckLateralDefinitions(string repo)
    {
        var projectiles=XDocument.Load(Path.Combine(repo,"1.6/Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml"));
        var effects=XDocument.Load(Path.Combine(repo,"1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml"));
        foreach (var (shell, fragment, count, threshold) in new[] {
            ("Shell", "Shield", 3, 18d), ("SteelShell", "Shield", 3, 18d), ("Rail", "Rail", 4, .5d) })
        {
            string name="DeltaV_Projectile_"+shell+"Tumbling";
            var def=projectiles.Root.Elements().Single(x=>(string)x.Element("defName")==name);
            if (shell == "SteelShell")
            {
                Check((string)def.Attribute("ParentName") == "DeltaV_TumblingShell",
                    "steel tumble retains the common kinetic fragment split and lifetime");
                def = projectiles.Root.Elements().Single(x => (string)x.Attribute("Name") == "DeltaV_TumblingShell");
            }
            var props=AuditComp(def,"DeltaV.CompProperties_SpaceProjectile");
            Check((bool)props.Element("shieldTumbling") && (int)props.Element("shieldFragmentCount")==count
                && (string)props.Element("shieldFragmentDef")=="DeltaV_Projectile_"+fragment+"Fragment",
                "production full-mass tumble Def retains its material and later tearing path");
            Check(AuditNumber(props,"shieldBreakDeltaV")==threshold,"production tumble Def keeps original tearing threshold");
            var matches=effects.Root.Elements().Where(x=>x.Element("projectileDefs")?.Elements("li").Any(n=>n.Value==name)==true).ToList();
            Check(matches.Count==3,"tumbling Def has structure, foundation and guard outcomes");
            foreach(var effect in matches)
            {
                var impact=effect.Element("defenderEffect");
                bool guard=(string)impact.Element("kind")=="ShieldDrain";
                nearEffect((double)impact.Element("damage"),shell=="Rail"?.16:guard?.75:6);
                if (!guard)
                    Check(impact.Element("highExplosive") == null && (bool)impact.Element("kineticMassScaling")
                        && impact.Element("penetrates") == null, "tumbling shells deal only single-contact kinetic damage");
                Check((string)effect.Element("attackerEffect")?.Element("kind")=="DestroySelf",
                    "full-mass tumble loses intact deep penetration");
            }
        }
        void nearEffect(double actual,double expected)=>Check(Near(actual,expected),"tumble and torn fragments use the same conversion, never double attenuation");
    }

}
