param(
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = @(
    'tools/TaskCancellationProtocolSmokeTest/TaskCancellationProtocolSmokeTest.csproj',
    'tools/DatabaseBackupSmokeTest/DatabaseBackupSmokeTest.csproj',
    'tools/TrafficWindowRollbackSmokeTest/TrafficWindowRollbackSmokeTest.csproj',
    'tools/DispatchPreSendSafetySmokeTest/DispatchPreSendSafetySmokeTest.csproj',
    'tools/DispatchTimeoutHoldSmokeTest/DispatchTimeoutHoldSmokeTest.csproj',
    'tools/CompletedRouteReleaseSmokeTest/CompletedRouteReleaseSmokeTest.csproj',
    'tools/MapSwitchSmokeTest/MapSwitchSmokeTest.csproj',
    'tools/ObstacleRecoveryRound7SmokeTest/ObstacleRecoveryRound7SmokeTest.csproj',
    'tools/ObstacleAutoRecoverySmokeTest/ObstacleAutoRecoverySmokeTest.csproj',
    'tools/RoadClosureSmokeTest/RoadClosureSmokeTest.csproj',
    'tools/ChargingFleetQueueSmokeTest/ChargingFleetQueueSmokeTest.csproj',
    'tools/ChargingRelayReturnSmokeTest/ChargingRelayReturnSmokeTest.csproj',
    'tools/TrafficWaitAlarmSmokeTest/TrafficWaitAlarmSmokeTest.csproj',
    'tools/SegmentReleaseBackoffSmokeTest/SegmentReleaseBackoffSmokeTest.csproj',
    'tools/ManualSystemLeaseReleaseSmokeTest/ManualSystemLeaseReleaseSmokeTest.csproj'
)

if ($Only.Count -gt 0)
{
    $projects = $projects | Where-Object {
        $candidate = $_
        $Only | Where-Object { $candidate -like "*$_*" }
    }
}

$failures = @()
foreach ($project in $projects)
{
    $path = Join-Path $root $project
    if (-not (Test-Path -LiteralPath $path))
    {
        $failures += "$project (missing)"
        Write-Error "缺少测试工程：$project"
        continue
    }

    Write-Host "=== $project ==="
    # Do not pass dotnet-run flags after the project: console smoke tests
    # interpret an unexpected argument as a replay archive path.
    & dotnet run --project $path -c Release --no-restore
    if ($LASTEXITCODE -ne 0)
    {
        $failures += "$project (exit $LASTEXITCODE)"
    }
}

if ($failures.Count -gt 0)
{
    throw "核心冒烟测试失败：`n$($failures -join "`n")"
}

Write-Host "PASS: $($projects.Count) core smoke projects."
