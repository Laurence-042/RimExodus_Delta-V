param([switch]$Strict, [string]$GameRoot, [ValidateSet('Fast','Balanced','Precise')][string]$Profile = 'Balanced', [switch]$Exact)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (!$GameRoot) { $GameRoot = "$repo/../.." }
$managed = (Resolve-Path "$GameRoot/RimWorldWin64_Data/Managed").Path
$sdkRoot = Join-Path $env:ProgramFiles 'dotnet/sdk'
$sdkVersion = Get-ChildItem $sdkRoot -Directory | Where-Object { $_.Name -match '^8\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdkVersion) { throw 'Install .NET 8 SDK to run the offline MPC regression.' }
$sdk = Join-Path $sdkVersion.FullName 'Roslyn/bincore/csc.dll'
$refRoot = Join-Path $env:ProgramFiles 'dotnet/packs/Microsoft.NETCore.App.Ref'
$refs = Get-ChildItem $refRoot -Directory | Where-Object { $_.Name -match '^8\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$out = Join-Path $repo 'Source/obj/MpcRegression'
New-Item -ItemType Directory -Force $out | Out-Null
$lines = @('/nowarn:0649', '/nostdlib+', '/target:exe', '/optimize+', '/langversion:latest', ('/out:"' + "$out/MpcRegression.dll" + '"'))
$lines += Get-ChildItem "$($refs.FullName)/ref/net8.0" -Filter '*.dll' | ForEach-Object { '/r:"' + $_.FullName + '"' }
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'Assembly-CSharp-firstpass.dll')) {
    Copy-Item -LiteralPath "$managed/$name" -Destination $out -Force
}
Copy-Item -LiteralPath "$repo/1.6/Assemblies/DeltaV.dll" -Destination $out -Force
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'DeltaV.dll')) { $lines += '/r:"' + "$out/$name" + '"' }
$lines += @("$PSScriptRoot/Program.cs", "$PSScriptRoot/SolverChecks.cs") | ForEach-Object { '"' + $_ + '"' }
$lines | Set-Content -LiteralPath "$out/build.rsp"
& dotnet $sdk /noconfig "@$out/build.rsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}' | Set-Content -LiteralPath "$out/MpcRegression.runtimeconfig.json"
$runArgs = @("--profile=$Profile")
if ($Strict) { $runArgs += '--strict' }
if ($Exact) { $runArgs += '--exact' }
Push-Location $repo
try { & dotnet "$out/MpcRegression.dll" @runArgs } finally { Pop-Location }
exit $LASTEXITCODE
