param([ValidateSet('1.6','1.7')][string]$Version='1.7')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$framework = [IO.Path]::GetFullPath((Join-Path $root '../RimExodusFramework'))
$frameworkBuild = Join-Path $framework 'build/Build.ps1'
if (!(Test-Path -LiteralPath $frameworkBuild)) { throw "Sibling REF build script missing: $frameworkBuild" }
$localSdk = Join-Path $root '.local-tools/dotnet/dotnet.exe'
if (!$env:DOTNET_EXE -and $Version -eq '1.7' -and (Test-Path -LiteralPath $localSdk)) {
    $env:DOTNET_EXE = $localSdk
}
$sdk = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' }
Push-Location $root
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $frameworkBuild -Version $Version
    if ($LASTEXITCODE -ne 0) { throw 'REF build failed; Delta-V was not built.' }
    $reference = Join-Path $root "References/REF/$Version"
    New-Item -ItemType Directory -Force -Path $reference | Out-Null
    Copy-Item -LiteralPath "$framework/$Version/Assemblies/RimExodus.Maps.dll" -Destination $reference -Force
    # Runtime ownership is exclusively REF; remove only the former embedded framework DLL.
    foreach ($name in @('RimExodus.Maps.dll', 'RimExodus.Maps.deps.json')) {
        $embedded = Join-Path $root "$Version/Assemblies/$name"
        if (Test-Path -LiteralPath $embedded) { Remove-Item -LiteralPath $embedded -Force }
    }
    & $sdk build Source/DeltaV.csproj -c Release "-p:RimWorldVersion=$Version" -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Delta-V build failed ($LASTEXITCODE)." }
} finally { Pop-Location }
