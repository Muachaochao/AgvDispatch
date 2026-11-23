<#
.SYNOPSIS
Registers the AGV dispatch Windows service and its automatic restart policy.

.DESCRIPTION
Run once on the industrial PC as an administrator. Without -ExecutablePath the
script only updates the failure actions of an existing service. Failure actions
restart the process after 60 s, 120 s and then 300 s, and reset the failure
counter after 24 h. The service stops gracefully through the host shutdown
timeout (30 s) when the service manager sends a stop request.

Written for this repository in round 7. It has NOT been run in this environment.
#>
param(
    [string]$ServiceName = 'AGV铝屑回收调度服务',
    [string]$ExecutablePath = ''
)

$ErrorActionPreference = 'Stop'

if ($ExecutablePath -ne '') {
    if (-not (Test-Path -LiteralPath $ExecutablePath)) {
        throw "Executable not found: $ExecutablePath"
    }
    # sc.exe requires a space after every "name=" argument.
    & sc.exe create $ServiceName binPath= "`"$ExecutablePath`"" start= auto DisplayName= $ServiceName
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE" }
}

& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/120000/restart/300000
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed with exit code $LASTEXITCODE" }

# Restart on a non-zero exit as well as on a crash, which is how a stopped-by-error host ends.
& sc.exe failureflag $ServiceName 1
if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag failed with exit code $LASTEXITCODE" }

& sc.exe qfailure $ServiceName
