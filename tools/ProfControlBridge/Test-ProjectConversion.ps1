$ErrorActionPreference = 'Stop'
$folder = Join-Path ([IO.Path]::GetTempPath()) ('agv-display-map-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $folder
try {
    $map = @{
        name='test'; nodes=@(
            @{id='A'; agvSiteId='LM1'; x=1; y=2; enabled=$true},
            @{id='B'; agvSiteId='LM2'; x=1; y=5; enabled=$true})
        edges=@(@{id='AB'; from='A'; to='B'; enabled=$true; geometryKind='Line'; bidirectional=$false})
        resources=@()
        areaAnnotations=@(
            @{id='REF'; sourceType='CadReferencePoint'; text='reference'; boundary=@(@{x=0;y=0},@{x=1;y=0},@{x=0;y=1})},
            @{id='AREA'; sourceType='CadSimulationEvidence'; text='area'; boundary=@(@{x=0;y=0},@{x=2;y=0},@{x=0;y=2})})
    }
    $plan = @{name='test'; mapId='M'; mapVersion='V'; vehicles=@(@{id='CAR'; startNode='A'; enabled=$true})}
    $mapPath = Join-Path $folder 'map.json'
    $planPath = Join-Path $folder 'plan.json'
    $map | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $mapPath -Encoding UTF8
    $plan | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $planPath -Encoding UTF8
    $payloadPath = Join-Path $folder 'payload.json'
    $reportPath = Join-Path $folder 'report.json'
    $null = & (Join-Path $PSScriptRoot 'Convert-ProjectToProfControl.ps1') -MapPath $mapPath -PlanPath $planPath -OutputPath $payloadPath -ReportPath $reportPath
    $first = Get-Content -LiteralPath $payloadPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($first.stations.Count -ne 2 -or $first.texts.Count -ne 2 -or $first.agvs.Count -ne 1 -or $first.lines.Count -ne 1) { throw 'Incomplete display map or captions exported as stations.' }
    if ($first.agvs[0].length -ne .8 -or $first.agvs[0].width -ne .52) { throw 'Vehicle dimensions reversed.' }
    if ($first.agvs[0].position.x -ne 1 -or $first.agvs[0].position.y -ne -2) { throw 'Axis reflection incorrect.' }
    if ([Math]::Abs($first.agvs[0].angle + [Math]::PI/2) -gt .000001) { throw 'Initial heading must be mirrored radians.' }
    $reference = $first.texts | Where-Object name -eq 'reference'
    if ($reference.layerName -ne (-join @([char]0x53C2,[char]0x8003,[char]0x70B9,[char]0x5C42))) { throw 'Reference layer lost.' }
    if ([Math]::Abs($reference.position.x - 1.0/3) -gt .000001 -or [Math]::Abs($reference.position.y + 1.0/3) -gt .000001) { throw 'OrderedDictionary reference point collapsed to origin.' }
    $null = & (Join-Path $PSScriptRoot 'Convert-ProjectToProfControl.ps1') -MapPath $mapPath -PlanPath $planPath -OutputPath $payloadPath -ReportPath $reportPath
    $second = Get-Content -LiteralPath $payloadPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($first.agvs[0].guid -ne $second.agvs[0].guid -or $first.networks[0].guid -ne $second.networks[0].guid) { throw 'Display identity changed.' }
    $script:postCount = 0
    function Invoke-RestMethod {
        param($Uri, $Method, $TimeoutSec, $ContentType, $Body)
        if ($Method -eq 'Post') { $script:postCount++; throw 'Unexpected target mutation.' }
        @{agvs=@(@{name='Existing car'}); stations=@(); lines=@(); arcs=@(); beziers=@(); networks=@()}
    }
    $rejected = $false
    try {
        $null = & (Join-Path $PSScriptRoot 'Convert-ProjectToProfControl.ps1') -MapPath $mapPath -PlanPath $planPath -OutputPath $payloadPath -ReportPath $reportPath -Send -Force
    }
    catch { if ($_.Exception.Message -match 'appends objects') { $rejected = $true } else { throw } }
    if (-not $rejected -or $script:postCount -ne 0) { throw 'Nonempty map must reject even Force before any target write.' }
    Write-Host 'PASS display conversion: markers, area layers, dimensions, axis and stable identities; no target writes.'
}
finally {
    $resolved = (Resolve-Path -LiteralPath $folder).ProviderPath
    $parent = [IO.Path]::GetDirectoryName($resolved).TrimEnd('\')
    if (-not $parent.Equals([IO.Path]::GetTempPath().TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('agv-display-map-')) { throw 'Unexpected temporary cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
