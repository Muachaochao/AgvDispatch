$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $repo '.artifacts\engineering-web-simulation'
$manifest = Join-Path $work 'instance.json'
if (!(Test-Path -LiteralPath $manifest)) { Write-Host '没有本模拟实例的启动记录。'; return }
$saved = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$process = Get-Process -Id $saved.pid -ErrorAction SilentlyContinue
if (!$process) { Write-Host '本模拟实例已停止。'; return }
$expected = [IO.Path]::GetFullPath((Join-Path $work 'runtime\AgvDispatch.WebHost.exe'))
if ($saved.port -eq 5199 -or ![StringComparer]::OrdinalIgnoreCase.Equals($process.Path, $expected)) { throw '进程与本模拟实例不一致，拒绝停止。' }
$started = [DateTimeOffset]::Parse($saved.startedAt).UtcDateTime
if ([Math]::Abs(($process.StartTime.ToUniversalTime() - $started).TotalSeconds) -gt 5) { throw 'PID已被复用，拒绝停止。' }
Stop-Process -Id $process.Id
Write-Host '仅停止了工程隔离模拟；工程草稿和报告保留。'
