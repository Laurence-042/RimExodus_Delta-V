using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed partial class SpaceProjectileService
    {
        // 「碰撞规避」关闭时，真实外层格挂接普通弹体链的隐形弹丸。
        // 本文件只维护弹丸和来源；命中查询、伤害均由原 SpaceCollision/后效链处理。
        private sealed class HullSource
        {
            internal DriveSubject subject;
            internal Map map;
            internal SpaceObject owner;
            internal int representation, version = -1;
            internal bool full = true;
            internal readonly HashSet<IntVec3> cells = new HashSet<IntVec3>();
            internal readonly HashSet<IntVec3> dirty = new HashSet<IntVec3>();
            internal readonly HashSet<IntVec3> spent = new HashSet<IntVec3>();
            internal readonly Dictionary<IntVec3, Projectile> rounds = new Dictionary<IntVec3, Projectile>();
        }

        private sealed class AttachedHullRound : SpaceObject
        {
            internal AttachedHullRound(int id, SpacePose pose) : base(id, pose)
            { baseTag = SpaceObjectTag.Projectile; }
            internal void Follow(SpacePose value, bool beginTick)
            {
                pose = value;
                // 点的运动来自母船，禁止主循环再次自由漂移；不改母船的推进时钟。
                if (beginTick) BeginMotion(Dt);
            }
        }

        private readonly Dictionary<DriveSubject, HullSource> hullSources = new Dictionary<DriveSubject, HullSource>();
        private readonly List<HullSource> hullSourceBuffer = new List<HullSource>();
        private readonly List<Projectile> hullRoundBuffer = new List<Projectile>();
        private readonly Dictionary<int, (SpaceObject owner, int version, SpacePose pose, float fraction)> hullStarts =
            new Dictionary<int, (SpaceObject, int, SpacePose, float)>();
        private readonly HashSet<(int a, int b, int left, int right)> hullHits = new HashSet<(int, int, int, int)>();
        private int hullHitTick = -1;
        private float hullFraction;
        private bool hullChangesPending;

        internal void RefreshHullSource(DriveSubject subject)
        {
            ShipBase ship = ShipRegistry.ShipByIdentity(subject);
            if (ship?.IsValid != true || universe.Controls.CollisionAvoidanceEnabled(ship))
            {
                if (hullSources.TryGetValue(subject, out var old)) RemoveHullSource(old);
                return;
            }
            if (!hullSources.ContainsKey(subject)) hullSources.Add(subject, new HullSource { subject = subject });
        }

        internal void NotifyHullCellsChanged(Map map, CellRect? range)
        {
            foreach (var source in hullSources.Values)
            {
                if (map != null && source.map != map) continue;
                hullChangesPending = true;
                if (!range.HasValue) { source.full = true; continue; }
                foreach (IntVec3 cell in range.Value)
                {
                    source.dirty.Add(cell);
                    foreach (IntVec3 offset in GenAdj.CardinalDirections) source.dirty.Add(cell + offset);
                }
            }
        }

        private static bool HullCell(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map)) return false;
            var kind = SpaceStructureRules.ClassifyCell(map, cell, out var building);
            return kind == SpaceCellKind.Substructure || building is Building b
                && !b.Destroyed && b.def.useHitPoints && b.def.destroyable;
        }

        private void RemoveHullRounds(HullSource source)
        {
            foreach (var round in source.rounds.Values.ToArray()) DestroyProjectile(round, "hull source changed");
            source.rounds.Clear(); source.cells.Clear(); source.dirty.Clear();
        }

        private void RemoveHullSource(HullSource source)
        { RemoveHullRounds(source); hullSources.Remove(source.subject); }

        private void ForgetHullRound(Projectile round)
        {
            HullSource source = round.hullSource;
            if (source == null) return;
            if (source.rounds.TryGetValue(round.hullCell, out var current) && ReferenceEquals(current, round))
            { source.rounds.Remove(round.hullCell); source.dirty.Add(round.hullCell); }
        }

        private void PrepareHullRounds()
        {
            hullStarts.Clear(); hullFraction = 0f;
            hullHitTick = Find.TickManager.TicksGame; hullHits.Clear();
            foreach (var source in hullSources.Values) source.spent.Clear();
            if (hullSources.Count == 0) return;
            foreach (var body in universe.objects)
                if (body.map != null && !body.map.Disposed)
                    hullStarts[body.objectId] = (body, body.RepresentationVersion, body.pose, 0f);
            RefreshHullRounds(true); hullChangesPending = false;
        }

        // 由 tick 起点及命中完成后的同一事件消费入口调用，不清掉本 tick 扫掠时钟。
        private void RefreshHullRounds(bool beginTick)
        {
            hullSourceBuffer.Clear(); hullSourceBuffer.AddRange(hullSources.Values);
            foreach (var source in hullSourceBuffer)
            {
                ShipBase ship = ShipRegistry.ShipByIdentity(source.subject);
                if (ship?.IsValid != true || universe.Controls.CollisionAvoidanceEnabled(ship))
                { RemoveHullSource(source); continue; }
                SpaceObject owner = universe.FrameOf(ship.Map);
                if (owner == null || owner.IsArchived || !FreeFlightBootstrap.IsZeroGravity(universe, ship.Map))
                { RemoveHullRounds(source); source.full = true; continue; }
                if (owner.projectileRoleOverride)
                { RemoveHullRounds(source); source.full = true; continue; }
                if (source.map != ship.Map || source.owner != owner || source.representation != owner.RepresentationVersion)
                {
                    RemoveHullRounds(source);
                    source.map = ship.Map; source.owner = owner; source.representation = owner.RepresentationVersion;
                    source.full = true;
                }
                int version = SubstructureOwnership.StructureVersion(source.map);
                var domain = ship.HullCells;
                if (version != source.version && source.dirty.Count == 0) source.full = true;
                if (source.full)
                {
                    foreach (var cell in source.cells) source.dirty.Add(cell);
                    source.cells.Clear();
                    foreach (var cell in domain)
                        if (HullCell(source.map, cell)) { source.cells.Add(cell); source.dirty.Add(cell); }
                    source.full = false;
                }
                else
                {
                    // 断连／重新接通也可能改变控制域；仅在结构事件后找差集，
                    // 不替换仍然有效的弹丸，不另建连通或碰撞几何。
                    if (version != source.version)
                    {
                        foreach (var cell in source.cells)
                            if (!domain.Contains(cell)) source.dirty.Add(cell);
                        foreach (var cell in domain)
                            if (!source.cells.Contains(cell) && HullCell(source.map, cell))
                            {
                                source.dirty.Add(cell);
                                foreach (var d in GenAdj.CardinalDirections) source.dirty.Add(cell + d);
                            }
                    }
                    foreach (var cell in source.dirty)
                        if (domain.Contains(cell) && HullCell(source.map, cell)) source.cells.Add(cell);
                        else source.cells.Remove(cell);
                }
                // 销毁会把来源格标脏，先复制本轮增量，避免修改正在枚举的集合。
                var changed = source.dirty.ToArray();
                source.dirty.Clear();
                foreach (var cell in changed)
                {
                    bool edge = source.cells.Contains(cell) && GenAdj.CardinalDirections.Any(d => !source.cells.Contains(cell + d));
                    if (source.rounds.TryGetValue(cell, out var round) && (!edge || round.dead))
                        DestroyProjectile(round, "hull cell removed");
                    if (edge && !source.rounds.ContainsKey(cell))
                    {
                        if (beginTick || !source.spent.Contains(cell)) CreateHullRound(source, cell, ship);
                        else source.dirty.Add(cell);
                    }
                }
                source.version = version;
                hullRoundBuffer.Clear(); hullRoundBuffer.AddRange(source.rounds.Values);
                foreach (var round in hullRoundBuffer) FollowHullRound(round, beginTick);
            }
        }

        private void CreateHullRound(HullSource source, IntVec3 cell, ShipBase ship)
        {
            var def = DeltaVDefOf.DeltaV_Projectile_HullContact;
            var round = projectilePool.Count > 0 ? projectilePool.Pop() : new Projectile();
            round.def = def; round.props = def.GetCompProperties<CompProperties_SpaceProjectile>();
            round.hullSource = source; round.hullCell = cell;
            round.sourceContainer = source.owner; round.launcher = ship.Anchor;
            round.launchSweepStart = null; round.selfFire = default;
            round.damageScale = round.armorPenetrationScale = round.mass = round.beamEnergyScale = 1f;
            round.spawnedAtTick = Find.TickManager.TicksGame;
            var actor = new AttachedHullRound(universe.AllocateOwnerId(), source.owner.pose) { faction = ship.Faction };
            round.container = actor;
            universe.RegisterObject(actor);
            source.rounds.Add(cell, round); projectiles.Add(round);
        }

        private bool FollowHullRound(Projectile round, bool beginTick)
        {
            var source = round.hullSource;
            if (source?.owner?.map != source.map || source.map == null || source.map.Disposed
                || source.owner.projectileRoleOverride
                || source.owner.RepresentationVersion != source.representation || !HullCell(source.map, round.hullCell))
            { DestroyProjectile(round, "hull source invalid"); return false; }
            Vector2 local = new Vector2(round.hullCell.x + .5f, round.hullCell.z + .5f)
                - SpaceTransform.ShipLocalOrigin(source.map);
            var pose = SpaceTransform.RigidPointPose(source.owner.pose, local, 0f);
            ((AttachedHullRound)round.container).Follow(pose, beginTick);
            if (beginTick) round.prevPosition = pose.position;
            return true;
        }

        private SpacePose HullPoseAt(SpaceObject owner, float fraction)
        {
            if (!hullStarts.TryGetValue(owner.objectId, out var start) || start.owner != owner
                || start.version != owner.RepresentationVersion) return owner.pose;
            float t = start.fraction >= 1f ? 0f : Mathf.Clamp01((fraction - start.fraction) / (1f - start.fraction));
            SpacePose pose = SpaceTransform.LerpPose(start.pose, owner.pose, t);
            // 已支付的本 tick 推力不再积分；只把当前真实速度输运到接触位置。
            pose.velocity = SpaceTransform.TransportVelocity(owner.pose, pose.position);
            pose.angularVelocity = owner.pose.angularVelocity;
            return pose;
        }

        private SpacePose? HullStartOf(SpaceObject owner) => HullPoseAt(owner, hullFraction);

        private void PrepareHullSweep(Projectile round)
        {
            if (round.dead || !FollowHullRound(round, false)) { round.body.dead = true; return; }
            var source = round.hullSource;
            var local = new Vector2(round.hullCell.x + .5f, round.hullCell.z + .5f) - SpaceTransform.ShipLocalOrigin(source.map);
            round.body.from = SpaceTransform.ShipLocalToUniverse(HullPoseAt(source.owner, hullFraction), local);
            round.body.to = round.container.pose.position;
            round.body.sweepStartFraction = hullFraction;
        }

        internal void BeginHullImpact(SpaceCollision.ImpactEvent evt)
        {
            hullFraction = Mathf.Max(hullFraction, evt.contactFraction);
            evt.hullSource.pose = HullPoseAt(evt.hullSource, hullFraction);
            evt.defender.pose = HullPoseAt(evt.defender, hullFraction);
            // 查询点在目标图的事实坐标已经确定，随接触时刻的目标位姿转换一次。
            evt.hitUniverse = SpaceTransform.MapPointToUniverse(evt.defender, evt.defender.map, evt.hitDefenderLocal);
            evt.contactNormal = SpaceTransform.MapDirectionToUniverseTangent(evt.defender, evt.defender.map,
                evt.hitDefenderLocal, evt.contactLocalNormal);
        }

        internal void EndHullImpact(SpaceCollision.ImpactEvent evt, ShipPhysics.MassReport own, ShipPhysics.MassReport other)
        {
            Finish(evt.hullSource, own); Finish(evt.defender, other);
            void Finish(SpaceObject body, ShipPhysics.MassReport report)
            {
                SpacePose contact = body.pose;
                hullStarts[body.objectId] = (body, body.RepresentationVersion, contact, hullFraction);
                if (report != null) body.pose = ShipPhysics.DriftAfterContact(contact, report, (1f - hullFraction) * Dt);
            }
        }

        private void FlushHullSweeps()
        {
            if (!resolvingProjectiles || !hullChangesPending || hullSources.Count == 0) return;
            hullChangesPending = false;
            int firstNew = projectiles.Count;
            RefreshHullRounds(false);
            for (int i = firstNew; i < projectiles.Count; i++)
                if (!projectiles[i].dead && projectiles[i].hullSource != null) AddProjectileSweep(projectiles[i]);
        }

        internal bool BeginHullHit(SpaceObject source, IntVec3 from, Thing own,
            SpaceObject target, IntVec3 to, Thing other)
        {
            int tick = Find.TickManager.TicksGame;
            if (hullHitTick != tick) { hullHitTick = tick; hullHits.Clear(); }
            int left = own?.thingIDNumber ?? -source.map.cellIndices.CellToIndex(from) - 1;
            int right = other?.thingIDNumber ?? -target.map.cellIndices.CellToIndex(to) - 1;
            return source.objectId < target.objectId
                ? hullHits.Add((source.objectId, target.objectId, left, right))
                : hullHits.Add((target.objectId, source.objectId, right, left));
        }

        // 战斗候选只把来袭弹丸变换到候选船体局部系；求交仍走现有 RayQuery。
        // 对方开关不参与筛选：实际登记的外层弹丸本身就是威胁来源。
        internal bool HullRouteClear(ShipBase ship, CombatTrajectoryIntent intent, float elapsed,
            out DriveSubject blocker, out int blockedSecond)
        {
            blocker = default; blockedSecond = 0;
            if (!universe.Controls.CollisionAvoidanceEnabled(ship)) return true;
            var target = universe.FrameOf(ship.Map);
            if (target == null) return true;
            using var timing = universe.Performance.Measure(PerformanceStage.CombatRoute);
            SpaceCollision.RayQuery query = null;
            const float step = .1f;
            foreach (var round in projectiles)
            {
                var source = round.hullSource;
                if (source == null || round.dead || source.owner == target || source.owner.map != source.map) continue;
                float reach = 4f * (source.owner.pose.velocity.magnitude + target.pose.velocity.magnitude)
                    + SpaceCollision.BoundsRadiusOf(source.owner) + SpaceCollision.BoundsRadiusOf(target) + 10f;
                if (OrbitSphere.ArcCells(source.owner.pose.position, target.pose.position) > reach) continue;
                query ??= new SpaceCollision.RayQuery(target, ship, collectCover: false);
                Vector2 local = new Vector2(round.hullCell.x + .5f, round.hullCell.z + .5f) - SpaceTransform.ShipLocalOrigin(source.map);
                Vector2 Previous(float seconds)
                {
                    var parent = source.owner.pose;
                    parent.position = OrbitSphere.Move(parent.position, parent.velocity * seconds);
                    parent.angle += parent.angularVelocity * seconds;
                    Vector3 point = SpaceTransform.ShipLocalToUniverse(parent, local);
                    intent.Sample(elapsed + seconds, out var position, out _, out float angle, out _);
                    return SpaceTransform.ShipLocalOrigin(ship.Map)
                        + SpaceTransform.UniverseToShipLocal(new SpacePose(position, angle), point);
                }
                Vector2 from = Previous(0);
                for (int i = 1; i <= 40; i++)
                {
                    Vector2 to = Previous(i * step), delta = to - from;
                    if (query.Next(from, delta, delta.magnitude, round.def, FootprintRadius(round.def),
                        null, null, _ => false, _ => false, out _))
                    { blocker = source.subject; blockedSecond = Mathf.CeilToInt(i * step); return false; }
                    from = to;
                }
            }
            return true;
        }
    }
}
