param([string]$SiteRoot='', [switch]$Open)
$ErrorActionPreference='Stop'
$managerProjectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$managerSiteRoot=if($SiteRoot){[IO.Path]::GetFullPath($SiteRoot)}else{[IO.Path]::GetFullPath((Join-Path $managerProjectRoot '../发布包'))}
if(!(Test-Path -LiteralPath (Join-Path $managerSiteRoot '最新版Web.json'))){throw '未找到日常入口配置。'}
$managerStamp=Get-Date -Format yyyyMMdd-HHmmss
$managerFolder="现场管理工具_$managerStamp"
$managerOutput=Join-Path $managerSiteRoot $managerFolder
if(Test-Path -LiteralPath $managerOutput){throw '发布目录已存在，拒绝覆盖。'}
$managerEvidence=Join-Path $managerProjectRoot "artifacts/site-manager-$managerStamp"
New-Item -ItemType Directory -Path $managerEvidence -Force | Out-Null
dotnet publish (Join-Path $managerProjectRoot 'AgvDispatch.Wpf/AgvDispatch.Wpf.csproj') -c Release -r win-x64 --self-contained true -o $managerOutput *> (Join-Path $managerEvidence 'publish.log')
if($LASTEXITCODE -ne 0){throw "发布失败，见 $managerEvidence/publish.log"}
Copy-Item -LiteralPath (Join-Path $managerProjectRoot 'docs/architecture/现场管理工具与WPF收缩-20261112.md') -Destination (Join-Path $managerOutput '使用说明.md')
$managerManifest=@{schema='agv-site-manager-v1';createdAt=(Get-Date).ToString('o');files=@(Get-ChildItem -LiteralPath $managerOutput -File -Recurse | ForEach-Object { @{path=[IO.Path]::GetRelativePath($managerOutput,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })}
$managerManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $managerOutput 'manager-manifest.json') -Encoding utf8
$managerEntry=Join-Path $managerSiteRoot '启动现场管理工具.cmd'
if(Test-Path -LiteralPath $managerEntry){Copy-Item -LiteralPath $managerEntry -Destination (Join-Path $managerEvidence 'previous-entry.cmd')}
$managerCmd="@echo off`r`nchcp 65001 >nul`r`nstart `"`" `"%~dp0$managerFolder\AgvDispatch.Wpf.exe`" --site-root `"%~dp0.`"`r`n"
[IO.File]::WriteAllText($managerEntry,$managerCmd,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $managerSiteRoot '启动软件助手.cmd'),$managerCmd,[Text.UTF8Encoding]::new($false))
# No service/Web replacement or stop: the manager is an independent local control surface.
Write-Output "已发布：$managerEntry"
if($Open){Start-Process -FilePath (Join-Path $managerOutput 'AgvDispatch.Wpf.exe') -ArgumentList @('--site-root',('"'+$managerSiteRoot+'"'))}
