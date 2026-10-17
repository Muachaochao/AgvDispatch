$ErrorActionPreference = 'Stop'
$recordPath = Join-Path $PSScriptRoot 'web-preview-process.json'
if (!(Test-Path -LiteralPath $recordPath)) { Write-Host 'No owned preview process record.'; exit 0 }
$record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
$webProcess = Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
if (!$webProcess) { Write-Host 'Preview already stopped.'; exit 0 }
$expectedExe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'host/AgvDispatch.WebHost.exe'))
if ($webProcess.Path -ne $expectedExe -or $webProcess.StartTime.ToUniversalTime().Ticks -ne $record.StartTimeTicks) {
    throw 'Process identity changed; refusing to stop an unrelated process.'
}
Stop-Process -Id $webProcess.Id
Write-Host 'Owned AGV Web preview stopped.'
