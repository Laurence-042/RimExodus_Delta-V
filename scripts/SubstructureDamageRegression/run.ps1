param([Parameter(Mandatory=$true)][string]$GameRoot,
      [Parameter(Mandatory=$true)][string]$HarmonyPath)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$managed = (Resolve-Path "$GameRoot/RimWorldWin64_Data/Managed").Path
if (!(Test-Path -LiteralPath $HarmonyPath)) { throw 'Pass -HarmonyPath with a .NET 8 Harmony runtime DLL (Lib.Harmony/lib/net8.0/0Harmony.dll).' }
$sdkVersion = Get-ChildItem "$env:ProgramFiles/dotnet/sdk" -Directory | Where-Object { $_.Name -match '^8\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$refs = Get-ChildItem "$env:ProgramFiles/dotnet/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object { $_.Name -match '^8\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdkVersion -or !$refs) { throw 'Install .NET 8 SDK to run the offline substructure damage regression.' }
$out = Join-Path $repo 'Source/obj/SubstructureDamageRegression'
New-Item -ItemType Directory -Force $out | Out-Null
$lines = @('/nostdlib+', '/target:exe', '/optimize+', '/langversion:latest', ('/out:"' + "$out/SubstructureDamageRegression.dll" + '"'))
$lines += Get-ChildItem "$($refs.FullName)/ref/net8.0" -Filter '*.dll' | ForEach-Object { '/r:"' + $_.FullName + '"' }
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'Assembly-CSharp-firstpass.dll')) {
    Copy-Item -LiteralPath "$managed/$name" -Destination $out -Force
}
Copy-Item -LiteralPath "$repo/1.6/Assemblies/DeltaV.dll" -Destination $out -Force
Copy-Item -LiteralPath $HarmonyPath -Destination $out -Force
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'DeltaV.dll', '0Harmony.dll')) { $lines += '/r:"' + "$out/$name" + '"' }
$lines += '"' + "$PSScriptRoot/Program.cs" + '"'
$lines | Set-Content -LiteralPath "$out/build.rsp"
& dotnet "$($sdkVersion.FullName)/Roslyn/bincore/csc.dll" /noconfig "@$out/build.rsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}' | Set-Content -LiteralPath "$out/SubstructureDamageRegression.runtimeconfig.json"
& dotnet "$out/SubstructureDamageRegression.dll" $managed
exit $LASTEXITCODE
