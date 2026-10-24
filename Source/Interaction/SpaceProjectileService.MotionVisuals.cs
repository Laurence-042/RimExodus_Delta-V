using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed partial class SpaceProjectileService
    {
        private const int MaxDriveExhaust = 768;
        private const int MaxDriveFlames = 384;
        private const int DustCount = 96;
        private const float DustRadius = 110f;
        private readonly List<CombatParticle> driveExhaust = new List<CombatParticle>();
        private readonly List<Vector3> nearDust = new List<Vector3>();
        private readonly Dictionary<int, EngineGroups> engineGroups = new Dictionary<int, EngineGroups>();
        private readonly List<int> staleEngineGroups = new List<int>();
        private readonly List<CombatParticle> driveFlames = new List<CombatParticle>();
        private int motionSequence;
        private int feedbackMapId = -1;
        private int hullSoundAfter;
        private PendingHullSound pendingHullSound;

        private sealed class EngineGroups
        {
            public int mapId, version, seen;
            public readonly float[] power = new float[4], level = new float[4], lastPower = new float[4];
            public readonly Vector2[] point = new Vector2[4], lastPoint = new Vector2[4];
            public readonly bool[] active = new bool[4];
            public readonly int[] lastPowered = new int[4], started = new int[4], nextStart = new int[4];
        }

        private struct PendingHullSound
        {
            public int objectId, version, mapId, due;
            public DriveSubject subject;
            public float strength;
        }

        // 在对象本 tick 出力/积分和命中之后采样。只产生表现，不写运动或推进器。
        internal void TickMotionFeedback()
        {
            int now = Find.TickManager.TicksGame;
            for (int i = driveExhaust.Count - 1; i >= 0; i--)
                if (now - driveExhaust[i].bornTick >= driveExhaust[i].lifetime) driveExhaust.RemoveAt(i);
            for (int i = driveFlames.Count - 1; i >= 0; i--)
                if (now - driveFlames[i].bornTick >= driveFlames[i].lifetime) driveFlames.RemoveAt(i);
            Map map = Find.CurrentMap;
            SpaceObject frame = map == null ? null : universe.FrameOf(map);
            if (frame == null || !WorldRendererUtility.DrawingMap)
            {
                engineGroups.Clear();
                nearDust.Clear();
                pendingHullSound = default;
                feedbackMapId = -1;
                return;
            }
            if (feedbackMapId != map.uniqueID)
            {
                engineGroups.Clear(); // 切图不重播已经点火的引擎，也不携带上一船的余响。
                pendingHullSound = default;
                feedbackMapId = map.uniqueID;
            }
            Vector3 camera = Find.Camera.transform.position;
            Vector3 center = SpaceTransform.MapPointToUniverse(frame, map, new Vector2(camera.x, camera.z));
            UpdateNearDust(center);
            foreach (Projectile p in projectiles)
            {
                if (!p.adoptedShuttle || p.dead || p.phase != FlightPhase.PoweredFlight) continue;
                ThrusterVisualProfile profile = p.building?.TryGetComp<CompSpaceShuttle>()?.ShuttleProps.afterburner;
                if (profile == null || p.container.map == null
                    || OrbitSphere.Offset(center, p.container.pose.position).sqrMagnitude > 240f * 240f) continue;
                for (int i = 0; i < p.exhaustPorts.Count; i++)
                {
                    ExhaustPort port = p.exhaustPorts[i];
                    EmitDriveExhaust(p.container, SpaceTransform.ShipLocalToMap(p.container.map, port.localPosition),
                        new Vector2(Mathf.Cos(port.localAngle), Mathf.Sin(port.localAngle)), 4f, 1f,
                        profile, p.container.objectId * 31 + i, now);
                }
            }
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj.map == null || obj.map.Disposed || obj.projectileRoleOverride
                    || obj != frame && !obj.ParticipatesInOverlays
                    || universe.Generation?.IsGenerating(obj.map) == true) continue;
                if (OrbitSphere.Offset(center, obj.pose.position).sqrMagnitude > 240f * 240f) continue;
                DriveInfo info = obj.MotionInfo;
                if (info == null) continue;
                bool existing = engineGroups.TryGetValue(obj.objectId, out EngineGroups groups)
                    && groups.version == obj.RepresentationVersion && groups.mapId == obj.map.uniqueID;
                if (!existing)
                    engineGroups[obj.objectId] = groups = new EngineGroups
                    { mapId = obj.map.uniqueID, version = obj.RepresentationVersion };
                groups.seen = now;
                System.Array.Clear(groups.power, 0, 4);
                System.Array.Clear(groups.level, 0, 4);
                System.Array.Clear(groups.point, 0, 4);
                bool fresh = Time.time - info.throttlesWrittenAt <= 0.25f;
                for (int i = 0; i < info.thrusters.Count; i++)
                {
                    ThrusterInfo thruster = info.thrusters[i];
                    float throttle = fresh && thruster.operational ? thruster.throttle : 0f;
                    if (throttle <= 0.001f) continue;
                    int group = (Mathf.RoundToInt(Mathf.Atan2(thruster.dir.y, thruster.dir.x)
                        / (Mathf.PI * 0.5f)) + 4) % 4;
                    float force = thruster.thrust * throttle;
                    groups.power[group] += force;
                    groups.level[group] = Mathf.Max(groups.level[group], throttle);
                    groups.point[group] += SpaceTransform.ShipLocalToMap(obj.map, thruster.localPos) * force;
                    ThrusterVisualProfile profile = thruster.visuals;
                    if (profile != null && ThrusterEffects.TryGetExhaustPort(thruster, obj.map,
                        out Vector2 point, out float size))
                        EmitDriveExhaust(obj, point, thruster.dir, size, throttle, profile,
                            obj.objectId * 31 + i, now);
                }
                for (int g = 0; g < 4; g++) UpdateEngineGroup(obj, groups, g, now, existing);
            }
            staleEngineGroups.Clear();
            foreach (var entry in engineGroups)
                if (entry.Value.seen != now) staleEngineGroups.Add(entry.Key);
            foreach (int id in staleEngineGroups) engineGroups.Remove(id);
            PlayPendingHullSound(now);
        }

        private void UpdateEngineGroup(SpaceObject obj, EngineGroups groups, int g, int now, bool existing)
        {
            bool powered = groups.level[g] >= (groups.active[g] ? 0.025f : 0.08f);
            if (powered)
            {
                groups.lastPowered[g] = now;
                groups.lastPower[g] = groups.power[g];
                groups.lastPoint[g] = groups.point[g] / Mathf.Max(0.001f, groups.power[g]);
                if (!groups.active[g])
                {
                    groups.active[g] = true;
                    groups.started[g] = now;
                    if (existing && now >= groups.nextStart[g])
                    {
                        PlayEngineEdge(obj, groups.lastPoint[g], true, groups.power[g]);
                        groups.nextStart[g] = now + 30;
                    }
                }
            }
            else if (groups.active[g] && now - groups.lastPowered[g] >= 12)
            {
                groups.active[g] = false;
                if (now - groups.started[g] >= 18)
                    PlayEngineEdge(obj, groups.lastPoint[g], false, groups.lastPower[g]);
            }
        }

        private void PlayEngineEdge(SpaceObject obj, Vector2 point, bool start, float power)
        {
            string name = start ? "DeltaV_EngineStart" : "DeltaV_EngineStop";
            PlayCombatSound(DefDatabase<SoundDef>.GetNamed(name + "Map"),
                DefDatabase<SoundDef>.GetNamed(name + "Shell"), obj, point,
                SpaceTransform.MapPointToUniverse(obj, obj.map, point), 12,
                Mathf.Lerp(0.2f, 0.65f, Mathf.Clamp01(power / 400f)), false);
        }

        private void UpdateNearDust(Vector3 center)
        {
            // 视觉点保存在真实宇宙位置；相机移出范围后才回收，不随按键或船速滚动。
            for (int i = nearDust.Count - 1; i >= 0; i--)
            {
                Vector2 offset = OrbitSphere.Offset(center, nearDust[i]);
                if (offset.sqrMagnitude <= DustRadius * DustRadius) continue;
                if (offset.sqrMagnitude > 4f * DustRadius * DustRadius) nearDust.RemoveAt(i);
                else
                    // 离开淡出边缘的点从对侧边缘补入，避免持续移动时不断在视野中心冒点。
                    nearDust[i] = OrbitSphere.Move(center, -offset.normalized * (DustRadius - 0.1f));
            }
            while (nearDust.Count < DustCount)
            {
                int seed = ++motionSequence;
                float angle = Variation(seed, 17) * Mathf.PI * 2f;
                float radius = DustRadius * Mathf.Sqrt(Variation(seed, 31));
                nearDust.Add(OrbitSphere.Move(center, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius));
            }
        }

        private void EmitDriveExhaust(SpaceObject obj, Vector2 point, Vector2 thrustDirection,
            float nozzleSize, float throttle, ThrusterVisualProfile profile, int seed, int now)
        {
            Vector3 position = SpaceTransform.MapPointToUniverse(obj, obj.map, point);
            Vector2 direction = SpaceTransform.MapDirectionToUniverseTangent(obj, obj.map, point, -thrustDirection);
            Vector2 velocity = SpaceTransform.VelocityAtLocalPoint(obj.pose,
                SpaceTransform.MapToShipLocal(obj.map, point), position);
            float scale = Mathf.Clamp(nozzleSize * 0.25f, 0.35f, 1.6f) * Mathf.Sqrt(throttle);
            int interval = Mathf.Max(1, profile.smokeInterval);
            if (profile.smoke && (now + seed) % interval == 0 && driveExhaust.Count < MaxDriveExhaust)
                AddDriveParticle(driveExhaust, position, velocity, direction, scale, throttle, profile, now, false);
            if (profile.flame && driveFlames.Count < MaxDriveFlames)
                AddDriveParticle(driveFlames, position, velocity, direction, scale, throttle, profile, now, true);
        }

        private void AddDriveParticle(List<CombatParticle> pool, Vector3 position, Vector2 velocity,
            Vector2 direction, float scale, float throttle, ThrusterVisualProfile profile, int now, bool hot)
        {
            float v = Variation(++motionSequence, 17);
            Vector2 side = new Vector2(-direction.y, direction.x);
            pool.Add(new CombatParticle
            {
                origin = position,
                velocity = velocity + direction * (hot ? profile.flameSpeed : profile.exhaustSpeed)
                    * Mathf.Lerp(0.5f, 1f, Mathf.Sqrt(throttle))
                    + side * ((v * 2f - 1f) * profile.spread * (hot ? 0.25f : 1f)),
                size = scale * (hot ? profile.flameSize : profile.smokeSize) * Mathf.Lerp(0.85f, 1.15f, v),
                angle = v * Mathf.PI * 2f, spin = (v - 0.5f) * 1.5f,
                bornTick = now, lifetime = Mathf.Max(1, hot ? profile.flameLifetime : profile.smokeLifetime),
                kind = ParticleKind.Exhaust, exhaustVisual = profile
            });
        }

        internal static void NearDustAppearance(float distanceSquared, out float size, out float opacity)
        {
            // 纯显示的接近感，不改变粒子宇宙锚点，不需要深度模拟或每粒子材质。
            float near = 1f - Mathf.Clamp01(distanceSquared / (60f * 60f));
            near = near * near * (3f - 2f * near);
            size = Mathf.Lerp(0.85f, 1.15f, near);
            opacity = Mathf.Lerp(0.8f, 0.9f, near);
        }

        internal int EmitMotionVisuals(SpaceObject frame, Map map, ShellLayer layer, int slot)
        {
            float altitude = AltitudeLayer.Terrain.AltitudeFor();
            Vector3 camera = Find.Camera.transform.position;
            float zoomFade = Mathf.InverseLerp(130f, 65f, Find.CameraDriver.RootSize);
            Vector2 shipCenter = SpaceTransform.ShipLocalToMap(map, Vector2.zero);
            MotionParticleRenderer.Begin(MotionParticleRenderer.Kind.Dust);
            if (zoomFade > 0f)
                foreach (Vector3 point in nearDust)
                {
                    Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, map, point, altitude);
                    float distance = new Vector2(pos.x - camera.x, pos.z - camera.z).magnitude;
                    NearDustAppearance((new Vector2(pos.x, pos.z) - shipCenter).sqrMagnitude,
                        out float size, out float opacity);
                    opacity *= Mathf.InverseLerp(DustRadius, DustRadius * 0.65f, distance) * zoomFade;
                    MotionParticleRenderer.Quad(MotionParticleRenderer.Kind.Dust, pos, Quaternion.identity,
                        size, size, new Color(0.85f, 0.93f, 1f, opacity));
                }
            slot = MotionParticleRenderer.Emit(MotionParticleRenderer.Kind.Dust, layer, slot);
            int now = Find.TickManager.TicksGame;
            CellRect view = Find.CameraDriver.CurrentViewRect;
            MotionParticleRenderer.Begin(MotionParticleRenderer.Kind.Smoke);
            foreach (CombatParticle particle in driveExhaust) DrawDriveParticle(particle, frame, map, altitude, now, false, view);
            slot = MotionParticleRenderer.Emit(MotionParticleRenderer.Kind.Smoke, layer, slot);
            MotionParticleRenderer.Begin(MotionParticleRenderer.Kind.Flame);
            foreach (CombatParticle particle in driveFlames) DrawDriveParticle(particle, frame, map, altitude, now, true, view);
            return MotionParticleRenderer.Emit(MotionParticleRenderer.Kind.Flame, layer, slot);
        }

        private static void DrawDriveParticle(CombatParticle p, SpaceObject frame, Map map, float altitude, int now, bool hot, CellRect view)
        {
            float age = (now - p.bornTick) / (float)p.lifetime;
            if (age < 0f || age >= 1f) return;
            float seconds = (now - p.bornTick) / 60f;
            Vector3 position = OrbitSphere.Move(p.origin, p.velocity * seconds);
            Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, map, position, altitude);
            float extent = hot ? p.size * (1f + age * 0.6f) * 1.42f
                : p.size * (1f + age * p.exhaustVisual.smokeGrowth) * 0.71f;
            if (pos.x + extent < view.minX || pos.x - extent > view.maxX + 1
                || pos.z + extent < view.minZ || pos.z - extent > view.maxZ + 1) return;
            Quaternion rotation = SpaceTransform.FocusedMeshRotation(frame, p.angle + p.spin * seconds);
            if (!hot)
            {
                float size = p.size * (1f + age * p.exhaustVisual.smokeGrowth);
                Color color = p.exhaustVisual.smokeColor;
                color.a = p.exhaustVisual.smokeOpacity * Mathf.Pow(1f - age, 1.25f);
                MotionParticleRenderer.Quad(MotionParticleRenderer.Kind.Smoke, pos, rotation, size, size, color);
                return;
            }
            float width = p.size * (1f + age * 0.6f);
            Color flame = Color.Lerp(new Color(1f, 0.75f, 0.12f), new Color(0.95f, 0.15f, 0.015f), age);
            flame.a = (1f - age) * 0.95f;
            MotionParticleRenderer.Quad(MotionParticleRenderer.Kind.Flame, pos, rotation, width * 2f, width * 2f, flame);
            if (age < 0.65f)
                MotionParticleRenderer.Quad(MotionParticleRenderer.Kind.Flame, pos, rotation, width, width,
                    new Color(1f, 1f, 0.85f, Mathf.InverseLerp(0.65f, 0f, age)));
        }

        private void QueueHullSound(SpaceCollision.VisualContact contact, CombatVisualStyle style)
        {
            if (contact.shieldMode.HasValue || contact.missileDestroyed
                || contact.defender.map != Find.CurrentMap
                || style != CombatVisualStyle.Heavy && style != CombatVisualStyle.Rail && !contact.explosive
                || DeltaVMod.CurrentSettings?.spaceImpactSound == false)
            return;
            if (!ShipStatusPanel.TryGetSelectedStructure(Find.CurrentMap, out DriveSubject selected)
                || selected != contact.defenderSubject || selected.key <= 0)
            return;
            int now = Find.TickManager.TicksGame;
            if (now < hullSoundAfter) return;
            float strength = contact.explosive ? 0.7f : style == CombatVisualStyle.Rail ? 0.6f : 0.45f;
            if (pendingHullSound.objectId != 0)
            {
                pendingHullSound.strength = Mathf.Max(pendingHullSound.strength, strength);
                return;
            }
            pendingHullSound = new PendingHullSound
            {
                objectId = contact.defender.objectId, version = contact.defender.RepresentationVersion,
                mapId = contact.defender.map.uniqueID, subject = selected, due = now + 4, strength = strength
            };
        }

        private void PlayPendingHullSound(int now)
        {
            if (pendingHullSound.objectId == 0 || now < pendingHullSound.due) return;
            PendingHullSound sound = pendingHullSound;
            pendingHullSound = default;
            SpaceObject obj = universe.ObjectById(sound.objectId);
            if (obj?.map == null || obj.map != Find.CurrentMap || obj.RepresentationVersion != sound.version
                || obj.map.uniqueID != sound.mapId || DeltaVMod.CurrentSettings?.spaceImpactSound == false
                || !ShipStatusPanel.TryGetSelectedStructure(obj.map, out DriveSubject selected)
                || selected != sound.subject)
            {
                return;
            }
            var info = Verse.Sound.SoundInfo.OnCamera();
            info.volumeFactor = sound.strength;
            Rand.PushState();
            try { Verse.Sound.SoundStarter.PlayOneShot(DefDatabase<SoundDef>.GetNamed("DeltaV_HullResonance"), info); }
            finally { Rand.PopState(); }
            hullSoundAfter = now + 30;
        }

        private void ForgetMotionFeedback(Map map)
        {
            staleEngineGroups.Clear();
            foreach (var entry in engineGroups)
                if (entry.Value.mapId == map.uniqueID) staleEngineGroups.Add(entry.Key);
            foreach (int id in staleEngineGroups) engineGroups.Remove(id);
            if (pendingHullSound.mapId == map.uniqueID) pendingHullSound = default;
            // 已脱离粒子不再持有 Map/Object 引用，按短寿命自行释放。
        }

        internal void ResetMotionFeedback()
        {
            driveExhaust.Clear(); driveFlames.Clear(); nearDust.Clear(); engineGroups.Clear(); staleEngineGroups.Clear();
            pendingHullSound = default; feedbackMapId = -1; hullSoundAfter = 0; motionSequence = 0;
        }
    }
}
