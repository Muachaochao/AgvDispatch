param([Parameter(Mandatory=$true)][string]$BundleDirectory)
$ErrorActionPreference='Stop'
$latestRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../发布包'))
$latestBundle=[IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\','/')
if(!$latestBundle.StartsWith($latestRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '最新版目录必须位于本项目发布包内。'}
$latestRelative=[IO.Path]::GetRelativePath($latestRoot,$latestBundle)
if($latestRelative -match '[%"!&|<>^\r\n]'){throw '目录包含不适合CMD入口的字符。'}
$latestOldPointer=Join-Path $latestRoot '最新版Web.json'
if(Test-Path -LiteralPath $latestOldPointer){
    $latestPrevious=Get-Content -LiteralPath $latestOldPointer -Raw | ConvertFrom-Json
    $latestPreviousDirectory=[IO.Path]::GetFullPath((Join-Path $latestRoot $latestPrevious.directory))
    if(!$latestPreviousDirectory.StartsWith($latestRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '旧入口目录不属于当前发布包，未改入口。'}
    $latestSessionPath=Join-Path $latestPreviousDirectory 'Runtime/launcher-session.json'
    if($latestPreviousDirectory -ne $latestBundle -and (Test-Path -LiteralPath $latestSessionPath)){
        $latestSession=Get-Content -LiteralPath $latestSessionPath -Raw | ConvertFrom-Json
        foreach($latestOwner in @($latestSession.Service,$latestSession.Web)){
            $latestProcess=Get-Process -Id $latestOwner.Id -ErrorAction SilentlyContinue
            if($latestProcess -and $latestProcess.Path -eq $latestOwner.Path -and $latestProcess.StartTime.ToUniversalTime().Ticks -eq $latestOwner.StartedUtcTicks){throw '旧入口的程序仍在使用；未改固定入口，请先用现有停止入口退出，再设置最新版。不会结束用户进程。'}
        }
    }
}
& (Join-Path $PSScriptRoot 'Verify-CommissioningBundle.ps1') -BundleDirectory $latestBundle
$latestManifest=Get-Content -LiteralPath (Join-Path $latestBundle 'release-manifest.json') -Raw | ConvertFrom-Json
if(!(Test-Path -LiteralPath (Join-Path $latestBundle 'Start-Latest-Web.cmd')) -or !(Test-Path -LiteralPath (Join-Path $latestBundle 'Stop-Latest-Web.cmd'))){throw '缺少完整启动器。'}
$latestBackup=Join-Path $latestRoot ('入口历史/'+(Get-Date -Format yyyyMMdd-HHmmss))
foreach($latestName in @('启动最新版Web.cmd','停止最新版Web.cmd','最新版Web.json')){
    $latestOld=Join-Path $latestRoot $latestName
    if(Test-Path -LiteralPath $latestOld){New-Item -ItemType Directory -Path $latestBackup -Force | Out-Null;Copy-Item -LiteralPath $latestOld -Destination $latestBackup}
}
@"
@echo off
chcp 65001 >nul
call "%~dp0$latestRelative\Start-Latest-Web.cmd" --project-store-path "%~dp0运行数据\Web工程方案"
"@ | Set-Content -LiteralPath (Join-Path $latestRoot '启动最新版Web.cmd') -Encoding utf8
@"
@echo off
chcp 65001 >nul
call "%~dp0$latestRelative\Stop-Latest-Web.cmd"
"@ | Set-Content -LiteralPath (Join-Path $latestRoot '停止最新版Web.cmd') -Encoding utf8
@{releaseId=$latestManifest.releaseId;directory=$latestRelative;url="http://127.0.0.1:$($latestManifest.port)/";configurationMigrated=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $latestRoot '最新版Web.json') -Encoding utf8
Write-Output "Latest Web entry updated: $($latestManifest.releaseId). Existing programs and data were not moved."
