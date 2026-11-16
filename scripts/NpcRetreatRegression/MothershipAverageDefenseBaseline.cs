#if MOTHERSHIP_OFFLINE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // Archived AverageDefense kernel recovered from its saved benchmark assembly.
	private sealed class MothershipAverageDefenseBaseline
	{
		internal sealed class Counters
		{
			public int exactGuns;

			public int exactFires;

			public int targets;

			public int defenseRays;

			public int gridVisits;

			public int layerVisits;

			public int shieldQueries;

			public int callbacks;

			public int affectedPaths;

			public int lookups;

			public int repeatRays;

			public int repeatLayers;

			public int repeatGridVisits;
		}

		internal sealed class Path
		{
			internal double TotalHp { get; private set; }

			internal int Layers { get; private set; }

			internal void Update(float before, float after)
			{
				TotalHp += (double)after - (double)before;
				Layers += ((after > 0f) ? 1 : 0) - ((before > 0f) ? 1 : 0);
			}
		}

		internal sealed class DurabilityIndex
		{
			internal sealed class Source
			{
				private readonly HashSet<Path> subscribers = new HashSet<Path>();

				internal float Hp { get; private set; }

				internal Source(float hp)
				{
					Hp = Math.Max(0f, hp);
				}

				internal void Register(Path path)
				{
					if (subscribers.Add(path))
					{
						path.Update(0f, Hp);
					}
				}

				internal int Notify(float hp)
				{
					hp = Math.Max(0f, hp);
					if (hp == Hp)
					{
						return 0;
					}
					foreach (Path subscriber in subscribers)
					{
						subscriber.Update(Hp, hp);
					}
					Hp = hp;
					return subscribers.Count;
				}
			}

			private readonly Dictionary<object, Source> sources = new Dictionary<object, Source>();

			internal IEnumerable<KeyValuePair<object, Source>> Sources => sources;

			internal void Register(object key, float hp, Path path)
			{
				if (!sources.TryGetValue(key, out var value))
				{
					sources.Add(key, value = new Source(hp));
				}
				value.Register(path);
			}

			internal int Notify(object key, float hp)
			{
				if (!sources.TryGetValue(key, out var value))
				{
					return 0;
				}
				return value.Notify(hp);
			}
		}

		internal readonly record struct PathKey(Thing Target, int Sector);

		private readonly record struct ShotWeight(PathKey Path, float Budget, bool Outgoing);

		private struct Volley
		{
			internal int Count;

			internal float Budget;

			internal float Penetrating;
		}

		private sealed class Defense
		{
			private readonly Map map;

			private readonly HashSet<IntVec3> hull;

			private readonly Thing[] targets;

			private readonly HashSet<Thing> propulsion;

			private readonly Vector2 center;

			private readonly Dictionary<PathKey, Path> paths = new Dictionary<PathKey, Path>();

			private readonly DurabilityIndex durability = new DurabilityIndex();

			private readonly SpaceCollision.RayQuery guardQuery;

			private readonly Counters counter;

			private readonly bool timed;

			internal long ScanTicks { get; private set; }

			internal int CriticalTotal { get; }
			internal MothershipForecastTable.Profile Probe;
			internal IEnumerable<MothershipForecastTable.PathSample> Observed(bool outgoing) => paths.Select(x =>
				new MothershipForecastTable.PathSample(outgoing, x.Key.Target, x.Key.Sector, x.Value.TotalHp, x.Value.Layers));

			internal Defense(ManeuverMotionObject receiver, ManeuverMotionObject attacker, Counters counter, bool timed)
			{
				this.counter = counter;
				this.timed = timed;
				map = receiver.map;
				hull = receiver.member.HullCells.ToHashSet();
				center = receiver.member.HullCenter;
				ShipFireControl.ExternalTargets externalTargets = new ShipFireControl.ExternalTargets(receiver.member);
				propulsion = externalTargets.propulsion.ToHashSet();
				CriticalTotal = externalTargets.weapons.Count * 5 + externalTargets.propulsion.Count * 4;
				Vector2 source = center + SpaceTransform.RotateInverse(OrbitSphere.Offset(receiver.pose.position, attacker.pose.position), receiver.pose.angle);
				targets = (from t in externalTargets.weapons.Concat(externalTargets.propulsion)
					orderby (Position(t) - source).sqrMagnitude, t.thingIDNumber
					select t).Take(6).ToArray();
				counter.targets = targets.Length;
				if (map.listerThings.AllThings.OfType<ThingWithComps>().Any((ThingWithComps t) => receiver.member.Contains(t) && t.GetComp<CompProjectileInterceptor>() != null))
				{
					guardQuery = new SpaceCollision.RayQuery(receiver, receiver.member);
				}
			}

			internal bool Contains(Thing target)
			{
				return Array.IndexOf(targets, target) >= 0;
			}

			internal int CriticalWeight(Thing target)
			{
				if (!propulsion.Contains(target))
				{
					return 5;
				}
				return 4;
			}

			private static Vector2 Position(Thing target)
			{
				return new Vector2(target.DrawPos.x, target.DrawPos.z);
			}

			private static Vector2 Direction(int sector)
			{
				Vector2 result = new Vector2(Mathf.Cos((float)sector * ((float)Math.PI / 16f)), Mathf.Sin((float)sector * ((float)Math.PI / 16f)));
				if (sector % 8 != 0)
				{
					return result;
				}
				return new Vector2(Mathf.Round(result.x), Mathf.Round(result.y));
			}

			internal PathKey Resolve(Thing target, Vector2 direction)
			{
				int sector = (Mathf.RoundToInt(Mathf.Atan2(direction.y, direction.x) / ((float)Math.PI / 16f)) + 32) % 32;
				if (target != null)
				{
					return new PathKey(target, sector);
				}
				Vector2 vector = Direction(sector);
				Vector2 rhs = new Vector2(0f - vector.y, vector.x);
				float num = float.PositiveInfinity;
				Thing target2 = null;
				Thing[] array = targets;
				foreach (Thing thing in array)
				{
					float num2 = Mathf.Abs(Vector2.Dot(Position(thing) - center, rhs));
					if (!(num2 >= num))
					{
						target2 = thing;
						num = num2;
					}
				}
				return new PathKey(target2, sector);
			}

			internal Path Get(PathKey key)
			{
				if (paths.TryGetValue(key, out var value))
				{
					return value;
				}
				long num = (timed ? Stopwatch.GetTimestamp() : 0);
				value = new Path();
				paths.Add(key, value);
				Vector2 endpoint = ((key.Target == null) ? center : Position(key.Target));
				Vector2 vector = Direction(key.Sector);
				long scanStart = MothershipForecastTable.Profile.Stamp(Probe);
				Scan(value, key.Target, endpoint, -vector);
				if (Probe != null) Probe.scan += Stopwatch.GetTimestamp() - scanStart;
				if (guardQuery != null)
				{
					RegisterGuards(value, endpoint, vector);
				}
				if (timed)
				{
					ScanTicks += Stopwatch.GetTimestamp() - num;
				}
				return value;
			}

			private void Scan(Path path, Thing target, Vector2 endpoint, Vector2 outward)
			{
				counter.defenseRays++;
				int num = Mathf.FloorToInt(endpoint.x);
				int num2 = Mathf.FloorToInt(endpoint.y);
				int num3 = ((outward.x > 0f) ? 1 : (-1));
				int num4 = ((outward.y > 0f) ? 1 : (-1));
				if (outward.x < 0f && endpoint.x == (float)num)
				{
					num--;
				}
				if (outward.y < 0f && endpoint.y == (float)num2)
				{
					num2--;
				}
				while (true)
				{
					IntVec3 intVec = new IntVec3(num, 0, num2);
					if (!intVec.InBounds(map))
					{
						break;
					}
					counter.gridVisits++;
					if (hull.Contains(intVec))
					{
						long classifyStart = MothershipForecastTable.Profile.Stamp(Probe);
						SpaceStructureRules.ClassifyCell(map, intVec, out var solidThing);
						if (Probe != null) Probe.classify += Stopwatch.GetTimestamp() - classifyStart;
						if (solidThing != null && solidThing != target && solidThing.def.useHitPoints)
						{
							counter.layerVisits++;
							long registerStart = MothershipForecastTable.Profile.Stamp(Probe);
							durability.Register(solidThing, solidThing.HitPoints, path);
							if (Probe != null) Probe.register += Stopwatch.GetTimestamp() - registerStart;
						}
					}
					float num5 = ((outward.x == 0f) ? float.PositiveInfinity : (((float)(num + ((num3 > 0) ? 1 : 0)) - endpoint.x) / outward.x));
					float num6 = ((outward.y == 0f) ? float.PositiveInfinity : (((float)(num2 + ((num4 > 0) ? 1 : 0)) - endpoint.y) / outward.y));
					if (num5 <= num6)
					{
						num += num3;
					}
					if (num6 <= num5)
					{
						num2 += num4;
					}
				}
			}

			private void RegisterGuards(Path path, Vector2 endpoint, Vector2 incoming)
			{
				HashSet<SpaceCollision.ShieldArc> seen = new HashSet<SpaceCollision.ShieldArc>();
				HashSet<CompSpaceShield> hashSet = new HashSet<CompSpaceShield>();
				float num = map.Size.x + map.Size.z;
				Vector2 from = endpoint - incoming * num;
				while (true)
				{
					counter.shieldQueries++;
					if (!guardQuery.Next(from, incoming, num, null, 0f, null, null, (CompSpaceShield _) => false, (SpaceCollision.ShieldArc g) => !seen.Contains(g), out var contact, skipSubstructure: false, null, null, skipHull: true))
					{
						break;
					}
					seen.Add(contact.guard);
					counter.layerVisits++;
					CompSpaceShield compSpaceShield = contact.building.TryGetComp<CompSpaceShield>();
					if (compSpaceShield == null)
					{
						durability.Register(contact.guard.interceptor, contact.guard.interceptor.currentHitPoints, path);
						continue;
					}
					IReadOnlyList<CompSpaceShield> ownedShields = compSpaceShield.OwnedShields;
					CompSpaceShield compSpaceShield2 = ((ownedShields == null) ? compSpaceShield : (ownedShields.OrderBy((CompSpaceShield m) => m.parent.thingIDNumber).FirstOrDefault() ?? compSpaceShield));
					if (hashSet.Add(compSpaceShield2))
					{
						durability.Register(compSpaceShield2, (float)compSpaceShield.SharedRegularCapacity, path);
					}
					durability.Register(contact.guard.interceptor, (float)compSpaceShield.EmergencyCapacity, path);
				}
			}

			internal void ExerciseCallbacks()
			{
				foreach (KeyValuePair<object, DurabilityIndex.Source> source in durability.Sources)
				{
					source.Deconstruct(out var key, out var value);
					object key2 = key;
					float hp = value.Hp;
					if (!(hp <= 0f))
					{
						counter.affectedPaths += durability.Notify(key2, hp * 0.5f);
						counter.affectedPaths += durability.Notify(key2, 0f);
						counter.affectedPaths += durability.Notify(key2, hp);
						counter.callbacks += 3;
					}
				}
			}
		}

		private const int TargetCount = 6;

		private const int DirectionCount = 32;

		private const float Sector = (float)Math.PI / 16f;

		private readonly Defense attack;

		private readonly Defense defend;

		private readonly Dictionary<ThingDef, ImpactOutcomePart> impacts = new Dictionary<ThingDef, ImpactOutcomePart>();

		internal readonly Counters outgoing = new Counters();

		internal readonly Counters incoming = new Counters();

		internal long ScanTicks => attack.ScanTicks + defend.ScanTicks;
		internal MothershipForecastTable.Profile Probe { get => attack.Probe; set { attack.Probe = value; defend.Probe = value; } }
		internal MothershipForecastTable.PathSample[] Observed() =>
			attack.Observed(true).Concat(defend.Observed(false)).ToArray();
		internal MothershipForecastTable.PathSample ReplayPath(MothershipForecastTable.PathSample key)
		{
			var path = (key.Outgoing ? attack : defend).Get(new PathKey(key.Target, key.Sector));
			return key with { Hp = path.TotalHp, Layers = path.Layers };
		}

		internal MothershipAverageDefenseBaseline(ManeuverMotionObject own, ManeuverMotionObject enemy, bool timed)
		{
			attack = new Defense(enemy, own, outgoing, timed);
			defend = new Defense(own, enemy, incoming, timed);
		}

		internal bool Allows(CombatDamageForecast _, Thing target)
		{
			if (target != null && !attack.Contains(target))
			{
				return defend.Contains(target);
			}
			return true;
		}

		internal static int CountArrivals(CombatEngagementEvaluation.OfflineShot shot, int window)
		{
			int num = shot.Weapon.CooldownTicksLeft + Mathf.CeilToInt(shot.Flight * 60f);
			if (num >= window)
			{
				return 0;
			}
			int num2 = Math.Max(1, (shot.Shot.cycleTicks > 0) ? shot.Shot.cycleTicks : shot.Weapon.ShotInterval);
			return (1 + (window - 1 - num) / num2) * shot.Weapon.WeaponProps.projectilesPerShot;
		}

		internal MothershipSharedDamageReference.Benefit[] Query(IReadOnlyList<CombatEngagementEvaluation.OfflineShot> shots, int window)
		{
			var probe = Probe;
			long phase = MothershipForecastTable.Profile.Stamp(probe);
			Dictionary<PathKey, Volley> dictionary = new Dictionary<PathKey, Volley>();
			Dictionary<PathKey, Volley> dictionary2 = new Dictionary<PathKey, Volley>();
			ShotWeight[] array = new ShotWeight[shots.Count];
			for (int i = 0; i < shots.Count; i++)
			{
				CombatEngagementEvaluation.OfflineShot shot = shots[i];
				int num = CountArrivals(shot, window);
				if (num == 0)
				{
					continue;
				}
				ThingDef projectile = shot.Shot.aim.Projectile;
				if (!impacts.TryGetValue(projectile, out var value))
				{
					value = SpaceImpactEffects.Resolve(projectile, ThingDefOf.Wall)?.defenderEffect;
					impacts.Add(projectile, value);
				}
				if (value == null || value.kind != ImpactEffectKind.DamageMap)
				{
					continue;
				}
				CompProperties_SpaceWeapon weaponProps = shot.Weapon.WeaponProps;
				float num2 = SpaceImpactEffects.ComputeDamage(value, shot.Speed, 0f, 1f, shot.Shot.mass / (float)weaponProps.projectilesPerShot) * weaponProps.impactScale * (float)shot.Shot.power;
				if (!(num2 <= 0f))
				{
					PathKey pathKey = (shot.Outgoing ? attack : defend).Resolve(shot.Part, shot.Direction);
					float num3 = (float)num * num2;
					array[i] = new ShotWeight(pathKey, num3, shot.Outgoing);
					Dictionary<PathKey, Volley> obj = (shot.Outgoing ? dictionary : dictionary2);
					obj.TryGetValue(pathKey, out var value2);
					value2.Count += num;
					value2.Budget += num3;
					if (value.penetrates)
					{
						value2.Penetrating += num3;
					}
					obj[pathKey] = value2;
				}
			}
			if (probe != null) { probe.grouping += Stopwatch.GetTimestamp() - phase; phase = Stopwatch.GetTimestamp(); }
			Dictionary<PathKey, MothershipSharedDamageReference.Benefit> dictionary3 = Evaluate(attack, outgoing, dictionary);
			Dictionary<PathKey, MothershipSharedDamageReference.Benefit> dictionary4 = Evaluate(defend, incoming, dictionary2);
			if (probe != null) { probe.response += Stopwatch.GetTimestamp() - phase; phase = Stopwatch.GetTimestamp(); }
			MothershipSharedDamageReference.Benefit[] array2 = new MothershipSharedDamageReference.Benefit[shots.Count];
			for (int j = 0; j < shots.Count; j++)
			{
				ShotWeight shotWeight = array[j];
				if (!(shotWeight.Budget <= 0f))
				{
					Dictionary<PathKey, Volley> dictionary5 = (shotWeight.Outgoing ? dictionary : dictionary2);
					MothershipSharedDamageReference.Benefit benefit = (shotWeight.Outgoing ? dictionary3 : dictionary4)[shotWeight.Path];
					float num4 = shotWeight.Budget / dictionary5[shotWeight.Path].Budget;
					array2[j] = new MothershipSharedDamageReference.Benefit(benefit.Damage * num4, 0f, benefit.Critical * num4, benefit.Armor * num4, shots[j].Shot.aim.Laser ? 0f : (benefit.Damage * num4), 0f);
				}
			}
			if (probe != null) probe.distribute += Stopwatch.GetTimestamp() - phase;
			return array2;
		}

		private static Dictionary<PathKey, MothershipSharedDamageReference.Benefit> Evaluate(Defense defense, Counters counter, Dictionary<PathKey, Volley> volleys)
		{
			Dictionary<PathKey, MothershipSharedDamageReference.Benefit> dictionary = new Dictionary<PathKey, MothershipSharedDamageReference.Benefit>(volleys.Count);
			foreach (KeyValuePair<PathKey, Volley> volley2 in volleys)
			{
				volley2.Deconstruct(out var key, out var value);
				PathKey key2 = key;
				Volley volley = value;
				Path path = defense.Get(key2);
				(float, float) tuple = MothershipDamageSurface.AtAverage(volley.Count, volley.Budget, volley.Penetrating, path.TotalHp, path.Layers);
				counter.lookups++;
				float num = key2.Target?.HitPoints ?? 0;
				float num2 = Math.Min(num, tuple.Item2);
				float num3 = defense.CriticalWeight(key2.Target);
				float critical = ((num > 0f && defense.CriticalTotal > 0) ? (num3 * num2 / (num * (float)defense.CriticalTotal)) : 0f);
				dictionary.Add(key2, new MothershipSharedDamageReference.Benefit(tuple.Item1 + num2, 0f, critical, (path.TotalHp > 0.0) ? (tuple.Item1 / (float)path.TotalHp) : 0f, 0f, 0f));
			}
			return dictionary;
		}

		// Diagnostic input bypasses the archived per-shot calculation while retaining
		// its grouping, path lookup, curve application and redistribution.
		internal MothershipSharedDamageReference.Benefit[] QueryFrozen(
			IReadOnlyList<MothershipWeaponOutputTable.Delivery> shots)
		{
			var probe = Probe;
			long phase = MothershipForecastTable.Profile.Stamp(probe);
			var outgoingGroups = new Dictionary<PathKey, Volley>();
			var incomingGroups = new Dictionary<PathKey, Volley>();
			var weights = new ShotWeight[shots.Count];
			for (int i = 0; i < shots.Count; i++)
			{
				var shot = shots[i];
				var key = (shot.Outgoing ? attack : defend).Resolve(shot.Part, shot.Direction);
				var output = shot.Output;
				weights[i] = new ShotWeight(key, output.Budget, shot.Outgoing);
				var groups = shot.Outgoing ? outgoingGroups : incomingGroups;
				groups.TryGetValue(key, out var volley);
				volley.Count += output.Arrivals;
				volley.Budget += output.Budget;
				volley.Penetrating += output.Penetrating;
				groups[key] = volley;
			}
			if (probe != null) { probe.grouping += Stopwatch.GetTimestamp() - phase; phase = Stopwatch.GetTimestamp(); }
			var outgoingResults = Evaluate(attack, outgoing, outgoingGroups);
			var incomingResults = Evaluate(defend, incoming, incomingGroups);
			if (probe != null) { probe.response += Stopwatch.GetTimestamp() - phase; phase = Stopwatch.GetTimestamp(); }
			var benefits = new MothershipSharedDamageReference.Benefit[shots.Count];
			for (int i = 0; i < shots.Count; i++)
			{
				var weight = weights[i];
				if (weight.Budget <= 0) continue;
				var groups = weight.Outgoing ? outgoingGroups : incomingGroups;
				var benefit = (weight.Outgoing ? outgoingResults : incomingResults)[weight.Path];
				float share = weight.Budget / groups[weight.Path].Budget;
				benefits[i] = new MothershipSharedDamageReference.Benefit(benefit.Damage * share, 0,
					benefit.Critical * share, benefit.Armor * share, shots[i].Laser ? 0 : benefit.Damage * share, 0);
			}
			if (probe != null) probe.distribute += Stopwatch.GetTimestamp() - phase;
			return benefits;
		}

		internal void ExerciseCallbacks()
		{
			attack.ExerciseCallbacks();
			defend.ExerciseCallbacks();
		}
	}
}
#endif
