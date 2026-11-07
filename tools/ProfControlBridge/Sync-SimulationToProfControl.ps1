[CmdletBinding()]
param(
    [string]$SimulationBaseUri = 'http://127.0.0.1:5199',

    [string]$ProfControlBaseUri = 'http://127.0.0.1:10001',

    [string]$ProfControlUserName = 'admin',

    [string]$ProfControlPassword = 'admin',

    [int]$PollMilliseconds = 500,

    [int]$DurationSeconds = 0,

    [double]$ProfControlCoordinateScale = 0.01,

    [bool]$InvertY = $true,

    [string]$EvidenceDirectory,

    [bool]$CaptureEvidence = $true,

    [switch]$ExitOnScenarioComplete,

    [string]$InputPath,

    [switch]$Once,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

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

function Join-CodePoints {
    param([int[]]$CodePoints)
    -join ($CodePoints | ForEach-Object { [char]$_ })
}

function ConvertTo-InvariantNumber {
    param([double]$Value)
    $Value.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
}

function New-JsonHttpClient {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseCookies = $true
    $handler.CookieContainer = New-Object System.Net.CookieContainer
    $handler.AllowAutoRedirect = $true
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $client
}

function Invoke-JsonRequest {
    param(
        [System.Net.Http.HttpClient]$Client,
        [string]$Uri,
        [string]$Method = 'Get',
        [object]$Body = $null
    )

    $content = $null
    if ($Method -eq 'Post') {
        $bodyText = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 20 -Compress }
        $content = New-Object System.Net.Http.StringContent(
            $bodyText,
            [Text.Encoding]::UTF8,
            'application/json'
        )
    }

    $response = $null
    try {
        if ($Method -eq 'Post') {
            $response = $Client.PostAsync($Uri, $content).GetAwaiter().GetResult()
        }
        else {
            $response = $Client.GetAsync($Uri).GetAwaiter().GetResult()
        }
        $responseText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "HTTP $([int]$response.StatusCode): $responseText"
        }
        if ([string]::IsNullOrWhiteSpace($responseText)) {
            return $null
        }
        $responseText | ConvertFrom-Json
    }
    finally {
        if ($null -ne $content) {
            $content.Dispose()
        }
        if ($null -ne $response) {
            $response.Dispose()
        }
    }
}

function Read-Status {
    if (-not [string]::IsNullOrWhiteSpace($InputPath)) {
        $document = [IO.File]::ReadAllText($InputPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($null -ne (Get-PropertyValue $document 'device' $null)) {
            return $document
        }
        return [pscustomobject]@{
            enabled = $true
            running = $true
            fresh   = $true
            device  = $document
        }
    }

    $base = $SimulationBaseUri.TrimEnd('/')
    try {
        # The live endpoint contains only device telemetry and a compact
        # scenario summary. It avoids rebuilding the map, PLC catalog,
        # guidance and history payload on every animation frame.
        return Invoke-JsonRequest -Client $simulationClient -Uri "$base/api/simulation/live"
    }
    catch {
        $liveError = $_.Exception.Message
        try {
            # Compatibility with WebHost versions deployed before the live
            # endpoint was introduced.
            return Invoke-JsonRequest -Client $simulationClient -Uri "$base/api/simulation"
        }
        catch {
            throw "仿真实时状态读取失败。轻量接口：$liveError；兼容接口：$($_.Exception.Message)"
        }
    }
}

function Get-TaskLabel {
    param(
        [string]$VehicleId,
        [object]$Car
    )

    $control = Get-PropertyValue $Car 'simulationControl' $null
    if ([bool](Get-PropertyValue $control 'offline' $false)) {
        return $VehicleId + ' ' + (Join-CodePoints @(0x00B7)) + ' ' + (Join-CodePoints @(0x79BB, 0x7EBF))
    }

    $taskId = [string](Get-PropertyValue $Car 'current_task' '')
    $waitReason = [string](Get-PropertyValue $Car 'waitReason' '')
    $loaded = [bool](Get-PropertyValue $Car 'loaded' $false)

    $status = if (-not [string]::IsNullOrWhiteSpace($waitReason)) {
        Join-CodePoints @(0x7B49, 0x5F85)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($taskId) -and $loaded) {
        (Join-CodePoints @(0x8F7D, 0x8D27)) + (Join-CodePoints @(0x6267, 0x884C))
    }
    elseif (-not [string]::IsNullOrWhiteSpace($taskId)) {
        Join-CodePoints @(0x6267, 0x884C, 0x4EFB, 0x52A1)
    }
    elseif ($loaded) {
        Join-CodePoints @(0x8F7D, 0x8D27)
    }
    else {
        return $VehicleId
    }

    $VehicleId + ' ' + (Join-CodePoints @(0x00B7)) + ' ' + $status
}

function Test-InnerSuccess {
    param([object]$Response, [string]$Operation)

    $status = [int](Get-PropertyValue $Response 'status' 0)
    if ($status -ne 0 -and $status -ne 200) {
        $message = [string](Get-PropertyValue $Response 'message' '')
        throw "$Operation failed: $message"
    }
}

if ($PollMilliseconds -lt 100) {
    throw 'PollMilliseconds must be at least 100.'
}
if ($ProfControlCoordinateScale -le 0) {
    throw 'ProfControlCoordinateScale must be positive.'
}

$suffixSeparator = ' ' + (Join-CodePoints @(0x00B7)) + ' '
$profBase = $ProfControlBaseUri.TrimEnd('/')
$simulationClient = New-JsonHttpClient
$profClient = New-JsonHttpClient
$yFactor = if ($InvertY) { -1.0 } else { 1.0 }
$runStartedAt = Get-Date

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $PSScriptRoot '..\..\artifacts\profcontrol-bridge\runs'
}
$runDirectory = Join-Path ([IO.Path]::GetFullPath($EvidenceDirectory)) ('run-' + $runStartedAt.ToString('yyyyMMdd-HHmmss'))
$eventsPath = Join-Path $runDirectory 'state-events.jsonl'
if ($CaptureEvidence) {
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $metadata = [ordered]@{
        startedAt = $runStartedAt.ToString('o')
        simulation = $SimulationBaseUri
        profControl = $ProfControlBaseUri
        invertY = $InvertY
        coordinateScale = $ProfControlCoordinateScale
    }
    [IO.File]::WriteAllText(
        (Join-Path $runDirectory 'metadata.json'),
        ($metadata | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false)
    )
}

if (-not $DryRun) {
    $loginValues = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
    $loginValues.Add([System.Collections.Generic.KeyValuePair[string,string]]::new('userName', $ProfControlUserName))
    $loginValues.Add([System.Collections.Generic.KeyValuePair[string,string]]::new('password', $ProfControlPassword))
    $loginContent = [System.Net.Http.FormUrlEncodedContent]::new($loginValues)
    $login = $null
    try {
        $login = $profClient.PostAsync("$profBase/login_process", $loginContent).GetAwaiter().GetResult()
        $loginText = $login.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $login.IsSuccessStatusCode) {
            throw "HTTP $([int]$login.StatusCode): $loginText"
        }
    }
    finally {
        $loginContent.Dispose()
        if ($null -ne $login) {
            $login.Dispose()
        }
    }
    if ($loginText -notmatch 'FrontEnd/Root\.html') {
        throw "ProfControl login failed with HTTP $($login.StatusCode)."
    }
}

$profMap = Invoke-JsonRequest -Client $profClient -Uri "$profBase/apiv2/getInfo/getMap"
$profVehicles = @(Get-PropertyValue $profMap 'agvs' @())
if ($profVehicles.Count -eq 0) {
    throw 'ProfControl has no AGV objects. Run Convert-ProjectToProfControl.ps1 -Send first.'
}

$vehicleState = @{}
foreach ($profVehicle in $profVehicles) {
    $name = [string](Get-PropertyValue $profVehicle 'name' '')
    if ([string]::IsNullOrWhiteSpace($name)) {
        continue
    }
    $baseName = $name
    $separatorIndex = -1
    foreach ($marker in @(' |', $suffixSeparator.TrimEnd())) {
        $index = $baseName.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase)
        if ($index -ge 0 -and ($separatorIndex -lt 0 -or $index -lt $separatorIndex)) {
            $separatorIndex = $index
        }
    }
    if ($separatorIndex -ge 0) {
        $baseName = $baseName.Substring(0, $separatorIndex).Trim()
    }
    $position = Get-PropertyValue $profVehicle 'position' $null
    $state = [pscustomobject]@{
        CurrentName = $name
        LastX       = [double](Get-PropertyValue $position 'x' 0)
        LastY       = [double](Get-PropertyValue $position 'y' 0)
        LastAngle   = [double](Get-PropertyValue $profVehicle 'angle' 0)
        Loaded      = $false
        LoadKnown   = $false
    }
    foreach ($area in @(Get-PropertyValue $profVehicle 'cargoAreas' @())) {
        if ([string](Get-PropertyValue $area 'name' '') -eq 'VehicleLoadArea') {
            $state.Loaded = @(Get-PropertyValue $area 'cargos' @()).Count -gt 0
            $state.LoadKnown = $true
        }
    }
    $vehicleState[$baseName] = $state
}

$missingLoadAreas = @($vehicleState.Keys | Where-Object { -not $vehicleState[$_].LoadKnown })
if ($missingLoadAreas.Count -gt 0) {
    throw "ProfControl vehicles have no VehicleLoadArea: $($missingLoadAreas -join ', '). Re-run Convert-ProjectToProfControl.ps1 -Send on a clean isolated instance."
}

if (-not $DryRun) {
    foreach ($vehicleId in $vehicleState.Keys) {
        $currentName = $vehicleState[$vehicleId].CurrentName
        foreach ($style in @(
            [ordered]@{ propertyName = 'AgvViewAgvNameTextSize'; value = '12' },
            [ordered]@{ propertyName = 'AgvViewAgvNameTextColor'; value = '#FF9AC8EA' }
        )) {
            $response = Invoke-JsonRequest -Client $profClient `
                -Uri "$profBase/apiv2/setInfo/property" `
                -Method Post `
                -Body ([ordered]@{
                    select = [ordered]@{ type = 'Agv'; name = $currentName }
                    propertyName = $style.propertyName
                    value = $style.value
                })
            Test-InnerSuccess -Response $response -Operation $style.propertyName
        }
    }
}

$startedAt = Get-Date
$iterations = 0
$lastSummary = ''
$lastEvidence = ''
$finalReportWritten = $false

while ($true) {
    $iterations++
    try {
        $status = Read-Status
    }
    catch {
        if ($Once) {
            throw
        }
        Write-Warning "Simulation status read failed; retrying: $($_.Exception.Message)"
        Start-Sleep -Milliseconds $PollMilliseconds
        continue
    }
    if (-not [bool](Get-PropertyValue $status 'enabled' $false)) {
        throw 'The current WebHost has no enabled isolated simulation environment.'
    }
    if (-not [bool](Get-PropertyValue $status 'running' $false)) {
        if ($Once) {
            throw 'The isolated simulation is not running.'
        }
        Write-Host 'Simulation is not running; waiting...'
    }
    else {
        $cars = Get-PropertyValue (Get-PropertyValue $status 'device' $null) 'cars' $null
        if ($null -eq $cars) {
            if ($Once) {
                throw 'Simulation status does not contain device.cars.'
            }
            Write-Host 'Simulation has no active device state yet; waiting...'
            Start-Sleep -Milliseconds $PollMilliseconds
            continue
        }

        $updated = 0
        $skipped = 0
        foreach ($property in $cars.PSObject.Properties) {
            $vehicleId = $property.Name
            $car = $property.Value
            if (-not $vehicleState.ContainsKey($vehicleId)) {
                $skipped++
                continue
            }

            $x = [double](Get-PropertyValue $car 'x' ([double]::NaN))
            $y = [double](Get-PropertyValue $car 'y' ([double]::NaN))
            if ([double]::IsNaN($x) -or [double]::IsNaN($y)) {
                $skipped++
                continue
            }
            $targetY = $y * $yFactor
            $angle = [double](Get-PropertyValue $car 'angle' ($vehicleState[$vehicleId].LastAngle))
            $displayAngle = $angle * $yFactor
            while ($displayAngle -gt 180) { $displayAngle -= 360 }
            while ($displayAngle -le -180) { $displayAngle += 360 }
            $profControlAngleRadians = $displayAngle * [Math]::PI / 180.0
            $loaded = [bool](Get-PropertyValue $car 'loaded' $false)
            $targetName = Get-TaskLabel -VehicleId $vehicleId -Car $car

            $positionChanged = [Math]::Abs($x - $vehicleState[$vehicleId].LastX) -gt 0.001 -or
                [Math]::Abs($targetY - $vehicleState[$vehicleId].LastY) -gt 0.001
            $angleChanged = [Math]::Abs($displayAngle - $vehicleState[$vehicleId].LastAngle) -gt 0.1
            $nameChanged = $targetName -ne $vehicleState[$vehicleId].CurrentName
            $loadChanged = $vehicleState[$vehicleId].LoadKnown -and
                $loaded -ne $vehicleState[$vehicleId].Loaded

            if ($DryRun) {
                if ($positionChanged -or $angleChanged -or $nameChanged -or $loadChanged) {
                    Write-Host ("{0}: x={1:N3} y={2:N3} angle={3:N1} loaded={4} label={5}" -f
                        $vehicleId, $x, $targetY, $displayAngle, $loaded, $targetName)
                }
            }
            elseif ($positionChanged -or $angleChanged -or $nameChanged -or $loadChanged) {
                if ($positionChanged) {
                    $pixelX = $x / $ProfControlCoordinateScale
                    $pixelY = $targetY / $ProfControlCoordinateScale
                    $location = (ConvertTo-InvariantNumber $pixelX) + ',' + (ConvertTo-InvariantNumber $pixelY)
                    Invoke-JsonRequest -Client $profClient `
                        -Uri "$profBase/api/command/changeAgvLocation" `
                        -Method Post `
                        -Body ([ordered]@{ agvName = $vehicleState[$vehicleId].CurrentName; location = $location }) | Out-Null
                }

                if ($angleChanged) {
                    Invoke-JsonRequest -Client $profClient `
                        -Uri "$profBase/api/command/changeAgvAngle" `
                        -Method Post `
                        -Body ([ordered]@{ agvName = $vehicleState[$vehicleId].CurrentName; angle = $profControlAngleRadians }) | Out-Null
                }

                if ($loadChanged) {
                    $cargoOperation = if ($loaded) { 'cargoPlace' } else { 'cargoTake' }
                    $response = Invoke-JsonRequest -Client $profClient `
                        -Uri "$profBase/apiv2/setInfo/$cargoOperation" `
                        -Method Post `
                        -Body ([ordered]@{
                            owner = [ordered]@{ type = 'Agv'; name = $vehicleState[$vehicleId].CurrentName }
                            cargoAreaName = 'VehicleLoadArea'
                            cargoName = 'Payload'
                            quanlity = 1.0
                        })
                    Test-InnerSuccess -Response $response -Operation $cargoOperation
                }

                if ($nameChanged) {
                    $response = Invoke-JsonRequest -Client $profClient `
                        -Uri "$profBase/apiv2/setInfo/property" `
                        -Method Post `
                        -Body ([ordered]@{
                            select = [ordered]@{ type = 'Agv'; name = $vehicleState[$vehicleId].CurrentName }
                            propertyName = 'AgvName'
                            value = $targetName
                        })
                    Test-InnerSuccess -Response $response -Operation 'AgvName'
                    $vehicleState[$vehicleId].CurrentName = $targetName
                }

                $updated++
            }

            $vehicleState[$vehicleId].LastX = $x
            $vehicleState[$vehicleId].LastY = $targetY
            $vehicleState[$vehicleId].LastAngle = $displayAngle
            $vehicleState[$vehicleId].Loaded = $loaded
            $vehicleState[$vehicleId].LoadKnown = $true
        }

        $lastSummary = "Updated $updated vehicle(s); skipped $skipped."
        Write-Host $lastSummary

        if ($CaptureEvidence) {
            $evidence = [ordered]@{
                capturedAt = [DateTimeOffset]::Now.ToString('o')
                running = [bool](Get-PropertyValue $status 'running' $false)
                fresh = [bool](Get-PropertyValue $status 'fresh' $false)
                phase = [string](Get-PropertyValue $status 'phase' '')
                scenario = [ordered]@{
                    id = [string](Get-PropertyValue (Get-PropertyValue $status 'scenario' $null) 'id' '')
                    status = [string](Get-PropertyValue (Get-PropertyValue $status 'scenario' $null) 'status' '')
                    completedRounds = [int](Get-PropertyValue (Get-PropertyValue $status 'scenario' $null) 'completedRounds' 0)
                    totalRounds = [int](Get-PropertyValue (Get-PropertyValue $status 'scenario' $null) 'totalRounds' 0)
                    message = [string](Get-PropertyValue (Get-PropertyValue $status 'scenario' $null) 'message' '')
                }
                vehicles = @($cars.PSObject.Properties | ForEach-Object {
                    [ordered]@{
                        id = $_.Name
                        x = [double](Get-PropertyValue $_.Value 'x' 0)
                        y = [double](Get-PropertyValue $_.Value 'y' 0)
                        angle = [double](Get-PropertyValue $_.Value 'angle' 0)
                        station = [string](Get-PropertyValue $_.Value 'station' '')
                        task = [string](Get-PropertyValue $_.Value 'current_task' '')
                        loaded = [bool](Get-PropertyValue $_.Value 'loaded' $false)
                        offline = [bool](Get-PropertyValue $_.Value 'offline' $false)
                        waitReason = [string](Get-PropertyValue $_.Value 'waitReason' '')
                    }
                })
            }
            $evidenceText = $evidence | ConvertTo-Json -Depth 12 -Compress
            if ($evidenceText -ne $lastEvidence) {
                [IO.File]::AppendAllText($eventsPath, $evidenceText + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
                $lastEvidence = $evidenceText
            }

            $scenario = Get-PropertyValue $status 'scenario' $null
            $scenarioStatus = ([string](Get-PropertyValue $scenario 'status' '')).ToLowerInvariant()
            if (-not $finalReportWritten -and $scenarioStatus -in @('passed', 'failed', 'aborted', 'inconclusive')) {
                $reportId = [string](Get-PropertyValue $scenario 'id' '')
                $reportUri = if ([string]::IsNullOrWhiteSpace($reportId)) {
                    "$($SimulationBaseUri.TrimEnd('/'))/api/simulation/report"
                }
                else {
                    "$($SimulationBaseUri.TrimEnd('/'))/api/simulation/report?id=$reportId"
                }
                try {
                    $completedReport = Invoke-JsonRequest -Client $simulationClient -Uri $reportUri
                    if ($null -ne $completedReport) {
                        [IO.File]::WriteAllText(
                            (Join-Path $runDirectory 'report.json'),
                            ($completedReport | ConvertTo-Json -Depth 50),
                            [Text.UTF8Encoding]::new($false)
                        )
                        $finalReportWritten = $true
                    }
                }
                catch {
                    Write-Warning "Completed-scenario report was not captured: $($_.Exception.Message)"
                }
            }
        }
    }

    if ($Once) {
        break
    }
    if ($ExitOnScenarioComplete -and
        ([string](Get-PropertyValue (Get-PropertyValue $status 'scenario' $null) 'status' '')).ToLowerInvariant() -in
        @('passed', 'failed', 'aborted', 'inconclusive')) {
        break
    }
    if ($DurationSeconds -gt 0 -and ((Get-Date) - $startedAt).TotalSeconds -ge $DurationSeconds) {
        break
    }
    Start-Sleep -Milliseconds $PollMilliseconds
}

[pscustomobject]@{
    iterations = $iterations
    lastSummary = $lastSummary
    dryRun = [bool]$DryRun
} | ConvertTo-Json

if ($CaptureEvidence) {
    try {
        $latestStatus = Read-Status
        $latestScenario = Get-PropertyValue $latestStatus 'scenario' $null
        $reportId = [string](Get-PropertyValue $latestScenario 'id' '')
        $reportUri = if ([string]::IsNullOrWhiteSpace($reportId)) {
            "$($SimulationBaseUri.TrimEnd('/'))/api/simulation/report"
        }
        else {
            "$($SimulationBaseUri.TrimEnd('/'))/api/simulation/report?id=$reportId"
        }
        $report = Invoke-JsonRequest -Client $simulationClient -Uri $reportUri
        if ($null -ne $report) {
            [IO.File]::WriteAllText(
                (Join-Path $runDirectory 'report.json'),
                ($report | ConvertTo-Json -Depth 50),
                [Text.UTF8Encoding]::new($false)
            )
        }
    }
    catch {
        Write-Warning "Final simulation report was not captured: $($_.Exception.Message)"
    }
}

$simulationClient.Dispose()
$profClient.Dispose()
