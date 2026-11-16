param([string]$GameRoot, [string]$HarmonyPath, [switch]$AimInputOnly, [switch]$PlayerFlightOnly,
      [switch]$SalvageOnly, [switch]$DrivePerformanceOnly, [switch]$ProjectileOnly, [switch]$ArchiveRecoveryOnly,
      [switch]$DockingOnly, [switch]$NpcOnly, [switch]$ProjectileFullOnly, [switch]$SeparationOnly,
      [switch]$ThrusterPauseOnly, [switch]$TractorOnly, [switch]$EvaOnly, [switch]$GlobeOnly, [switch]$CombatManeuverOnly, [switch]$EngagementBaseline, [switch]$EngagementRegression, [switch]$EngagementBattle, [switch]$AutoVolleyOnly,
      [ValidateSet('Count', 'Time', 'Parity')][string]$StaticEngagement,
      [ValidateSet('Normal', 'Dense')][string]$StaticScene = 'Normal',
      [ValidateSet('Wide', 'Point')][string]$CollisionFleet,
      [switch]$CollisionDense, [switch]$CollisionParity,
      [ValidateSet('Preflight','All','Crossover','Experiment','HeadingTime','AimRefresh','AimRefreshTime')][string]$Mothership,
      [ValidateRange(1,3600)][int]$DamageWindowTicks = 600)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (!$GameRoot) { $GameRoot = "$repo/../.." }
$managed = (Resolve-Path "$GameRoot/RimWorldWin64_Data/Managed").Path
if (!$HarmonyPath) {
    $HarmonyPath = Join-Path $GameRoot '../../workshop/content/294100/2009463077/1.6/Assemblies/0Harmony.dll'
}
if (!(Test-Path -LiteralPath $HarmonyPath)) { throw 'Pass -HarmonyPath with a .NET 8 Harmony runtime DLL (Lib.Harmony/lib/net8.0/0Harmony.dll).' }
$sdkVersion = Get-ChildItem "$env:ProgramFiles/dotnet/sdk" -Directory | Where-Object { $_.Name -match '^8\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$refs = Get-ChildItem "$env:ProgramFiles/dotnet/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object { $_.Name -match '^8\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdkVersion -or !$refs) { throw 'Install .NET 8 SDK to run the offline retreat regression.' }
$out = Join-Path $repo 'Source/obj/NpcRetreatRegression'
if ($Mothership) { $out = Join-Path $repo 'Source/obj/MothershipEngagement' }
if ($Mothership -eq 'All') { $out = Join-Path $out 'DualTable' }
if ($Mothership -eq 'Crossover') { $out = Join-Path $out 'Crossover' }
if ($Mothership -eq 'Experiment') { $out = Join-Path $out 'SharedAim' }
if ($Mothership -eq 'HeadingTime') { $out = Join-Path $out 'SharedAimTiming' }
if ($Mothership -eq 'AimRefresh') { $out = Join-Path $out 'AimRefresh' }
if ($Mothership -eq 'AimRefreshTime') { $out = Join-Path $out 'AimRefreshTiming' }
if ($Mothership -eq 'Preflight') { $out = Join-Path $out 'reference' }
New-Item -ItemType Directory -Force $out | Out-Null
if ($Mothership -in @('All','Crossover')) {
    function Patch-OfflineCopy([string]$Text, [string]$Before, [string]$After) {
        $Before = $Before.Replace("`r`n","`n")
        $After = $After.Replace("`r`n","`n")
        if ([regex]::Matches($Text, [regex]::Escape($Before)).Count -ne 1) {
            throw "Mothership offline hook no longer matches the production source: $Before"
        }
        $Text.Replace($Before,$After)
    }
    $evaluation = (git -C $repo show '74e9c18:Source/Interaction/CombatEngagementEvaluation.cs') -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Historical Mothership evaluator is unavailable.' }
    $evaluation = Patch-OfflineCopy $evaluation '        internal sealed class Result' @'
        internal readonly struct OfflineShot
        {
            internal readonly bool Outgoing;
            internal readonly CombatDamageForecast Forecast;
            internal readonly Thing Part;
            internal readonly Vector2 Endpoint, From, Direction;
            internal readonly float Length, Speed, Flight;
            internal readonly SpaceWeaponParameters Shot;
            internal readonly CompSpaceWeapon Weapon;
            internal OfflineShot(bool outgoing, CombatDamageForecast forecast, Thing part, Vector2 endpoint,
                Vector2 from, Vector2 direction, float length, float speed, float flight, SpaceWeaponParameters shot, CompSpaceWeapon weapon)
            {
                Outgoing=outgoing; Forecast=forecast; Part=part; Endpoint=endpoint; From=from;
                Direction=direction; Length=length; Speed=speed; Flight=flight; Shot=shot; Weapon=weapon;
            }
        }
        internal delegate CombatDamageEstimate OfflineEstimate(OfflineShot input, out float critical);
        internal static OfflineEstimate OfflineApproximate;
        internal static Func<CombatDamageForecast, Thing, bool> OfflineTarget;
        internal static bool OfflineSkipObservations;
        internal static int OfflineAimCalls, OfflineLegacyAimCalls, OfflineFireCalls;
        internal readonly struct OfflineBattery
        {
            internal readonly CombatAttackWeapon Gun;
            internal readonly SpaceWeaponParameters Shot;
            internal readonly SpacePose Pose;
            internal readonly bool Outgoing;
            internal readonly CombatPart Priority;
            internal readonly CombatWeaponFamily Family;
            internal OfflineBattery(CombatAttackWeapon gun, SpaceWeaponParameters shot, SpacePose pose,
                bool outgoing, CombatPart priority, CombatWeaponFamily family)
            { Gun=gun; Shot=shot; Pose=pose; Outgoing=outgoing; Priority=priority; Family=family; }
        }
        internal SpacePose OfflineTargetPose => targetPose;
        internal IEnumerable<OfflineBattery> OfflineBatteries
        {
            get
            {
                foreach (var battery in batteries)
                    yield return new OfflineBattery(battery.gun, battery.shot, battery.pose,
                        battery.outgoing, battery.priority, battery.family);
            }
        }

        internal sealed class Result
'@
    $evaluation = Patch-OfflineCopy $evaluation 'forecast, out var damage)) continue;' 'forecast, out var damage, out float critical)) continue;'
    $evaluation = Patch-OfflineCopy $evaluation 'critical = forecast.CriticalDamage * rate' 'critical = critical * rate'
    $evaluation = Patch-OfflineCopy $evaluation 'CombatDamageForecast forecast, out CombatDamageEstimate damage)' 'CombatDamageForecast forecast, out CombatDamageEstimate damage, out float critical)'
    $evaluation = Patch-OfflineCopy $evaluation '            damage = default;' "            OfflineAimCalls++;`n            damage = default;`n            critical = 0;"
    $evaluation = Patch-OfflineCopy $evaluation '            float length = 0, speed = 0;' "            float length = 0, speed = 0, flight = 0;`n            Thing selected = null;`n            Vector2 selectedEndpoint = default;"
    $evaluation = Patch-OfflineCopy $evaluation '                float flight = 0;' '                flight = 0;'
    $evaluation = Patch-OfflineCopy $evaluation '                Vector3 draw = part?.DrawPos ?? default;' @'
                if (OfflineTarget != null && !OfflineTarget(forecast, part)) return false;
                Vector3 draw = part?.DrawPos ?? default;
'@
    $evaluation = Patch-OfflineCopy $evaluation @'
            var ownObservation = new CombatDamageForecast.Observation(universe.FrameOf(own.Map), own);
            var enemyObservation = new CombatDamageForecast.Observation(universe.FrameOf(enemy.Map), enemy);
            ownShot = new CombatDamageForecast(ownObservation);
            enemyShot = new CombatDamageForecast(enemyObservation);
'@ @'
            if (!OfflineSkipObservations)
            {
                ownShot = new CombatDamageForecast(universe.FrameOf(own.Map), own);
                enemyShot = new CombatDamageForecast(universe.FrameOf(enemy.Map), enemy);
            }
'@
    $evaluation = Patch-OfflineCopy $evaluation '                if (!weapon.WeaponProps.casemate) return true;' @'
                selected = part;
                selectedEndpoint = endpoint;
                if (!weapon.WeaponProps.casemate) return true;
'@
    $evaluation = Patch-OfflineCopy $evaluation '            forecast.ResetShot();' @'
            if (OfflineApproximate != null)
            {
                var input = new OfflineShot(battery.outgoing, forecast, selected, selectedEndpoint,
                    from, direction, length, speed, flight, shot, weapon);
                damage = OfflineApproximate(input, out critical);
                damage.uncertain |= !shot.aim.Laser;
                return true;
            }
            forecast.ResetShot();
'@
    $evaluation = Patch-OfflineCopy $evaluation "            damage.uncertain |= !shot.aim.Laser;`n            return true;" "            damage.uncertain |= !shot.aim.Laser;`n            critical = forecast.CriticalDamage;`n            return true;"
    $planner = (git -C $repo show '74e9c18:Source/Interaction/ShipAutoCombat.Engagement.cs') -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Historical Mothership planner is unavailable.' }
    $planner = Patch-OfflineCopy $planner '        private sealed class EngagementCandidate' @'
        internal readonly struct OfflineRow
        {
            internal readonly SpacePose State;
            internal readonly float Legacy;
            internal readonly EngagementCandidate Candidate;
            internal OfflineRow(SpacePose state,float legacy,EngagementCandidate candidate)
            { State=state; Legacy=legacy; Candidate=candidate; }
        }
        internal sealed class OfflinePool
        {
            internal readonly CombatEngagementEvaluation Evaluator;
            internal readonly CombatEngagementSettings Settings;
            internal readonly CombatManeuverCurves Curves;
            internal readonly bool Offense;
            internal readonly List<OfflineRow> Rows;
            internal readonly Func<List<EngagementCandidate>,List<EngagementCandidate>> Select;
            internal readonly long GenerationTicks;
            internal OfflinePool(CombatEngagementEvaluation evaluator, CombatEngagementSettings settings,
                CombatManeuverCurves curves, bool offense, List<OfflineRow> rows,
                Func<List<EngagementCandidate>,List<EngagementCandidate>> select, long generationTicks)
            { Evaluator=evaluator; Settings=settings; Curves=curves; Offense=offense; Rows=rows;
              Select=select; GenerationTicks=generationTicks; }
        }
        internal static Func<OfflinePool,List<EngagementCandidate>> OfflineCapture;
        internal static Func<List<EngagementCandidate>> OfflineComparisonReplay;
        internal sealed class EngagementCandidate
'@
    $planner = Patch-OfflineCopy $planner '            universe.Performance.engagementStarted++;' @'
            if (OfflineComparisonReplay != null) return OfflineComparisonReplay();
            long generationStart = OfflineCapture == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            var rows = OfflineCapture == null ? null : new List<OfflineRow>();
            universe.Performance.engagementStarted++;
'@
    $planner = Patch-OfflineCopy $planner '                    var value = evaluator.Evaluate(state);' @'
                    var value = rows == null ? evaluator.Evaluate(state) : new CombatEngagementEvaluation.Result();
'@
    $planner = Patch-OfflineCopy $planner '                    pose.EvaluateFire(weapons, Vector2.zero, offenseEnabled, rule.maneuverCurves);' @'
                    if (rows == null) pose.EvaluateFire(weapons, Vector2.zero, offenseEnabled, rule.maneuverCurves);
'@
    $planner = Patch-OfflineCopy $planner '                        float heading = gun.LeadHeading(radius, Vector2.zero);' @'
                        float heading = rows == null ? gun.LeadHeading(radius, Vector2.zero) : gun.OfflineStaticHeading(radius);
'@
    $planner = Patch-OfflineCopy $planner @'
                    candidates.Add(new EngagementCandidate { phase = phase, pose = pose,
                        value = value, score = value.Score(rule.engagement) + extra });
'@ @'
                    var candidate = new EngagementCandidate { phase = phase, pose = pose,
                        value = value, score = rows == null ? value.Score(rule.engagement) + extra : 0 };
                    if (rows == null) candidates.Add(candidate);
                    else rows.Add(new OfflineRow(state, extra, candidate));
'@
    $planner = Patch-OfflineCopy $planner '            // Check routes for leading attitude variants and both continuation candidates.' @'
            if (rows != null)
                return OfflineCapture(new OfflinePool(evaluator, rule.engagement, rule.maneuverCurves, offenseEnabled,
                    rows, Select, System.Diagnostics.Stopwatch.GetTimestamp() - generationStart));
            return Select(candidates);

            List<EngagementCandidate> Select(List<EngagementCandidate> candidates)
            {
            // Check routes for leading attitude variants and both continuation candidates.
'@
    $planner = Patch-OfflineCopy $planner "            return result;`n        }" "            return result;`n            }`n        }"
    $pose = [IO.File]::ReadAllText("$repo/Source/Interaction/CombatAttackPose.cs").Replace("`r`n","`n")
    $pose = Patch-OfflineCopy $pose '            Vector2 delta = Vector2.right * distance - SpaceTransform.Rotate(muzzle, heading);' @'
            CombatEngagementEvaluation.OfflineLegacyAimCalls++;
            Vector2 delta = Vector2.right * distance - SpaceTransform.Rotate(muzzle, heading);
'@
    $pose = Patch-OfflineCopy $pose '        internal float LeadHeading(float distance, Vector2 relativeVelocity)' @'
        internal float OfflineStaticHeading(float distance)
        {
            float angle = -mount;
            for (int i = 0; i < 4; i++)
            {
                Vector2 delta = Vector2.right * distance - SpaceTransform.Rotate(muzzle, angle);
                angle = Mathf.Atan2(delta.y, delta.x) - mount;
            }
            return angle;
        }

        internal float LeadHeading(float distance, Vector2 relativeVelocity)
'@
    $forecast = [IO.File]::ReadAllText("$repo/Source/Interaction/CombatDamageForecast.cs").Replace("`r`n","`n")
    $forecast = Patch-OfflineCopy $forecast '            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.EngagementDamage);' @'
            CombatEngagementEvaluation.OfflineFireCalls++;
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.EngagementDamage);
'@
    $evaluation | Set-Content -Encoding utf8 "$out/CombatEngagementEvaluation.Offline.cs"
    $planner | Set-Content -Encoding utf8 "$out/ShipAutoCombat.Engagement.Offline.cs"
    $pose | Set-Content -Encoding utf8 "$out/CombatAttackPose.Offline.cs"
    $forecast | Set-Content -Encoding utf8 "$out/CombatDamageForecast.Offline.cs"
    @"
<Project>
  <ItemGroup>
    <Compile Remove="Interaction/CombatEngagementEvaluation.cs;Interaction/ShipAutoCombat.Engagement.cs;Interaction/CombatAttackPose.cs;Interaction/CombatDamageForecast.cs;Interaction/CombatWeaponOutputTable.cs"/>
    <Compile Include="$out/CombatEngagementEvaluation.Offline.cs;$out/ShipAutoCombat.Engagement.Offline.cs;$out/CombatAttackPose.Offline.cs;$out/CombatDamageForecast.Offline.cs"/>
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding utf8 "$out/Offline.targets"
    & dotnet msbuild "$repo/Source/DeltaV.csproj" -restore -t:Compile -p:Configuration=Release -p:RimWorldVersion=1.6 `
        "-p:TargetPlatformSdkPath=C:/Program Files (x86)/Windows Kits/10/" -p:TargetPlatformDisplayName=Windows `
        "-p:IntermediateOutputPath=$out/compile/" "-p:MSBuildProjectExtensionsPath=$out/compile/" `
        "-p:CustomAfterMicrosoftCommonTargets=$out/Offline.targets" -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$extraCompile = @()
if ($Mothership -eq 'HeadingTime') {
    $table = [IO.File]::ReadAllText("$repo/Source/Interaction/CombatWeaponOutputTable.cs")
    $table = $table.Replace('        internal const int WindowTicks = 600;',
        "        internal static bool OfflineSharedAim = true;`n        internal const int WindowTicks = 600;")
    $mark = 'FixedDirect = battery.outgoing'
    if (!$table.Contains($mark)) { throw 'Heading timing marker is unavailable.' }
    $table.Replace($mark, 'FixedDirect = OfflineSharedAim && battery.outgoing') |
        Set-Content -Encoding utf8 "$out/CombatWeaponOutputTable.Offline.cs"
    @"
<Project>
  <ItemGroup>
    <Compile Remove="Interaction/CombatWeaponOutputTable.cs"/>
    <Compile Include="$out/CombatWeaponOutputTable.Offline.cs"/>
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding utf8 "$out/Offline.targets"
    $extraCompile += "-p:CustomAfterMicrosoftCommonTargets=$out/Offline.targets"
}
if ($Mothership -eq 'AimRefreshTime') {
    $tactics = [IO.File]::ReadAllText("$repo/Source/Interaction/ShipAutoCombat.Tactics.cs")
    $planner = [IO.File]::ReadAllText("$repo/Source/Interaction/CombatFlightPlanner.cs")
    $needles = @('        internal bool Boarding { get; private set; }',
        'var updated = CombatFlightPlanner.RetargetHeading(tacticIntent, self, heading, elapsed);',
        'bool screenHeading = elapsed > 0f && !intent.holdCurrent && OrbitSphere.Valid;')
    foreach ($needle in $needles) { if (!$tactics.Contains($needle)) { throw "Missing aim refresh marker $needle" } }
    $tactics = $tactics.Replace($needles[0], "        internal static bool OfflineReuseAuthority, OfflineScreenRoute;`n$($needles[0])")
    $tactics = $tactics.Replace($needles[1], @'
var input = tacticIntent;
                    if (!OfflineReuseAuthority) input.yawAuthority = Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
                    var updated = CombatFlightPlanner.RetargetHeading(input, self, heading, elapsed);
'@)
    $tactics.Replace($needles[2], 'bool screenHeading = OfflineScreenRoute && elapsed > 0f && !intent.holdCurrent && OrbitSphere.Valid;') |
        Set-Content -Encoding utf8 "$out/ShipAutoCombat.Tactics.Offline.cs"
    $basis = 'intent.SampleHeadingBasis(elapsed, out float angle, out float omega);'
    if (!$planner.Contains($basis)) { throw 'Aim refresh basis marker is unavailable.' }
    $planner.Replace($basis, @'
float angle, omega;
            if (ShipAutoManeuverControl.OfflineReuseAuthority) intent.SampleHeadingBasis(elapsed, out angle, out omega);
            else
            {
                var bearing = intent;
                bearing.localAnchor = Vector2.zero;
                bearing.frameAngle = bearing.headingError = bearing.headingRateError = 0f;
                bearing.Sample(elapsed, out _, out _, out angle, out omega);
            }
'@) | Set-Content -Encoding utf8 "$out/CombatFlightPlanner.Offline.cs"
    @"
<Project>
  <ItemGroup>
    <Compile Remove="Interaction/ShipAutoCombat.Tactics.cs;Interaction/CombatFlightPlanner.cs"/>
    <Compile Include="$out/ShipAutoCombat.Tactics.Offline.cs;$out/CombatFlightPlanner.Offline.cs"/>
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding utf8 "$out/Offline.targets"
    $extraCompile += "-p:CustomAfterMicrosoftCommonTargets=$out/Offline.targets"
}
if ($Mothership -in @('Experiment','HeadingTime','AimRefresh','AimRefreshTime')) {
    & dotnet msbuild "$repo/Source/DeltaV.csproj" -restore -t:Compile -p:Configuration=Release -p:RimWorldVersion=1.6 `
        "-p:TargetPlatformSdkPath=C:/Program Files (x86)/Windows Kits/10/" -p:TargetPlatformDisplayName=Windows `
        "-p:IntermediateOutputPath=$out/compile/" "-p:MSBuildProjectExtensionsPath=$out/compile/" `
        -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true @extraCompile
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$lines = @('/nostdlib+', '/target:exe', '/optimize+', '/langversion:latest', ('/out:"' + "$out/NpcRetreatRegression.dll" + '"'))
if ($Mothership -in @('All','Crossover')) { $lines += '/define:MOTHERSHIP_OFFLINE' }
if ($Mothership -in @('Experiment','HeadingTime','AimRefresh','AimRefreshTime')) { $lines += '/define:MOTHERSHIP_PRODUCTION' }
if ($Mothership -eq 'HeadingTime') { $lines += '/define:MOTHERSHIP_HEADING_TIMING' }
if ($Mothership -eq 'AimRefreshTime') { $lines += '/define:MOTHERSHIP_AIM_REFRESH_TIMING' }
$lines += Get-ChildItem "$($refs.FullName)/ref/net8.0" -Filter '*.dll' | ForEach-Object { '/r:"' + $_.FullName + '"' }
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll', 'Assembly-CSharp-firstpass.dll')) {
    Copy-Item -LiteralPath "$managed/$name" -Destination $out -Force
}
$deltaV = if ($Mothership -in @('All','Crossover','Experiment','HeadingTime','AimRefresh','AimRefreshTime')) { "$out/compile/DeltaV.dll" } else { "$repo/1.6/Assemblies/DeltaV.dll" }
Copy-Item -LiteralPath $deltaV -Destination $out -Force
Copy-Item -LiteralPath "$repo/References/REF/1.6/RimExodus.Maps.dll" -Destination $out -Force
Copy-Item -LiteralPath $HarmonyPath -Destination $out -Force
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll', 'DeltaV.dll', 'RimExodus.Maps.dll', '0Harmony.dll')) { $lines += '/r:"' + "$out/$name" + '"' }
$lines += Get-ChildItem $PSScriptRoot -Filter '*.cs' | ForEach-Object { '"' + $_.FullName + '"' }
$lines | Set-Content -LiteralPath "$out/build.rsp"
& dotnet "$($sdkVersion.FullName)/Roslyn/bincore/csc.dll" /noconfig "@$out/build.rsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}' | Set-Content -LiteralPath "$out/NpcRetreatRegression.runtimeconfig.json"
$regressionArguments = @($managed)
if ($Mothership) { $regressionArguments += @('--mothership-engagement', $GameRoot, $out) }
if ($CombatManeuverOnly) { $regressionArguments += '--combat-maneuver-only' }
if ($EngagementBaseline) { $regressionArguments += '--engagement-baseline' }
if ($EngagementRegression) { $regressionArguments += '--engagement-regression' }
if ($AutoVolleyOnly) { $regressionArguments += '--auto-volley-only' }
if ($EngagementBattle) { $regressionArguments += '--engagement-battle' }
if ($StaticEngagement) { $regressionArguments += @('--engagement-static', $StaticScene, $StaticEngagement) }
if ($CollisionFleet) {
    $regressionArguments += @('--engagement-fleet-regression', '--engagement-collision-edits')
    if ($CollisionFleet -eq 'Point') { $regressionArguments += '--engagement-collision-point' }
    if ($CollisionDense) { $regressionArguments += '--engagement-cover-dense' }
    if ($CollisionParity) { $regressionArguments += '--engagement-range-parity' }
}
if ($GlobeOnly) { $regressionArguments += '--globe-only' }
if ($AimInputOnly) { $regressionArguments += '--aim-input-only' }
if ($PlayerFlightOnly) { $regressionArguments += '--player-flight-only' }
if ($SalvageOnly) { $regressionArguments += '--salvage-only' }
if ($DrivePerformanceOnly) { $regressionArguments += '--drive-performance-only' }
if ($ArchiveRecoveryOnly) { $regressionArguments += '--archive-recovery-only' }
if ($ProjectileOnly) { $regressionArguments += '--projectile-only' }
if ($DockingOnly) { $regressionArguments += '--docking-only' }
if ($NpcOnly) { $regressionArguments += '--npc-only' }
if ($ProjectileFullOnly) { $regressionArguments += '--projectile-full-only' }
if ($SeparationOnly) { $regressionArguments += '--separation-only' }
if ($ThrusterPauseOnly) { $regressionArguments += '--thruster-pause-only' }
if ($EvaOnly) { $regressionArguments += '--eva-only' }
if ($TractorOnly) { $regressionArguments += '--tractor-only' }
if ($Mothership -in @('Experiment','AimRefresh')) {
    $modes = if ($Mothership -eq 'AimRefresh') { @('topology','aim-cause','health','production','heading') } else { @('health','production','heading') }
    foreach ($mode in $modes) {
        & dotnet "$out/NpcRetreatRegression.dll" @regressionArguments $mode
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    exit 0
}
if ($Mothership -eq 'HeadingTime') {
    & dotnet "$out/NpcRetreatRegression.dll" @regressionArguments heading-time
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Get-Content "$out/heading-timing.jsonl" | ConvertFrom-Json | Export-Csv "$out/heading-timing.csv" -NoTypeInformation
    exit 0
}
if ($Mothership -eq 'AimRefreshTime') {
    foreach ($mode in 'count','time') {
        & dotnet "$out/NpcRetreatRegression.dll" @regressionArguments $mode
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    exit 0
}
if ($Mothership -eq 'Crossover') {
    & dotnet "$out/NpcRetreatRegression.dll" @regressionArguments crossover 0 $DamageWindowTicks
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $rows = @(Get-Content "$out/crossover.jsonl" | ConvertFrom-Json)
    $rows | Export-Csv "$out/crossover.csv" -NoTypeInformation
    $paired = foreach ($group in ($rows | Group-Object suite,block,pair)) {
        $a = $group.Group | Where-Object arm -eq 'old'
        $b = $group.Group | Where-Object arm -eq 'new'
        if (@($a).Count -ne 8 -or @($b).Count -ne 8) { throw "Incomplete crossover pair $($group.Name)." }
        $row = [ordered]@{suite = $a[0].suite;block = $a[0].block;pair = $a[0].pair}
        foreach ($field in 'totalMs','queryMs','scanMs','ddaMs','classifyMs','registerMs','cacheMs',
            'groupingMs','responseMs','distributeMs','fillMs','scoreMs','sortMs','copyMs',
            'otherMs','bytes','gc0','gc1','gc2') {
            $old = ($a | Measure-Object $field -Sum).Sum
            $new = ($b | Measure-Object $field -Sum).Sum
            $row["old_$field"] = $old
            $row["new_$field"] = $new
            $row["delta_$field"] = $new-$old
        }
        [pscustomobject]$row
    }
    $paired | Export-Csv "$out/pairs.csv" -NoTypeInformation
    Write-Output "Crossover results written to $out"
    exit 0
}
if ($Mothership -eq 'All') {
    $casesLog = @(& dotnet "$out/NpcRetreatRegression.dll" @regressionArguments cases 0 $DamageWindowTicks)
    if ($LASTEXITCODE -ne 0) { $casesLog | Write-Output; exit $LASTEXITCODE }
    $casesLog | Set-Content -Encoding utf8 "$out/cases.txt"
    $casesLog | Write-Output
    & dotnet "$out/NpcRetreatRegression.dll" @regressionArguments reference 0 $DamageWindowTicks
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet "$out/NpcRetreatRegression.dll" @regressionArguments count 0 $DamageWindowTicks
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    for ($round = 1; $round -le 5; $round++) {
        & dotnet "$out/NpcRetreatRegression.dll" @regressionArguments time $round $DamageWindowTicks
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $quality = @(Get-Content "$out/reference.jsonl" | ForEach-Object { ConvertFrom-Json $_ })
    $counts = @(Get-Content "$out/count.jsonl" | ForEach-Object { ConvertFrom-Json $_ })
    $times = @(1..5 | ForEach-Object { Get-Content "$out/time-$_.jsonl" } |
        ForEach-Object { ConvertFrom-Json $_ })
    if ($quality.Count -ne 8 -or $counts.Count -ne 8 -or $times.Count -ne 40) {
        throw 'The comparison is missing result rows.'
    }
    $summary = foreach ($row in $counts) {
        $reference = @($quality | Where-Object { $_.scene -eq $row.scene -and $_.side -eq $row.side })
        $samples = @($times | Where-Object { $_.scene -eq $row.scene -and $_.side -eq $row.side })
        if ($reference.Count -ne 1 -or $reference[0].candidateHash -cne $row.candidateHash -or $samples.Count -ne 5 -or
            @($samples | Where-Object {
                $_.candidateHash -cne $row.candidateHash -or $_.winner -ne $row.winner -or
                ($_.ranking -join ',') -cne ($row.ranking -join ',')
            }).Count -ne 0) {
            throw "Inputs or selections changed between phases for $($row.scene) $($row.side)."
        }
        $ref = $reference[0]
        $gap = if ($ref.winner -ge 0 -and $row.winner -ge 0) {
            [double]$ref.scores[$ref.winner] - [double]$ref.scores[$row.winner]
        } else { $null }
        $refRank = if ($ref.winner -ge 0) { [Array]::IndexOf($row.ranking, $ref.winner) + 1 } else { 0 }
        [pscustomobject]@{
            scene = $row.scene
            side = $row.side
            candidates = $row.candidates
            candidate_hash = $row.candidateHash
            top_dual = $row.topRanked
            selected_dual = $row.winner
            selected_reference = $ref.winner
            same_selection = $row.routeValid -and $ref.routeValid -and $row.winner -eq $ref.winner
            reference_rank_in_dual = $refRank
            reference_score_gap = $gap
            dual_distance = $row.selectedDistance
            reference_distance = $ref.selectedDistance
            dual_bearing_deg = $row.selectedBearing
            reference_bearing_deg = $ref.selectedBearing
            dual_heading_deg = $row.selectedHeading
            reference_heading_deg = $ref.selectedHeading
            dual_route_valid = $row.routeValid
            reference_route_valid = $ref.routeValid
            window_ticks = $row.windowTicks
            full_aim = $row.fullAimCalls
            legacy_aim = $row.legacyAimCalls
            exact_fire = $row.exactFireCalls
            first_rays = $row.outgoing.defenseRays + $row.incoming.defenseRays
            first_layers = $row.outgoing.layerVisits + $row.incoming.layerVisits
            repeat_rays = $row.outgoing.repeatRays + $row.incoming.repeatRays
            repeat_layers = $row.outgoing.repeatLayers + $row.incoming.repeatLayers
            repeat_grid_visits = $row.outgoing.repeatGridVisits + $row.incoming.repeatGridVisits
        }
    }
    $summary | Export-Csv "$out/summary.csv" -NoTypeInformation
    $times | Group-Object round | ForEach-Object {
        [pscustomobject]@{
            round = $_.Name
            candidates_ms = ($_.Group | Measure-Object candidateMs -Sum).Sum
            weapon_table_ms = ($_.Group | Measure-Object weaponTableMs -Sum).Sum
            defense_init_ms = ($_.Group | Measure-Object defenseInitMs -Sum).Sum
            simple_gate_ms = ($_.Group | Measure-Object gatesMs -Sum).Sum
            first_scan_ms = ($_.Group | Measure-Object firstScanMs -Sum).Sum
            callbacks_ms = ($_.Group | Measure-Object callbacksMs -Sum).Sum
            query_and_rank_ms = ($_.Group | Measure-Object queryMs -Sum).Sum
            repeat_query_ms = ($_.Group | Measure-Object repeatQueryMs -Sum).Sum
            route_and_trajectory_ms = ($_.Group | Measure-Object routeMs -Sum).Sum
            cold_search_ms = ($_.Group | Measure-Object totalMs -Sum).Sum
        }
    } | Export-Csv "$out/rounds.csv" -NoTypeInformation
    Write-Output "Results written to $out"
    exit 0
}
& dotnet "$out/NpcRetreatRegression.dll" @regressionArguments
exit $LASTEXITCODE
