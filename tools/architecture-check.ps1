$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-NoMatch {
    param(
        [string]$Name,
        [string]$Pattern,
        [string[]]$Paths,
        [string[]]$Glob = @('*.cs')
    )
    $args = @('-n', '--pcre2')
    foreach ($item in $Glob) { $args += @('--glob', $item) }
    $args += $Pattern
    $args += $Paths
    $output = & rg @args 2>$null
    $exit = $LASTEXITCODE
    $output = @($output | Where-Object { $_ -notmatch '^.*:\d+:\s*//' })
    if ($output.Count -gt 0) {
        $failures.Add("[$Name]`n$($output -join "`n")")
    }
    elseif ($exit -notin @(0, 1)) {
        throw "rg failed while checking $Name (exit $exit)"
    }
}

# objects 是存档权威列表；只有 UniverseManager 的注册入口可改变它。
Assert-NoMatch 'direct universe object-list mutation' `
    '\.objects\s*\.\s*(Add|Remove|RemoveAll|Clear|Insert)\s*\(' `
    @('Source') @('*.cs', '!UniverseManager.cs')

# 当前内部图语义只能由 SpaceObject.InteriorMap 暴露，调用点不得重新拼 ternary。
Assert-NoMatch 'hand-written current interior-map selection' `
    '(docked\s*\?[^;\r\n]*dockedMap\s*:[^;\r\n]*map|dockedMap\s*!=\s*null\s*\?[^;\r\n]*dockedMap\s*:[^;\r\n]*map)' `
    @('Source') @('*.cs', '!ShipState.cs')

# 停放锚换算的原版私有语义只允许在 SpaceTransform 的唯一镜像中出现。
Assert-NoMatch 'parking-anchor conversion outside SpaceTransform' `
    '(PrefabUtility\.GetRoot\s*\(|\.engineToCenter\b)' `
    @('Source') @('*.cs', '!SpaceTransform.cs')

# 游戏状态积分必须服从 tick。生成预算与表现层不属于模拟；这里锁定三类业务目录。
Assert-NoMatch 'frame-clock simulation in Core/Interaction/Lifecycle' `
    'Time\.(deltaTime|unscaledDeltaTime|realtimeSinceStartup)' `
    @('Source/Core', 'Source/Interaction', 'Source/Lifecycle') @('*.cs')

# 休眠集合只由生命周期实例服务持有，调用点不得直接碰底层 dormancy 类型。
Assert-NoMatch 'dormancy implementation used outside lifecycle service' `
    '\bMapDormancyService\b' `
    @('Source') @('*.cs', '!MapDormancyService.cs', '!SpaceLifecycle.cs')

Assert-NoMatch 'map destruction outside lifecycle service' `
    '(PocketMapUtility\.DestroyPocketMap\s*\(|Current\.Game\.DeinitAndRemoveMap\s*\()' `
    @('Source') @('*.cs', '!SpaceLifecycle.cs')

Assert-NoMatch 'retired DockController facade' `
    '\bDockController\b' `
    @('Source')

# 已知允许的 static 可变状态按文件白名单维护；其余游戏态必须进入实例服务。
$staticAllowFiles = @(
    'Source/Core/DeltaVDefOf.cs', 'Source/Core/DeltaVMod.cs', 'Source/Core/OrbitSphere.cs',
    'Source/Debug/DebugActions_DeltaV.cs', 'Source/Harmony/Patch_GravshipLanding.cs',
    'Source/Harmony/Patch_KeyBindingSuppression.cs', 'Source/Harmony/Patch_SpaceEnvironment.cs',
    'Source/Interaction/BoardingPodFlight.cs', 'Source/Interaction/Designator_ReassignSubstructure.cs',
    'Source/Interaction/PilotController.cs', 'Source/Interaction/PilotKeyBridge.cs',
    'Source/Rendering/DockingGhostRenderer.cs',
    'Source/Rendering/MapOverlayRenderer.cs', 'Source/Rendering/PlanetGlobeRenderer.cs',
    'Source/Rendering/ShellRenderer.cs', 'Source/Rendering/ShipStatusPanel.cs',
    'Source/Rendering/ThrusterAudio.cs', 'Source/Rendering/ThrusterEffects.cs',
    'Source/Rendering/WorldShipMarkerRenderer.cs', 'Source/Buildings/ThrusterPower.cs',
    'Source/Lifecycle/SectorContentService.cs'
)
$mutableStatic = & rg -n --pcre2 --glob '*.cs' `
    '^\s*(public|internal|private|protected)?\s*static\s+(?!readonly\b|class\b|[\w<>?,.\[\]]+\s+\w+\s*\()[^;=]+(?:=|;)' Source 2>$null
if ($LASTEXITCODE -notin @(0, 1)) { throw "rg failed while checking mutable static state" }
foreach ($line in $mutableStatic) {
    if ($line -match '=>') { continue }
    $path = ($line -split ':', 2)[0].Replace('\', '/')
    if ($staticAllowFiles -notcontains $path) {
        $failures.Add("[unlisted mutable static state]`n$line")
    }
}

if ($failures.Count -gt 0) {
    Write-Error ("Architecture checks failed:`n`n" + ($failures -join "`n`n"))
}

Write-Host 'Architecture checks passed.'
