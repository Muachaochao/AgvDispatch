[CmdletBinding()]
param(
    [string]$MapPath,

    [string]$PlanPath,

    [string]$SimulationBaseUri,

    [string]$BaseUri = 'http://127.0.0.1:10001',

    [string]$OutputPath,

    [string]$ReportPath,

    [double]$DefaultStrokeWidthMeters = 0.8,

    [double]$VehicleWidthMeters = 0.52,

    [double]$VehicleLengthMeters = 0.8,

    [bool]$InvertY = $true,

    [switch]$Send,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-JsonFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Input file not found: $Path"
    }

    [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json
}

function Get-PropertyValue {
    param(
        [object]$InputObject,
        [string]$Name,
        [object]$Default = $null
    )

    if ($null -eq $InputObject) {
        return $Default
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $Default
    }

    return $property.Value
}

function New-StableGuid {
    param([string]$Identity)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Identity))
        $bytes = [byte[]]::new(16)
        [Array]::Copy($hash, 0, $bytes, 0, 16)
        # RFC 4122 version 5-style marker; deterministic namespace UUID.
        $bytes[6] = [byte](($bytes[6] -band 0x0f) -bor 0x50)
        $bytes[8] = [byte](($bytes[8] -band 0x3f) -bor 0x80)
        [guid]::new($bytes).ToString()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-FileSha256 {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([string]$Text)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))
        ([BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-EdgeAngle {
    param(
        [object]$Edge,
        [hashtable]$NodeById,
        [double]$YFactor = 1.0
    )

    $from = $NodeById[[string](Get-PropertyValue $Edge 'from' '')]
    $to = $NodeById[[string](Get-PropertyValue $Edge 'to' '')]
    if ($null -eq $from -or $null -eq $to) {
        return 0.0
    }

    $targetX = [double](Get-PropertyValue $to 'x' 0)
    $targetY = [double](Get-PropertyValue $to 'y' 0)
    $controlPoints = @(Get-PropertyValue $Edge 'controlPoints' @())
    if ($controlPoints.Count -gt 0) {
        $targetX = [double](Get-PropertyValue $controlPoints[0] 'x' $targetX)
        $targetY = [double](Get-PropertyValue $controlPoints[0] 'y' $targetY)
    }

    $deltaX = $targetX - [double](Get-PropertyValue $from 'x' 0)
    $deltaY = $targetY - [double](Get-PropertyValue $from 'y' 0)
    if ([Math]::Abs($deltaX) -lt 1e-9 -and [Math]::Abs($deltaY) -lt 1e-9) {
        return 0.0
    }

    [Math]::Atan2($deltaY * $YFactor, $deltaX)
}

function Convert-ToProfControlPoint {
    param(
        [object]$Point,
        [double]$YFactor
    )

    [ordered]@{
        x = [double](Get-PropertyValue $Point 'x' 0)
        y = [double](Get-PropertyValue $Point 'y' 0) * $YFactor
    }
}

if (-not [string]::IsNullOrWhiteSpace($SimulationBaseUri)) {
    $simulationStatus = Invoke-RestMethod `
        -Uri "$($SimulationBaseUri.TrimEnd('/'))/api/simulation" `
        -Method Get `
        -TimeoutSec 30
    if (-not [bool](Get-PropertyValue $simulationStatus 'enabled' $false)) {
        throw 'The selected WebHost does not expose an enabled isolated simulation.'
    }

    $map = Get-PropertyValue $simulationStatus 'map' $null
    if ($null -eq $map -or @(Get-PropertyValue $map 'nodes' @()).Count -eq 0) {
        throw 'The isolated simulation did not return a map with nodes.'
    }

    $configuration = Get-PropertyValue $simulationStatus 'configuration' $null
    $mapIdentity = Get-PropertyValue $configuration 'mapIdentity' $null
    $configuredVehicles = @(Get-PropertyValue $configuration 'vehicles' @())
    $planVehicles = foreach ($configuredVehicle in $configuredVehicles) {
        $vehicleId = [string](Get-PropertyValue $configuredVehicle 'id' '')
        $initialSite = [string](Get-PropertyValue $configuredVehicle 'initialSite' '')
        $startNodes = @((Get-PropertyValue $map 'nodes' @()) | Where-Object {
            [string](Get-PropertyValue $_ 'id' '') -eq $initialSite -or
            [string](Get-PropertyValue $_ 'agvSiteId' '') -eq $initialSite
        })
        if ([string]::IsNullOrWhiteSpace($vehicleId) -or $startNodes.Count -eq 0) {
            throw "Simulation vehicle $vehicleId cannot be mapped to initial site $initialSite."
        }
        [pscustomobject]([ordered]@{
            id         = $vehicleId
            startNode  = [string](Get-PropertyValue $startNodes[0] 'id' '')
            enabled    = $true
            capability = 'AGV'
        })
    }

    $plan = [pscustomobject]([ordered]@{
        name       = [string](Get-PropertyValue $configuration 'mapName' (Get-PropertyValue $map 'name' 'Simulation'))
        mapId      = [string](Get-PropertyValue $mapIdentity 'mapId' '')
        mapVersion = [string](Get-PropertyValue $mapIdentity 'mapVersionId' '')
        vehicles   = @($planVehicles)
    })
    $mapFile = "$($SimulationBaseUri.TrimEnd('/'))/api/simulation#map"
    $planFile = "$($SimulationBaseUri.TrimEnd('/'))/api/simulation#configuration"
    $mapSourceSha256 = Get-TextSha256 ($map | ConvertTo-Json -Depth 100 -Compress)
    $planSourceSha256 = Get-TextSha256 ($plan | ConvertTo-Json -Depth 100 -Compress)
}
else {
    if ([string]::IsNullOrWhiteSpace($MapPath) -or [string]::IsNullOrWhiteSpace($PlanPath)) {
        throw 'Provide -SimulationBaseUri, or provide both -MapPath and -PlanPath.'
    }
    $mapFile = (Resolve-Path -LiteralPath $MapPath).Path
    $planFile = (Resolve-Path -LiteralPath $PlanPath).Path
    $map = Read-JsonFile -Path $mapFile
    $planDocument = Read-JsonFile -Path $planFile
    $plan = Get-PropertyValue $planDocument 'plan' $planDocument
    $mapSourceSha256 = Get-FileSha256 -Path $mapFile
    $planSourceSha256 = Get-FileSha256 -Path $planFile
}

$sourceNodes = @(Get-PropertyValue $map 'nodes' @())
$sourceEdges = @(Get-PropertyValue $map 'edges' @())
$sourceResources = @(Get-PropertyValue $map 'resources' @())
$sourceReferencePoints = @((Get-PropertyValue $map 'areaAnnotations' @()) | Where-Object {
    [string](Get-PropertyValue $_ 'sourceType' '') -eq 'CadReferencePoint' -and
    @(Get-PropertyValue $_ 'boundary' @()).Count -ge 3
})
$sourceAreaLabels = @((Get-PropertyValue $map 'areaAnnotations' @()) | Where-Object {
    [string](Get-PropertyValue $_ 'sourceType' '') -eq 'CadSimulationEvidence' -and
    -not [string]::IsNullOrWhiteSpace([string](Get-PropertyValue $_ 'text' '')) -and
    @(Get-PropertyValue $_ 'boundary' @()).Count -ge 3
})
$sourceVehicles = @(Get-PropertyValue $plan 'vehicles' @())
$mapId = [string](Get-PropertyValue $plan 'mapId' (Get-PropertyValue $map 'name' 'current-project'))
$mapVersion = [string](Get-PropertyValue $plan 'mapVersion' '')
$yFactor = if ($InvertY) { -1.0 } else { 1.0 }

if ($sourceNodes.Count -eq 0) {
    throw 'The current map has no convertible navigation nodes.'
}

$nodeById = @{}
foreach ($node in $sourceNodes) {
    $nodeId = [string](Get-PropertyValue $node 'id' '')
    if ([string]::IsNullOrWhiteSpace($nodeId)) {
        throw 'The map contains a navigation node without an id.'
    }
    if ($nodeById.ContainsKey($nodeId)) {
        throw "Duplicate map node: $nodeId"
    }
    $nodeById[$nodeId] = $node
}

$networkGuid = New-StableGuid "profcontrol/network/$mapId/$mapVersion"
$loadAreaName = "VehicleLoadArea"
$loadCargoName = "Payload"
$payload = [ordered]@{
    networks = @(
        [ordered]@{
            name      = [string](Get-PropertyValue $plan 'name' (Get-PropertyValue $map 'name' 'Current project'))
            guid      = $networkGuid
            floor     = ''
        }
    )
    layers      = @()
    agvTypes    = @()
    agvs        = @()
    stations    = @()
    lines       = @()
    arcs        = @()
    beziers     = @()
    pictures    = @()
    texts       = @()
    cargoAreaDefines = @(
        [ordered]@{
            name   = $loadAreaName
            width  = $VehicleWidthMeters
            length = $VehicleLengthMeters
            height = 0.5
        }
    )
    cargoInfos       = @(
        [ordered]@{ name = $loadCargoName }
    )
    containerInfos   = @()
}

foreach ($node in $sourceNodes) {
    if (-not [bool](Get-PropertyValue $node 'enabled' $true)) {
        continue
    }

    $nodeId = [string](Get-PropertyValue $node 'id' '')
    $siteId = [string](Get-PropertyValue $node 'agvSiteId' $nodeId)
    $payload.stations += [ordered]@{
        guid        = New-StableGuid "profcontrol/station/$mapId/$nodeId"
        networkGuid = $networkGuid
        layerName   = (-join @([char]0x7AD9, [char]0x70B9, [char]0x5C42))
        mark        = if ([string]::IsNullOrWhiteSpace($siteId)) { $nodeId } else { $siteId }
        position    = Convert-ToProfControlPoint -Point $node -YFactor $yFactor
        cargoAreas  = @()
    }
}

foreach ($referencePoint in $sourceReferencePoints) {
    $referenceId = [string](Get-PropertyValue $referencePoint 'id' '')
    $boundary = @(Get-PropertyValue $referencePoint 'boundary' @())
    if ([string]::IsNullOrWhiteSpace($referenceId) -or $boundary.Count -lt 3) {
        continue
    }
    $point = [ordered]@{
        x = ($boundary | Measure-Object -Property x -Average).Average
        y = ($boundary | Measure-Object -Property y -Average).Average
    }
    $payload.stations += [ordered]@{
        guid        = New-StableGuid "profcontrol/cad-reference/$mapId/$referenceId"
        networkGuid = $networkGuid
        layerName   = (-join @([char]0x53C2, [char]0x8003, [char]0x70B9, [char]0x5C42))
        mark        = [string](Get-PropertyValue $referencePoint 'text' $referenceId)
        position    = Convert-ToProfControlPoint -Point $point -YFactor $yFactor
        cargoAreas  = @()
    }
}

foreach ($areaLabel in $sourceAreaLabels) {
    $labelId = [string](Get-PropertyValue $areaLabel 'id' '')
    $boundary = @(Get-PropertyValue $areaLabel 'boundary' @())
    if ([string]::IsNullOrWhiteSpace($labelId) -or $boundary.Count -lt 3) {
        continue
    }
    $point = [ordered]@{
        x = ($boundary | Measure-Object -Property x -Average).Average
        y = ($boundary | Measure-Object -Property y -Average).Average
    }
    $payload.stations += [ordered]@{
        guid        = New-StableGuid "profcontrol/area-label/$mapId/$labelId"
        networkGuid = $networkGuid
        layerName   = (-join @([char]0x533A, [char]0x57DF, [char]0x6807, [char]0x6CE8, [char]0x5C42))
        mark        = [string](Get-PropertyValue $areaLabel 'text' $labelId)
        position    = Convert-ToProfControlPoint -Point $point -YFactor $yFactor
        cargoAreas  = @()
    }
}

foreach ($vehicle in $sourceVehicles) {
    if (-not [bool](Get-PropertyValue $vehicle 'enabled' $true)) {
        continue
    }

    $vehicleId = [string](Get-PropertyValue $vehicle 'id' '')
    $startNodeId = [string](Get-PropertyValue $vehicle 'startNode' '')
    if ([string]::IsNullOrWhiteSpace($vehicleId) -or -not $nodeById.ContainsKey($startNodeId)) {
        throw "The start node $startNodeId for vehicle $vehicleId is not in the map."
    }

    $startNode = $nodeById[$startNodeId]
    $angleEdge = $sourceEdges |
        Where-Object { [string](Get-PropertyValue $_ 'from' '') -eq $startNodeId -and [bool](Get-PropertyValue $_ 'enabled' $true) } |
        Select-Object -First 1

    $payload.agvs += [ordered]@{
        guid        = New-StableGuid "profcontrol/agv/$mapId/$vehicleId"
        networkGuid = $networkGuid
        layerName   = (-join @([char]0x8BBE, [char]0x5907, [char]0x5C42))
        name        = $vehicleId
        position    = Convert-ToProfControlPoint -Point $startNode -YFactor $yFactor
        angle       = if ($null -eq $angleEdge) { 0.0 } else { Get-EdgeAngle -Edge $angleEdge -NodeById $nodeById -YFactor $yFactor }
        width       = $VehicleWidthMeters
        length      = $VehicleLengthMeters
        type        = [string](Get-PropertyValue $vehicle 'capability' 'AGV')
        cargoAreas  = @(
            [ordered]@{
                name       = $loadAreaName
                cargos     = @()
                containers = @()
            }
        )
    }
}

$unsupportedDirectionEdges = 0
$unsupportedResourceCount = $sourceResources.Count
$skippedEdges = @()

foreach ($edge in $sourceEdges) {
    if (-not [bool](Get-PropertyValue $edge 'enabled' $true)) {
        continue
    }

    $edgeId = [string](Get-PropertyValue $edge 'id' '')
    $fromId = [string](Get-PropertyValue $edge 'from' '')
    $toId = [string](Get-PropertyValue $edge 'to' '')
    if (-not $nodeById.ContainsKey($fromId) -or -not $nodeById.ContainsKey($toId)) {
        $skippedEdges += $edgeId
        continue
    }

    if (-not [bool](Get-PropertyValue $edge 'bidirectional' $false)) {
        $unsupportedDirectionEdges++
    }

    $from = $nodeById[$fromId]
    $to = $nodeById[$toId]
    $base = [ordered]@{
        guid        = New-StableGuid "profcontrol/path/$mapId/$edgeId"
        networkGuid = $networkGuid
        layerName   = (-join @([char]0x7EBF, [char]0x8DEF, [char]0x5C42))
        name        = $edgeId
        strokeWidth = $DefaultStrokeWidthMeters
    }

    $geometryKind = [string](Get-PropertyValue $edge 'geometryKind' 'Line')
    $controlPoints = @(Get-PropertyValue $edge 'controlPoints' @())
    if ($geometryKind -eq 'CubicBezier' -and $controlPoints.Count -eq 2) {
        $payload.beziers += [ordered]@{
            guid        = $base.guid
            networkGuid = $base.networkGuid
            layerName   = $base.layerName
            name        = $base.name
            strokeWidth = $base.strokeWidth
            p1          = Convert-ToProfControlPoint -Point $from -YFactor $yFactor
            c1          = Convert-ToProfControlPoint -Point $controlPoints[0] -YFactor $yFactor
            c2          = Convert-ToProfControlPoint -Point $controlPoints[1] -YFactor $yFactor
            p2          = Convert-ToProfControlPoint -Point $to -YFactor $yFactor
        }
    }
    else {
        $payload.lines += [ordered]@{
            guid        = $base.guid
            networkGuid = $base.networkGuid
            layerName   = $base.layerName
            name        = $base.name
            strokeWidth = $base.strokeWidth
            p1          = Convert-ToProfControlPoint -Point $from -YFactor $yFactor
            p2          = Convert-ToProfControlPoint -Point $to -YFactor $yFactor
        }
    }
}

function Get-ProfControlMap {
    param([string]$Uri)
    Invoke-RestMethod -Uri "$($Uri.TrimEnd('/'))/apiv2/getInfo/getMap" -Method Get -TimeoutSec 10
}

if ($Send) {
    $currentMap = Get-ProfControlMap -Uri $BaseUri
    $currentObjectCount =
        @($currentMap.agvs).Count +
        @($currentMap.stations).Count +
        @($currentMap.lines).Count +
        @($currentMap.arcs).Count +
        @($currentMap.beziers).Count

    $alreadyContainsNetwork = @($currentMap.networks) |
        Where-Object { [string](Get-PropertyValue $_ 'guid' '') -eq $networkGuid }

    if ($currentObjectCount -gt 0 -or $null -ne $alreadyContainsNetwork) {
        throw "The target ProfControl instance is not an empty map (object count: $currentObjectCount). The setMap API appends objects and cannot replace a map, even with -Force. Use a clean display instance."
    }
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $artifactRoot = Join-Path $PSScriptRoot '..\..\artifacts\profcontrol-bridge'
    $OutputPath = Join-Path $artifactRoot 'current-project-setmap.json'
}

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$payloadText = $payload | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($outputFullPath, $payloadText, [Text.UTF8Encoding]::new($false))
$payloadHash = Get-FileSha256 -Path $outputFullPath

$report = [ordered]@{
    generatedAt             = [DateTimeOffset]::Now.ToString('o')
    sourceMap               = $mapFile
    sourceMapSha256         = $mapSourceSha256
    sourcePlan              = $planFile
    sourcePlanSha256        = $planSourceSha256
    mapId                   = $mapId
    mapVersion              = $mapVersion
    networkGuid             = $networkGuid
    outputPayload           = $outputFullPath
    outputPayloadSha256     = $payloadHash
    vehicleDimensionsMeters = [ordered]@{
        width  = $VehicleWidthMeters
        length = $VehicleLengthMeters
    }
    axisTransform           = [ordered]@{
        invertY = $InvertY
    }
    converted               = [ordered]@{
        stations = $payload.stations.Count
        vehicles = $payload.agvs.Count
        lines     = $payload.lines.Count
        beziers   = $payload.beziers.Count
        arcs      = $payload.arcs.Count
    }
    sourceCounts            = [ordered]@{
        nodes     = $sourceNodes.Count
        edges     = $sourceEdges.Count
        resources = $sourceResources.Count
        cadReferencePoints = $sourceReferencePoints.Count
        cadAreaLabels = $sourceAreaLabels.Count
        vehicles  = $sourceVehicles.Count
    }
    unsupported             = [ordered]@{
        trafficResourcePolygons = $unsupportedResourceCount
        nonBidirectionalEdges   = $unsupportedDirectionEdges
        edgesWithMissingNode    = $skippedEdges.Count
    }
    warnings                = @(
        'The ProfControl setMap API does not carry traffic-resource polygons; capacities and boundaries must be checked separately.',
        'The ProfControl setMap API does not carry path direction; one-way and bidirectional flags are not written to the target map.',
        'CAD deployment reference markers are display-only stations; they are not dispatch destinations or proof of an AGV route.',
        'Only the map, stations and vehicle start positions are migrated. PLC signals, the production dispatcher and task protocols are out of scope.'
    )
    sent                    = $false
    target                  = $BaseUri
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = [IO.Path]::ChangeExtension($outputFullPath, '.report.json')
}
$reportFullPath = [IO.Path]::GetFullPath($ReportPath)

if ($Send) {
    $response = Invoke-RestMethod `
        -Uri "$($BaseUri.TrimEnd('/'))/apiv2/setInfo/setMap" `
        -Method Post `
        -ContentType 'application/json; charset=utf-8' `
        -Body ([IO.File]::ReadAllText($outputFullPath)) `
        -TimeoutSec 30

    $status = [int](Get-PropertyValue $response 'status' 0)
    if ($status -ne 0 -and $status -ne 200) {
        throw "ProfControl rejected the map: $($response | ConvertTo-Json -Depth 10 -Compress)"
    }

    Start-Sleep -Milliseconds 800
    $targetMap = Get-ProfControlMap -Uri $BaseUri
    $verified = [ordered]@{
        network = @($targetMap.networks | Where-Object { [string](Get-PropertyValue $_ 'guid' '') -eq $networkGuid }).Count
        stations = @($targetMap.stations | Where-Object { [string](Get-PropertyValue $_ 'networkGuid' '') -eq $networkGuid }).Count
        vehicles = @($targetMap.agvs | Where-Object { [string](Get-PropertyValue $_ 'networkGuid' '') -eq $networkGuid }).Count
        lines = @($targetMap.lines | Where-Object { [string](Get-PropertyValue $_ 'networkGuid' '') -eq $networkGuid }).Count
        beziers = @($targetMap.beziers | Where-Object { [string](Get-PropertyValue $_ 'networkGuid' '') -eq $networkGuid }).Count
    }
    $targetVehicles = @($targetMap.agvs | Where-Object {
        [string](Get-PropertyValue $_ 'networkGuid' '') -eq $networkGuid
    })
    $dimensionMismatch = @($targetVehicles | Where-Object {
        [Math]::Abs([double](Get-PropertyValue $_ 'width' 0) - $VehicleWidthMeters) -gt 0.000001 -or
        [Math]::Abs([double](Get-PropertyValue $_ 'length' 0) - $VehicleLengthMeters) -gt 0.000001
    })

    if ($verified.network -ne 1 -or
        $verified.stations -ne $payload.stations.Count -or
        $verified.vehicles -ne $payload.agvs.Count -or
        $verified.lines -ne $payload.lines.Count -or
        $verified.beziers -ne $payload.beziers.Count -or
        $dimensionMismatch.Count -ne 0) {
        throw "Map readback validation mismatch: $($verified | ConvertTo-Json -Compress)"
    }

    $report.sent = $true
    $report.response = $response
    $report.verified = $verified
}

$report.outputReport = $reportFullPath
$reportText = $report | ConvertTo-Json -Depth 20
$reportDirectory = Split-Path -Parent $reportFullPath
if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
}
[IO.File]::WriteAllText($reportFullPath, $reportText, [Text.UTF8Encoding]::new($false))

$reportText
