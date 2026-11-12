using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DeltaV
{
    public sealed partial class SpaceProjectileService
    {
        private const int MaxCombatFlashes = 128;
        private const int MaxCombatParticles = 768;
        private const int ImpactLifetimeTicks = 60;

        // Attached heat/flash follows the same representation. Detached debris keeps the
        // rigid-point velocity captured at birth. Neither modifies a Thing or a SpaceObject.
        private struct VisualAnchor
        {
            public int objectId, representationVersion, mapId, bornTick;
            public Vector2 local;
            public SpacePose birth;
            public Vector3 position;
            public Vector2 velocity;
        }

        private sealed class MuzzleFlash
        {
            public VisualAnchor anchor;
            public float angle, size;
            public int bornTick;
            public CombatVisualStyle style;
            public bool accelerator;
        }

        private sealed class ImpactFx
        {
            public VisualAnchor anchor;
            public int bornTick;
            public float size;
            public CombatVisualStyle style;
            public bool explosive, nativeExplosion, shieldBroken, shieldEmergency;
            public SpaceShieldMode? shieldMode;
            public Vector2 normal, shieldCenter;
            public Vector2 lineStart, lineEnd;
            public float shieldRadius;
        }

        private enum ParticleKind { Smoke, Spark, Chip, Fireball, Exhaust }
        private struct CombatParticle
        {
            public Vector3 origin;
            public Vector2 velocity;
            public float angle, spin, size;
            public int bornTick, lifetime;
            public ParticleKind kind;
            public CombatVisualStyle style;
            public ThrusterVisualProfile exhaustVisual;
        }

        private readonly List<CombatParticle> particles = new List<CombatParticle>();
        private readonly List<SpaceCollision.VisualContact> visualContacts = new List<SpaceCollision.VisualContact>();
        private readonly Dictionary<SoundDef, int> soundTicks = new Dictionary<SoundDef, int>();
        private int visualSequence;
        private static Material[] coreMaterials, railMaterials, electromagneticMaterials, kineticShieldMaterials, emergencyShieldMaterials;
        private static Material[] beamCoreMaterials, beamGlowMaterials, beamRailMaterials, beamAmberMaterials;
        private static Texture2D beamStripTexture;

        private VisualAnchor Anchor(SpaceObject obj, Vector2 mapPoint, Vector3 position)
        {
            Vector2 local = SpaceTransform.MapToShipLocal(obj.map, mapPoint);
            return new VisualAnchor
            {
                objectId = obj.objectId, representationVersion = obj.RepresentationVersion, mapId = obj.map.uniqueID,
                bornTick = Find.TickManager.TicksGame,
                local = local, birth = obj.pose, position = position,
                velocity = SpaceTransform.VelocityAtLocalPoint(obj.pose, local, position)
            };
        }

        private void QueueVisualContact(SpaceCollision.VisualContact contact) =>
            AddBounded(visualContacts, contact, 1024);

        private SpacePose AnchorPose(VisualAnchor anchor)
        {
            SpaceObject obj = universe.ObjectById(anchor.objectId);
            return obj != null && obj.RepresentationVersion == anchor.representationVersion
                ? obj.pose : anchor.birth;
        }

        private Vector3 AnchorPosition(VisualAnchor anchor)
        {
            SpaceObject obj = universe.ObjectById(anchor.objectId);
            return obj != null && obj.RepresentationVersion == anchor.representationVersion
                ? SpaceTransform.ShipLocalToUniverse(obj.pose, anchor.local)
                : OrbitSphere.Move(anchor.position, anchor.velocity * ((Find.TickManager.TicksGame - anchor.bornTick) / 60f));
        }

        private void BeamEndpoints(Beam beam, out Vector3 from, out Vector3 to)
        {
            from = beam.followsMuzzle && beam.continuousWeapon?.Container != null
                ? SpaceTransform.MapPointToUniverse(beam.continuousWeapon.Container,
                    beam.continuousWeapon.parent.Map, SpaceWeaponGeometry.MuzzleMapPoint(beam.continuousWeapon))
                : beam.fromAnchor.HasValue ? AnchorPosition(beam.fromAnchor.Value) : beam.from;
            to = beam.toAnchor.HasValue ? AnchorPosition(beam.toAnchor.Value) : beam.to;
        }

        /// <summary>Only the successful shot supplies muzzle, ammunition and paid accelerator stages.</summary>
        public void RegisterShotVisuals(SpaceWeaponShot shot)
        {
            CompProperties_SpaceProjectile props = shot.projectileProps;
            CombatVisualStyle style = props.visualStyle;
            if (props.continuousBeam)
            {
                int now = Find.TickManager.TicksGame;
                if (!beams.Exists(b => b.continuousLauncher == shot.weapon.parent
                    && b.sourceObjectId == shot.container.objectId && now - b.bornTick <= 1))
                    PlayCombatSound(props.fireSoundMap, props.fireSoundShell, shot.container,
                        shot.muzzleMap, shot.muzzleUniverse, 1);
                return;
            }
            float size = shot.weapon.WeaponProps.muzzleFlashSize;
            if (props.laserRange > 0f) size = Mathf.Max(size, props.laserBeamWidth * 3.5f);
            if (size > 0f)
            {
                VisualAnchor anchor = Anchor(shot.container, shot.muzzleMap, shot.muzzleUniverse);
                AddBounded(muzzleFlashes, new MuzzleFlash
                {
                    anchor = anchor, angle = shot.launchMapAngle, size = size,
                    bornTick = Find.TickManager.TicksGame, style = style
                }, MaxCombatFlashes);
                if (props.laserRange <= 0f)
                    SpawnParticles(anchor, shot.container, shot.muzzleMap,
                        SpaceTransform.Rotate(Vector2.right, shot.launchMapAngle),
                        size * 0.35f, style, ParticleKind.Smoke, 3, 1.1f, 2.4f);
            }
            shot.weapon.NotifyVisualShot(shot.launchMapAngle);
            foreach (CompSpaceRailgun stage in shot.ActiveAccelerators)
            {
                Vector3 draw = stage.parent.DrawPos;
                Vector2 point = new Vector2(draw.x, draw.z);
                AddBounded(muzzleFlashes, new MuzzleFlash
                {
                    anchor = Anchor(shot.container, point,
                        SpaceTransform.MapPointToUniverse(shot.container, stage.parent.Map, point)),
                    angle = shot.launchMapAngle, size = stage.parent.def.size.x * 0.55f,
                    bornTick = Find.TickManager.TicksGame, style = CombatVisualStyle.Rail, accelerator = true
                }, MaxCombatFlashes);
            }
            PlayCombatSound(props.fireSoundMap, props.fireSoundShell, shot.container,
                shot.muzzleMap, shot.muzzleUniverse, style == CombatVisualStyle.Kinetic ? 3 : 1);
        }

        private void RegisterImpactFx(SpaceCollision.VisualContact contact)
        {
            if (contact.defender?.map == null) return;
            CompProperties_SpaceProjectile props = contact.projectileDef
                ?.GetCompProperties<CompProperties_SpaceProjectile>();
            CombatVisualStyle style = props?.visualStyle ?? CombatVisualStyle.Kinetic;
            QueueHullSound(contact, style);
            // 非破坏性耦合只显示光束；击中盾时仍显示统一盾面反馈。
            if (style == CombatVisualStyle.Coupling && !contact.shieldMode.HasValue) return;
            VisualAnchor anchor = Anchor(contact.defender, contact.localPoint, contact.position);
            // The interception burst outlives the destroyed missile's disposable map.
            if (contact.missileDestroyed) { anchor.objectId = 0; anchor.mapId = -1; }
            if (props?.tracerLength > 0f)
            {
                Vector2 back = OrbitSphere.Offset(contact.position, contact.pathFrom);
                AddBounded(beams, new Beam
                {
                    from = OrbitSphere.Move(contact.position, back.normalized * Mathf.Min(back.magnitude, props.tracerLength)),
                    to = contact.position, bornTick = Find.TickManager.TicksGame,
                    lifetimeTicks = style == CombatVisualStyle.Rail ? 6 : 3, style = style, width = props.tracerWidth
                }, 256);
            }
            float size = Mathf.Clamp(0.6f + Mathf.Sqrt(Mathf.Max(0f, contact.damageScale)) * 0.65f, 0.7f, 3.5f);
            if (style == CombatVisualStyle.Heavy) size *= 1.35f;
            if (style == CombatVisualStyle.Energy) size *= props.guaranteedHitMissiles ? 0.6f : 0.9f;
            bool explosion = contact.explosive || contact.missileDestroyed;
            if (explosion) size = Mathf.Max(size, Mathf.Clamp(contact.explosionRadius * 1.5f, 1.4f, 6f));
            AddBounded(impacts, new ImpactFx
            {
                anchor = anchor, bornTick = Find.TickManager.TicksGame, size = size, style = style,
                explosive = explosion, nativeExplosion = contact.nativeExplosion,
                shieldMode = contact.shieldMode, shieldBroken = contact.shieldBroken,
                shieldEmergency = contact.shieldEmergency,
                normal = contact.localNormal,
                shieldCenter = SpaceTransform.MapToShipLocal(contact.defender.map, contact.shieldCenter),
                lineStart = SpaceTransform.MapToShipLocal(contact.defender.map, contact.lineStart),
                lineEnd = SpaceTransform.MapToShipLocal(contact.defender.map, contact.lineEnd),
                shieldRadius = contact.shieldRadius
            }, MaxCombatFlashes);

            // Surface splash points back out of the struck face; actual penetration additionally
            // throws a narrow forward jet. No visual fragment participates in collision.
            int count = style == CombatVisualStyle.Kinetic ? 5 : 9;
            if (contact.shieldMode != SpaceShieldMode.Electromagnetic)
            {
                SpawnParticles(anchor, contact.defender, contact.localPoint, contact.localNormal,
                    size, style, ParticleKind.Spark, count, explosion ? Mathf.PI : 1.15f, 10f);
                if (contact.penetrated)
                    SpawnParticles(anchor, contact.defender, contact.localPoint, contact.localDirection,
                        size, style, ParticleKind.Spark, 7, 0.25f, 19f);
            }
            if (!contact.shieldMode.HasValue || explosion)
                SpawnParticles(anchor, contact.defender, contact.localPoint, contact.localNormal,
                    size, style, ParticleKind.Smoke, explosion ? 6 : 2, 1.8f, 2f);
            if (explosion)
                SpawnParticles(anchor, contact.defender, contact.localPoint, contact.localNormal,
                    size, CombatVisualStyle.Heavy, ParticleKind.Fireball, 4, Mathf.PI, 3.5f);

            SoundDef mapSound = props?.impactSoundMap, shellSound = props?.impactSoundShell;
            if (contact.shieldMode.HasValue)
            {
                string family = contact.shieldBroken ? "ShieldBreak" : "ShieldHit";
                mapSound = DefDatabase<SoundDef>.GetNamed("DeltaV_" + family + "Map");
                shellSound = DefDatabase<SoundDef>.GetNamed("DeltaV_" + family + "Shell");
            }
            else if (contact.nativeExplosion)
            {
                // GenExplosion already voices the focused map.
                mapSound = null;
                shellSound = DefDatabase<SoundDef>.GetNamed("DeltaV_ImpactExplosionShell");
            }
            PlayCombatSound(mapSound, shellSound, contact.defender, contact.localPoint, contact.position,
                style == CombatVisualStyle.Kinetic ? 3 : 1,
                shellPitch: contact.shieldMode.HasValue || contact.missileDestroyed ? 0.65f : 0.45f);
        }

        // Hash-based variation is local to visuals; it never advances Verse.Rand.
        private static float Variation(int seed, int salt)
        {
            uint x = unchecked((uint)(seed * 747796405 + salt * 289133645));
            x = (x ^ (x >> 16)) * 2246822519u;
            return (x & 65535u) / 65535f;
        }

        private void SpawnParticles(VisualAnchor anchor, SpaceObject owner, Vector2 point, Vector2 direction,
            float size, CombatVisualStyle style, ParticleKind kind, int count, float spread, float speed)
        {
            int seed = ++visualSequence;
            for (int i = 0; i < count && particles.Count < MaxCombatParticles; i++)
            {
                if (style == CombatVisualStyle.Kinetic && particles.Count >= 512) break;
                float v = Variation(seed, i * 5);
                Vector2 local = SpaceTransform.Rotate(direction, (v * 2f - 1f) * spread);
                Vector2 tangent = SpaceTransform.MapDirectionToUniverseTangent(owner, owner.map, point, local);
                particles.Add(new CombatParticle
                {
                    origin = anchor.position, velocity = anchor.velocity
                        + tangent * speed * (0.35f + Variation(seed, i * 5 + 1)) * Mathf.Sqrt(size),
                    angle = Mathf.Atan2(tangent.y, tangent.x),
                    spin = 0f,
                    size = kind == ParticleKind.Smoke || kind == ParticleKind.Fireball ? size * (0.65f + v * 0.6f)
                        : 0.22f + v * size * 0.25f,
                    bornTick = Find.TickManager.TicksGame,
                    lifetime = kind == ParticleKind.Fireball ? 9 + (int)(v * 9)
                        : kind == ParticleKind.Smoke ? 36 + (int)(v * 24)
                        : 10 + (int)(v * 17),
                    kind = kind, style = style
                });
            }
        }

        internal void NotifyFoundationDestroyed(Map map, IntVec3 cell)
            => EmitStructureFragments(map, cell);

        internal void NotifyRoofDestroyed(Map map, IntVec3 cell)
            => EmitStructureFragments(map, cell);

        private void EmitStructureFragments(Map map, IntVec3 cell)
        {
            SpaceObject owner = universe.FrameOf(map);
            if (owner == null || map.Disposed) return;
            Vector2 point = new Vector2(cell.x + 0.5f, cell.z + 0.5f);
            Vector3 origin = SpaceTransform.MapPointToUniverse(owner, map, point);
            Vector2 inherited = SpaceTransform.VelocityAtLocalPoint(owner.pose,
                SpaceTransform.MapToShipLocal(map, point), origin);
            // 毁坏入口给出的格点是唯一来源；不从命中贴图或残留建筑反推破口。
            int seed = ++visualSequence;
            for (int i = 0; i < 4 && particles.Count < MaxCombatParticles; i++)
            {
                float v = Variation(seed, i * 3), angle = v * Mathf.PI * 2f;
                Vector2 tangent = SpaceTransform.MapDirectionToUniverseTangent(owner, map, point,
                    new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)));
                particles.Add(new CombatParticle
                {
                    origin = origin, velocity = inherited + tangent * (2f + Variation(seed, i * 3 + 1) * 6f),
                    angle = angle, spin = (v > 0.5f ? 1f : -1f) * (3f + v * 7f),
                    size = 0.25f + v * 0.35f, bornTick = Find.TickManager.TicksGame,
                    lifetime = 150 + (int)(v * 90f), kind = ParticleKind.Chip
                });
            }
        }

        private void PlayCombatSound(SoundDef mapSound, SoundDef shellSound, SpaceObject owner,
            Vector2 local, Vector3 position, int spacing, float volume = 1f, bool combatSetting = true, float shellPitch = 1f)
        {
            if (combatSetting && DeltaVMod.CurrentSettings?.spaceImpactSound == false || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            bool focused = Find.CurrentMap == owner.map;
            SoundDef sound = focused ? mapSound : shellSound;
            if (sound == null)
            {
                return;
            }
            int tick = Find.TickManager.TicksGame;
            if (soundTicks.TryGetValue(sound, out int previous) && tick - previous < spacing)
            {
                return;
            }
            SoundInfo info;
            if (focused)
            {
                IntVec3 cell = new IntVec3(Mathf.FloorToInt(local.x), 0, Mathf.FloorToInt(local.y))
                    .ClampInsideMap(owner.map);
                info = SoundInfo.InMap(new TargetInfo(cell, owner.map));
            }
            else
            {
                Map map = Find.CurrentMap;
                SpaceObject frame = map == null ? null : universe.FrameOf(map);
                if (frame == null)
                {
                    return;
                }
                Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, map, position, 0f);
                // Planar distance: zooming out must not mute a battle that remains on screen.
                Vector3 camera = Find.Camera.transform.position;
                float distance = new Vector2(camera.x - pos.x, camera.z - pos.z).magnitude;
                if (distance >= ImpactHearFar)
                {
                    return;
                }
                info = SoundInfo.OnCamera();
                info.pitchFactor = shellPitch;
                info.volumeFactor = 1f - Mathf.InverseLerp(ImpactHearNear, ImpactHearFar, distance);
            }
            soundTicks[sound] = tick;
            info.volumeFactor *= volume;
            Rand.PushState();
            try { sound.PlayOneShot(info); }
            finally { Rand.PopState(); }
        }

        private static void AddBounded<T>(List<T> list, T value, int max)
        {
            if (list.Count >= max) list.RemoveAt(0);
            list.Add(value);
        }

        private void TickCombatVisuals(int tick)
        {
            for (int i = muzzleFlashes.Count - 1; i >= 0; i--)
                if (tick - muzzleFlashes[i].bornTick >= 16) muzzleFlashes.RemoveAt(i);
            for (int i = impacts.Count - 1; i >= 0; i--)
                if (tick - impacts[i].bornTick >= ImpactLifetimeTicks) impacts.RemoveAt(i);
            for (int i = particles.Count - 1; i >= 0; i--)
                if (tick - particles[i].bornTick >= particles[i].lifetime) particles.RemoveAt(i);
        }

        private void ForgetCombatVisuals(Map map)
        {
            muzzleFlashes.RemoveAll(f => f.anchor.mapId == map.uniqueID);
            impacts.RemoveAll(f => f.anchor.mapId == map.uniqueID);
            // Debris is already detached; bounded particles expire even after the source map is gone.
        }

        private static void EnsureCombatMaterials()
        {
            EnsureImpactMaterials();
            if (coreMaterials != null) return;
            coreMaterials = BuildImpactTiers("Things/Mote/BrightFlash", ShaderDatabase.MoteGlow, Color.white);
            railMaterials = BuildImpactTiers("Things/Mote/BrightFlash", ShaderDatabase.MoteGlow,
                new Color(0.35f, 0.75f, 1f));
            electromagneticMaterials = BuildImpactTiers("Things/Mote/BrightFlash", ShaderDatabase.MoteGlow,
                CompSpaceShield.ElectromagneticColor);
            kineticShieldMaterials = BuildImpactTiers("Things/Mote/BrightFlash", ShaderDatabase.MoteGlow,
                CompSpaceShield.KineticColor);
            emergencyShieldMaterials = BuildImpactTiers("Things/Mote/BrightFlash", ShaderDatabase.MoteGlow,
                CompSpaceShield.EmergencyColor);
            // A procedural cross-section preserves full beam length and a soft edge;
            // stretching a round flash over the entire shot would erase its endpoints.
            beamStripTexture = new Texture2D(64, 16, TextureFormat.RGBA32, false);
            beamStripTexture.wrapMode = TextureWrapMode.Clamp;
            var pixels = new Color[64 * 16];
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 64; x++)
            {
                float edge = 1f - Mathf.Abs((y + 0.5f) / 8f - 1f);
                float end = Mathf.Clamp01(Mathf.Min(x, 63 - x) / 2f);
                float light = edge * edge * end;
                pixels[y * 64 + x] = new Color(light, light, light, 1f);
            }
            beamStripTexture.SetPixels(pixels);
            beamStripTexture.Apply(false, true);
            beamCoreMaterials = BeamTiers(new Color(1f, 0.95f, 0.85f));
            beamGlowMaterials = BeamTiers(new Color(1f, 0.15f, 0.08f));
            beamRailMaterials = BeamTiers(new Color(0.35f, 0.75f, 1f));
            beamAmberMaterials = BeamTiers(new Color(1f, 0.6f, 0.25f));
        }

        private static Material[] BeamTiers(Color color)
        {
            var materials = new Material[12];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = new Material(ShaderDatabase.MoteGlow)
                {
                    mainTexture = beamStripTexture, renderQueue = 3202,
                    color = new Color(color.r, color.g, color.b, 1f - i / (float)materials.Length)
                };
            return materials;
        }

        private static Material Fade(Material[] materials, float age) =>
            materials[Mathf.Clamp((int)(Mathf.Clamp01(age) * materials.Length), 0, materials.Length - 1)];

        private static Material[] Glow(CombatVisualStyle style) =>
            style == CombatVisualStyle.Rail ? railMaterials
                : style == CombatVisualStyle.Energy ? impactEnergyMaterials : impactFlashMaterials;

        private static void FxQuad(Material material, Vector3 pos, Quaternion rot, float length, float width)
        {
            if (length <= 0f || width <= 0f) return;
            MapOverlayRenderer.AddQuadToBatch(MeshPool.plane10,
                Matrix4x4.TRS(pos, rot, new Vector3(length, 1f, width)), material);
        }

        private static void FxLine(Material material, Vector3 a, Vector3 b, float width, SpaceObject frame)
        {
            Vector3 delta = b - a;
            float length = new Vector2(delta.x, delta.z).magnitude;
            if (length < 0.001f) return;
            Quaternion rotation = SpaceTransform.FocusedMeshRotation(frame,
                Mathf.Atan2(delta.z, delta.x) + frame.pose.angle);
            FxQuad(material, (a + b) * 0.5f, rotation, length, width);
        }

        private void EmitCombatVisuals(SpaceObject frame, Map map, ShellLayer layer, ref int slot)
        {
            if (impacts.Count + particles.Count + muzzleFlashes.Count + beams.Count == 0) return;
            EnsureCombatMaterials();
            int now = Find.TickManager.TicksGame;
            float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 12f;
            Quaternion level = SpaceTransform.FocusedMeshRotation(frame, frame.pose.angle);
            MapOverlayRenderer.BeginQuadBatch();
            // Smoke first; hot cores and fragments remain readable above it.
            MotionParticleRenderer.Begin(MotionParticleRenderer.Kind.Metal);
            foreach (CombatParticle p in particles)
                if (p.kind == ParticleKind.Smoke) DrawParticle(p, frame, map, altitude, now);
            foreach (MuzzleFlash f in muzzleFlashes)
            {
                int age = now - f.bornTick;
                float fade = age / (f.style == CombatVisualStyle.Kinetic ? 7f : 13f);
                if (age < 0 || fade >= 1f) continue;
                Vector3 point = AnchorPosition(f.anchor);
                SpacePose pose = AnchorPose(f.anchor);
                Quaternion heading = SpaceTransform.FocusedMeshRotation(frame, pose.angle + f.angle);
                Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, map, point, altitude);
                float pulse = 1f - fade * fade;
                float length = f.size * (f.accelerator ? 1.1f : 2.4f) * pulse;
                FxQuad(Fade(Glow(f.style), fade), pos, heading, length, f.size * 0.9f * pulse);
                if (age < 4)
                    FxQuad(Fade(coreMaterials, age / 4f), pos, heading,
                        f.size * 1.4f, f.size * 0.38f);
            }
            foreach (ImpactFx f in impacts)
            {
                int age = now - f.bornTick;
                if (age < 0 || age >= ImpactLifetimeTicks) continue;
                Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, map, AnchorPosition(f.anchor), altitude);
                if (f.shieldMode.HasValue)
                {
                    DrawShieldImpact(f, frame, map, altitude, age);
                    if (!f.explosive) continue;
                }
                bool nativeFlash = f.nativeExplosion && map.uniqueID == f.anchor.mapId;
                float fade = age / (f.explosive ? 19f : f.style == CombatVisualStyle.Energy ? 30f : 12f);
                if (fade < 1f && !nativeFlash)
                {
                    float size = f.size * (f.explosive ? 2.4f : 1.4f) * (0.65f + fade);
                    FxQuad(Fade(Glow(f.style), Mathf.Sqrt(fade)), pos, level, size, size);
                }
                if (age < 4 && !nativeFlash)
                    FxQuad(Fade(coreMaterials, age / 4f), pos, level, f.size * 1.3f, f.size * 1.3f);
            }
            foreach (CombatParticle p in particles)
                if (p.kind != ParticleKind.Smoke) DrawParticle(p, frame, map, altitude, now);
            foreach (Beam beam in beams)
            {
                if (!beam.VisibleAt(now)) continue;
                float age = beam.FadeAge(now);
                if (age >= 1f) continue;
                BeamEndpoints(beam, out Vector3 from, out Vector3 to);
                Vector3 a = SpaceTransform.FocusedDrawPosition(frame, map, from, altitude);
                Vector3 b = SpaceTransform.FocusedDrawPosition(frame, map, to, altitude);
                if (beam.style == CombatVisualStyle.Tractor)
                {
                    Vector3 axis = b - a;
                    Vector3 side = new Vector3(-axis.z, 0f, axis.x).normalized;
                    float opening = Mathf.Min(.65f, axis.magnitude * .12f);
                    FxLine(Fade(beamRailMaterials, .45f), a, b, beam.width * 2f, frame);
                    FxLine(Fade(beamAmberMaterials, .3f), a, b + side * opening, beam.width * .4f, frame);
                    FxLine(Fade(beamAmberMaterials, .3f), a, b - side * opening, beam.width * .4f, frame);
                    // Tick-only inward chevrons; both endpoints use the actual collection pose.
                    for (int band = 0; band < 4; band++)
                    {
                        float t = Mathf.Repeat(band / 4f - now % 60 / 60f, 1f);
                        Vector3 tip = Vector3.Lerp(a, b, t);
                        Vector3 back = Vector3.Lerp(a, b, Mathf.Min(1f, t + .025f));
                        float width = opening * t;
                        FxLine(Fade(beamCoreMaterials, .25f), back + side * width, tip, beam.width * .45f, frame);
                        FxLine(Fade(beamCoreMaterials, .25f), back - side * width, tip, beam.width * .45f, frame);
                    }
                    continue;
                }
                float pulse = 1f - age * age;
                Material[] glow = beam.style == CombatVisualStyle.Energy ? beamGlowMaterials
                    : beam.style == CombatVisualStyle.Rail || beam.style == CombatVisualStyle.Coupling
                        ? beamRailMaterials : beamAmberMaterials;
                FxLine(Fade(glow, age), a, b, beam.width * 3.2f * pulse, frame);
                FxLine(Fade(beamCoreMaterials, age), a, b, beam.width * 0.65f * pulse, frame);
            }
            ShellLayer.Slot batch = layer.At(slot++);
            batch.layer.Emit(batch);
            slot = MotionParticleRenderer.Emit(MotionParticleRenderer.Kind.Metal, layer, slot);
        }

        private void DrawParticle(CombatParticle p, SpaceObject frame, Map map, float altitude, int now)
        {
            float age = (now - p.bornTick) / (float)p.lifetime;
            if (age < 0f || age >= 1f) return;
            float seconds = (now - p.bornTick) / 60f;
            Vector3 point = OrbitSphere.Move(p.origin, p.velocity * seconds);
            Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, map, point, altitude);
            Quaternion rotation = SpaceTransform.FocusedMeshRotation(frame, p.angle + p.spin * seconds);
            if (p.kind == ParticleKind.Smoke)
            {
                float size = p.size * (0.5f + age * 2f);
                FxQuad(Fade(impactSmokeMaterials, 0.35f + age * 0.65f), pos, rotation, size, size);
            }
            else if (p.kind == ParticleKind.Fireball)
            {
                float size = p.size * (0.6f + age * 1.8f);
                FxQuad(Fade(impactFlashMaterials, Mathf.Sqrt(age)), pos, rotation, size, size * 0.8f);
            }
            else if (p.kind == ParticleKind.Chip)
            {
                float tumble = Mathf.Abs(Mathf.Cos(p.spin * seconds));
                float light = Mathf.Lerp(0.25f, 1f, Mathf.Pow(tumble, 8f));
                MotionParticleRenderer.Quad(MotionParticleRenderer.Kind.Metal, pos, rotation,
                    p.size, p.size * (0.08f + tumble * 0.5f),
                    new Color(light * 0.9f, light * 0.95f, light, Mathf.InverseLerp(1f, 0.7f, age)));
            }
            else
            {
                float size = p.size * (1.4f - age);
                FxQuad(Fade(Glow(p.style), age), pos, rotation, size * 2f, size * 0.55f);
                FxQuad(Fade(coreMaterials, age), pos, rotation, size, size * 0.22f);
            }
        }

        private void DrawShieldImpact(ImpactFx f, SpaceObject frame, Map map, float altitude, int age)
        {
            float fade = age / (f.shieldBroken ? 28f : 18f);
            if (fade >= 1f) return;
            SpacePose pose = AnchorPose(f.anchor);
            Material[] color = f.shieldMode == SpaceShieldMode.Electromagnetic
                ? electromagneticMaterials : f.shieldMode == SpaceShieldMode.Kinetic
                ? kineticShieldMaterials : f.shieldEmergency ? emergencyShieldMaterials : coreMaterials;
            Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, map, AnchorPosition(f.anchor), altitude);
            Quaternion rotation = SpaceTransform.FocusedMeshRotation(frame, pose.angle
                + Mathf.Atan2(f.normal.y, f.normal.x) + Mathf.PI * 0.5f);
            if (f.shieldRadius > 0f)
                FxQuad(Fade(color, fade), pos, rotation, 1.5f, 0.7f * (1f - fade));
            else
            {
                Vector2 axis = (f.lineEnd - f.lineStart).normalized;
                float along = Vector2.Dot(f.anchor.local - f.lineStart, axis);
                float reach = 0.5f + fade * 4f;
                Vector2 a = f.lineStart + axis * Mathf.Max(0f, along - reach);
                Vector2 b = f.lineStart + axis * Mathf.Min((f.lineEnd - f.lineStart).magnitude, along + reach);
                FxLine(Fade(color, fade),
                    SpaceTransform.FocusedDrawPosition(frame, map, SpaceTransform.ShipLocalToUniverse(pose, a), altitude),
                    SpaceTransform.FocusedDrawPosition(frame, map, SpaceTransform.ShipLocalToUniverse(pose, b), altitude),
                    0.7f * (1f - fade), frame);
            }
            if (age < 4) FxQuad(Fade(coreMaterials, age / 4f), pos, rotation, 1.5f, 0.6f);
            if (f.shieldRadius <= 0f) return;
            // Ripple stays on the exact circular shield used by the collision query.
            float centerAngle = Mathf.Atan2(f.normal.y, f.normal.x);
            float spread = (0.2f + fade * (f.shieldBroken ? 10f : 5f)) / f.shieldRadius;
            for (int i = 0; i < 12; i++)
            {
                if (f.shieldBroken && i % 3 == 1) continue;
                Vector2 a = f.shieldCenter + SpaceTransform.Rotate(Vector2.right,
                    centerAngle - spread + 2f * spread * i / 12f) * f.shieldRadius;
                Vector2 b = f.shieldCenter + SpaceTransform.Rotate(Vector2.right,
                    centerAngle - spread + 2f * spread * (i + 1) / 12f) * f.shieldRadius;
                FxLine(Fade(color, fade),
                    SpaceTransform.FocusedDrawPosition(frame, map, SpaceTransform.ShipLocalToUniverse(pose, a), altitude),
                    SpaceTransform.FocusedDrawPosition(frame, map, SpaceTransform.ShipLocalToUniverse(pose, b), altitude),
                    f.shieldBroken ? 0.55f : 0.28f, frame);
            }
        }

        private void EmitTracer(Projectile p, SpaceObject frame, Vector3 center, Quaternion spriteRotation,
            ShellLayer layer, ref int slot)
        {
            if (p.props.tracerLength <= 0f || p.laser) return;
            float length = Mathf.Min(p.props.tracerLength,
                OrbitSphere.ArcCells(p.prevPosition, p.container.pose.position));
            if (length < 0.01f) return;
            Vector3 tail = center - (spriteRotation * Vector3.forward) * length;
            EnsureCombatMaterials();
            MapOverlayRenderer.BeginQuadBatch();
            FxLine(Glow(p.props.visualStyle)[0], tail, center, p.props.tracerWidth * 2.6f, frame);
            FxLine(coreMaterials[0], tail, center, p.props.tracerWidth, frame);
            ShellLayer.Slot batch = layer.At(slot++);
            batch.layer.Emit(batch);
        }

        private static void DestroyCombatMaterials()
        {
            DestroyImpactTiers(ref coreMaterials);
            DestroyImpactTiers(ref railMaterials);
            DestroyImpactTiers(ref electromagneticMaterials);
            DestroyImpactTiers(ref kineticShieldMaterials);
            DestroyImpactTiers(ref emergencyShieldMaterials);
            DestroyImpactTiers(ref beamCoreMaterials);
            DestroyImpactTiers(ref beamGlowMaterials);
            DestroyImpactTiers(ref beamRailMaterials);
            DestroyImpactTiers(ref beamAmberMaterials);
            if (beamStripTexture != null) Object.Destroy(beamStripTexture);
            beamStripTexture = null;
        }
    }
}
