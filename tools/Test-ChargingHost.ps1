param([string]$ServiceDirectory='', [string]$WebHostDirectory='', [switch]$RequireChargerReachable)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$ServiceDirectory){$ServiceDirectory=Join-Path $project 'AgvDispatch.Service/bin/Debug/net10.0-windows10.0.19041.0/win-x64'}
if(!$WebHostDirectory){$WebHostDirectory=Join-Path $project 'AgvDispatch.WebHost/bin/Debug/net10.0-windows10.0.19041.0'}
$root=Join-Path ([IO.Path]::GetTempPath()) ('agv-charging-host-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $project 'AgvDispatch.Service/appsettings.example.json') -Destination (Join-Path $root 'appsettings.json')
Copy-Item -LiteralPath (Join-Path $project 'AgvDispatch.Service/NLog.config') -Destination $root
$pipeName='AgvDispatch.ChargingSmoke.'+[guid]::NewGuid().ToString('N')
$probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$probe.Start();$port=$probe.LocalEndpoint.Port;$probe.Stop()
function Start-Isolated([string]$exe,[string[]]$arguments){
    $info=[Diagnostics.ProcessStartInfo]::new($exe)
    $info.WorkingDirectory=$root;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
    foreach($arg in $arguments){$info.ArgumentList.Add($arg)}
    return [Diagnostics.Process]::Start($info)
}
$service=$null;$web=$null
try{
    $serviceArguments=@('--Database:Path',(Join-Path $root 'isolated.db'),'--Security:PipeName',$pipeName)
    if(!$RequireChargerReachable){$serviceArguments += '--ChargerMonitoring:Enabled=False'}
    $service=Start-Isolated (Join-Path $ServiceDirectory 'AgvDispatch.Service.exe') $serviceArguments
    $web=Start-Isolated (Join-Path $WebHostDirectory 'AgvDispatch.WebHost.exe') @('--Web:Port',"$port",'--Web:PipeName',$pipeName,'--Web:StaticPath',(Join-Path $project 'AgvDispatch.Web/dist'),'--Web:ProjectStorePath',(Join-Path $root 'projects'))
    $reply=$null;$deadline=[DateTime]::UtcNow.AddSeconds(35)
    while([DateTime]::UtcNow -lt $deadline){
        if($service.HasExited -or $web.HasExited){throw '隔离后台或Web进程提前退出。'}
        try{$reply=Invoke-RestMethod "http://127.0.0.1:$port/api/charging" -TimeoutSec 3;break}catch{Start-Sleep -Milliseconds 250}
    }
    if(!$reply -or $reply.enabled -ne $false -or $reply.options.startBatteryPercent -ne 25 -or $reply.options.targetBatteryPercent -ne 80 -or $reply.sessions.Count -ne 0){throw '真实进程DI/IPC充电状态与默认配置不符。'}
    if($RequireChargerReachable){
        for($i=0;$i -lt 20 -and $reply.chargers.Count -eq 0;$i++){Start-Sleep -Milliseconds 250; $reply=Invoke-RestMethod "http://127.0.0.1:$port/api/charging" -TimeoutSec 3}
        if($reply.chargers.Count -lt 1 -or $reply.chargers[0].ackPortReachable -ne $true -or $reply.chargers[0].statusPortReachable -ne $true){throw '充电桩常驻通信检查未取得20207和20204可达结果。'}
    }
    $requestId=[guid]::NewGuid().ToString('N')
    $requestBody=@{requestId=$requestId;vehicleId='AGV-01';requiredPositiveSeconds=60;returnSiteId='LM44'} | ConvertTo-Json
    $disabledRequest=Invoke-RestMethod "http://127.0.0.1:$port/api/charging/request" -Method Post -ContentType 'application/json' -Body $requestBody -SkipHttpErrorCheck -TimeoutSec 5
    if($disabledRequest.accepted -ne $false -or $disabledRequest.message -notmatch '未启用'){throw '默认关闭时充电申请必须明确拒绝。'}
    $response=Invoke-WebRequest "http://127.0.0.1:$port/" -TimeoutSec 5
    if($response.Content -notmatch 'assets/index-'){throw 'Web静态首页不可用。'}
    $badStop=Invoke-WebRequest "http://127.0.0.1:$port/api/charging/stop" -Method Post -ContentType 'application/json' -Body '{"sessionId":"missing","reason":"隔离验证不存在会话不可停止"}' -SkipHttpErrorCheck -TimeoutSec 5
    if($badStop.StatusCode -lt 400){throw '不存在的会话不应接受停止操作。'}
    if(Test-Path -LiteralPath (Join-Path $root 'charging-sessions.json')){throw '无车辆默认禁用时不得创建充电会话。'}
    Write-Output "PASS: 真实Service/Web进程依赖构造、Named Pipe充电查询与申请、Web静态入口、无效停止拒绝、默认禁用不产生会话；临时端口=$port。Evidence=$root ChargerChecked=$RequireChargerReachable"
}finally{
    foreach($process in @($web,$service)){if($process){if(!$process.HasExited){$process.Kill();$process.WaitForExit(5000)|Out-Null};$process.Dispose()}}
}
