param(
    [switch]$SkipDatabaseBackup
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$packageRoot = Join-Path $workspace '发布包'
$release = [IO.Path]::GetFullPath((Join-Path $packageRoot '候车均衡与下游准入_20261111_v2'))
$candidate = [IO.Path]::GetFullPath((Join-Path $workspace 'DispatchingSystem\artifacts\navigation-pause-resume-20261114'))
$versioned = [IO.Path]::GetFullPath((Join-Path $workspace 'DispatchingSystem\artifacts\navigation-pause-resume-versioned-20261114'))
$releaseId = 'commissioning-navigation-pause-resume-20261114'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = [IO.Path]::GetFullPath((Join-Path $packageRoot "更新备份\navigation-pause-resume-$stamp"))
$database = [IO.Path]::GetFullPath((Join-Path $packageRoot '运行数据\后台5199\agv-dispatch.db'))
$releasePrefix = $release.TrimEnd('\') + '\'
$backupPrefix = $backup.TrimEnd('\') + '\'

if (!$release.StartsWith([IO.Path]::GetFullPath($packageRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release path is outside 发布包.' }
if (!$backup.StartsWith([IO.Path]::GetFullPath($packageRoot).TrimEnd('\') + '\更新备份\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup path is outside 发布包\更新备份.' }
if (!(Test-Path -LiteralPath $release)) { throw "Release directory not found: $release" }
if (!(Test-Path -LiteralPath $candidate)) { throw "Candidate directory not found: $candidate" }
if (!(Test-Path -LiteralPath (Join-Path $versioned 'Service\AgvDispatch.Service.exe'))) { throw 'Versioned candidate Service executable missing.' }
if (!(Test-Path -LiteralPath (Join-Path $versioned 'WebHost\AgvDispatch.WebHost.exe'))) { throw 'Versioned candidate WebHost executable missing.' }
if (!(Test-Path -LiteralPath (Join-Path $candidate 'Web\wwwroot\index.html'))) { throw 'Candidate wwwroot missing.' }
if (Test-Path -LiteralPath $backup) { throw "Backup path already exists: $backup" }

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds,
        [string]$FailureMessage
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 400
    }
    throw $FailureMessage
}

function Get-ReleaseProcesses {
    $prefix = $releasePrefix.ToLowerInvariant()
    Get-CimInstance Win32_Process |
        Where-Object {
            $_.ExecutablePath -and
            $_.ExecutablePath.ToLowerInvariant().StartsWith($prefix) -and
            $_.Name -in @('AgvDispatch.Service.exe', 'AgvDispatch.WebHost.exe')
        }
}

function Stop-ReleaseProcesses {
    $processes = @(Get-ReleaseProcesses)
    foreach ($process in ($processes | Sort-Object { if ($_.Name -eq 'AgvDispatch.WebHost.exe') { 0 } else { 1 } })) {
        Stop-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
    }
    Wait-Until {
        @(Get-ReleaseProcesses).Count -eq 0 -and
        !(Get-NetTCPConnection -LocalPort 5199 -State Listen -ErrorAction SilentlyContinue)
    } 30 'Release processes or port 5199 did not stop.'
}

function Start-Release {
    $launcher = Join-Path $release 'Web\host\AgvDispatch.WebHost.exe'
    & $launcher '--launch-local' '--bundle-root' $release '--daily-data' '--daily-launch-settings' (Join-Path $packageRoot '最新版Web.json') '--project-store-path' (Join-Path $packageRoot '运行数据\Web工程方案') '--no-browser'
    if ($LASTEXITCODE -ne 0) { throw "Canonical launcher failed with exit code $LASTEXITCODE." }
}

function Start-OriginalDirect {
    $serviceExe = Join-Path $release 'Service\AgvDispatch.Service.exe'
    $webExe = Join-Path $release 'Web\host\AgvDispatch.WebHost.exe'
    $serviceArgs = @(
        '--Security:PipeName=AgvDispatch.Candidate.20261111-221246',
        "--Database:Path=$(Join-Path $packageRoot '运行数据\后台5199\agv-dispatch.db')",
        '--Plc:Enabled=false', '--Plc:AllowWrites=false',
        '--Agv:Enabled=false', '--Agv:AllowCommands=false',
        '--TaskCreation:Enabled=false', '--TaskCreation:AutomaticCreationEnabled=false',
        '--Dispatching:Enabled=false', '--AgvCommandOutbox:Enabled=false',
        '--DispatchTaskSteps:Enabled=false', '--HandshakeExecution:Enabled=false',
        '--FleetTest:Enabled=false', '--NavigationTest:Enabled=false',
        '--PlcCycleTest:Enabled=false'
    )
    $webArgs = @(
        '--Web:Port=5199', '--Web:PipeName=AgvDispatch.Candidate.20261111-221246',
        '--Web:EnableTaskSubmission=True', '--Web:EnableRecoveryActions=true',
        '--Web:EnableTaskCancellation=true', '--Web:EnableAlarmActions=true',
        '--Web:EnableEngineeringRuntimeApply=true',
        "--Web:ProjectStorePath=$(Join-Path $packageRoot '运行数据\Web工程方案')",
        "--Web:StaticPath=$(Join-Path $release 'Web\wwwroot')"
    )
    Start-Process -FilePath $serviceExe -ArgumentList $serviceArgs -WorkingDirectory (Split-Path $serviceExe) -WindowStyle Hidden | Out-Null
    Start-Sleep -Seconds 2
    Start-Process -FilePath $webExe -ArgumentList $webArgs -WorkingDirectory (Split-Path $webExe) -WindowStyle Hidden | Out-Null
}

function Get-Health {
    try { Invoke-RestMethod -Uri 'http://127.0.0.1:5199/health' -TimeoutSec 3 } catch { $null }
}

function Test-Deployment {
    Wait-Until { $null -ne (Get-Health) } 75 '5199 did not become healthy.'
    $config = Invoke-RestMethod -Uri 'http://127.0.0.1:5199/api/config' -TimeoutSec 8
    if ($config.adapter.informationalVersion -ne $releaseId) { throw "Adapter version mismatch: $($config.adapter.informationalVersion)" }
    if (!$config.taskSubmissionEnabled -or !$config.taskCancellationEnabled -or
        !$config.recoveryActionsEnabled -or !$config.alarmActionsEnabled -or
        !$config.engineeringRuntimeApplyEnabled) {
        throw 'One or more expected daily Web capabilities are disabled after deployment.'
    }
    $snapshot = Invoke-WebRequest -Uri 'http://127.0.0.1:5199/api/snapshot' -TimeoutSec 15
    if ($snapshot.StatusCode -ne 200) { throw "Snapshot returned $($snapshot.StatusCode)." }
    $tasks = Invoke-RestMethod -Uri 'http://127.0.0.1:5199/api/tasks?page=1&pageSize=1' -TimeoutSec 10
    if ($null -eq $tasks.items) { throw 'Task API did not return an items collection.' }
    $navigation = Invoke-RestMethod -Uri 'http://127.0.0.1:5199/api/navigation-test' -TimeoutSec 10
    if ($null -eq $navigation.enabled) { throw 'Navigation test status endpoint is unavailable.' }
    $index = Invoke-WebRequest -Uri 'http://127.0.0.1:5199/' -TimeoutSec 10
    if ($index.StatusCode -ne 200 -or $index.Content -notmatch '<div id="app"></div>') { throw 'Static landing page did not load.' }
    return [pscustomobject]@{
        Health = 'ok'
        Config = $config
        SnapshotBytes = $snapshot.RawContentLength
        TaskApiLoaded = $true
        NavigationStatusLoaded = $true
        StaticPageLoaded = $true
    }
}

function Restore-Backup {
    Stop-ReleaseProcesses
    Copy-Item -Path (Join-Path $backup 'Service\*') -Destination (Join-Path $release 'Service') -Recurse -Force
    Copy-Item -Path (Join-Path $backup 'Web\host\*') -Destination (Join-Path $release 'Web\host') -Recurse -Force
    Copy-Item -Path (Join-Path $backup 'Web\wwwroot\*') -Destination (Join-Path $release 'Web\wwwroot') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $backup 'release-manifest.json') -Destination (Join-Path $release 'release-manifest.json') -Force
    Copy-Item -LiteralPath (Join-Path $backup '最新版Web.json') -Destination (Join-Path $packageRoot '最新版Web.json') -Force
    if (Test-Path -LiteralPath (Join-Path $backup 'launcher-session.original.json')) {
        Copy-Item -LiteralPath (Join-Path $backup 'launcher-session.original.json') -Destination (Join-Path $release 'Runtime\launcher-session.json') -Force
    }
    Start-OriginalDirect
    Wait-Until { $null -ne (Get-Health) } 75 'Rollback start did not become healthy.'
}

New-Item -ItemType Directory -Path $backup -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $backup 'Web') -Force | Out-Null
$running = @(Get-ReleaseProcesses)
if ($running.Count -ne 2) { throw "Expected Service and WebHost under the release directory; found $($running.Count)." }
$portOwner = (Get-NetTCPConnection -LocalPort 5199 -State Listen -ErrorAction Stop).OwningProcess
if ($portOwner -notin $running.ProcessId) { throw "Port 5199 is owned by unexpected process $portOwner." }

Copy-Item -LiteralPath (Join-Path $release 'Service') -Destination (Join-Path $backup 'Service') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $release 'Web\host') -Destination (Join-Path $backup 'Web\host') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $release 'Web\wwwroot') -Destination (Join-Path $backup 'Web\wwwroot') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $release 'release-manifest.json') -Destination (Join-Path $backup 'release-manifest.json') -Force
Copy-Item -LiteralPath (Join-Path $packageRoot '启动最新版Web.cmd') -Destination (Join-Path $backup '启动最新版Web.cmd') -Force
Copy-Item -LiteralPath (Join-Path $packageRoot '停止最新版Web.cmd') -Destination (Join-Path $backup '停止最新版Web.cmd') -Force
Copy-Item -LiteralPath (Join-Path $packageRoot '最新版Web.json') -Destination (Join-Path $backup '最新版Web.json') -Force
$launcherSession = Join-Path $release 'Runtime\launcher-session.json'
if (Test-Path -LiteralPath $launcherSession) {
    Copy-Item -LiteralPath $launcherSession -Destination (Join-Path $backup 'launcher-session.original.json') -Force
}

$before = @{
    createdAt = (Get-Date).ToString('o')
    release = $release
    candidate = $candidate
    backup = $backup
    processes = @($running | Select-Object ProcessId, Name, ExecutablePath, CommandLine)
    preservedConfig = @(
        'Service\appsettings.json',
        'Web\host\appsettings.json'
    )
}
$before | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'before.json') -Encoding utf8

if (!$SkipDatabaseBackup) {
    $sqlite = (Get-Command sqlite3 -ErrorAction Stop).Source
    $databaseBackup = Join-Path $backup 'agv-dispatch-before-deploy.db'
    & $sqlite $database ".backup '$databaseBackup'"
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $databaseBackup)) { throw 'SQLite database backup failed.' }
    $databaseHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $databaseBackup).Hash
    @{ source = $database; snapshot = $databaseBackup; sha256 = $databaseHash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'database-backup.json') -Encoding utf8
}

$serviceConfig = Join-Path $release 'Service\appsettings.json'
$webConfig = Join-Path $release 'Web\host\appsettings.json'
if (!(Test-Path -LiteralPath $serviceConfig) -or !(Test-Path -LiteralPath $webConfig)) { throw 'Current daily configuration files are missing.' }
$serviceConfigBackup = Join-Path $backup 'Service\appsettings.preserved.json'
$webConfigBackup = Join-Path $backup 'Web\host\appsettings.preserved.json'
Copy-Item -LiteralPath $serviceConfig -Destination $serviceConfigBackup -Force
Copy-Item -LiteralPath $webConfig -Destination $webConfigBackup -Force

$deploymentRecord = Join-Path $backup 'deployment.json'
try {
    Stop-ReleaseProcesses
    Copy-Item -Path (Join-Path $versioned 'Service\*') -Destination (Join-Path $release 'Service') -Recurse -Force
    Copy-Item -Path (Join-Path $versioned 'WebHost\*') -Destination (Join-Path $release 'Web\host') -Recurse -Force
    Copy-Item -Path (Join-Path $candidate 'Web\wwwroot\*') -Destination (Join-Path $release 'Web\wwwroot') -Recurse -Force
    Copy-Item -LiteralPath $serviceConfigBackup -Destination $serviceConfig -Force
    Copy-Item -LiteralPath $webConfigBackup -Destination $webConfig -Force

    $hashes = @(
        @{ Name = 'Service\AgvDispatch.Service.dll'; Deployed = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $release 'Service\AgvDispatch.Service.dll')).Hash; Candidate = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $versioned 'Service\AgvDispatch.Service.dll')).Hash },
        @{ Name = 'Web\host\AgvDispatch.WebHost.dll'; Deployed = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $release 'Web\host\AgvDispatch.WebHost.dll')).Hash; Candidate = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $versioned 'WebHost\AgvDispatch.WebHost.dll')).Hash },
        @{ Name = 'Web\wwwroot\index.html'; Deployed = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $release 'Web\wwwroot\index.html')).Hash; Candidate = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $candidate 'Web\wwwroot\index.html')).Hash }
    )
    foreach ($hash in $hashes) { if ($hash.Deployed -ne $hash.Candidate) { throw "Deployed hash mismatch: $($hash.Name)" } }

    $manifestPath = Join-Path $release 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.releaseId = $releaseId
    $manifest.createdAt = (Get-Date).ToString('o')
    foreach ($entry in @($manifest.files)) {
        $relative = $entry.path.Replace('/', '\')
        $path = Join-Path $release $relative
        if (Test-Path -LiteralPath $path) {
            $entry.bytes = (Get-Item -LiteralPath $path).Length
            $entry.sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        }
    }
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $latestPath = Join-Path $packageRoot '最新版Web.json'
    $latest = Get-Content -LiteralPath $latestPath -Raw | ConvertFrom-Json
    $latest.releaseId = $releaseId
    $latest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $latestPath -Encoding utf8
    if (Test-Path -LiteralPath $launcherSession) {
        Move-Item -LiteralPath $launcherSession -Destination (Join-Path $backup 'launcher-session.previous.json') -Force
    }

    Start-Release
    $checks = Test-Deployment
    @{
        deployedAt = (Get-Date).ToString('o')
        status = 'success'
        release = $release
        candidate = $candidate
        backup = $backup
        hashes = $hashes
        checks = $checks
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $deploymentRecord -Encoding utf8
    Copy-Item -LiteralPath $deploymentRecord -Destination (Join-Path $release "navigation-pause-resume-deployment-$stamp.json") -Force
    Write-Output "DEPLOYMENT SUCCESS"
    Write-Output "Backup: $backup"
    Write-Output "Record: $deploymentRecord"
    $checks | ConvertTo-Json -Depth 6
}
catch {
    $failure = $_
    @{
        failedAt = (Get-Date).ToString('o')
        status = 'failed-and-rolled-back'
        error = $failure.Exception.Message
        backup = $backup
    } | ConvertTo-Json | Set-Content -LiteralPath $deploymentRecord -Encoding utf8
    try {
        Restore-Backup
        Write-Output "DEPLOYMENT FAILED; ROLLBACK HEALTHY"
    }
    catch {
        Write-Output "DEPLOYMENT FAILED; ROLLBACK FAILED: $($_.Exception.Message)"
    }
    throw $failure
}
