using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal enum PawnSpaceflightEligibility : byte
    {
        Eligible,
        MissingPawn,
        CannotAct,
        NoApparelTracker,
        NoTorsoVacuumApparel
    }

    public sealed class DefModExtension_PawnSpaceflight : DefModExtension
    {
        public float thrust = 800f;
    }

    public sealed class PawnSpaceflightSource : IExposable
    {
        public int thingId;
        public float thrust;
        public bool required;

        public void ExposeData()
        {
            Scribe_Values.Look(ref thingId, "thingId");
            Scribe_Values.Look(ref thrust, "thrust");
            Scribe_Values.Look(ref required, "required");
        }
    }

    /// <summary>一次有图飞行表示的能力快照；新增装备不会进入该列表。</summary>
    public sealed class PawnSpaceflightSnapshot : IExposable
    {
        internal const float BaseFlightThrust = 800f;
        public List<PawnSpaceflightSource> sources = new List<PawnSpaceflightSource>();
        public int grappleCooldownUntilTick;
        [Unsaved] private PawnWeaponMount grappleMount;
        public ShipCombatState combat = new ShipCombatState
        {
            weaponsArmed = true,
            fireControlMode = ShipFireControlMode.Manual
        };

        internal static PawnSpaceflightEligibility Eligibility(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead) return PawnSpaceflightEligibility.MissingPawn;
            if (!CanAct(pawn)) return PawnSpaceflightEligibility.CannotAct;
            if (pawn.apparel == null) return PawnSpaceflightEligibility.NoApparelTracker;
            return HasMobilityApparel(pawn)
                ? PawnSpaceflightEligibility.Eligible
                : PawnSpaceflightEligibility.NoTorsoVacuumApparel;
        }

        internal static bool HasMobilityApparel(Pawn pawn) => pawn?.apparel?.WornApparel
            .Any(IsMobilityApparel) == true;

        private static bool IsMobilityApparel(Apparel apparel) => apparel?.def?.apparel?.bodyPartGroups
            ?.Contains(BodyPartGroupDefOf.Torso) == true
            && VacuumUtility.IsProtectiveApparel(apparel.def);

        internal static bool TryCreate(Pawn pawn, out PawnSpaceflightSnapshot snapshot,
            out PawnSpaceflightEligibility eligibility)
        {
            snapshot = null;
            eligibility = Eligibility(pawn);
            if (eligibility != PawnSpaceflightEligibility.Eligible) return false;
            var result = new PawnSpaceflightSnapshot();
            foreach (Apparel apparel in pawn.apparel.WornApparel)
            {
                if (IsMobilityApparel(apparel))
                    result.sources.Add(new PawnSpaceflightSource
                    {
                        thingId = apparel.thingIDNumber,
                        thrust = BaseFlightThrust,
                        required = true
                    });
                DefModExtension_PawnSpaceflight extension = apparel.def.GetModExtension<DefModExtension_PawnSpaceflight>();
                if (extension == null || extension.thrust <= 0f) continue;
                result.sources.Add(new PawnSpaceflightSource
                {
                    thingId = apparel.thingIDNumber,
                    thrust = extension.thrust,
                    required = false
                });
            }
            if (!result.sources.Any(source => source.required))
            {
                eligibility = PawnSpaceflightEligibility.NoTorsoVacuumApparel;
                return false;
            }
            snapshot = result;
            return true;
        }

        internal static bool CanAct(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.Downed
                || pawn.health?.capacities == null || PawnCapacityDefOf.Moving == null
                || DefDatabase<PawnCapacityDef>.AllDefsListForReading.Count == 0) return false;
            return pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving);
        }

        internal bool CanMove(Pawn pawn) => CanAct(pawn)
            && sources.Any(source => source.required && SourcePresent(pawn, source.thingId));

        internal bool SourcePresent(Pawn pawn, int thingId) => pawn?.apparel?.WornApparel
            .Any(apparel => apparel.thingIDNumber == thingId && !apparel.Destroyed) == true;

        internal IEnumerable<ShipPhysics.Thruster> CreateThrusters(Pawn pawn)
        {
            PawnSpaceflightSource baseSource = sources
                .Where(source => source.required && SourcePresent(pawn, source.thingId))
                .OrderBy(source => source.thingId)
                .FirstOrDefault();
            if (baseSource != null)
                foreach (ShipPhysics.Thruster thruster in CreateThrusters(pawn, baseSource))
                    yield return thruster;
            foreach (PawnSpaceflightSource source in sources.Where(source => !source.required))
                foreach (ShipPhysics.Thruster thruster in CreateThrusters(pawn, source))
                    yield return thruster;
        }

        private static IEnumerable<ShipPhysics.Thruster> CreateThrusters(Pawn pawn,
            PawnSpaceflightSource source)
        {
            Apparel apparel = pawn?.apparel?.WornApparel
                .FirstOrDefault(item => item.thingIDNumber == source.thingId && !item.Destroyed);
            if (apparel == null) yield break;
            float diagonalThrust = source.thrust / Mathf.Sqrt(2f);
            foreach ((Vector2 position, Vector2 direction) in DiagonalThrusters)
            {
                yield return new ShipPhysics.Thruster
                {
                    localPos = position,
                    dir = direction,
                    thrust = diagonalThrust,
                    baseThrust = diagonalThrust,
                    operational = CanAct(pawn),
                    personnel = pawn,
                    personnelSource = apparel
                };
            }
        }

        internal CompSpaceWeapon GrappleFor(Pawn pawn)
        {
            VirtualWeaponDef definition = DefDatabase<VirtualWeaponDef>.GetNamed(
                "DeltaV_PawnGrapple", false);
            if (pawn == null || definition == null) return null;
            grappleMount ??= new PawnWeaponMount(pawn, uint.MaxValue, definition,
                grappleCooldownUntilTick);
            return grappleMount.Weapon;
        }

        internal void CaptureWeaponRuntime()
        {
            if (grappleMount != null) grappleCooldownUntilTick = grappleMount.Weapon.cooldownUntilTick;
        }

        private static readonly (Vector2 position, Vector2 direction)[] DiagonalThrusters =
        {
            (new Vector2(-.3f, -.3f), new Vector2(1f, 1f).normalized),
            (new Vector2(-.3f, .3f), new Vector2(1f, -1f).normalized),
            (new Vector2(.3f, -.3f), new Vector2(-1f, 1f).normalized),
            (new Vector2(.3f, .3f), new Vector2(-1f, -1f).normalized)
        };

        public void ExposeData()
        {
            Scribe_Collections.Look(ref sources, "sources", LookMode.Deep);
            Scribe_Values.Look(ref grappleCooldownUntilTick, "grappleCooldownUntilTick");
            Scribe_Deep.Look(ref combat, "combat");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                sources ??= new List<PawnSpaceflightSource>();
                combat ??= new ShipCombatState
                {
                    weaponsArmed = true,
                    fireControlMode = ShipFireControlMode.Manual
                };
                grappleMount = null;
            }
        }
    }

    /// <summary>仅在独立人员飞行图中存在的临时 ShipBase 视图。</summary>
    public sealed class PawnSpaceflightShip : ShipBase, IHardDockingExecutor
    {
        internal const float BoardingArrivalVelocityTolerance =
            HardDockingService.LightArrivalVelocityTolerance;
        internal const float BoardingApproachSpeed = HardDockingService.LightApproachSpeed;

        internal readonly Pawn pawn;
        internal readonly PawnSpaceflightSnapshot snapshot;
        private Vector2 weaponAimDirection = Vector2.right;

        internal PawnSpaceflightShip(Pawn pawn, PawnSpaceflightSnapshot snapshot)
        {
            this.pawn = pawn;
            this.snapshot = snapshot;
        }

        public override bool IsValid => pawn?.Spawned == true && !pawn.Destroyed && Map?.Disposed == false
            && Current.Game?.GetComponent<UniverseManager>()?.FrameOf(Map) is MapSpaceObject frame
            && frame.PersonnelPawn == pawn && frame.PersonnelSnapshot == snapshot;
        public override Map Map => pawn?.Map;
        public override DriveSubject Identity => new DriveSubject(0, pawn?.thingIDNumber ?? 0,
            DriveSubjectKind.Personnel);
        public override Thing Anchor => pawn;
        public override float FrameAngle => pawn == null ? 0f : ShipOrientation.AngleFromRot4(pawn.Rotation);
        public override Vector2 HullCenter => pawn == null ? Vector2.zero
            : new Vector2(pawn.Position.x + 0.5f, pawn.Position.z + 0.5f);
        public override IEnumerable<IntVec3> HullCells => pawn?.Spawned == true
            ? new[] { pawn.Position } : System.Array.Empty<IntVec3>();
        public override Faction Faction => pawn?.Faction;
        public override string Label => pawn?.LabelCap ?? string.Empty;
        public override ShipDerivedState State => ShipDerivedState.FreeFlight;
        public override ShipCombatState Combat => snapshot?.combat;
        public override bool CanDriveIn(SpaceObject container) => IsValid && container?.map == Map
            && container.HasTag(SpaceObjectTag.Personnel);
        public override bool Contains(Thing thing) => IsValid && (thing == pawn
            || thing?.ParentHolder == pawn.inventory || thing?.ParentHolder == pawn.carryTracker);
        public override IEnumerable<ShipPhysics.Thruster> BuildThrusters()
        {
            foreach (ShipPhysics.Thruster thruster in StructuralState?.VirtualThrusters
                ?? System.Array.Empty<ShipPhysics.Thruster>())
                yield return thruster.NominalSnapshot();
        }
        internal override IEnumerable<ShipPhysics.Thruster> CreateVirtualThrusters() =>
            snapshot?.CreateThrusters(pawn) ?? System.Array.Empty<ShipPhysics.Thruster>();
        protected override IEnumerable<CompSpaceWeapon> CollectWeapons()
        {
            CompSpaceWeapon grapple = snapshot?.GrappleFor(pawn);
            return grapple == null ? System.Array.Empty<CompSpaceWeapon>() : new[] { grapple };
        }
        public override IEnumerable<SpaceCollisionCell> CollisionCells
        {
            get
            {
                if (pawn?.Spawned == true)
                    yield return new SpaceCollisionCell(pawn.Position, SpaceCellKind.Building, pawn);
            }
        }
        public override SpaceObjectTag RolesIn(SpaceObject container) => SpaceObjectTag.Ship | SpaceObjectTag.Personnel;
        public override ShipTransferSpec Transfer => new ShipTransferSpec(null, null, 0, false, pawn);
        public override bool MoveColocatedItems => false;
        public override bool IsPersonnel => true;
        internal override IHardDockingExecutor HardDockingExecutor => this;
        public override bool RequiresHeadingAlignment => false;
        public override bool RequiresAngularVelocityMatch => false;
        public override bool MatchDockingPointVelocity => true;
        public override bool AllowsAngularMotion => false;
        public override bool UsesCameraInput => true;
        public override bool CanDisableCollisionAvoidance => false;
        public override int FlightMapMargin => 0;
        public override bool HasGunnerStations => false;
        public override ShipFireControlMode DefaultFireControlMode => ShipFireControlMode.Manual;
        public override bool SupportsFireControlMode(ShipFireControlMode mode) =>
            mode != ShipFireControlMode.Automatic;
        public override ShipAimHandler CreateAimHandler(UniverseManager universe) =>
            new ArcadeShipAimHandler(universe, this);
        internal Vector2 WeaponAimDirection => weaponAimDirection;
        internal override void NotifyManualAim(Vector2 mapPoint) => AimPersonalWeapon(mapPoint);
        internal void AimPersonalWeapon(Vector2 mapPoint)
        {
            Vector2 direction = mapPoint - HullCenter;
            if (direction.sqrMagnitude < .0001f)
                direction = SpaceTransform.Rotate(Vector2.right, FrameAngle);
            if (direction.sqrMagnitude < .0001f) direction = Vector2.right;
            weaponAimDirection = direction.normalized;
        }
        internal override void HandleManualVolley(UniverseManager universe, SpaceObject container,
            ShipVolleyIntent intent)
        {
            if (intent.main && intent.localPoint.HasValue)
                PawnSpaceWeaponBridge.TryFire(universe, this, container, intent.localPoint.Value);
        }
        public override void NotifyManualControlEntered()
        {
            if (pawn?.Spawned == true && pawn.drafter != null && !pawn.Drafted)
                pawn.drafter.Drafted = true;
        }

        ShipBase IHardDockingExecutor.Vessel => this;
        Thing IHardDockingExecutor.Body => pawn;
        ThingDef IHardDockingExecutor.CollisionDef =>
            DefDatabase<ThingDef>.GetNamed("DeltaV_PawnSpaceflightProxy", false);
        HardDockingCompletion IHardDockingExecutor.Completion => HardDockingCompletion.Arrival;
        bool IHardDockingExecutor.RequiresAvoidanceDisabled => false;
        bool IHardDockingExecutor.UsesProjectileRole => false;
        float IHardDockingExecutor.ArrivalVelocityTolerance => BoardingArrivalVelocityTolerance;
        float IHardDockingExecutor.ArrivalApproachSpeed => BoardingApproachSpeed;
        CompSpaceWeapon IHardDockingExecutor.CaptureTether => snapshot?.GrappleFor(pawn);
        float IHardDockingExecutor.NoseHalfWidth => 0f;
        float IHardDockingExecutor.NoseForwardOffset => 0f;
        string IHardDockingExecutor.StartLabelKey => "DeltaV_PersonnelLanding";
        string IHardDockingExecutor.StartTipKey => "DeltaV_PersonnelLandingDesc";
        string IHardDockingExecutor.CancelLabelKey => "DeltaV_PersonnelLandingCancel";
        string IHardDockingExecutor.CancelTipKey => "DeltaV_PersonnelLandingCancelDesc";
        string IHardDockingExecutor.BlockReasonKey(UniverseManager universe, SpaceObject source,
            bool playerInitiated) => snapshot?.CanMove(pawn) == true
                ? null : "DeltaV_PersonnelLandingFailed";
        bool IHardDockingExecutor.ValidFor(SpaceObject source) => IsValid && CanDriveIn(source)
            && snapshot?.CanMove(pawn) == true;
        bool IHardDockingExecutor.TryResolveTarget(SpaceObject target, Vector2 targetMapPoint,
            out HardDockingDestination destination)
        {
            IntVec3 targetCell = new IntVec3(Mathf.FloorToInt(targetMapPoint.x), 0,
                Mathf.FloorToInt(targetMapPoint.y));
            destination = new HardDockingDestination(targetCell);
            return PersonnelAccess.TryResolveLanding(target?.map, targetCell, out _);
        }
        bool IHardDockingExecutor.TargetStillValid(SpaceObject target,
            HardDockingDestination destination)
            => PersonnelAccess.TryResolveLanding(target?.map, destination.cell, out _);
        void IHardDockingExecutor.NotifyHardDockingActive(bool active) { }
        void IHardDockingExecutor.CompleteHardDocking(UniverseManager universe,
            HardDockingContact contact) => universe.Personnel.CompleteHardDocking(contact);
    }
}
