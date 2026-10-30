param([string]$GameRoot = 'D:/SteamLibrary/steamapps/common/RimWorld', [string]$DeltaVAssembly)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (!$DeltaVAssembly) { $DeltaVAssembly = "$repo/1.7/Assemblies/DeltaV.dll" }
$dotnet = "$repo/.local-tools/dotnet/dotnet.exe"
$sdk = Get-ChildItem "$repo/.local-tools/dotnet/sdk" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$refs = Get-ChildItem "$repo/.local-tools/dotnet/packs/Microsoft.NETCore.App.Ref" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$out = Join-Path $repo 'Source/obj/WeaponTuningRegression'
New-Item -ItemType Directory -Force $out | Out-Null
$lines = @('/nostdlib+', '/target:exe', '/optimize+', '/langversion:latest', ('/out:"' + "$out/WeaponTuningRegression.dll" + '"'))
$lines += Get-ChildItem "$($refs.FullName)/ref/net10.0" -Filter '*.dll' | ForEach-Object { '/r:"' + $_.FullName + '"' }
foreach ($path in @("$GameRoot/Assembly-CSharp.dll", "$GameRoot/UnityStubs.dll", "$GameRoot/Waylith.dll",
    "$GameRoot/Brutal.Core.Numerics.dll", $DeltaVAssembly, "$repo/References/REF/1.7/RimExodus.Maps.dll")) {
    Copy-Item -LiteralPath $path -Destination $out -Force
    $lines += '/r:"' + $path + '"'
}
$lines += '"' + "$PSScriptRoot/Program.cs" + '"'
$lines | Set-Content -LiteralPath "$out/build.rsp"
& $dotnet "$($sdk.FullName)/Roslyn/bincore/csc.dll" /noconfig "@$out/build.rsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -LiteralPath "$out/WeaponTuningRegression.runtimeconfig.json"
& $dotnet "$out/WeaponTuningRegression.dll" $GameRoot $repo
exit $LASTEXITCODE
