param([Parameter(Mandatory=$true)][string]$BundleDirectory,[string[]]$FleetVehicleIds=@(),[int]$FleetMaxConcurrentTasks=0)
$ErrorActionPreference='Stop'
$latestRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../发布包'))
$latestBundle=[IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\','/')
if(!$latestBundle.StartsWith($latestRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '最新版目录必须位于本项目发布包内。'}
$latestRelative=[IO.Path]::GetRelativePath($latestRoot,$latestBundle)
if($latestRelative -match '[%"!&|<>^\r\n]'){throw '目录包含不适合CMD入口的字符。'}
. (Join-Path $PSScriptRoot 'FleetLaunchConfiguration.ps1')
# Validate before changing any entry or pointer. Preserve explicit site selection only.
$latestFleetSelection=Resolve-FleetLaunchSelection -Root $latestRoot -VehicleIds $FleetVehicleIds -MaxConcurrentTasks $FleetMaxConcurrentTasks
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
& (Join-Path $PSScriptRoot 'Verify-CommissioningBundle.ps1') -BundleDirectory $latestBundle -AllowRuntimeData
$latestManifest=Get-Content -LiteralPath (Join-Path $latestBundle 'release-manifest.json') -Raw | ConvertFrom-Json
if(!(Test-Path -LiteralPath (Join-Path $latestBundle 'Start-Latest-Web.cmd')) -or !(Test-Path -LiteralPath (Join-Path $latestBundle 'Stop-Latest-Web.cmd'))){throw '缺少完整启动器。'}
$latestBackup=Join-Path $latestRoot ('入口历史/'+(Get-Date -Format yyyyMMdd-HHmmss))
foreach($latestName in @('启动最新版Web.cmd','停止最新版Web.cmd','最新版Web.json','启动界面修复版Web.cmd','停止界面修复版Web.cmd','启动双车联调Web.cmd','启动多车联调Web.cmd','先看这里-软件使用说明.md','简明使用说明.md')){
    $latestOld=Join-Path $latestRoot $latestName
    if(Test-Path -LiteralPath $latestOld){New-Item -ItemType Directory -Path $latestBackup -Force | Out-Null;Copy-Item -LiteralPath $latestOld -Destination $latestBackup}
}
$latestStartText=@"
@echo off
chcp 65001 >nul
call "%~dp0$latestRelative\Start-Latest-Web.cmd" --daily-data --project-store-path "%~dp0运行数据\Web工程方案" %*
"@
$latestStopText=@"
@echo off
chcp 65001 >nul
call "%~dp0$latestRelative\Stop-Latest-Web.cmd"
"@
# CMD must parse the Chinese path after chcp; enforce CRLF even when this source uses LF.
foreach($latestEntry in @(@{Name='启动最新版Web.cmd';Text=$latestStartText},@{Name='停止最新版Web.cmd';Text=$latestStopText})){
    $latestEntryText=$latestEntry.Text.Replace("`r`n","`n").Replace("`n","`r`n").TrimEnd()+"`r`n"
    [IO.File]::WriteAllText((Join-Path $latestRoot $latestEntry.Name),$latestEntryText,[Text.UTF8Encoding]::new($false))
}
# Keep the former entry names as forwarders to the single canonical pair.
 $latestFleetText=New-FleetLaunchEntryText -Selection $latestFleetSelection
 [IO.File]::WriteAllText((Join-Path $latestRoot '启动多车联调Web.cmd'),$latestFleetText,[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $latestRoot '启动双车联调Web.cmd'),"@echo off`r`nchcp 65001 >nul`r`ncall `"%~dp0启动多车联调Web.cmd`" %*`r`n",[Text.UTF8Encoding]::new($false))
# They must not retain a separately selected release after promotion.
foreach($latestAlias in @(@{Name='启动界面修复版Web.cmd';Target='启动最新版Web.cmd'},@{Name='停止界面修复版Web.cmd';Target='停止最新版Web.cmd'})){
    $latestAliasText="@echo off`r`nchcp 65001 >nul`r`ncall `"%~dp0$($latestAlias.Target)`" %*`r`n"
    [IO.File]::WriteAllText((Join-Path $latestRoot $latestAlias.Name),$latestAliasText,[Text.UTF8Encoding]::new($false))
}
@{releaseId=$latestManifest.releaseId;directory=$latestRelative;url="http://127.0.0.1:$($latestManifest.port)/";configurationMigrated=[bool]$latestManifest.siteConfigurationImported} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $latestRoot '最新版Web.json') -Encoding utf8
$latestGuide=@"
# Web 启动说明

当前固定入口版本：**$($latestManifest.releaseId)**。程序目录：$latestRelative。

1. 双击 **启动最新版Web.cmd**；正常就绪后打开 http://127.0.0.1:$($latestManifest.port)/ 。
2. 退出时使用 **停止最新版Web.cmd**。关闭浏览器不会停止后台；停止器只处理本入口拥有的进程。
3. 原“启动/停止界面修复版Web.cmd”转到上述同一入口，不再维护独立版本指向。
4. 工程方案目录为“运行数据/Web工程方案”，由根入口传入。现场配置和数据库不自动迁移；本候选入口保持其原有的设备通信与执行门禁。
5. 本包自带 .NET 运行时。缺配置、地图、设备采样时应按界面提示核对；界面演示不代表实车验收。
6. 多车竞争测试使用“启动多车联调Web.cmd”，仍为5199；需先停止原模式。入口保留此前明确配置的车辆清单和并发上限，没有清单时必须传入配置车辆编号；旧“启动双车联调Web.cmd”转入同一入口。可在CMD运行“启动多车联调Web.cmd VEHICLE-A,VEHICLE-B,VEHICLE-C 2”选择车辆和可选并发上限，其后可继续传入启动器选项，带空格的路径须加双引号；有已保留清单时也可直接追加选项。示例编号须换成工程配置实际编号。启动不会自动发车，在工程测试中心选择本组车辆、工位及用例后启动；车辆需独立初始停车位、有效地图与包络配置，不会统一驶向同一个待机点。

具体功能、验证范围见 [$latestRelative/README.md]($latestRelative/README.md) 和该目录 Docs。版本选择以“最新版Web.json”为准。不要直接改指向文件；更新时在主工程运行 tools/Set-LatestWebRelease.ps1，让启动、停止和说明同步更新。

此次入口更新未启动程序、迁移数据库或更改设备连接。旧入口与原说明备份在“入口历史”对应时间目录中。
"@
[IO.File]::WriteAllText((Join-Path $latestRoot '先看这里-软件使用说明.md'),$latestGuide,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $latestRoot '简明使用说明.md'),"使用说明统一维护于 [先看这里-软件使用说明.md](先看这里-软件使用说明.md)。`r`n",[Text.UTF8Encoding]::new($false))
Write-Output "Latest Web entry updated: $($latestManifest.releaseId). Existing programs and data were not moved."
