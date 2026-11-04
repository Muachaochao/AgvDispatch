#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [Parameter(Mandatory=$true)][string]$PatchDirectory,
    [string]$WebPayloadDirectory='',
    [string]$StaticPayloadDirectory='',
    [string]$SimulationRoot='',
    [string]$PythonPath='',
    [int]$SimulationPort=0,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
function FullPath([string]$Path){[IO.Path]::GetFullPath($Path).TrimEnd('\','/')}
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Json([string]$Path){Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable}
function SaveJson([string]$Path,$Value){
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $Value | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function NoLinks([string]$Path){
    for($ancestor=$Path;$ancestor;$ancestor=Split-Path $ancestor){
        if((Get-Item -LiteralPath $ancestor -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "路径不接受链接跳转：$Path"}
    }
}
function Owned($Identity){
    $process=Get-Process -Id $Identity.Id -ErrorAction Stop
    if($process.HasExited -or $process.StartTime.ToUniversalTime().Ticks -ne $Identity.StartedUtcTicks -or $process.Path -ne $Identity.Path){throw "进程归属已变化，拒绝停止：$($Identity.Id)"}
    return $process
}
function Describe($Process,[string]$Path){@{Id=$Process.Id;StartedUtcTicks=$Process.StartTime.ToUniversalTime().Ticks;Path=$Path}}
function StartWeb($Arguments){
    $info=[Diagnostics.ProcessStartInfo]::new($script:webExe)
    $info.WorkingDirectory=Split-Path $script:webExe
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    foreach($argument in $Arguments){$info.ArgumentList.Add([string]$argument)}
    return [Diagnostics.Process]::Start($info)
}
function DailyState {
    $config=Invoke-RestMethod 'http://127.0.0.1:5199/api/config' -TimeoutSec 10
    $snapshot=Invoke-RestMethod 'http://127.0.0.1:5199/api/snapshot' -TimeoutSec 15
    $recovery=Invoke-RestMethod 'http://127.0.0.1:5199/api/recovery' -TimeoutSec 10
    if(!$snapshot.connected -or $snapshot.status.tasks.activeCount -ne 0 -or !$recovery.released){throw '5199后台未连接、存在活动任务或恢复未放行；不进行Web切换。'}
    if($config.servicePipeName -ne $script:manifest.pipeName -or $snapshot.status.service.processId -ne $script:session.Service.Id -or $config.adapter.informationalVersion -ne $script:manifest.releaseId){throw '5199接口与发布目录、后台身份不匹配。'}
    return @{projectStoreId=$config.projectStoreId;adapterSha=$config.adapter.sha256;mapId=$snapshot.status.safety.activeMapId;mapVersion=$snapshot.status.safety.activeMapVersionId;taskCount=$snapshot.tasks.totalCount;serviceId=$snapshot.status.service.processId}
}
$bundle=FullPath $BundleDirectory;$patchRoot=FullPath $PatchDirectory
NoLinks $bundle;NoLinks $patchRoot
if($bundle -eq $patchRoot -or $patchRoot.StartsWith($bundle+'\',[StringComparison]::OrdinalIgnoreCase) -or $bundle.StartsWith($patchRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '补丁与发布目录不能相互包含。'}
$manifestPath=Join-Path $bundle 'release-manifest.json'
$sessionPath=Join-Path $bundle 'Runtime/launcher-session.json'
$manifest=Json $manifestPath;$session=Json $sessionPath
if($manifest.schema -ne 'agv-commissioning-release' -or $manifest.port -ne 5199 -or $session.ReleaseId -ne $manifest.releaseId){throw '仅支持原日常5199完整发布目录。'}
$webExe=Join-Path $bundle 'Web/host/AgvDispatch.WebHost.exe'
$serviceExe=Join-Path $bundle 'Service/AgvDispatch.Service.exe'
if($session.Web.Path -ne $webExe -or $session.Service.Path -ne $serviceExe){throw '启动记录指向其他目录。'}
$null=Owned $session.Service;$null=Owned $session.Web
$daily=DailyState
$dailyRoot=Join-Path (Split-Path $bundle) '运行数据'
NoLinks $dailyRoot
if($patchRoot -eq $dailyRoot -or $patchRoot.StartsWith($dailyRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $dailyRoot.StartsWith($patchRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '补丁/备份目录不能包含或位于日常运行数据中。'}
if(!$Apply){
    if(Test-Path -LiteralPath $patchRoot){throw '暂存目录已存在，拒绝覆盖；安装已核对的暂存补丁请显式使用-Apply。'}
    foreach($required in @($WebPayloadDirectory,$StaticPayloadDirectory,$SimulationRoot,$PythonPath)){if(!$required){throw '暂存必须提供WebPayloadDirectory、StaticPayloadDirectory、SimulationRoot、PythonPath。'}}
    $webSource=FullPath $WebPayloadDirectory;$staticSource=FullPath $StaticPayloadDirectory
    $sim=FullPath $SimulationRoot;$python=FullPath $PythonPath
    foreach($path in @($webSource,$staticSource,$sim,$python)){NoLinks $path}
    foreach($sourceRoot in @($webSource,$staticSource,$sim)){
        if($patchRoot -eq $sourceRoot -or $patchRoot.StartsWith($sourceRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $sourceRoot.StartsWith($patchRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '补丁/备份目录不能与源码暂存或实验目录互相包含。'}
    }
    if($SimulationPort -lt 1024 -or $SimulationPort -gt 65535 -or $SimulationPort -in @(5198,5199)){throw '仿真控制器需要独立临时端口。'}
    $controller=Join-Path $PSScriptRoot 'SimulationLab/controller.py'
    $artifacts=FullPath (Join-Path $PSScriptRoot '../artifacts')
    if((Split-Path $sim) -ne $artifacts -or (Split-Path $sim -Leaf) -notlike 'web-single-vehicle-*'){throw '仿真实例必须位于本工程artifacts/web-single-vehicle-*。'}
    if(!(Test-Path -LiteralPath $python -PathType Leaf) -or !(Test-Path -LiteralPath $controller -PathType Leaf)){throw 'Python或仿真控制器不存在。'}
    # Reuse the controller's complete loopback/data/pipe validation without starting it.
    $validationCode='import sys; sys.path.insert(0,sys.argv[1]); from controller import validate_root; validate_root(sys.argv[2])'
    & $python -c $validationCode (Split-Path $controller) $sim
    if($LASTEXITCODE -ne 0){throw '仿真实例隔离检查失败。'}
    $labConfig=Json (Join-Path $sim 'lab.json')
    $deviceConfig=Json (Join-Path $sim 'service/appsettings.json')
    $devicePorts=@($labConfig.webPort,$deviceConfig.Plc.Port)
    foreach($robot in $deviceConfig.Agv.Robots){$devicePorts+=@($robot.Port,$robot.NavigationPort)}
    if($SimulationPort -in $devicePorts){throw '仿真控制端口不能与实验Web或设备端口重复。'}
    $probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$SimulationPort)
    try{$probe.Start()}finally{$probe.Stop()}
    $newDll=Join-Path $webSource 'AgvDispatch.WebHost.dll'
    if([Diagnostics.FileVersionInfo]::GetVersionInfo($newDll).ProductVersion -ne $manifest.releaseId){throw "WebHost必须用基线InformationalVersion=$($manifest.releaseId)构建。"}
    $dependencies=@(Get-ChildItem -LiteralPath (Join-Path $bundle 'Web/host') -Filter 'AgvDispatch.*.dll' | Where-Object Name -ne 'AgvDispatch.WebHost.dll')
    if(!$dependencies.Count){throw '缺少基线共享程序集。'}
    foreach($dependency in $dependencies){
        $candidate=Join-Path $webSource $dependency.Name
        if(!(Test-Path -LiteralPath $candidate) -or (Hash $candidate) -ne (Hash $dependency.FullName)){throw "依赖程序集不同，需使用原发布引用编译WebHost：$($dependency.Name)"}
    }
    if(!(Test-Path -LiteralPath (Join-Path $staticSource 'index.html'))){throw '前端构建缺少index.html。'}
    foreach($file in Get-ChildItem -LiteralPath $staticSource -Recurse -Force){
        NoLinks $file.FullName
        if(!$file.PSIsContainer -and [IO.Path]::GetRelativePath($staticSource,$file.FullName).Replace('\','/') -notmatch '^(index\.html|assets/[\w.-]+)$'){throw '静态暂存仅允许index.html和assets文件。'}
    }
    New-Item -ItemType Directory -Path (Join-Path $patchRoot 'payload/Web/host'),(Join-Path $patchRoot 'payload/Web/wwwroot'),(Join-Path $patchRoot 'backup') -Force | Out-Null
    foreach($name in @('AgvDispatch.WebHost.dll','AgvDispatch.WebHost.pdb')){
        $source=Join-Path $webSource $name
        if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $patchRoot "payload/Web/host/$name")}
    }
    Copy-Item -LiteralPath (Join-Path $staticSource 'index.html') -Destination (Join-Path $patchRoot 'payload/Web/wwwroot/index.html')
    Copy-Item -LiteralPath (Join-Path $staticSource 'assets') -Destination (Join-Path $patchRoot 'payload/Web/wwwroot/assets') -Recurse
    $configPath=Join-Path $bundle 'Web/host/appsettings.json'
    $config=if(Test-Path -LiteralPath $configPath){Json $configPath}else{@{}}
    if(!$config.ContainsKey('Web')){$config.Web=@{}}
    $config.Web.Simulation=@{Enabled=$true;Root=$sim;PythonPath=$python;ControllerPath=$controller;Port=$SimulationPort}
    SaveJson (Join-Path $patchRoot 'payload/Web/host/appsettings.json') $config
    $entries=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $manifest.files){
        $relative=$entry.path.Replace('\','/')
        if($entries.ContainsKey($relative)){throw '发布清单存在重复路径。'}
        $entries.Add($relative,$entry)
    }
    $changes=@(foreach($source in Get-ChildItem -LiteralPath (Join-Path $patchRoot 'payload') -File -Recurse){
        $relative=[IO.Path]::GetRelativePath((Join-Path $patchRoot 'payload'),$source.FullName).Replace('\','/')
        $target=Join-Path $bundle $relative;NoLinks $target
        $before=if(Test-Path -LiteralPath $target){Hash $target}else{$null}
        if($entries.ContainsKey($relative) -and $before -ne $entries[$relative].sha256){throw "发布文件已变化：$relative"}
        $entry=@{path=$relative;bytes=$source.Length;sha256=(Hash $source.FullName)}
        $entries[$relative]=$entry
        @{path=$relative;beforeSha256=$before;afterSha256=$entry.sha256}
    })
    $patchId='simulation-web-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[guid]::NewGuid().ToString('N').Substring(0,6)
    $manifest.files=@($entries.Values)
    $manifest.appliedPatches=@($manifest.appliedPatches | Where-Object {$_})+@(@{patchId=$patchId;createdAt=[DateTimeOffset]::Now.ToString('o');scope='WebHost and frontend only; original Service remains running'})
    SaveJson (Join-Path $patchRoot 'payload/release-manifest.json') $manifest
    $changes+=@{path='release-manifest.json';beforeSha256=(Hash $manifestPath);afterSha256=(Hash (Join-Path $patchRoot 'payload/release-manifest.json'))}
    foreach($change in $changes){
        if($change.beforeSha256){
            $backup=Join-Path $patchRoot ('backup/program/'+$change.path)
            New-Item -ItemType Directory -Path (Split-Path $backup) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $bundle $change.path) -Destination $backup
        }
    }
    Copy-Item -LiteralPath $sessionPath -Destination (Join-Path $patchRoot 'backup/launcher-session.json')
    foreach($name in @('启动最新版Web.cmd','停止最新版Web.cmd','最新版Web.json')){
        Copy-Item -LiteralPath (Join-Path (Split-Path $bundle) $name) -Destination (Join-Path $patchRoot ('backup/'+$name))
    }
    # Back up live SQLite through its backup API, never copy a live main DB file.
    $db=Join-Path $dailyRoot '后台5199/agv-dispatch.db'
    $backupData=Join-Path $patchRoot 'backup/运行数据'
    New-Item -ItemType Directory -Path (Join-Path $backupData '后台5199') -Force | Out-Null
    $backupCode='import sqlite3,sys; s=sqlite3.connect("file:"+sys.argv[1].replace(chr(92),"/")+"?mode=ro",uri=True); d=sqlite3.connect(sys.argv[2]); s.backup(d); assert d.execute("pragma quick_check").fetchone()[0]=="ok"; d.close(); s.close()'
    & $python -c $backupCode $db (Join-Path $backupData '后台5199/agv-dispatch.db')
    if($LASTEXITCODE -ne 0){throw 'SQLite在线备份失败。'}
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $dailyRoot '后台5199') -File -Recurse){
        if($file.Name -match '\.(db|sqlite3?)(-wal|-shm|-journal)?$'){continue}
        NoLinks $file.FullName
        $destination=Join-Path (Join-Path $backupData '后台5199') ([IO.Path]::GetRelativePath((Join-Path $dailyRoot '后台5199'),$file.FullName))
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    foreach($name in @('Web工程方案','engineering-center','vendor-map-sources','display-areas')){
        $store=Join-Path $dailyRoot $name
        if(Test-Path -LiteralPath $store){
            foreach($file in Get-ChildItem -LiteralPath $store -Recurse -Force){NoLinks $file.FullName}
            Copy-Item -LiteralPath $store -Destination (Join-Path $backupData $name) -Recurse
        }
    }
    # CommandLineToArgvW preserves exact existing launch arguments without shell evaluation.
    if(-not ('AgvSimulationPatchArgv' -as [type])){Add-Type @'
using System; using System.Runtime.InteropServices;
public static class AgvSimulationPatchArgv {
 [DllImport("shell32.dll",SetLastError=true)] static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)]string command,out int count);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
 public static string[] Split(string command){int count;IntPtr pointer=CommandLineToArgvW(command,out count);if(pointer==IntPtr.Zero)throw new Exception("Command line parse failed");try{string[] result=new string[count];for(int i=0;i<count;i++)result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,i*IntPtr.Size));return result;}finally{LocalFree(pointer);}}
}
'@}
    $command=(Get-CimInstance Win32_Process -Filter "ProcessId = $($session.Web.Id)").CommandLine
    $arguments=[AgvSimulationPatchArgv]::Split($command)
    if($arguments[0] -ne $webExe){throw 'Web命令行身份不符。'}
    $null=Owned $session.Service;$null=Owned $session.Web
    $patch=@{schema='agv-simulation-web-patch-v1';patchId=$patchId;baseReleaseId=$manifest.releaseId;bundle=$bundle;sessionHash=(Hash $sessionPath);session=$session;daily=$daily;webArguments=@($arguments | Select-Object -Skip 1);files=$changes;createdAt=[DateTimeOffset]::Now.ToString('o')}
    SaveJson (Join-Path $patchRoot 'patch.json') $patch
    $backupHashes=@(Get-ChildItem -LiteralPath (Join-Path $patchRoot 'backup') -File -Recurse | ForEach-Object {@{path=[IO.Path]::GetRelativePath((Join-Path $patchRoot 'backup'),$_.FullName);sha256=(Hash $_.FullName)}})
    SaveJson (Join-Path $patchRoot 'backup-sha256.json') $backupHashes
    Write-Output "STAGED ONLY: $patchId. 已核对依赖、暂存Web补丁和原程序/在线数据库/设置/工程方案备份；未停止、修改或重启日常程序。核对patch.json后显式使用-Apply。"
    return
}
$patch=Json (Join-Path $patchRoot 'patch.json')
if(Test-Path -LiteralPath (Join-Path $patchRoot 'superseded.json')){throw '该暂存补丁已被标记为废弃，仅保留审阅记录，禁止安装。'}
if($patch.schema -ne 'agv-simulation-web-patch-v1' -or $patch.bundle -ne $bundle -or $patch.baseReleaseId -ne $manifest.releaseId -or (Hash $sessionPath) -ne $patch.sessionHash){throw '补丁身份、基线版本或运行会话已变化，需重新暂存。'}
$lock=[IO.FileStream]::new((Join-Path $bundle 'Runtime/launcher.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$newWeb=$null;$stopped=$false;$written=[Collections.Generic.List[object]]::new()
try{
    foreach($entry in (Json (Join-Path $patchRoot 'backup-sha256.json'))){
        $path=FullPath (Join-Path (Join-Path $patchRoot 'backup') $entry.path)
        if(!$path.StartsWith((Join-Path $patchRoot 'backup')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Hash $path) -ne $entry.sha256){throw '原始备份已变化。'}
    }
    foreach($entry in $patch.files){
        if($entry.path -notmatch '^(release-manifest\.json|Web/host/(AgvDispatch\.WebHost\.(dll|pdb)|appsettings\.json)|Web/wwwroot/(index\.html|assets/[\w.-]+))$'){throw '补丁超出Web范围。'}
        $target=Join-Path $bundle $entry.path;$source=Join-Path (Join-Path $patchRoot 'payload') $entry.path
        NoLinks $source;NoLinks $target
        if((Hash $source) -ne $entry.afterSha256){throw "补丁内容变化：$($entry.path)"}
        if($entry.beforeSha256){if((Hash $target) -ne $entry.beforeSha256){throw '日常程序已变化。'}}elseif(Test-Path -LiteralPath $target){throw '新增目标已存在。'}
    }
    $daily=DailyState
    $service=Owned $session.Service;$oldWeb=Owned $session.Web
    $oldWeb.Kill($false);$oldWeb.WaitForExit();$stopped=$true
    foreach($entry in $patch.files){
        $target=Join-Path $bundle $entry.path
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        $written.Add($entry)
        Copy-Item -LiteralPath (Join-Path (Join-Path $patchRoot 'payload') $entry.path) -Destination $target -Force
        if((Hash $target) -ne $entry.afterSha256){throw '写入哈希检查失败。'}
    }
    $newWeb=StartWeb $patch.webArguments
    $session.Web=Describe $newWeb $webExe
    SaveJson $sessionPath $session
    $deadline=[DateTime]::UtcNow.AddSeconds(45);$verified=$false
    while([DateTime]::UtcNow -lt $deadline){
        if($newWeb.HasExited){throw '新Web进程提前退出。'}
        $null=Owned $session.Service
        try{
            $config=Invoke-RestMethod 'http://127.0.0.1:5199/api/config' -TimeoutSec 3
            $snapshot=Invoke-RestMethod 'http://127.0.0.1:5199/api/snapshot' -TimeoutSec 5
            if($snapshot.connected){
                if($config.adapter.sha256 -ne (Hash (Join-Path $bundle 'Web/host/AgvDispatch.WebHost.dll')) -or $config.projectStoreId -ne $daily.projectStoreId -or $config.servicePipeName -ne $manifest.pipeName -or $snapshot.status.service.processId -ne $session.Service.Id -or $snapshot.status.safety.activeMapId -ne $daily.mapId -or $snapshot.status.safety.activeMapVersionId -ne $daily.mapVersion){throw '新Web版本、后台、工程路径或活动地图核对失败。'}
                $simulation=Invoke-RestMethod 'http://127.0.0.1:5199/api/simulation' -TimeoutSec 15
                if(!$simulation.enabled){throw '仿真入口未启用。'}
                $verified=$true;break
            }
        }catch [Microsoft.PowerShell.Commands.HttpResponseException] {} catch [System.Net.Http.HttpRequestException] {}
        Start-Sleep -Milliseconds 300
    }
    if(!$verified){throw '5199新Web未在时限内就绪。'}
    SaveJson (Join-Path $patchRoot 'applied.json') @{patchId=$patch.patchId;appliedAt=[DateTimeOffset]::Now.ToString('o');service=$session.Service;web=$session.Web;mapId=$daily.mapId;mapVersion=$daily.mapVersion}
    Write-Output "APPLIED: $($patch.patchId). 5199 Web已更新并核对；原Service PID $($session.Service.Id)保持运行，日常数据库、地图、通信设置、入口指针不变。"
}catch{
    $failure=$_
    if($stopped){
        if($newWeb -and !$newWeb.HasExited){$newWeb.Kill($false);$newWeb.WaitForExit()}
        foreach($entry in $written){
            $target=Join-Path $bundle $entry.path
            if($entry.beforeSha256){Copy-Item -LiteralPath (Join-Path (Join-Path $patchRoot 'backup/program') $entry.path) -Destination $target -Force}
            elseif(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target}
        }
        $restored=StartWeb $patch.webArguments
        $session.Web=Describe $restored $webExe
        SaveJson $sessionPath $session
        Write-Warning "Web安装失败，已恢复原Web程序并更新启动归属；未停止Service。原始原因：$failure"
    }
    throw $failure
}finally{$lock.Dispose()}
