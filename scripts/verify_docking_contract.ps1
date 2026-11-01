param(
    [string]$GameRoot = (Resolve-Path "$PSScriptRoot/../../..").Path,
    [string]$MetadataAssembly = "$env:USERPROFILE/.nuget/packages/system.reflection.metadataloadcontext/8.0.0/lib/net8.0/System.Reflection.MetadataLoadContext.dll"
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/..").Path
Add-Type -Path $MetadataAssembly
$paths = [string[]]@((Get-ChildItem "$GameRoot/RimWorldWin64_Data/Managed" -Filter '*.dll').FullName)
$paths += "$repo/1.6/Assemblies/DeltaV.dll"
$resolver = [System.Reflection.PathAssemblyResolver]::new([string[]]$paths)
$context = [System.Reflection.MetadataLoadContext]::new($resolver, 'mscorlib')
try {
    $game = $context.LoadFromAssemblyPath("$GameRoot/RimWorldWin64_Data/Managed/Assembly-CSharp.dll")
    $mod = $context.LoadFromAssemblyPath("$repo/1.6/Assemblies/DeltaV.dll")
    $flags = [System.Reflection.BindingFlags]'Instance, Public, NonPublic'
    $targets = @(
        @('RimWorld.Designator_MoveGravship', 'SelectedUpdate', ''),
        @('RimWorld.Building_Door', 'DoorPreDraw', ''),
        @('Verse.Room', 'get_ExposedToSpace', ''),
        @('Verse.Room', 'get_Vacuum', ''),
        @('Verse.TerrainGrid', 'DoTerrainChangedEffects', 'c:Verse.IntVec3,oldTerr:Verse.TerrainDef,newTerr:Verse.TerrainDef,oldFoundation:Verse.TerrainDef'),
        @('Verse.TerrainGrid', 'RemoveGravshipTerrainUnsafe', 'cell:Verse.IntVec3,index:System.Int32'),
        @('Verse.Building', 'DeSpawn', 'mode:Verse.DestroyMode'),
        @('Verse.Thing', 'DeSpawn', 'mode:Verse.DestroyMode'),
        @('Verse.Game', 'set_CurrentMap', 'value:Verse.Map'),
        @('Verse.WorldComponent_GravshipController', 'LandingEnded', ''),
        @('RimWorld.Dialog_EnterPortal', 'TryAccept', '')
    )
    foreach ($target in $targets) {
        $method = $game.GetType($target[0], $true).GetMethod($target[1], $flags)
        if (!$method) { throw "Missing Harmony target: $($target[0]).$($target[1])" }
        $parameters = ($method.GetParameters() | ForEach-Object { $_.Name + ':' + $_.ParameterType.FullName }) -join ','
        if ($parameters -ne $target[2]) { throw "Signature changed: $method ($parameters)" }
        Write-Output "PASS target $($target[0]).$($target[1])"
    }
    $field = $game.GetType('Verse.WorldComponent_GravshipController').GetField('map', $flags)
    if ($field.FieldType.FullName -ne 'Verse.Map') { throw 'LandingEnded map binding changed' }
    $door = $game.GetType('RimWorld.Building_Door')
    foreach ($name in @('DeltaV.Building_DockingPort', 'DeltaV.Building_DockingChannel')) {
        if (!$door.IsAssignableFrom($mod.GetType($name, $true))) { throw "$name is not a vanilla door" }
    }
    function Check-Fields($node, [string]$typeName) {
        $type = $game.GetType($typeName, $false)
        if (!$type) { $type = $mod.GetType($typeName, $true) }
        foreach ($child in $node.ChildNodes) {
            if ($child.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
            if (!$type.GetField($child.Name, $flags)) { throw "Unknown XML field $typeName.$($child.Name)" }
        }
    }
    [xml]$defs = Get-Content -Raw "$repo/1.6/Defs/ThingDefs_Buildings/DeltaV_Docking.xml"
    foreach ($def in $defs.Defs.ThingDef) {
        Check-Fields $def 'Verse.ThingDef'
        Check-Fields $def.building 'RimWorld.BuildingProperties'
        Check-Fields $def.graphicData 'Verse.GraphicData'
        foreach ($comp in $def.comps.li) {
            if ($comp.Class -eq 'CompProperties_Hackable') { Check-Fields $comp 'RimWorld.CompProperties_Hackable' }
        }
    }
    [xml]$tractor = Get-Content -Raw "$repo/1.6/Defs/SpaceCombat/DeltaV_Tractor.xml"
    foreach ($def in $tractor.Defs.ThingDef) {
        Check-Fields $def 'Verse.ThingDef'
        Check-Fields $def.building 'RimWorld.BuildingProperties'
        Check-Fields $def.graphicData 'Verse.GraphicData'
        foreach ($comp in $def.comps.li) { Check-Fields $comp $comp.Class }
        foreach ($extension in $def.modExtensions.li) { Check-Fields $extension $extension.Class }
    }
    foreach ($impact in $tractor.Defs.'DeltaV.SpaceImpactEffectDef') {
        Check-Fields $impact 'DeltaV.SpaceImpactEffectDef'
        Check-Fields $impact.defenderEffect 'DeltaV.ImpactOutcomePart'
        Check-Fields $impact.attackerEffect 'DeltaV.ImpactOutcomePart'
    }
    [xml]$jobs = Get-Content -Raw "$repo/1.6/Defs/Misc/DeltaV_SoftDockJobs.xml"
    foreach ($job in $jobs.Defs.JobDef) {
        Check-Fields $job 'Verse.JobDef'
        if (!$game.GetType('Verse.AI.JobDriver').IsAssignableFrom($mod.GetType($job.driverClass, $true))) {
            throw "Invalid soft docking job driver: $($job.driverClass)"
        }
    }
    [xml]$portal = Get-Content -Raw "$repo/1.6/Defs/Misc/DeltaV_SoftDockPortal.xml"
    Check-Fields $portal.Defs.ThingDef 'Verse.ThingDef'
    Check-Fields $portal.Defs.ThingDef.building 'RimWorld.BuildingProperties'
    if (!$game.GetType('RimWorld.MapPortal').IsAssignableFrom($mod.GetType('DeltaV.SoftDockPortal', $true))) {
        throw 'Soft docking does not use the native MapPortal contract'
    }
    if ($mod.GetType('DeltaV.Dialog_SoftDockTransfer') -or $mod.GetType('DeltaV.JobDriver_EnterSoftDock')) {
        throw 'Duplicate transfer dialog/job remains in production'
    }
    [xml]$terrain = Get-Content -Raw "$repo/1.6/Defs/TerrainDefs/DeltaV_DockingChannelTerrain.xml"
    Check-Fields $terrain.Defs.TerrainDef 'Verse.TerrainDef'
    [xml]$en = Get-Content -Raw "$repo/1.6/Languages/English/Keyed/DeltaV.xml"
    [xml]$zh = Get-Content -Raw "$repo/1.6/Languages/ChineseSimplified/Keyed/DeltaV.xml"
    foreach ($key in @('DeltaV_DockConnected', 'DeltaV_DockFolded', 'DeltaV_DockRetractConfirm',
        'DeltaV_SyncButton', 'DeltaV_StartEVA', 'DeltaV_SettingsWorksiteMargin', 'DeltaV_EVAGravity', 'DeltaV_EVAOverlap',
        'DeltaV_SoftDockToggle', 'DeltaV_SoftDockEnter', 'DeltaV_SoftDockHaul', 'DeltaV_SoftDockHack',
        'DeltaV_SoftDockProgress', 'DeltaV_TractorModeCollect', 'DeltaV_TractorModeStructure', 'DeltaV_TractorModeDesc', 'DeltaV_TractorNeedsMoonPool', 'DeltaV_TractorBlocked',
        'DeltaV_SoftDockTransfer', 'DeltaV_SoftDockTransferDesc', 'DeltaV_SoftDockTransferCancel',
        'DeltaV_SoftDockTransferDisconnected', 'DeltaV_SoftDockTransferNoWorker')) {
        if (!$en.LanguageData.$key -or !$zh.LanguageData.$key) { throw "Missing translation: $key" }
    }
    Write-Output 'PASS XML fields, door inheritance, landing map binding and translations'
    Write-Output 'Metadata checks only; in-game Harmony installation, atmosphere and pathfinding still require verification.'
}
finally { $context.Dispose() }
