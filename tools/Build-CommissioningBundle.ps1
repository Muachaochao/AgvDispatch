param([string]$OutputDirectory='', [ValidateRange(1024,65535)][int]$WebPort=5198)
$ErrorActionPreference='Stop'
$bundleProject=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bundleStamp=Get-Date -Format yyyyMMdd-HHmmss
$bundleId="commissioning-$bundleStamp"
$bundlePipe="AgvDispatch.Candidate.$bundleStamp"
$bundleOutput=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{[IO.Path]::GetFullPath((Join-Path $bundleProject "../发布包/联调候选_$bundleStamp"))}
if(Test-Path -LiteralPath $bundleOutput){throw '候选目标已存在；拒绝覆盖程序或数据，请指定新的目录。'}
$bundleEvidence=Join-Path $bundleProject "artifacts/$bundleId"
New-Item -ItemType Directory -Path $bundleEvidence,$bundleOutput -Force | Out-Null
$bundleParts=@(@{Name='Service';Project='AgvDispatch.Service'},@{Name='WPF';Project='AgvDispatch.Wpf'},@{Name='Web/host';Project='AgvDispatch.WebHost'})
foreach($bundlePart in $bundleParts){
    $bundleStage=Join-Path $bundleEvidence $bundlePart.Project
    # Rebuild first: a successful incremental WPF compile can still reference missing cached BAML.
    dotnet build (Join-Path $bundleProject "$($bundlePart.Project)/$($bundlePart.Project).csproj") -t:Rebuild -c Release -r win-x64 --self-contained true "-p:InformationalVersion=$bundleId" '-p:IncludeSourceRevisionInInformationalVersion=false' *> (Join-Path $bundleEvidence "$($bundlePart.Project)-build.log")
    if($LASTEXITCODE -ne 0){throw "候选重建失败：$($bundlePart.Project)。日志在$bundleEvidence"}
    dotnet publish (Join-Path $bundleProject "$($bundlePart.Project)/$($bundlePart.Project).csproj") --no-build -c Release -r win-x64 --self-contained true "-p:InformationalVersion=$bundleId" '-p:IncludeSourceRevisionInInformationalVersion=false' -o $bundleStage *> (Join-Path $bundleEvidence "$($bundlePart.Project).log")
    if($LASTEXITCODE -ne 0){throw "候选编译失败：$($bundlePart.Project)。日志在$bundleEvidence"}
    $bundleDestination=Join-Path $bundleOutput $bundlePart.Name
    New-Item -ItemType Directory -Path $bundleDestination -Force | Out-Null
    foreach($bundleFile in Get-ChildItem -LiteralPath $bundleStage -File -Recurse){
        $bundleRelative=[IO.Path]::GetRelativePath($bundleStage,$bundleFile.FullName)
        # Never package saved site settings, databases or logs from a developer's output.
        if($bundleRelative -match '(^|[\\/])(config|data|logs)([\\/]|$)' -or $bundleFile.Name -like 'appsettings*.json' -or $bundleFile.Extension -in @('.db','.sqlite','.sqlite3','.log')){continue}
        $bundleTarget=Join-Path $bundleDestination $bundleRelative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($bundleTarget)) -Force | Out-Null
        Copy-Item -LiteralPath $bundleFile.FullName -Destination $bundleTarget
    }
    $bundleVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bundleDestination "$($bundlePart.Project).dll")).ProductVersion
    if($bundleVersion -ne $bundleId){throw "组件批次不匹配：$($bundlePart.Project)=$bundleVersion"}
}
$bundleService=Get-Content -LiteralPath (Join-Path $bundleProject 'AgvDispatch.Service/appsettings.example.json') -Raw | ConvertFrom-Json
$bundleService.Security.PipeName=$bundlePipe
$bundleService | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $bundleOutput 'Service/appsettings.json') -Encoding utf8
@{Service=@{Transport='NamedPipe';PipeName=$bundlePipe}} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bundleOutput 'WPF/appsettings.json') -Encoding utf8
@{Web=@{Port=$WebPort;PipeName=$bundlePipe;EnableTaskSubmission=$false;EnableRecoveryActions=$false;StaticPath='wwwroot'}} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bundleOutput 'Web/appsettings.json') -Encoding utf8
$bundleOldEnvironment=$env:AGV_RELEASE_ID
try{
    $env:AGV_RELEASE_ID=$bundleId
    Push-Location (Join-Path $bundleProject 'AgvDispatch.Web')
    try{npm run build -- --outDir (Join-Path $bundleOutput 'Web/wwwroot') *> (Join-Path $bundleEvidence 'frontend.log');if($LASTEXITCODE -ne 0){throw '前端构建失败。'}}finally{Pop-Location}
}finally{$env:AGV_RELEASE_ID=$bundleOldEnvironment}
foreach($bundleShared in @('AgvDispatch.Application.dll','AgvDispatch.Domain.dll')){
    $bundleSharedHashes=@($bundleParts | ForEach-Object {(Get-FileHash -LiteralPath (Join-Path (Join-Path $bundleOutput $_.Name) $bundleShared)).Hash} | Select-Object -Unique)
    if($bundleSharedHashes.Count -ne 1){throw "组件共享程序集不一致：$bundleShared"}
}
New-Item -ItemType Directory -Path (Join-Path $bundleOutput 'Web/licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $bundleProject 'AgvDispatch.Web/src/vendor/agv-web-visible/LICENSE'),(Join-Path $bundleProject 'AgvDispatch.Web/src/vendor/agv-web-visible/UPSTREAM.md') -Destination (Join-Path $bundleOutput 'Web/licenses')
New-Item -ItemType Directory -Path (Join-Path $bundleOutput 'Docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $bundleProject 'docs/architecture/Robokit厂家道路属性只读与双向核对-20261023.md') -Destination (Join-Path $bundleOutput 'Docs')
Copy-Item -LiteralPath (Join-Path $bundleProject 'docs/architecture/车端站点选择与3051单次导航-20261023.md') -Destination (Join-Path $bundleOutput 'Docs/单次导航说明.md')
Copy-Item -LiteralPath (Join-Path $bundleProject 'docs/architecture/Web地图管理与布局修复-20261019.md') -Destination (Join-Path $bundleOutput 'Docs')
Copy-Item -LiteralPath (Join-Path $bundleProject 'docs/architecture/六项路线进度与统一候选发布-20261017.md'),(Join-Path $bundleProject 'docs/architecture/单车与异常现场验收记录模板-20261017.md'),(Join-Path $bundleProject 'docs/architecture/Web工程配置方案库与任务地图联动-20261018.md'),(Join-Path $bundleProject 'docs/architecture/Web中央地图联动与取消恢复演练-20261018.md'),(Join-Path $bundleProject 'docs/architecture/Web等待队列筛选与资源定位-20261018.md'),(Join-Path $bundleProject 'docs/architecture/Web阻塞链详情与释放条件-20261018.md'),(Join-Path $bundleProject 'docs/architecture/多车冲突断连与切图组合验证-20261018.md'),(Join-Path $bundleProject 'docs/architecture/地图激活回执丢失与只读复核-20261018.md') -Destination (Join-Path $bundleOutput 'Docs')
@'
@echo off
pushd "%~dp0Service"
echo Candidate backend. Device communication and execution are OFF by default.
AgvDispatch.Service.exe
if errorlevel 1 pause
popd
'@ | Set-Content -LiteralPath (Join-Path $bundleOutput 'Start-Service.cmd') -Encoding ascii
@"
@echo off
pushd "%~dp0Web"
echo Open http://127.0.0.1:$WebPort/ after Now listening appears. Ctrl+C stops this Web adapter.
host\AgvDispatch.WebHost.exe --Web:Port=$WebPort --Web:PipeName=$bundlePipe --Web:EnableTaskSubmission=false --Web:EnableRecoveryActions=false --Web:StaticPath="%~dp0Web\wwwroot"
if errorlevel 1 pause
popd
"@ | Set-Content -LiteralPath (Join-Path $bundleOutput 'Start-Web.cmd') -Encoding ascii
@'
@echo off
pushd "%~dp0WPF"
start "" "AgvDispatch.Wpf.exe"
popd
'@ | Set-Content -LiteralPath (Join-Path $bundleOutput 'Start-WPF.cmd') -Encoding ascii
@"
# 同批联调候选包

批次：$bundleId

1. 首选双击Start-Latest-Web.cmd：启动本版后台与Web，等待就绪并打开浏览器；重复点击复用原进程。
2. 需要停止本入口启动的程序时，双击Stop-Latest-Web.cmd；仅核对并停止本版记录的进程，不关闭浏览器。不要与分组件启动入口混用。
3. 页面地址 http://127.0.0.1:$WebPort/ 。WPF仅在需未迁移配置时使用Start-WPF.cmd。
4. 分组件Start-Service/Start-Web.cmd保留作工程排查，它们需分别管理窗口；不属于一键启动记录。

单次导航现场测试使用显式入口 Start-Navigation-Test.cmd AGV-01；它仅针对指定测试车启用导航发送许可，保留业务任务和PLC执行关闭。默认启动不启用此功能，详见 Docs/单次导航说明.md。页面核验不会发车，须人工确认区域后再点击发送。

三份程序均为Windows x64自带运行时；CMD不调用PowerShell脚本。候选使用独立管道$bundlePipe、独立空数据库、关闭的通信和执行配置；没有导入现场数据。原有服务、WPF、5188预览及配置不会被替换或停止。一键入口强制关闭设备通信、PLC写入、AGV发送、任务创建/派发和新握手，用于安全开发联调；不能通过此入口现场开跑。分组件入口的窗口Ctrl+C停止各自进程。

联调检查出现缺地图、缺车辆、缺点位是当前候选配置为空的真实结果，不会生成示例。配置迁移与现场启用需沿原系统的地图、身份、任务、恢复门禁办理；不能覆盖原data/config后直接视为现场可运行。正式任务提交仍重新核验。

release-manifest.json记录逐文件SHA256与共享程序集身份。若本目录运行或修改过配置，原清单不再代表其当前全部文件；保留原包作为部署基线。批次一致只证明软件版本关系，不证明PLC固件匹配或实车验收。
"@ | Set-Content -LiteralPath (Join-Path $bundleOutput 'README.md') -Encoding utf8
@'
@echo off
"%~dp0Web\host\AgvDispatch.WebHost.exe" --launch-local --bundle-root "%~dp0." %*
if errorlevel 1 pause
'@ | Set-Content -LiteralPath (Join-Path $bundleOutput 'Start-Latest-Web.cmd') -Encoding ascii
@'
@echo off
"%~dp0Web\host\AgvDispatch.WebHost.exe" --launch-local --stop --bundle-root "%~dp0."
if errorlevel 1 pause
'@ | Set-Content -LiteralPath (Join-Path $bundleOutput 'Stop-Latest-Web.cmd') -Encoding ascii
@'
@echo off
if "%~1"=="" (
  echo Usage: Start-Navigation-Test.cmd AGV-01
  echo Requires a configured robot and an isolated test area. No task is sent on startup.
  exit /b 1
)
"%~dp0Web\host\AgvDispatch.WebHost.exe" --launch-local --bundle-root "%~dp0." --navigation-test-vehicle "%~1"
if errorlevel 1 pause
'@ | Set-Content -LiteralPath (Join-Path $bundleOutput 'Start-Navigation-Test.cmd') -Encoding ascii
$bundleFiles=@(Get-ChildItem -LiteralPath $bundleOutput -File -Recurse | ForEach-Object {@{path=[IO.Path]::GetRelativePath($bundleOutput,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length}})
@{schema='agv-commissioning-release';version=1;releaseId=$bundleId;createdAt=(Get-Date).ToString('o');runtime='win-x64 self-contained';pipeName=$bundlePipe;port=$WebPort;files=$bundleFiles;siteConfigurationImported=$false;deviceExecutionEnabled=$false} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $bundleOutput 'release-manifest.json') -Encoding utf8
Write-Output "Candidate=$bundleOutput"
Write-Output "Release=$bundleId"
Write-Output "Evidence=$bundleEvidence"
