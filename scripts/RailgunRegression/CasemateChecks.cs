using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static int casemateVisuals;
    static WeaponReject casemateReject;
    static void RecordCasemateReject(WeaponReject __0) => casemateReject = __0;
    static bool CountCasemateVisuals() { casemateVisuals++; return false; }
    static bool CasemateSkill(ref int __result) { __result = 10; return false; }
    static bool CasemateFoundation(ref bool __result) { __result = true; return false; }
    static bool CasemateAltitude(ref float __result) { __result = 0; return false; }

    static void CheckCasemate()
    {
        Reset();
        var patch = new Harmony("DeltaV.Regression.Casemate");
        patch.Patch(typeof(CompRefuelable).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        patch.Patch(AccessTools.Method(typeof(SpaceProjectileService), "RegisterShotVisuals"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountCasemateVisuals)));
        patch.Patch(AccessTools.Method(typeof(SkillRecord), "GetLevel"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CasemateSkill)));
        patch.Patch(AccessTools.PropertyGetter(typeof(TerrainDef), "IsSubstructure"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CasemateFoundation)));
        patch.Patch(AccessTools.PropertyGetter(typeof(BuildableDef), "Altitude"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CasemateAltitude)));
        patch.Patch(AccessTools.Method(typeof(DeltaVPerformanceProbe), "RecordWeaponResult"),
            prefix: new HarmonyMethod(typeof(Program), nameof(RecordCasemateReject)));
        try
        {
            var xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_Casemate.xml");
            XElement Definition(string name) => xml.Root.Elements().Single(e => (string)e.Element("defName") == name);
            CheckVacuumBarrierBlueprint(Definition("DeltaV_VacuumBarrier"));
            var pelletDef = MakeDef("DeltaV_Projectile_CasematePellet");
            var projectile = Definition(pelletDef.defName).Descendants("li").Single();
            var pelletProps = new CompProperties_SpaceProjectile {
                hasMap = (bool)projectile.Element("hasMap"), collisionRadius = (float)projectile.Element("collisionRadius"),
                speed = (float)projectile.Element("speed"),
                timeoutTicks = (int)projectile.Element("timeoutTicks"), visualStyle = CombatVisualStyle.Kinetic };
            pelletDef.comps.Add(pelletProps);
            var impact = Definition("DeltaV_Impact_CasematePellet").Element("defenderEffect");
            DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef {
                defName = "CasemateRegressionImpact", projectileDefs = new List<ThingDef> { pelletDef },
                defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damageDef = bullet,
                    damage = (float)impact.Element("damage"), referenceSpeed = 60, speedExponent = 1, incidenceMinFactor = .35f },
                attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf } });
            SpaceImpactEffects.ResetCache();

            var gunDef = Definition("DeltaV_Weapon_Casemate");
            XElement Config(string name) => gunDef.Element("comps").Elements("li")
                .Single(e => (string)e.Attribute("Class") == "DeltaV.CompProperties_" + name);
            var firing = Config("SpaceWeapon");
            var wp = new CompProperties_SpaceWeapon { projectileDef = pelletDef, topTurret = false, casemate = true,
                defaultPdTargets = SpaceObjectTag.Shuttle, fireArcDeg = (float)firing.Element("fireArcDeg"),
                range = (float)firing.Element("range"), turretMuzzleDistance = (float)firing.Element("turretMuzzleDistance"),
                projectilesPerShot = (int)firing.Element("projectilesPerShot"),
                projectileFanHalfAngle = (float)firing.Element("projectileFanHalfAngle") };
            var weapon = new CompSpaceWeapon();
            weapon.Initialize(wp);
            var tune = new CompWeaponTuning();
            tune.Initialize(new CompProperties_WeaponTuning { chemical = true, heatPerShot = 0,
                minInterval = (int)Config("WeaponTuning").Element("minInterval"),
                maxInterval = (int)Config("WeaponTuning").Element("maxInterval"),
                defaultInterval = (int)Config("WeaponTuning").Element("defaultInterval") });
            var traverse = new CompSpaceWeaponTraverse { props = new CompProperties_SpaceWeaponTraverse { mechanicalArcDeg = 90 } };
            var man = new CompMannable { props = new CompProperties_Mannable() };
            var mag = new CompSpaceWeaponMagazine { props = new CompProperties_SpaceWeaponMagazine { ammoPerShot = 1 } };
            var heads = new CompGunSupply { props = new CompProperties_GunSupply { kind = GunSupplyKind.Heads, fuelCapacity = 300 } };
            var charge = new CompGunSupply { props = new CompProperties_GunSupply { kind = GunSupplyKind.Propellant, fuelCapacity = 300 } };
            Set(heads, "fuel", 10f); Set(charge, "fuel", 10f);
            var gun = Add(5, 5, 290, weapon, tune, traverse, man, mag, heads, charge);
            var pivot = ParseHelper.FromString<Vector2>((string)gunDef.Element("building").Element("turretTopOffset"));
            map.thingGrid.Deregister(gun);
            gun.def = MakeDef(gunDef.Element("defName").Value);
            gun.def.size = ParseHelper.FromString<IntVec2>((string)gunDef.Element("size"));
            gun.def.hasInteractionCell = true;
            gun.def.interactionCellOffset = ParseHelper.FromString<IntVec3>((string)gunDef.Element("interactionCellOffset"));
            gun.def.building.turretTopOffset = pivot;
            foreach (Rot4 facing in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                Set(gun, "rotationInt", facing);
                IntVec3 front = facing.FacingCell;
                Vector2 axis = new Vector2(front.x, front.z);
                var expected = SpaceWeaponGeometry.FootprintCenter(gun) + axis * (pivot.y + wp.turretMuzzleDistance);
                var muzzle = SpaceWeaponGeometry.MuzzleMapPointAtAngle(weapon, Mathf.Atan2(axis.y, axis.x));
                var rect = gun.OccupiedRect();
                Vector2 diagonal = SpaceWeaponGeometry.MuzzleMapPointAtAngle(weapon, Mathf.Atan2(axis.y, axis.x) + Mathf.PI / 4);
                Check((muzzle - expected).sqrMagnitude < .000001f && rect.Area == 6
                    && !rect.Contains(gun.InteractionCell)
                    && !rect.Contains(new IntVec3(Mathf.FloorToInt(diagonal.x), 0, Mathf.FloorToInt(diagonal.y))),
                    $"six-cell casemate, rear operator and exit clear its footprint in rotation {facing}");
            }
            Set(gun, "rotationInt", Rot4.East);
            map.thingGrid.Register(gun);
            var console = new CompDeltaVPilotConsole { isMain = true, shipId = 771 };
            Add(4, 5, 300, console);
            console.combat.weaponsArmed = true;
            var ship = new FoundationShip(new Ship(console.shipId, map, console));
            hull.members.Add(ship);
            var own = new SubstructureOwnership(map);
            map.components.Add(own);
            var ownerGrid = new int[map.cellIndices.NumGridCells];
            Set(own, "ownerGrid", ownerGrid);
            var foundation = (TerrainDef)Raw(typeof(TerrainDef));
            foundation.defName = "CasemateFixtureDeck";
            var wall = Add(7, 5, 290);
            wall.def.building.isWall = true;
            foreach (var cell in gun.OccupiedRect().Concat(new[] { wall.Position, console.parent.Position }))
            {
                int index = map.cellIndices.CellToIndex(cell);
                map.terrainGrid.foundationGrid[index] = foundation;
                ownerGrid[index] = console.shipId;
            }
            void BindOwner()
            {
                Set(weapon, "cachedOwner", ship); Set(weapon, "ownerMap", map);
                Set(weapon, "ownerPosition", gun.Position); Set(weapon, "ownerRotation", gun.Rotation);
                Set(weapon, "ownerVersion", CompSpaceWeapon.OwnerBinding(weapon.parent));
                Set(weapon, "ownerCacheValid", true); Set(weapon, "ownerWasValid", true);
            }
            BindOwner();
            var aim = new WeaponControlCommand(new Vector2(20.5f, 5.5f), true);
            Check(!universe.Weapons.ApplyControl(weapon, hull, aim) && mag.HeadCount == 10,
                "casemate cannot fire with the gunner absent");
            var gunner = (Pawn)Raw(typeof(Pawn));
            Set(gunner, "mapIndexOrState", (sbyte)0);
            SkillDefOf.Shooting = new SkillDef { defName = "Shooting" };
            gunner.skills = new Pawn_SkillTracker(gunner);
            gunner.skills.skills.Add(new SkillRecord(gunner, SkillDefOf.Shooting));
            Set(man, "lastManPawn", gunner); Set(man, "lastManTick", Find.TickManager.TicksGame);
            Check(weapon.HasFireControl, "casemate uses the real manned-turret contract");
            Check(!universe.Weapons.ApplyControl(weapon, hull, new WeaponControlCommand(new Vector2(5.5f, 20.5f), true))
                && mag.HeadCount == 10, "casemate rejects a target outside its 90 degree arc");
            for (int i = 0; i < 16; i++)
            {
                Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
                Set(man, "lastManTick", Find.TickManager.TicksGame);
                traverse.AimAt(0);
            }
            casemateVisuals = 0;
            Check(!universe.Weapons.ApplyControl(weapon, hull, aim) && casemateReject == WeaponReject.Safety && mag.HeadCount == 10
                && mag.PropellantCount == 10 && universe.Projectiles.InFlightCount == 0,
                "hull safety rejects the entire fan with no expenditure");
            weapon.safeFire = false;
            bool fired = universe.Weapons.ApplyControl(weapon, hull, aim);
            Check(fired && mag.HeadCount == 9 && mag.PropellantCount == 9 && tune.RemainingTicks == tune.Settings.defaultInterval
                && universe.Projectiles.InFlightCount == 5 && casemateVisuals == 1,
                $"casemate fan commits once ({fired}, {casemateReject}, heads={mag.HeadCount}, charge={mag.PropellantCount}, "
                    + $"cooldown={tune.RemainingTicks}, pellets={universe.Projectiles.InFlightCount}, visuals={casemateVisuals})");
            var records = (IList)AccessTools.Field(typeof(SpaceProjectileService), "projectiles").GetValue(universe.Projectiles);
            var angles = new List<float>();
            Vector2 muzzleAtLaunch = SpaceWeaponGeometry.FootprintCenter(gun) + new Vector2(pivot.y + wp.turretMuzzleDistance, 0);
            Check(muzzleAtLaunch.x >= wall.Position.x + 1f, "the extended barrel reaches through the adjacent wall");
            foreach (object record in records)
            {
                var obj = (SpaceObject)AccessTools.Property(record.GetType(), "container").GetValue(record);
                Check(Math.Abs(obj.pose.velocity.magnitude - 60) < .001f
                    && Math.Abs((float)AccessTools.Field(record.GetType(), "mass").GetValue(record) - .2f) < .0001f,
                    "pellet preserves launch speed and carries one fifth of the shot mass");
                Check((SpaceTransform.UniverseToMapPoint(hull, map, obj.pose.position) - muzzleAtLaunch).sqrMagnitude < .0001f,
                    "pellet starts from the forward-shifted muzzle");
                angles.Add(obj.pose.angle * Mathf.Rad2Deg);
                obj.pose.position = OrbitSphere.Move(obj.pose.position, obj.pose.velocity * .10f);
            }
            Check(angles.Zip(new[] { -3f, -1.5f, 0f, 1.5f, 3f }, (a, b) => Math.Abs(a - b) < .08f).All(x => x),
                "live projectile directions match the specified five-ray fan");
            universe.Projectiles.ResolveCollisions();
            Check(wall.HitPoints < 290 && gun.HitPoints == 290, "unsafe casemate pellets hit the ship's wall, excluding the gun");
            CheckCasemateWallLinks(gun.def.size);
            CheckCasemateFilters(weapon);
            CheckFoundationForecast(wall, pelletDef);
            CheckCasemateSustainedDps(tune, pelletDef);
            CheckDefenseComparison(weapon.WeaponProps, pelletDef, tune.Settings.defaultInterval);
        }
        finally { patch.UnpatchAll(patch.Id); }
    }
    static void CheckVacuumBarrierBlueprint(XElement xml)
    {
        var def = MakeDef((string)xml.Element("defName"));
        def.thingClass = typeof(Building_VacuumBarrier);
        def.drawerType = Enum.Parse<DrawerType>((string)xml.Element("drawerType"));
        var graphic = xml.Element("graphicData");
        def.graphicData = new GraphicData
        {
            graphicClass = typeof(Graphic_Single),
            texPath = (string)graphic.Element("texPath"),
            linkType = Enum.Parse<LinkDrawerType>((string)graphic.Element("linkType") ?? "None"),
            linkFlags = Enum.Parse<LinkFlags>((string)graphic.Element("linkFlags"))
        };
        // RimWorld copies the finished building's drawer type and link type to its blueprint.
        var blueprint = MakeDef("Blueprint_" + def.defName);
        blueprint.drawerType = def.drawerType;
        blueprint.graphicData = new GraphicData
        {
            graphicClass = typeof(Graphic_Single),
            texPath = (string)xml.Element("building").Element("blueprintGraphicData").Element("texPath"),
            linkType = def.graphicData.linkType
        };
        var errors = (IEnumerable<string>)AccessTools.Method(typeof(GraphicData), "ConfigErrors")
            .Invoke(blueprint.graphicData, new object[] { blueprint });
        Check(!errors.Any() && !blueprint.graphicData.Linked && def.CanAffectLinker
            && def.graphicData.linkFlags == LinkFlags.Wall,
            "vacuum barrier produces a standalone realtime blueprint and still contributes to the wall LinkGrid");
    }

    static void CheckCasemateWallLinks(IntVec2 casemateSize)
    {
        foreach (Type type in new[] { typeof(Building_Casemate), typeof(Building_VacuumBarrier) })
        {
            var insert = (Building)Raw(type);
            var center = new IntVec3(5, 0, 5);
            insert.def = MakeDef(type.Name);
            if (insert is Building_Casemate) insert.def.size = casemateSize;
            Set(insert, "positionInt", center);
            foreach (var rotation in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                Set(insert, "rotationInt", rotation);
                CellRect rect = insert.OccupiedRect();
                var sides = rect.SelectMany(cell => new[] { cell + IntVec3.North, cell + IntVec3.East,
                    cell + IntVec3.South, cell + IntVec3.West }).Where(cell => !rect.Contains(cell)).Distinct();
                bool correct = sides.All(cell =>
                {
                    bool westOrEast = cell.x < rect.minX || cell.x > rect.maxX;
                    bool expected = insert is Building_VacuumBarrier || westOrEast != rotation.IsHorizontal;
                    return HullWallInsert.LinksFrom(insert, cell) == expected;
                });
                Check(correct && !HullWallInsert.LinksFrom(insert, new IntVec3(rect.maxX + 1, 0, rect.maxZ + 1)),
                    $"{type.Name} uses the expected footprint-edge wall connections in rotation {rotation}");
            }
        }
    }

    // Shuttle is a class role; it is interceptable only while an integrated hull flies independently.
    sealed class FlyingShuttleHull : MapSpaceObject
    {
        public FlyingShuttleHull(Map map) : base(nextId++, map, new SpacePose(Vector3.forward, 0)) { }
        protected override bool HasIndependentShuttle => true;
    }

    static void CheckCasemateFilters(CompSpaceWeapon weapon)
    {
        var candidate = new FlyingShuttleHull(map);
        candidate.baseTag = SpaceObjectTag.Projectile;
        Check(!weapon.AcceptsPointDefenseTarget(candidate), "casemate defaults to shuttle-only interception");
        candidate.baseTag |= SpaceObjectTag.Shuttle;
        Check(weapon.AcceptsPointDefenseTarget(candidate), "intrusion shuttle matches the shuttle selection");
        weapon.pdTargets = SpaceObjectTag.Projectile;
        Check(!weapon.AcceptsPointDefenseTarget(candidate), "intrusion shuttle does not match the projectile-only selection");
        weapon.pdTargets |= SpaceObjectTag.Shuttle;
        Check(weapon.AcceptsPointDefenseTarget(candidate), "the two tags can be enabled together");
        candidate.baseTag = SpaceObjectTag.Projectile;
        Check(weapon.AcceptsPointDefenseTarget(candidate), "the combined filter also admits mapped projectiles");
        weapon.pdTargets = 0;
        Check(!weapon.AcceptsPointDefenseTarget(candidate), "an empty filter admits no interception targets");
    }

    static void CheckFoundationForecast(Target wall, ThingDef pellet)
    {
        var floor = new SpaceImpactEffectDef { defName = "CasemateFixtureFloor",
            projectileDefs = new List<ThingDef> { pellet }, targetsSubstructure = true, substructureHitChance = 1,
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damageDef = bullet,
                damage = 4f, substructureBreakThreshold = 1f } };
        DefDatabase<SpaceImpactEffectDef>.Add(floor);
        SpaceImpactEffects.ResetCache();
        int before = wall.HitPoints;
        CombatDamageEstimate Trace(CombatDamageForecast forecast) =>
            forecast.Fire(new Vector2(7.25f, 5.5f), Vector2.right, 3f, pellet, .2f, 60f);
        var solid = new CombatDamageForecast(hull);
        Check(Trace(solid).contacts == 1 && solid.RemainingHP(wall) > 0 && solid.RemainingHP(wall) < before,
            "full-cover building intercepts first, retaining the covered foundation");
        // A standing covering building takes a foundation-cell hit first. Only a passable building that
        // cannot hide the foundation (a conduit) lets the forecast reach the foundation beneath it.
        wall.def.building.isWall = false;
        wall.def.fillPercent = 0;
        wall.def.passability = Traversability.Standable;
        wall.def.building.isPowerConduit = true;
        var exposed = new CombatDamageForecast(hull);
        Check(Trace(exposed).contacts == 1 && exposed.RemainingHP(wall) == 0 && wall.HitPoints == before,
            "forecast of a covered foundation hit removes the supported building only in the private ledger");
        floor.defenderEffect.substructureBreakThreshold = float.MaxValue;
        var hold = new CombatDamageForecast(hull);
        Check(Trace(hold).contacts == 1 && hold.RemainingHP(wall) == before,
            "a foundation hit can stop a projectile while retaining the foundation and mounted building");
        floor.substructureHitChance = 0;
        Check(Trace(new CombatDamageForecast(hull)).contacts == 0, "both layers may let the shot pass");
    }

    static void CheckCasemateSustainedDps(CompWeaponTuning casemate, ThingDef pelletDef)
    {
        var xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml");
        float PeakSustained(string defName)
        {
            var tuning = xml.Root.Elements("ThingDef").Single(e => (string)e.Element("defName") == defName)
                .Descendants("li").Single(e => (string)e.Attribute("Class") == "DeltaV.CompProperties_WeaponTuning");
            var props = new CompProperties_WeaponTuning {
                chemical = true, defaultCharge = (int)tuning.Element("defaultCharge"),
                defaultMass = (int)tuning.Element("defaultMass"), heatPerShot = (float)tuning.Element("heatPerShot"),
                heatCapacity = (float)tuning.Element("heatCapacity"), coolingPerSecond = (float)tuning.Element("coolingPerSecond") };
            float speed = SpaceKinetics.LaunchSpeed(props.defaultCharge, props.defaultMass);
            float perShot = SpaceImpactEffects.ComputeDamage(new ImpactOutcomePart { damage = 24, kineticMassScaling = true },
                speed, 0f, 1f, props.defaultMass);
            float best = 0f;
            for (int interval = (int)tuning.Element("minInterval"); interval <= (int)tuning.Element("maxInterval"); interval++)
            {
                var gun = new CompWeaponTuning();
                gun.Initialize(props);
                gun.interval = interval;
                int shots = 0;
                for (int tick = 0; tick < 36600; tick++)
                {
                    if (gun.RemainingTicks == 0)
                    {
                        gun.CommitShot();
                        if (tick >= 600) shots++;
                    }
                    gun.AdvanceThermalTick();
                }
                best = Math.Max(best, shots * perShot / 600f);
            }
            return best;
        }

        float small = PeakSustained("DeltaV_Weapon_RapidMainSmall");
        float medium = PeakSustained("DeltaV_Weapon_RapidMainMedium");
        float perPellet = SpaceImpactEffects.Resolve(pelletDef, MakeDef("CasemateBenchmarkHull")).defenderEffect.damage;
        float threePellets = 3 * perPellet * 60 / casemate.Settings.defaultInterval;
        Check(threePellets > 1.25f * Math.Max(small, medium),
            $"three-pellet close-in sustained DPS {threePellets:F1} exceeds the best tower heat cycles ({small:F1}/{medium:F1})");
    }

    static void CheckDefenseComparison(CompProperties_SpaceWeapon fan, ThingDef pelletDef, int casemateCadence)
    {
        var laser = MakeDef("DeltaV_Projectile_PDLaser");
        var defs = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml");
        var laserConfig = defs.Root.Elements().Single(e => (string)e.Element("defName") == laser.defName)
            .Descendants("li").Single(e => (string)e.Attribute("Class") == "DeltaV.CompProperties_SpaceProjectile");
        var laserProps = new CompProperties_SpaceProjectile { hasMap = false,
            laserRange = (float)laserConfig.Element("laserRange"),
            guaranteedHitMissiles = (bool)laserConfig.Element("guaranteedHitMissiles") };
        laser.comps.Add(laserProps);
        var weapons = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml");
        int laserCadence = (int)weapons.Root.Elements("ThingDef").Single(e => (string)e.Element("defName") == "DeltaV_Weapon_PDTurret")
            .Descendants("li").Single(e => (string)e.Attribute("Class") == "DeltaV.CompProperties_SpaceWeapon").Element("cooldownTicks");
        var effects = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml");
        float laserDamage = (float)effects.Root.Elements().Single(e => (string)e.Element("defName") == "DeltaV_Impact_PdLaserVsStructure")
            .Element("defenderEffect").Element("damage");
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef {
            defName = "CasemateRegressionLaser", projectileDefs = new List<ThingDef> { laser },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damageDef = bullet,
                damage = laserDamage, speedExponent = 0, incidenceMinFactor = 1, armorPenetration = 4 },
            attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf } });
        SpaceImpactEffects.ResetCache();
        (int killTick, int groups, int peak) Trial(bool missile, bool useLaser)
        {
            Reset();
            var target = Add(40, 6, missile ? 25 : 300);
            map.thingGrid.Deregister(target);
            if (missile)
            {
                target.def.fillPercent = .12f;
                target.def.comps.Add(new CompProperties_SpaceProjectile { hasMap = true, guided = true });
            }
            else target.def.size = new IntVec2(3, 5);
            map.thingGrid.Register(target);
            Vector3 muzzle = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(19.5f, 6.5f));
            OrbitSphere.Basis(muzzle, out Vector3 east, out Vector3 north);
            Vector2 tangent = SpaceTransform.MapDirectionToUniverseTangent(hull, map, new Vector2(19.5f, 6.5f), Vector2.right);
            hull.pose.velocity = -2f * SpaceTransform.TransportTangent(tangent, muzzle, hull.pose.position);
            var service = new SpaceProjectileService(universe);
            var records = (IList)AccessTools.Field(typeof(SpaceProjectileService), "projectiles").GetValue(service);
            var runFrame = AccessTools.Method(typeof(UniverseManager), "RunFrameActions");
            int groups = 0, peak = 0, killed = -1;
            Rand.PushState(11722);
            try
            {
                for (int tick = 0; tick <= 360 && !target.Destroyed; tick++)
                {
                    Set(Find.TickManager, "ticksGameInt", 2000 + tick);
                    if (tick % (useLaser ? laserCadence : casemateCadence) == 0)
                    {
                        groups++;
                        int count = useLaser ? 1 : fan.projectilesPerShot;
                        for (int i = 0; i < count; i++)
                        {
                            Vector2 dir = SpaceTransform.Rotate(tangent, useLaser ? 0 : SpaceWeaponShot.FanOffset(fan, i));
                            service.Launch(useLaser ? laser : pelletDef, null, muzzle, east * dir.x + north * dir.y,
                                null, null, useLaser ? CombatTarget.ForContainer(hull) : default,
                                new SpaceProjectileLaunchSpec(60f, 0, 60f, mass: 1f / count));
                        }
                    }
                    service.Tick();
                    hull.pose.position = OrbitSphere.Move(hull.pose.position, hull.pose.velocity / 60f);
                    foreach (object record in records)
                    {
                        var obj = (SpaceObject)AccessTools.Property(record.GetType(), "container").GetValue(record);
                        obj.pose.position = OrbitSphere.Move(obj.pose.position, obj.pose.velocity / 60f);
                    }
                    peak = Math.Max(peak, service.InFlightCount);
                    service.ResolveCollisions();
                    runFrame.Invoke(universe, null);
                    if (target.Destroyed) killed = tick;
                }
            }
            finally { Rand.PopState(); }
            Console.WriteLine($"PD TRIAL target={(missile ? "missile" : "shuttle")} weapon={(useLaser ? "laser" : "casemate")}"
                + $" killTick={killed} shotGroups={groups} ammoPairs={(useLaser ? 0 : groups)} peakInFlight={peak}");
            return (killed, groups, peak);
        }
        var shuttle = Trial(false, false);
        var laserShuttle = Trial(false, true);
        var missile = Trial(true, false);
        var laserMissile = Trial(true, true);
        Check(shuttle.killTick >= 0 && (laserShuttle.killTick < 0 || shuttle.killTick < laserShuttle.killTick),
            "fixed-lane casemate fire downs the high-HP shuttle faster than laser point defense");
        Check(missile.killTick >= 0 && laserMissile.killTick >= 0 && missile.groups > laserMissile.groups,
            "both defenses intercept the missile, with more casemate ammunition groups than laser pulses");
    }

}
