using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using DeltaV;
using HarmonyLib;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void SearchExport(string directory, string name)
    {
        var xml = new XmlDocument();
        xml.Load(Path.Combine(directory,name+".xml"));
        using var records = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,name+".records.json")));
        using var layout = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,name+".layout.json")));
        var entries = xml.SelectNodes("/MapPrefab/thingStates/li").Cast<XmlNode>().ToArray();
        Check(entries.Length == records.RootElement.GetArrayLength(),name+" prefab and plan have the same instance count");
        int i = 0;
        foreach (var row in records.RootElement.EnumerateArray())
        {
            var entry = entries[i++];
            var def = row.GetProperty("name").GetString();
            Check(entry["def"].InnerText == def && int.Parse(entry["rotation"].InnerText) == row.GetProperty("rotation").GetInt32(),
                name+" prefab and plan agree on the Def and rotation");
            var pos = IntVec3.FromString(entry["pos"].InnerText);
            Check(pos.x == row.GetProperty("root")[0].GetInt32() && pos.z == row.GetProperty("root")[1].GetInt32(),
                name+" prefab and plan agree on the native root");
            Check(def != "GravEngine" && def != "GravFieldExtender" && def != "GravcorePowerCell",
                name+" uses conventional power and drive");
        }
        foreach (var field in new[] { ("foundation","foundations"), ("roofs","roofs") })
        {
            var authored = layout.RootElement.GetProperty(field.Item1).EnumerateArray()
                .Select(c => new IntVec3(c[0].GetInt32(),0,c[1].GetInt32())).ToHashSet();
            var exported = xml.SelectNodes("/MapPrefab/"+field.Item2+"/li/pos").Cast<XmlNode>()
                .Select(c => IntVec3.FromString(c.InnerText)).ToHashSet();
            Check(authored.SetEquals(exported),name+" prefab and plan agree on "+field.Item1);
        }
    }

    static void VerifySearchMotion(string recordsPath)
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(recordsPath.Replace(".records.json",".motion.json")));
        double distance = expected.RootElement[2].GetProperty("route").GetProperty("distance").GetDouble();
        double harbor = expected.RootElement[2].GetProperty("harbor").GetProperty("distance").GetDouble();
        using var actual = JsonDocument.Parse(SearchMotion(recordsPath,distance,harbor));
        for (int k = 0; k < 3; k++)
        {
            var axes = actual.RootElement[k].GetProperty("axes");
            for (int j = 0; j < axes.GetArrayLength(); j++)
            {
                var now = axes[j];
                var before = expected.RootElement[k].GetProperty("axes")[j];
                double accel = now.GetProperty("acceleration").GetDouble();
                double residual = now.GetProperty("residual").GetDouble();
                Check(accel > 0 && Math.Abs(accel-before.GetProperty("acceleration").GetDouble()) < 1e-5,
                    "native acceleration agrees with the authored motion report");
                Check(residual <= .1 && Math.Abs(residual-before.GetProperty("residual").GetDouble()) < 1e-5,
                    "native directional residual stays within 10 percent");
            }
        }
        foreach (string leg in new[] {"route","harbor"})
        {
            double a = actual.RootElement[2].GetProperty(leg).GetProperty("requiredFuel").GetDouble();
            double b = expected.RootElement[2].GetProperty(leg).GetProperty("requiredFuel").GetDouble();
            Check(a > 0 && Math.Abs(a-b) < .1, "native fuel estimate agrees with the authored motion report");
        }
    }

    static void VerifyPlayerShell(string directory, string name, JsonElement shell, JsonElement widths)
    {
        using var layout = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,name+".layout.json")));
        using var records = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,name+".records.json")));
        (int,int) Cell(JsonElement c) => (c[0].GetInt32(),c[1].GetInt32());
        var doc = layout.RootElement;
        var foundation = doc.GetProperty("foundation").EnumerateArray().Select(Cell).ToHashSet();
        var source = shell.EnumerateArray().Select(Cell).ToHashSet();
        var cutouts = doc.GetProperty("exhaustCutouts").EnumerateArray().Select(Cell).ToHashSet();
        var rim = doc.TryGetProperty("rimCutouts",out var rimCells)
            ? rimCells.EnumerateArray().Select(Cell).ToHashSet() : new HashSet<(int,int)>();
        var bodies = records.RootElement.EnumerateArray().Where(r => r.GetProperty("layer").GetString() == "body").ToArray();
        var exhaust = bodies.Where(r => r.TryGetProperty("exclusionCells", out _))
            .SelectMany(r => r.GetProperty("exclusionCells").EnumerateArray().Select(Cell)).ToHashSet();
        Check(foundation.IsSubsetOf(source) && !cutouts.Overlaps(rim) &&
            cutouts.Union(rim).ToHashSet().SetEquals(source.Except(foundation)) &&
            cutouts.IsSubsetOf(exhaust) && cutouts.Count <= Math.Min(60, source.Count / 12),
            "player shell preserves the foundation except exhaust and the allowed outer rim");
        Check(rim.Count <= Math.Min(60,source.Count/12) && rim.All(c =>
            new[] {(c.Item1+1,c.Item2),(c.Item1-1,c.Item2),(c.Item1,c.Item2+1),(c.Item1,c.Item2-1)}
                .Any(n => !source.Contains(n))), "rim trimming is confined to one original boundary layer");
        int width = doc.GetProperty("corridorWidth").GetInt32();
        Check(widths.EnumerateArray().Any(w => w.GetInt32() == width),"requested corridor width");
        var lane = doc.GetProperty("corridorLane").EnumerateArray().Select(Cell).ToHashSet();
        var clear = doc.GetProperty("corridorClear").EnumerateArray().Select(Cell).ToHashSet();
        var center = doc.GetProperty("centerline").EnumerateArray().Select(Cell).ToHashSet();
        var occupied = bodies.SelectMany(r => r.GetProperty("cells").EnumerateArray().Select(Cell)).ToHashSet();
        var heaterPositions = bodies.Where(r => r.GetProperty("name").GetString() == "Heater")
            .Select(r => Cell(r.GetProperty("root"))).ToHashSet();
        var local = doc.GetProperty("localHeaters").EnumerateArray().Select(Cell).ToHashSet();
        var central = heaterPositions.Except(local).ToHashSet();
        Check(clear.Count > 0 && clear.IsSubsetOf(lane) && center.IsSubsetOf(clear) &&
            !clear.Overlaps(occupied) && lane.Intersect(occupied).ToHashSet().SetEquals(central),
            "only corridor heaters can occupy the width outside its clear passage");
        var directions = new[] { (0,1), (1,0), (0,-1), (-1,0) };
        (int,int) Add((int,int) a, (int,int) b) => (a.Item1+b.Item1, a.Item2+b.Item2);
        (int,int) Negate((int,int) a) => (-a.Item1,-a.Item2);
        var sectionWidths = new Dictionary<(int,int),int>();
        var expectedLane = new HashSet<(int,int)>();
        if (doc.TryGetProperty("corridorSections", out var sections) && sections.GetArrayLength() > 0)
        {
            foreach (var s in sections.EnumerateArray())
            {
                var c = Cell(s[0]);
                int w = s[1].GetInt32(), a = s[2].GetInt32(), b = s[3].GetInt32();
                Check(widths.EnumerateArray().Any(v => v.GetInt32() == w) && a >= 0 && b >= 0 && a < w && b < w,
                    "each corridor section fits a requested width and contains its center");
                sectionWidths[c] = Math.Max(w,sectionWidths.GetValueOrDefault(c));
                expectedLane.UnionWith(from dx in Enumerable.Range(-a,w) from dz in Enumerable.Range(-b,w)
                                       select Add(c,(dx,dz)));
            }
            Check(center.SetEquals(sectionWidths.Keys) && width == sectionWidths.Values.Max(),
                "variable-width sections agree with the reported centerline and maximum width");
        }
        else
        {
            var offsets = Enumerable.Range(width == 3 ? -1 : 0,width);
            expectedLane.UnionWith(from c in center from dx in offsets from dz in offsets select Add(c,(dx,dz)));
        }
        Check(lane.SetEquals(expectedLane) && lane.IsSubsetOf(foundation),
            "corridor width follows the entire routed centerline on retained foundation");
        var seen = new HashSet<(int,int)> { clear.First() };
        var queue = new Queue<(int,int)>(seen);
        while (queue.TryDequeue(out var current))
            foreach (var dir in directions)
            {
                var next = Add(current,dir);
                if (clear.Contains(next) && seen.Add(next)) queue.Enqueue(next);
            }
        Check(seen.SetEquals(clear), "corridor clear passage is connected");
        Check(width > 1 || (central.Count == 0 && lane.SetEquals(clear)),
            "one-cell corridors are heated from the rooms");
        foreach (var heater in central)
            Check(directions.Take(sectionWidths.Count == 0 && width == 2 ? 2 : 4).Any(dir =>
            {
                var anchor = Add(heater,Negate(dir));
                var parallel = (dir.Item2,dir.Item1);
                return sectionWidths.GetValueOrDefault(anchor,width) > 1 &&
                    center.Contains(anchor) && !center.Contains(heater) &&
                    center.Contains(Add(anchor,parallel)) && center.Contains(Add(anchor,Negate(parallel))) &&
                    !center.Contains(Add(anchor,Negate(dir)));
            }), "corridor heater is on an allowed side of a straight segment");
        Check(local.IsSubsetOf(heaterPositions) && !local.Overlaps(lane),
            "supplemental heaters are emitted at the resolved room locations");
    }

    static void VerifyTaskDeck(string directory, string name, JsonElement limits, JsonElement task)
    {
        using var layout = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,name+".layout.json")));
        var doc = layout.RootElement;
        var deck = doc.GetProperty("deck");
        HashSet<(int,int)> Cells(JsonElement v) => v.EnumerateArray()
            .Select(c => (c[0].GetInt32(),c[1].GetInt32())).ToHashSet();
        var hull = Cells(doc.GetProperty("foundation"));
        var unused = Cells(deck.GetProperty("unused"));
        var useful = new HashSet<(int,int)>();
        foreach (string category in new[] {"rooms","circulation","boundary","reserved"})
            useful.UnionWith(Cells(deck.GetProperty(category)));
        Check(useful.IsSubsetOf(hull) && hull.Except(useful).ToHashSet().SetEquals(unused),
            "deck accounting covers exactly the retained foundation");
        int largest = 0;
        var left = new HashSet<(int,int)>(unused);
        while (left.Count > 0)
        {
            var first = left.First();
            left.Remove(first);
            var queue = new Queue<(int,int)>();
            queue.Enqueue(first);
            int size = 0;
            while (queue.TryDequeue(out var c))
            {
                size++;
                foreach (var n in new[] {(c.Item1+1,c.Item2),(c.Item1-1,c.Item2),
                                         (c.Item1,c.Item2+1),(c.Item1,c.Item2-1)})
                    if (left.Remove(n)) queue.Enqueue(n);
            }
            largest = Math.Max(largest,size);
        }
        Check(largest == deck.GetProperty("largestUnused").GetInt32() &&
              largest <= limits.GetProperty("maxUnusedBlock").GetInt32() &&
              unused.Count*100 <= hull.Count*limits.GetProperty("maxUnusedPercent").GetInt32(),
            "task preview satisfies both empty deck gates");
        Check(doc.GetProperty("batteryCapacityWd").GetDouble() >= task.GetProperty("burstEnergyWd").GetDouble(),
            "accepted task preview has enough connected storage for its burst");
    }

    // This is a fixed-heading, accelerate/brake design case. It samples the
    // production allocator and does not execute a navigation order or spend fuel.
    static object RouteFuel(ShipPhysics.MassReport mass, double distance, Vector2 heading)
    {
        const double dt = 1.0 / 60.0, rate = .00011;
        double decay = ShipPhysics.PassiveDragFactors((float)dt).linear;
        (double acceleration, double fuel) Read(float direction)
        {
            ShipPhysics.PreviewAllocation(mass, new ShipPhysics.Command { fx = heading.x * direction,
                fz = heading.y * direction }, out var force, out _);
            double thrust = mass.thrusters.Select((engine, i) => engine.thrust * mass.previewScratch[i]).Sum();
            return (direction * Vector2.Dot(force,heading) / mass.mass, rate * thrust * dt);
        }
        var forward = Read(1);
        var brake = Read(-1);
        if (forward.acceleration <= 0 || brake.acceleration <= 0) throw new Exception("route needs forward and reverse authority");
        (double distance, double ticks) Stop(double speed)
        {
            if (speed <= 0) return (0, 0);
            double steady = -decay * brake.acceleration * dt / (1 - decay);
            double stop = Math.Log(-steady / (speed - steady)) / Math.Log(decay);
            int n = (int)Math.Floor(stop);
            double sum = n*steady + (speed-steady)*decay*(1-Math.Pow(decay,n))/(1-decay);
            double finalSpeed = steady + (speed - steady) * Math.Pow(decay,n);
            return (dt * sum, n + Math.Min(1, finalSpeed / (brake.acceleration * dt)));
        }
        double speed = 0, position = 0;
        int ticks = 0;
        while (ticks < 2_000_000)
        {
            double next = decay * (speed + forward.acceleration*dt);
            if (position + next*dt + Stop(next).distance > distance) break;
            speed = next;
            position += speed*dt;
            ticks++;
        }
        var end = Stop(speed);
        double braking = end.ticks * brake.fuel;
        double used = ticks * forward.fuel + braking;
        if (distance - position - end.distance > 2) throw new Exception("route exceeds offline horizon");
        return new { distance, elapsedSeconds = (ticks + end.ticks)*dt, brakingFuel = braking, fuel = used,
            requiredFuel = used + Math.Max(5, Math.Max(.15*used, .25*braking)) };
    }

    static string SearchMotion(string recordsPath, double mainDistance, double harborDistance)
    {
        string layoutPath = recordsPath.Replace(".records.json", ".layout.json");
        using var records = JsonDocument.Parse(File.ReadAllText(recordsPath));
        using var layout = JsonDocument.Parse(File.ReadAllText(layoutPath));
        var report = new ShipPhysics.MassReport();
        int facing = layout.RootElement.TryGetProperty("flightRotation",out var rotation) ? rotation.GetInt32() : 0;
        var heading = ShipPhysics.ThrustDir(new Rot4(facing));
        var right = new Vector2(heading.y,-heading.x);
        var commands = new[] { new ShipPhysics.Command { fx=right.x,fz=right.y },
            new ShipPhysics.Command { fx=-right.x,fz=-right.y },
            new ShipPhysics.Command { fx=heading.x,fz=heading.y },
            new ShipPhysics.Command { fx=-heading.x,fz=-heading.y },
            new ShipPhysics.Command { torque=1 }, new ShipPhysics.Command { torque=-1 } };
        var names = new[] { "east","west","forward","brake","turnRight","turnLeft" };
        foreach (var row in records.RootElement.EnumerateArray().Where(r => r.TryGetProperty("exclusionSize", out _)))
        {
            var cells = row.GetProperty("cells").EnumerateArray().ToArray();
            var pos = new Vector2((float)cells.Average(c => c[0].GetInt32()) + 20.5f,
                                  (float)cells.Average(c => c[1].GetInt32()) + 20.5f);
            float signedThrust = ThrusterPower.BaseThrust(NativeThrusterDef(row.GetProperty("name").GetString()));
            float power = row.TryGetProperty("stateOverrides", out var state)
                && state.TryGetProperty("DeltaV_powerPercent", out var configuredPower) ? configuredPower.GetSingle() : 100f;
            report.thrusters.Add(new ShipPhysics.Thruster { localPos=pos,
                dir=ShipPhysics.ThrustDir(new Rot4(row.GetProperty("rotation").GetInt32())) * (signedThrust < 0 ? -1 : 1),
                thrust=Math.Abs(signedThrust) * .01f * power,
                operational=signedThrust != 0 });
        }
        var mod = Raw<DeltaVMod>();
        Set(mod,"<Settings>k__BackingField",new DeltaVSettings());
        var mods = (Dictionary<Type,Mod>)AccessTools.Field(typeof(LoadedModManager),"runningModClasses").GetValue(null);
        mods[typeof(DeltaVMod)] = mod;
        var cases = new[] { 0,12,48 }.Select(stacks =>
        {
            ApplySectorMass(records.RootElement, layoutPath, report, stacks);
            double radius = layout.RootElement.GetProperty("foundation").EnumerateArray().Max(c =>
                Math.Sqrt(Math.Pow(c[0].GetInt32()+20.5-report.comLocal.x,2)
                        + Math.Pow(c[1].GetInt32()+20.5-report.comLocal.y,2)));
            var axes = commands.Select((command,i) =>
            {
                ShipPhysics.PreviewAllocation(report, command, out var force, out var torque);
                double primary = command.torque != 0 ? torque * command.torque : force.x * command.fx + force.y * command.fz;
                double offAxis = command.torque != 0 ? force.magnitude * radius : Math.Abs(command.fx != 0 ? force.y : force.x);
                double offTorque = command.torque != 0 ? 0 : Math.Abs(torque) / radius;
                return new { axis=names[i], acceleration=primary / (command.torque != 0 ? report.inertia : report.mass),
                    residual=Math.Max(offAxis,offTorque) / Math.Max(1,primary) };
            }).ToArray();
            return new { stacks, mass=report.mass, inertia=report.inertia,
                center=new[] {report.comLocal.x-20,report.comLocal.y-20}, axes,
                route = stacks == 48 ? RouteFuel(report,mainDistance,heading) : null,
                harbor = stacks == 48 ? RouteFuel(report,harborDistance,heading) : null };
        }).ToArray();
        return JsonSerializer.Serialize(cases);
    }
}
