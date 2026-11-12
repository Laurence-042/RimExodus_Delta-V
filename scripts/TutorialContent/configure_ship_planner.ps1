param(
    [ValidateSet('1.6','1.7')][string]$Version = '1.6',
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [Parameter(Mandatory=$true)][string]$HarmonyPath,
    [Parameter(Mandatory=$true)][string]$SaveDataDirectory,
    [string]$Python
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$game = (Resolve-Path -LiteralPath $GameRoot).Path
$harmony = (Resolve-Path -LiteralPath $HarmonyPath).Path
$savedata = (Resolve-Path -LiteralPath $SaveDataDirectory).Path
if (!$Python) { $Python = (& py -3.14 -c "import sys; print(sys.executable)") }
if (!$Python) { throw 'Install Python 3.14 or pass the full path to python.exe.' }
$Python = (Resolve-Path -LiteralPath $Python).Path
$deps = Join-Path $repo 'Source/obj/TutorialContent/SolverDependencies'
$probe = @'
import sys
sys.path.insert(0, sys.argv[1])
try:
    import PIL, ortools
except ImportError:
    sys.exit(1)
sys.exit(ortools.__version__ != '9.15.6755')
'@
& $Python -c $probe $deps
if ($LASTEXITCODE) {
    & $Python -m pip install --target $deps -r "$PSScriptRoot/ship-search-requirements.txt" Pillow
    if ($LASTEXITCODE) { throw 'Could not install the local ship solver.' }
}
$output = Join-Path $repo "Source/obj/ShipPlannerWorker$Version"
& "$repo/scripts/TutorialRegression/run.ps1" -Version $Version -GameRoot $game -HarmonyPath $harmony -CompileOnly -OutputDirectory $output
if ($LASTEXITCODE) { throw 'Could not build the local motion worker.' }
$managed = if ($Version -eq '1.7') { $game } else { Join-Path $game 'RimWorldWin64_Data/Managed' }
$dotnet = if ($Version -eq '1.7') { "$repo/.local-tools/dotnet/dotnet.exe" } else { "$env:ProgramFiles/dotnet/dotnet.exe" }
$directory = Join-Path $savedata 'DeltaV'
New-Item -ItemType Directory -Force $directory | Out-Null
$config = [System.Xml.XmlDocument]::new()
$root = $config.AppendChild($config.CreateElement('ShipPlanner'))
foreach ($entry in @{
    python = $Python; pythonPath = $deps; worker = "$PSScriptRoot/generate_ships.py";
    dotnet = $dotnet; data = "$game/Data"; managed = $managed; native = "$output/TutorialRegression.dll"
}.GetEnumerator()) {
    $root.AppendChild($config.CreateElement($entry.Key)).InnerText = $entry.Value
}
$path = Join-Path $directory "ShipPlanner-$Version.xml"
$config.Save($path)
Write-Output "Configured the ship planner: $path"
