param([switch]$Alpha)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$parent = Split-Path $root -Parent
$targetName = if ($Alpha) { 'RimExodus_Delta-V.alpha.release' } else { 'RimExodus_Delta-V.release' }
$target = [IO.Path]::GetFullPath((Join-Path $parent $targetName))
if ((Split-Path $target -Parent) -ne $parent -or (Split-Path $target -Leaf) -ne $targetName) {
    throw "Unexpected release destination: $target"
}
if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Refusing to replace a linked release directory.'
}

$frameworkRoot = [IO.Path]::GetFullPath((Join-Path $parent 'RimExodusFramework'))
. (Join-Path $frameworkRoot 'build/ReleaseBuild.ps1')
$results = @()
foreach ($version in @('1.6', '1.7')) {
    $results += Invoke-ReleaseVersionBuild -ProjectRoot $root -Version $version `
        -AssemblyName 'DeltaV.dll' -BuildScript "$PSScriptRoot/Build.ps1" `
        -AdditionalInputDirs @((Join-Path $frameworkRoot 'Source')) `
        -AdditionalInputFiles @((Join-Path $frameworkRoot "$version/Assemblies/RimExodus.Maps.dll"), `
                                (Join-Path $frameworkRoot 'build/Build.ps1'))
}
if (@($results | Where-Object { !$_.Usable }).Count -gt 0) {
    throw 'Release aborted: both current game-version assemblies are required.'
}

$about = New-Object System.Xml.XmlDocument
$about.PreserveWhitespace = $true
$about.Load((Join-Path $root 'About/About.xml'))
if ($about.ModMetaData.packageId -ne 'RimExodus.DeltaV.dev') {
    throw "Unexpected development packageId: $($about.ModMetaData.packageId)"
}
$about.ModMetaData.name = 'RimExodus: Delta-V'
$about.ModMetaData.packageId = 'RimExodus.DeltaV'
$framework = $about.ModMetaData.modDependencies.SelectSingleNode("li[packageId='RimExodus.Framework.dev']")
if ($null -eq $framework) { throw 'Development REF dependency missing.' }
$framework.packageId = 'RimExodus.Framework'
$framework.displayName = 'RimExodus Framework'
if ($Alpha) {
    $about.ModMetaData.name = 'RimExodus: Delta-V [ALPHA]'
    $about.ModMetaData.packageId = 'RimExodus.DeltaV.alpha'
    $workshopUrl = $about.CreateElement('steamWorkshopUrl')
    $workshopUrl.InnerText = 'steam://url/CommunityFilePage/3830299150'
    $framework.AppendChild($workshopUrl) | Out-Null
}
$after = $about.ModMetaData.loadAfter.SelectSingleNode("li[.='RimExodus.Framework.dev']")
if ($null -eq $after) { throw 'Development REF load order entry missing.' }
$after.InnerText = 'RimExodus.Framework'
if (($about.ModMetaData.supportedVersions.li -join ',') -ne '1.7') {
    throw 'Release metadata must declare 1.7 as the only officially supported version.'
}

$publishedFileId = Join-Path $target 'About/PublishedFileId.txt'
$alphaWorkshopId = $null
if ($Alpha -and (Test-Path -LiteralPath $publishedFileId -PathType Leaf)) {
    $alphaWorkshopId = [IO.File]::ReadAllBytes($publishedFileId)
}
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $target 'About') -Force | Out-Null
if ($null -ne $alphaWorkshopId) {
    [IO.File]::WriteAllBytes($publishedFileId, $alphaWorkshopId)
}
$about.Save((Join-Path $target 'About/About.xml'))
foreach ($version in @('1.6', '1.7')) {
    Copy-Item -LiteralPath (Join-Path $root $version) -Destination (Join-Path $target $version) -Recurse
    $assemblyDir = Join-Path $target "$version/Assemblies"
    Get-ChildItem -LiteralPath $assemblyDir -File | Where-Object { $_.Name -ne 'DeltaV.dll' } | Remove-Item -Force
}
Copy-Item -LiteralPath (Join-Path $root 'Common') -Destination (Join-Path $target 'Common') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $target
Copy-Item -LiteralPath (Join-Path $root 'LoadFolders.xml') -Destination $target
Write-Output "Packaged $($about.ModMetaData.name) 1.6 and 1.7 at $target. Install RimExodus Framework separately."
