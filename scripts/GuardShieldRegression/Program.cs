using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using DeltaV;
using Circle = DeltaV.GuardShieldOutline.Circle;
using Point = DeltaV.GuardShieldOutline.Point;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }
    private static Circle C(int id, double x=0, double z=0, double radius=25)
        => new Circle { id=id, x=x, z=z, radius=radius };
    // Independent ray-casting oracle, not the production half-plane clipping algorithm.
    private static bool Inside(IReadOnlyList<Point> polygon, double x, double z)
    {
        bool inside=false;
        for(int i=0,j=polygon.Count-1; i<polygon.Count; j=i++)
        {
            Point a=polygon[i], b=polygon[j];
            if ((a.z>z)!=(b.z>z) && x<(b.x-a.x)*(z-a.z)/(b.z-a.z)+a.x) inside=!inside;
        }
        return inside;
    }
    private static double Area(IReadOnlyList<Point> p)
    { double sum=0; for(int i=0;i<p.Count;i++) sum+=p[i].x*p[(i+1)%p.Count].z-p[(i+1)%p.Count].x*p[i].z; return Math.Abs(sum)*0.5; }
    private static void Main(string[] args)
    {
        var single=C(1);
        var full=GuardShieldOutline.Clip(single,Array.Empty<Circle>());
        Check(full.Count==4 && Math.Abs(Area(full)-Math.Pow(50*GuardShieldOutline.TextureScale,2))<1e-8,"isolated field preserves native quad and UV extent");
        Check(GuardShieldOutline.Clip(C(2),new[]{C(1)}).Count==0,"coincident fields draw only once");
        Check(GuardShieldOutline.Clip(C(1,radius:10),new[]{C(2)}).Count==0,"contained field draws no internal ring");
        Check(GuardShieldOutline.Clip(C(1),new[]{C(2,80)}).Count==4,"distant field preserves original glow");
        var random=new Random(71107);
        for(int layout=0;layout<200;layout++)
        {
            var circles=Enumerable.Range(0,1+layout%12).Select(i=>C(i,random.NextDouble()*80-40,random.NextDouble()*80-40,8+random.NextDouble()*32)).ToArray();
            var polygons=circles.Select(c=>GuardShieldOutline.Clip(c,circles)).ToArray();
            for(int i=0;i<circles.Length;i++)
            {
                var self=circles[i]; var polygon=polygons[i];
                foreach(var p in polygon) Check(Math.Abs(p.x)<=self.radius*GuardShieldOutline.TextureScale+1e-8 && Math.Abs(p.z)<=self.radius*GuardShieldOutline.TextureScale+1e-8,"UV remains in original texture");
                Check(Math.Abs(Area(polygon)-Area(GuardShieldOutline.Clip(self,circles.Reverse().ToArray())))<1e-6,"member order preserves clipped area");
                for(int sample=0;sample<257;sample++)
                {
                    double angle=(sample+0.371)*Math.PI*2/257;
                    double x=Math.Cos(angle)*self.radius,z=Math.Sin(angle)*self.radius;
                    bool exposed=!circles.Any(c=>c.id!=self.id && Math.Pow(self.x+x-c.x,2)+Math.Pow(self.z+z-c.z,2)<c.radius*c.radius);
                    Check(Inside(polygon,x,z)==exposed,"actual union boundary is retained exactly; internal arcs are removed");
                }
            }
            for(int sample=0;sample<500;sample++)
            {
                double x=random.NextDouble()*140-70,z=random.NextDouble()*140-70;
                if(!circles.Any(c=>Math.Pow(x-c.x,2)+Math.Pow(z-c.z,2)<c.radius*c.radius)) continue;
                int owners=0;for(int i=0;i<circles.Length;i++) if(Inside(polygons[i],x-circles[i].x,z-circles[i].z)) owners++;
                Check(owners==1,"union interior has exactly one textured owner, without overlap or holes");
            }
        }
        Console.WriteLine($"PASS: {checks} native texture clipping checks.");
        Console.WriteLine("BENCHMARK layout,n,median_ms,p95_ms,vertices,triangles (CPU clipping only)");
        foreach(string layout in new[]{"dense","chain","separate"}) foreach(int n in new[]{8,32,128})
        {
            var cs=Enumerable.Range(0,n).Select(i=>C(i,layout=="chain"?i*30:layout=="separate"?i*60:i%8*5,layout=="dense"?i/8*5:0,24.9)).ToArray();
            int vertices=0,triangles=0;
            void Build(){vertices=triangles=0;foreach(var c in cs){var p=GuardShieldOutline.Clip(c,cs);vertices+=p.Count;triangles+=Math.Max(0,p.Count-2);}}
            for(int i=0;i<100;i++)Build();var times=new double[200];var sw=new Stopwatch();
            for(int i=0;i<times.Length;i++){sw.Restart();Build();sw.Stop();times[i]=sw.Elapsed.TotalMilliseconds;}
            Array.Sort(times);Console.WriteLine($"{layout},{n},{times[100]:F4},{times[190]:F4},{vertices},{triangles}");
        }
    }
}
