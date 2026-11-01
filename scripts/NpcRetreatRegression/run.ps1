param([string]$GameRoot, [string]$HarmonyPath, [switch]$AimInputOnly, [switch]$PlayerFlightOnly,
      [switch]$SalvageOnly, [switch]$DrivePerformanceOnly, [switch]$ProjectileOnly, [switch]$ArchiveRecoveryOnly,
      [switch]$DockingOnly, [switch]$NpcOnly, [switch]$ProjectileFullOnly, [switch]$SeparationOnly,
      [switch]$ThrusterPauseOnly, [switch]$TractorOnly)
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
New-Item -ItemType Directory -Force $out | Out-Null
$lines = @('/nostdlib+', '/target:exe', '/optimize+', '/langversion:latest', ('/out:"' + "$out/NpcRetreatRegression.dll" + '"'))
$lines += Get-ChildItem "$($refs.FullName)/ref/net8.0" -Filter '*.dll' | ForEach-Object { '/r:"' + $_.FullName + '"' }
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll', 'Assembly-CSharp-firstpass.dll')) {
    Copy-Item -LiteralPath "$managed/$name" -Destination $out -Force
}
Copy-Item -LiteralPath "$repo/1.6/Assemblies/DeltaV.dll" -Destination $out -Force
Copy-Item -LiteralPath "$repo/References/REF/1.6/RimExodus.Maps.dll" -Destination $out -Force
Copy-Item -LiteralPath $HarmonyPath -Destination $out -Force
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll', 'DeltaV.dll', 'RimExodus.Maps.dll', '0Harmony.dll')) { $lines += '/r:"' + "$out/$name" + '"' }
$lines += Get-ChildItem $PSScriptRoot -Filter '*.cs' | ForEach-Object { '"' + $_.FullName + '"' }
$lines | Set-Content -LiteralPath "$out/build.rsp"
& dotnet "$($sdkVersion.FullName)/Roslyn/bincore/csc.dll" /noconfig "@$out/build.rsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}' | Set-Content -LiteralPath "$out/NpcRetreatRegression.runtimeconfig.json"
$regressionArguments = @($managed)
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
if ($TractorOnly) { $regressionArguments += '--tractor-only' }
& dotnet "$out/NpcRetreatRegression.dll" @regressionArguments
exit $LASTEXITCODE
