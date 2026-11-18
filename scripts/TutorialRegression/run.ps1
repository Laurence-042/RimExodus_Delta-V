param(
    [string]$GameRoot,
    [ValidateSet('1.6','1.7')][string]$Version = '1.6',
    [Parameter(Mandatory=$true)][string]$HarmonyPath,
    [switch]$RangeOnly,
    [switch]$SectorOnly,
    [switch]$ShipyardOnly,
    [switch]$ModulesOnly,
    [switch]$CompileOnly,
    [string]$ShipSearchDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (!$GameRoot) { $GameRoot = if ($Version -eq '1.7') { 'D:/SteamLibrary/steamapps/common/RimWorld' } else { 'D:/SteamLibrary/steamapps/common/RimWorld_1.6' } }
$managed = if ($Version -eq '1.7') { (Resolve-Path $GameRoot).Path } else { (Resolve-Path "$GameRoot/RimWorldWin64_Data/Managed").Path }
$major = if ($Version -eq '1.7') { '10' } else { '8' }
$dotnetRoot = if ($Version -eq '1.7') { "$repo/.local-tools/dotnet" } else { "$env:ProgramFiles/dotnet" }
$dotnet = "$dotnetRoot/dotnet.exe"
$sdkVersion = Get-ChildItem "$dotnetRoot/sdk" -Directory | Where-Object { $_.Name -match "^$major\.\d+\.\d+$" } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$refs = Get-ChildItem "$dotnetRoot/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object { $_.Name -match "^$major\.\d+\.\d+$" } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdkVersion -or !$refs) { throw "Install .NET $major SDK for the native offline tutorial checks." }
$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $repo $(if ($Version -eq '1.7') { 'Source/obj/TutorialRegression17' } else { 'Source/obj/TutorialRegression' })
}
New-Item -ItemType Directory -Force $out | Out-Null
$lines = @('/nostdlib+', '/target:exe', '/optimize+', '/langversion:latest', ('/out:"' + "$out/TutorialRegression.dll" + '"'))
$lines += Get-ChildItem "$($refs.FullName)/ref/net$major.0" -Filter '*.dll' | ForEach-Object { '/r:"' + $_.FullName + '"' }
$gameAssemblies = if ($Version -eq '1.7') { @('Assembly-CSharp.dll','UnityStubs.dll','Waylith.dll','Brutal.Core.Numerics.dll') } else { @('Assembly-CSharp.dll','UnityEngine.CoreModule.dll','Assembly-CSharp-firstpass.dll') }
foreach ($name in $gameAssemblies) {
    Copy-Item -LiteralPath "$managed/$name" -Destination $out -Force
}
if ($Version -eq '1.7') {
    # The fixture uses UnityStubs; the shared ASF reference still names 1.6
    # Unity assemblies. These forwarders are only for offline type discovery.
    $forwarders = @{
        CoreModule = 'Camera Color Color32 Graphics KeyCode LightProbeProxyVolume Material MaterialPropertyBlock Mathf Matrix4x4 Mesh Object Quaternion Rect Shader Texture Texture2D Time Transform Vector2 Vector3'
        IMGUIModule = 'Event EventType GUI GUIContent GUISkin GUIStyle GUIStyleState'
        TextRenderingModule = 'TextAnchor'
    }
    foreach ($module in $forwarders.Keys) {
        $source = "$out/UnityEngine.$module.cs"
        $forwarders[$module].Split(' ') | ForEach-Object {
            "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(UnityEngine.$_))]"
        } | Set-Content -LiteralPath $source
        & $dotnet "$($sdkVersion.FullName)/Roslyn/bincore/csc.dll" /noconfig /nologo /nostdlib+ /target:library "/r:$($refs.FullName)/ref/net10.0/System.Runtime.dll" "/r:$out/UnityStubs.dll" "/out:$out/UnityEngine.$module.dll" $source
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
Copy-Item -LiteralPath "$repo/$Version/Assemblies/DeltaV.dll" -Destination $out -Force
Copy-Item -LiteralPath "$repo/References/REF/$Version/RimExodus.Maps.dll" -Destination $out -Force
Copy-Item -LiteralPath $HarmonyPath -Destination "$out/0Harmony.dll" -Force
foreach ($name in @('AdaptiveStorageFramework', '1ITransformable')) {
    Copy-Item -LiteralPath "D:/SteamLibrary/steamapps/workshop/content/294100/3033901359/1.6/Assemblies/$name.dll" -Destination $out -Force
}
foreach ($name in ($gameAssemblies + @('DeltaV.dll', 'RimExodus.Maps.dll', '0Harmony.dll'))) {
    $lines += '/r:"' + "$out/$name" + '"'
}
$lines += '"' + "$PSScriptRoot/Program.cs" + '"'
$lines += '"' + "$PSScriptRoot/ShipyardChecks.cs" + '"'
$lines += '"' + "$PSScriptRoot/ShipSearchChecks.cs" + '"'
$lines += '"' + "$PSScriptRoot/PrefabContentChecks.cs" + '"'
$lines += '"' + "$repo/scripts/PrefabRegression/PrefabContentValidation.cs" + '"'
$lines += '"' + "$PSScriptRoot/ArrivalChecks.cs" + '"'
$lines += '"' + "$PSScriptRoot/MissionChecks.cs" + '"'
$lines += '"' + "$PSScriptRoot/PilotConsoleChecks.cs" + '"'
$lines += '"' + "$PSScriptRoot/ThrusterThrustChecks.cs" + '"'
$lines += '"' + "$PSScriptRoot/ShieldSettingChecks.cs" + '"'
$lines += '"' + "$PSScriptRoot/RangeChecks.cs" + '"'
$lines | Set-Content -LiteralPath "$out/build.rsp"
& $dotnet "$($sdkVersion.FullName)/Roslyn/bincore/csc.dll" /noconfig "@$out/build.rsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
"{`"runtimeOptions`":{`"tfm`":`"net$major.0`",`"framework`":{`"name`":`"Microsoft.NETCore.App`",`"version`":`"$major.0.0`"}}}" | Set-Content -LiteralPath "$out/TutorialRegression.runtimeconfig.json"
if ($CompileOnly) { exit 0 }
[string[]]$extra = if ($ShipSearchDirectory) {
    @('ship-search', (Resolve-Path -LiteralPath $ShipSearchDirectory).Path)
} elseif ($ModulesOnly) { @('modules') } elseif ($RangeOnly) { @('range') } elseif ($SectorOnly) { @('sector') } elseif ($ShipyardOnly) { @('shipyard') } else { @() }
& $dotnet "$out/TutorialRegression.dll" $managed "$repo/Source/obj/TutorialContent/ArrivalStation.records.json" "$repo/Common/Prefabs/Tutorial/ArrivalStation.xml" "$repo/Common/Prefabs" @extra
exit $LASTEXITCODE
