#requires -Version 7.0
$ErrorActionPreference='Stop'
$record=Join-Path (Split-Path $PSScriptRoot) 'integrations/PaiceSimulation/.runtime/processes.json'
if(!(Test-Path -LiteralPath $record)){Write-Output '独立应用没有启动记录。';return}
$lifecycleLock=[IO.File]::Open((Join-Path (Split-Path $record) 'lifecycle.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
$entries=@(Get-Content -LiteralPath $record -Raw | ConvertFrom-Json)
$processes=@()
foreach($entry in $entries){
    $process=Get-Process -Id $entry.id -ErrorAction SilentlyContinue
    if(!$process){continue}
    if($process.StartTime.ToUniversalTime().Ticks -ne $entry.startedUtcTicks -or $process.Path -ne $entry.path){throw "进程身份已变化，保留启动记录供核对 $($entry.id)"}
    $processes+=$process
}
foreach($process in $processes){$process.Kill();$process.WaitForExit()}
Remove-Item -LiteralPath $record
Write-Output '排策智演独立应用已停止。'

} finally { $lifecycleLock.Dispose() }
