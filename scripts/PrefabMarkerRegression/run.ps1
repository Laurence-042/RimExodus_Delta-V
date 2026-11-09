param([string]$GameRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (!$GameRoot) { $GameRoot = 'D:/SteamLibrary/steamapps/common/RimWorld_1.6' }
$managed = (Resolve-Path "$GameRoot/RimWorldWin64_Data/Managed").Path
$sdkVersion = Get-ChildItem "$env:ProgramFiles/dotnet/sdk" -Directory | Where-Object { $_.Name -match '^8\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$refs = Get-ChildItem "$env:ProgramFiles/dotnet/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object { $_.Name -match '^8\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdkVersion -or !$refs) { throw 'Install .NET 8 SDK to run the prefab marker regression.' }
$out = Join-Path $repo 'Source/obj/PrefabMarkerRegression'
New-Item -ItemType Directory -Force $out | Out-Null
$lines = @('/nostdlib+', '/target:exe', '/optimize+', '/langversion:latest', ('/out:"' + "$out/PrefabMarkerRegression.dll" + '"'))
$lines += Get-ChildItem "$($refs.FullName)/ref/net8.0" -Filter '*.dll' | ForEach-Object { '/r:"' + $_.FullName + '"' }
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll', 'UnityEngine.SharedInternalsModule.dll', 'Assembly-CSharp-firstpass.dll')) {
    Copy-Item -LiteralPath "$managed/$name" -Destination $out -Force
}
Copy-Item -LiteralPath "$repo/1.6/Assemblies/DeltaV.dll" -Destination $out -Force
Copy-Item -LiteralPath "$repo/References/REF/1.6/RimExodus.Maps.dll" -Destination $out -Force
foreach ($name in @('AdaptiveStorageFramework', '1ITransformable')) {
    Copy-Item -LiteralPath "D:/SteamLibrary/steamapps/workshop/content/294100/3033901359/1.6/Assemblies/$name.dll" -Destination $out -Force
}
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll', 'UnityEngine.SharedInternalsModule.dll', 'DeltaV.dll', 'RimExodus.Maps.dll')) { $lines += '/r:"' + "$out/$name" + '"' }
$lines += '"' + "$PSScriptRoot/Program.cs" + '"'
$lines | Set-Content -LiteralPath "$out/build.rsp"
& dotnet "$($sdkVersion.FullName)/Roslyn/bincore/csc.dll" /noconfig "@$out/build.rsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}' | Set-Content -LiteralPath "$out/PrefabMarkerRegression.runtimeconfig.json"
& dotnet "$out/PrefabMarkerRegression.dll"
exit $LASTEXITCODE
