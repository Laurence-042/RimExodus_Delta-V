param(
    [switch]$Quick,
    [switch]$TestOnly,
    [int]$Events = 0,
    [int]$Repeats = 0,
    [int]$Seed = 20261101,
    [string]$Scenario,
    [string]$Output = "$PSScriptRoot/results/latest"
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$reference = Join-Path $repo 'references/TopologyLab/GridTopology.cs'
if (!(Test-Path -LiteralPath $reference)) { throw "Required supplied reference missing: $reference" }
$dotnet = Join-Path $repo '.local-tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet).Source }
& $dotnet build "$PSScriptRoot/TopologyBenchmark.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$oldTiering = $env:DOTNET_TieredCompilation
$oldCpu = $env:TOPOLOGY_BENCH_CPU
try {
    $env:DOTNET_TieredCompilation = '0'
    $env:TOPOLOGY_BENCH_CPU = (Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name) -join '; '
    $arguments = @('--seed', $Seed, '--output', $Output, '--reference', $reference)
    if ($Quick) { $arguments += '--quick' }
    if ($TestOnly) { $arguments += '--test-only' }
    if ($Events -gt 0) { $arguments += @('--events', $Events) }
    if ($Repeats -gt 0) { $arguments += @('--repeats', $Repeats) }
    if ($Scenario) { $arguments += @('--scenario', $Scenario) }
    & $dotnet "$PSScriptRoot/bin/Release/net10.0/TopologyBenchmark.dll" @arguments
    $result = $LASTEXITCODE
} finally {
    $env:DOTNET_TieredCompilation = $oldTiering
    $env:TOPOLOGY_BENCH_CPU = $oldCpu
}
exit $result
