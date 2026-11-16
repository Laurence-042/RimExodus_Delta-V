[CmdletBinding(DefaultParameterSetName='Regression')]
param([Parameter(Mandatory=$true, ParameterSetName='Regression')][string]$GameRoot,
      [Parameter(Mandatory=$true, ParameterSetName='Regression')][string]$HarmonyPath,
      [Parameter(ParameterSetName='Regression')][string]$DeltaVAssembly,
      [Parameter(ParameterSetName='Regression')]
      [Parameter(Mandatory=$true, ParameterSetName='RefExtraction')][string]$RefAssembly,
      [Parameter(Mandatory=$true, ParameterSetName='RefExtraction')][switch]$BuildRefExtractionAB,
      [Parameter(ParameterSetName='RefExtraction')][ValidateSet('1.6','1.7')][string]$Version = '1.7',
      [Parameter(Mandatory=$true, ParameterSetName='ShellTerrainAB')][switch]$BuildShellTerrainAB)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if ($BuildRefExtractionAB) {
    $artifacts = Join-Path $repo "Source/obj/ShellTerrainAB/refExtraction/$Version"
    $source = Join-Path $artifacts 'source'
    $archive = Join-Path $artifacts 'fd0f39d.zip'
    $sdk = Join-Path $repo '.local-tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    & git -C $repo archive --format=zip --output=$archive fd0f39d Source build/RimWorldReferences.props
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Expand-Archive -LiteralPath $archive -DestinationPath $source -Force
    $references = Join-Path $source "References/REF/$Version"
    New-Item -ItemType Directory -Force -Path $references | Out-Null
    Copy-Item -LiteralPath $RefAssembly -Destination "$references/RimExodus.Maps.dll" -Force
    Copy-Item -LiteralPath $RefAssembly -Destination "$artifacts/RimExodus.Maps.dll" -Force
    Get-FileHash -LiteralPath "$artifacts/RimExodus.Maps.dll" | Format-List Hash,Path
    foreach ($variant in @('baseline', 'extracted')) {
        if ($variant -eq 'extracted') {
            foreach ($file in @('MapOverlayRenderer.cs', 'ShellTerrainBatches.cs')) {
                Copy-Item -LiteralPath "$repo/Source/Rendering/$file" -Destination "$source/Source/Rendering/$file" -Force
            }
        }
        $directory = Join-Path $artifacts $variant
        & $sdk msbuild "$source/Source/DeltaV.csproj" -restore -t:Compile -p:Configuration=Release "-p:RimWorldVersion=$Version" `
            "-p:TargetPlatformSdkPath=C:/Program Files (x86)/Windows Kits/10/" -p:TargetPlatformDisplayName=Windows `
            "-p:IntermediateOutputPath=$directory/" "-p:MSBuildProjectExtensionsPath=$directory/" `
            -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Get-FileHash -LiteralPath "$directory/DeltaV.dll" | Format-List Hash,Path
    }
    exit 0
}
if ($BuildShellTerrainAB) {
    $artifacts = Join-Path $repo 'Source/obj/ShellTerrainAB'
    $sdk = Join-Path $repo '.local-tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    $baselineTargets = Join-Path $artifacts 'PerCell.targets'
    @'
<Project>
  <Target Name="DefinePerCellShellTerrain" BeforeTargets="CoreCompile">
    <PropertyGroup>
      <DefineConstants>$(DefineConstants);DELTAV_SHELL_TERRAIN_AB_BASELINE</DefineConstants>
    </PropertyGroup>
  </Target>
</Project>
'@ | Set-Content -LiteralPath $baselineTargets
    foreach ($variant in @('perCell', 'batched')) {
        $directory = Join-Path $artifacts $variant
        $extra = @()
        if ($variant -eq 'perCell') { $extra += "-p:CustomAfterMicrosoftCommonTargets=$baselineTargets" }
        & $sdk msbuild "$repo/Source/DeltaV.csproj" -restore -t:Compile -p:Configuration=Release -p:RimWorldVersion=1.7 `
            "-p:TargetPlatformSdkPath=C:/Program Files (x86)/Windows Kits/10/" -p:TargetPlatformDisplayName=Windows `
            "-p:IntermediateOutputPath=$directory/" "-p:MSBuildProjectExtensionsPath=$directory/" `
            -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true @extra
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Get-FileHash -LiteralPath "$directory/DeltaV.dll" | Format-Table -AutoSize
    }
    exit 0
}
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
if (!$DeltaVAssembly) { $DeltaVAssembly = "$repo/1.6/Assemblies/DeltaV.dll" }
Copy-Item -LiteralPath $DeltaVAssembly -Destination "$out/DeltaV.dll" -Force
if (!$RefAssembly) { $RefAssembly = "$repo/References/REF/1.6/RimExodus.Maps.dll" }
Copy-Item -LiteralPath $RefAssembly -Destination "$out/RimExodus.Maps.dll" -Force
Copy-Item -LiteralPath $HarmonyPath -Destination $out -Force
foreach ($name in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'DeltaV.dll', 'RimExodus.Maps.dll', '0Harmony.dll')) { $lines += '/r:"' + "$out/$name" + '"' }
$lines += '"' + "$PSScriptRoot/Program.cs" + '"'
$lines += '"' + "$PSScriptRoot/RoofChecks.cs" + '"'
$lines | Set-Content -LiteralPath "$out/build.rsp"
& dotnet "$($sdkVersion.FullName)/Roslyn/bincore/csc.dll" /noconfig "@$out/build.rsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}' | Set-Content -LiteralPath "$out/SubstructureDamageRegression.runtimeconfig.json"
& dotnet "$out/SubstructureDamageRegression.dll" $managed
exit $LASTEXITCODE
