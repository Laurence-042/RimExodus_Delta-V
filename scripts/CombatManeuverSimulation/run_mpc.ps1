param([string]$GameRoot, [switch]$Fighter, [switch]$Expansion)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (!$GameRoot) { $GameRoot = "$repo/../.." }
$managed = (Resolve-Path "$GameRoot/RimWorldWin64_Data/Managed").Path
$sdk = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet/sdk') -Directory |
    Where-Object { $_.Name -match '^8\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$refs = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet/packs/Microsoft.NETCore.App.Ref') -Directory |
    Where-Object { $_.Name -match '^8\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$refs) { throw 'The .NET 8 SDK is required.' }
$out = Join-Path $repo 'Source/obj/CombatManeuverSimulation'
New-Item -ItemType Directory -Force $out | Out-Null
$lines = @('/nostdlib+', '/target:exe', '/optimize+', '/langversion:latest',
    ('/out:"' + "$out/MpcRegression.dll" + '"'))
$lines += Get-ChildItem "$($refs.FullName)/ref/net8.0" -Filter '*.dll' |
    ForEach-Object { '/r:"' + $_.FullName + '"' }
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'Assembly-CSharp-firstpass.dll')) {
    Copy-Item -LiteralPath "$managed/$name" -Destination $out -Force
}
Copy-Item -LiteralPath "$repo/1.6/Assemblies/DeltaV.dll" -Destination $out -Force
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'DeltaV.dll')) {
    $lines += '/r:"' + "$out/$name" + '"'
}
$lines += Get-ChildItem $PSScriptRoot -Filter '*.cs' | ForEach-Object { '"' + $_.FullName + '"' }
$lines | Set-Content -LiteralPath "$out/build.rsp"
& dotnet "$($sdk.FullName)/Roslyn/bincore/csc.dll" /noconfig "@$out/build.rsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}' |
    Set-Content -LiteralPath "$out/MpcRegression.runtimeconfig.json"
Push-Location $repo
try {
    if ($Fighter -or $Expansion) {
        & python "$PSScriptRoot/fighter_inputs.py"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        if ($Expansion) { & dotnet "$out/MpcRegression.dll" --expansion }
        else { & dotnet "$out/MpcRegression.dll" --fighter }
    } else { & dotnet "$out/MpcRegression.dll" }
} finally { Pop-Location }
exit $LASTEXITCODE
