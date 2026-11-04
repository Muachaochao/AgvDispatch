#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$ReferenceBundle,[Parameter(Mandatory=$true)][string]$PythonPath)
$ErrorActionPreference='Stop'
# All API/process reads below are fixture functions. No site endpoint or process is used.
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('agv-simulation-web-patch-test-'+[guid]::NewGuid().ToString('N'))
$bundle=Join-Path $fixture 'bundle';$payload=Join-Path $fixture 'web';$static=Join-Path $fixture 'dist'
$patch=Join-Path $fixture 'patch'
$sim=Join-Path $PSScriptRoot ('../artifacts/web-single-vehicle-patch-test-'+[guid]::NewGuid().ToString('N'))
foreach($directory in @('Web/host','Web/wwwroot/assets','Runtime','Service')){New-Item -ItemType Directory -Path (Join-Path $bundle $directory) -Force | Out-Null}
New-Item -ItemType Directory -Path $payload,(Join-Path $static 'assets'),(Join-Path $fixture '运行数据/后台5199'),(Join-Path $fixture '运行数据/Web工程方案'),(Join-Path $sim 'service'),(Join-Path $sim 'webhost') -Force | Out-Null
$referenceHost=Join-Path $ReferenceBundle 'Web/host'
foreach($file in Get-ChildItem -LiteralPath $referenceHost -Filter 'AgvDispatch.*.dll'){
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $bundle ('Web/host/'+$file.Name))
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $payload $file.Name)
}
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'AgvDispatch.WebHost.dll')).ProductVersion
Set-Content -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html') -Value 'old'
Set-Content -LiteralPath (Join-Path $static 'index.html') -Value 'new'
Set-Content -LiteralPath (Join-Path $static 'assets/test.js') -Value 'new'
$entries=@(Get-ChildItem -LiteralPath (Join-Path $bundle 'Web') -File -Recurse | ForEach-Object {@{path=[IO.Path]::GetRelativePath($bundle,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
@{schema='agv-commissioning-release';version=1;releaseId=$version;port=5199;pipeName='fixture-pipe';files=$entries} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundle 'release-manifest.json')
$started=[datetime]'2026-01-01T00:00:00Z'
$global:SimulationPatchFixture=@{Bundle=$bundle;Started=$started;Version=$version}
$session=@{ReleaseId=$version;Service=@{Id=990001;StartedUtcTicks=$started.ToUniversalTime().Ticks;Path=(Join-Path $bundle 'Service/AgvDispatch.Service.exe')};Web=@{Id=990002;StartedUtcTicks=$started.ToUniversalTime().Ticks;Path=(Join-Path $bundle 'Web/host/AgvDispatch.WebHost.exe')}}
$session | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bundle 'Runtime/launcher-session.json')
foreach($name in @('启动最新版Web.cmd','停止最新版Web.cmd','最新版Web.json')){Set-Content -LiteralPath (Join-Path $fixture $name) -Value 'fixture'}
Set-Content -LiteralPath (Join-Path $fixture '运行数据/后台5199/communication-settings.json') -Value '{"sentinel":true}'
Set-Content -LiteralPath (Join-Path $fixture '运行数据/Web工程方案/plan.json') -Value '{"sentinel":true}'
New-Item -ItemType Directory -Path (Join-Path $fixture '运行数据/engineering-center') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $fixture '运行数据/engineering-center/version.json') -Value '{"engineeringVersion":"preserved"}'
& $PythonPath -c 'import sqlite3,sys;c=sqlite3.connect(sys.argv[1]);c.execute("create table sentinel(value text)");c.execute("insert into sentinel values(?)",("preserved",));c.commit();c.close()' (Join-Path $fixture '运行数据/后台5199/agv-dispatch.db')
if($LASTEXITCODE -ne 0){throw 'Fixture SQLite initialization failed'}
$sim=(Resolve-Path -LiteralPath $sim).Path
@{simulationOnly=$true;pipe='Agv28StageLab-fixture';webPort=54321} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sim 'lab.json')
@{HandshakeExecution=@{PlcContractVersion='plc-28-stage-v2'};Plc=@{IpAddress='127.0.0.1';Port=54322};Agv=@{Robots=@(@{VehicleId='A';IpAddress='127.0.0.1';Port=54323;NavigationPort=54324})};Database=@{Path=(Join-Path $sim 'data/agv-dispatch.db')};Security=@{PipeName='Agv28StageLab-fixture'};TaskCreation=@{AutomaticCreationEnabled=$false}} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $sim 'service/appsettings.json')
@{Web=@{Port=54321;PipeName='Agv28StageLab-fixture'}} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sim 'webhost/appsettings.json')
function Get-Process {param($Id,$ErrorAction)
    $path=if($Id -eq 990001){Join-Path $global:SimulationPatchFixture.Bundle 'Service/AgvDispatch.Service.exe'}else{Join-Path $global:SimulationPatchFixture.Bundle 'Web/host/AgvDispatch.WebHost.exe'}
    [pscustomobject]@{Id=$Id;HasExited=$false;StartTime=$global:SimulationPatchFixture.Started;Path=$path}
}
function Get-CimInstance {param($ClassName,$Filter) [pscustomobject]@{CommandLine=('"'+(Join-Path $global:SimulationPatchFixture.Bundle 'Web/host/AgvDispatch.WebHost.exe')+'" --Web:Port=5199 --Web:PipeName=fixture-pipe')}}
function Invoke-RestMethod {param($Uri,$TimeoutSec)
    switch -Wildcard ($Uri){
        '*/api/config' {@{servicePipeName='fixture-pipe';adapter=@{informationalVersion=$global:SimulationPatchFixture.Version;sha256='fixture'};projectStoreId='fixture-store'}}
        '*/api/snapshot' {@{connected=$true;status=@{tasks=@{activeCount=0};service=@{processId=990001};safety=@{activeMapId='fixture-map';activeMapVersionId='fixture-map-v1'}};tasks=@{totalCount=5}}}
        '*/api/recovery' {@{released=$true}}
        default {throw "Unexpected fixture API: $Uri"}
    }
}
$scriptPath=Join-Path $PSScriptRoot 'Apply-SimulationWebPatch.ps1'
$oldHash=(Get-FileHash -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html')).Hash
& $scriptPath -BundleDirectory $bundle -PatchDirectory $patch -WebPayloadDirectory $payload -StaticPayloadDirectory $static -SimulationRoot $sim -PythonPath $PythonPath -SimulationPort 54325
if((Get-FileHash -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html')).Hash -ne $oldHash){throw 'Stage-only mode modified the installed frontend'}
if(!(Test-Path -LiteralPath (Join-Path $patch 'patch.json')) -or !(Test-Path -LiteralPath (Join-Path $patch 'backup/运行数据/后台5199/agv-dispatch.db'))){throw 'Missing staged patch or live SQLite backup'}
if(!(Test-Path -LiteralPath (Join-Path $patch 'backup/运行数据/engineering-center/version.json'))){throw 'Engineering center versions were not backed up'}
& $PythonPath -c 'import sqlite3,sys;c=sqlite3.connect(sys.argv[1]);assert c.execute("select value from sentinel").fetchone()[0]=="preserved";c.close()' (Join-Path $patch 'backup/运行数据/后台5199/agv-dispatch.db')
if($LASTEXITCODE -ne 0){throw 'SQLite online backup contents differ'}
Set-Content -LiteralPath (Join-Path $patch 'payload/Web/wwwroot/index.html') -Value 'tampered'
$rejected=$false
try{& $scriptPath -BundleDirectory $bundle -PatchDirectory $patch -Apply}catch{if($_.Exception.Message -like '*补丁内容变化*'){$rejected=$true}else{throw}}
if(!$rejected -or (Get-FileHash -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html')).Hash -ne $oldHash){throw 'Tampered patch must fail before stopping or mutating the fixture'}
Write-Output "PASS: stage-only unchanged, dependency checks, original program/data/settings/projects/pointers backups, SQLite backup integrity, tampered payload rejected before stopping. Fixtures retained: $fixture ; $sim"
