$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$dotnet = Join-Path $repo '.local-tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet).Source }
& $dotnet run --project "$PSScriptRoot/FoundationTopologyRegression.csproj" -c Release
exit $LASTEXITCODE
