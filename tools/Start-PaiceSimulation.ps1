#requires -Version 7.0
param([string]$JavaHome='', [string]$MavenPath='', [switch]$Rebuild)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot
$application=Join-Path $project 'integrations/PaiceSimulation'
$runtime=Join-Path $application '.runtime'
$frontend=Join-Path $application 'agv-frontend'
$jar=Join-Path $application 'agv-backend/target/agv-backend-1.0.0.jar'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$lifecycleLock=[IO.File]::Open((Join-Path $runtime 'lifecycle.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
$record=Join-Path $runtime 'processes.json'
if(Test-Path -LiteralPath $record){throw '独立应用已有启动记录，请先运行 Stop-PaiceSimulation.ps1 核对并停止。'}
foreach($port in @(5270,5271)){
    if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){throw "端口 $port 已被占用。"}
}
if(!$JavaHome){
    $JavaHome=@($env:JAVA_HOME,'C:/Program Files/JetBrains/DataGrip 2023.2.3/jbr') | Where-Object {$_ -and (Test-Path -LiteralPath (Join-Path $_ 'bin/java.exe'))} | Select-Object -First 1
}
if(!$JavaHome){throw '找不到 Java 17，请通过 -JavaHome 指定 JDK 目录。'}
$java=[IO.Path]::GetFullPath((Join-Path $JavaHome 'bin/java.exe'))
if(!(Test-Path -LiteralPath $java)){throw '指定的 Java 可执行文件不存在。'}
$node=(Get-Command node -ErrorAction Stop).Source
$npm=(Get-Command npm.cmd -ErrorAction Stop).Source
if(!(Test-Path -LiteralPath (Join-Path $frontend 'node_modules/vite/bin/vite.js'))){
    & $npm ci --prefix $frontend
    if($LASTEXITCODE){throw '前端依赖安装失败。'}
}
if($Rebuild -or !(Test-Path -LiteralPath $jar)){
    if(!$MavenPath){$MavenPath=(Get-Command mvn.cmd -ErrorAction SilentlyContinue).Source}
    if(!$MavenPath -and (Test-Path -LiteralPath 'F:/ChaoChao/AGV-Study/.tools/apache-maven-3.9.11/bin/mvn.cmd')){$MavenPath='F:/ChaoChao/AGV-Study/.tools/apache-maven-3.9.11/bin/mvn.cmd'}
    if(!$MavenPath){throw '找不到 Maven，请通过 -MavenPath 指定 mvn.cmd。'}
    $env:JAVA_HOME=$JavaHome
    & $MavenPath -B -q -f (Join-Path $application 'pom.xml') install
    if($LASTEXITCODE){throw 'Java 构建或测试失败。'}
    & $MavenPath -B -q -f (Join-Path $application 'pom.xml') -pl agv-backend -DskipTests package spring-boot:repackage
    if($LASTEXITCODE){throw 'Java 可执行包构建失败。'}
    & $npm --prefix $frontend run type-check
    if($LASTEXITCODE){throw '前端类型检查失败。'}
    & $npm --prefix $frontend run build
    if($LASTEXITCODE){throw '前端构建失败。'}
}
$created=@()
try{
    $backend=Start-Process -FilePath $java -ArgumentList @('-jar',('"'+$jar+'"'),'--server.address=127.0.0.1','--server.port=5271') -WorkingDirectory $runtime -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runtime 'backend.stdout.log') -RedirectStandardError (Join-Path $runtime 'backend.stderr.log')
    $created+=@{id=$backend.Id;startedUtcTicks=$backend.StartTime.ToUniversalTime().Ticks;path=$java}
    $vite=Join-Path $frontend 'node_modules/vite/bin/vite.js'
    $web=Start-Process -FilePath $node -ArgumentList @(('"'+$vite+'"'),'--host','127.0.0.1','--port','5270','--strictPort') -WorkingDirectory $frontend -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runtime 'frontend.stdout.log') -RedirectStandardError (Join-Path $runtime 'frontend.stderr.log')
    $created+=@{id=$web.Id;startedUtcTicks=$web.StartTime.ToUniversalTime().Ticks;path=$node}
    $created | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $record -Encoding utf8
    $ready=$false
    for($attempt=0;$attempt -lt 120;$attempt++){
        $backend.Refresh();$web.Refresh()
        if($backend.HasExited -or $web.HasExited){throw '独立应用进程提前退出，请查看 .runtime 日志。'}
        try{
            $health=Invoke-RestMethod 'http://127.0.0.1:5271/api/health' -TimeoutSec 2
            $response=Invoke-WebRequest 'http://127.0.0.1:5270/' -TimeoutSec 2
            if($response.StatusCode -eq 200){$ready=$true;break}
        }catch{}
        Start-Sleep -Milliseconds 500
    }
    if(!$ready){throw '独立应用启动超时。'}
    Write-Output '排策智演完整独立应用已启动 http://127.0.0.1:5270/'
}catch{
    foreach($entry in $created){
        $process=Get-Process -Id $entry.id -ErrorAction SilentlyContinue
        if($process -and $process.StartTime.ToUniversalTime().Ticks -eq $entry.startedUtcTicks -and $process.Path -eq $entry.path){$process.Kill();$process.WaitForExit()}
    }
    if(Test-Path -LiteralPath $record){Remove-Item -LiteralPath $record}
    throw
}

} finally { $lifecycleLock.Dispose() }
