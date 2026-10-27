param([int]$Port = 5297, [switch]$Build, [switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
if ($Port -eq 5199 -or $Port -lt 1024 -or $Port -gt 65535) { throw '模拟入口必须使用独立临时端口，禁止使用日常5199。' }
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $repo '.artifacts\engineering-web-simulation'
$manifest = Join-Path $work 'instance.json'
$runtime = Join-Path $work 'runtime'
$static = Join-Path $work 'static'
$exe = Join-Path $runtime 'AgvDispatch.WebHost.exe'
$url = "http://127.0.0.1:$Port/"
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    throw "端口$Port已在使用。请先运行本模拟入口的停止脚本；不会接管或重启其他实例。"
}
New-Item -ItemType Directory -Path $work -Force | Out-Null
if ($Build -or !(Test-Path -LiteralPath $exe)) {
    Push-Location (Join-Path $repo 'AgvDispatch.Web')
    try { & npm.cmd run build; if ($LASTEXITCODE -ne 0) { throw 'Web构建失败。' } } finally { Pop-Location }
    & dotnet publish (Join-Path $repo 'AgvDispatch.WebHost\AgvDispatch.WebHost.csproj') -c Release -o $runtime --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WebHost构建失败。' }
    New-Item -ItemType Directory -Path $static -Force | Out-Null
    Copy-Item -Path (Join-Path $repo 'AgvDispatch.Web\dist\*') -Destination $static -Recurse -Force
}
$arguments = @("--Web:Port=$Port", '--Web:EngineeringSimulationOnly=true', '--Web:PipeName=ENGINEERING-NO-DEVICE', ('--Web:StaticPath="' + $static + '"'), ('--Web:EngineeringStorePath="' + (Join-Path $work 'data\engineering') + '"'), ('--Web:ProjectStorePath="' + (Join-Path $work 'data\projects') + '"'))
$instance = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $runtime -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'host.log') -RedirectStandardError (Join-Path $work 'host-error.log') -PassThru
@{ pid = $instance.Id; executable = $exe; port = $Port; url = $url; startedAt = [DateTimeOffset]::Now.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    if ($instance.HasExited) { throw "模拟后台启动失败，请检查$work中的日志。" }
    try { $config = Invoke-RestMethod ($url + 'api/config'); if ($config.engineeringSimulationOnly -eq $true) { $ready = $true; break } } catch { }
    Start-Sleep -Milliseconds 250
}
if (!$ready) { throw "模拟后台尚未就绪，请检查$work中的日志。" }
Write-Host "工程隔离模拟已启动：${url}#/engineering"
Write-Host '禁止设备、任务与现场恢复接口；没有启动调度服务，不连接PLC或AGV。日常5199未改动。'
if (!$NoBrowser) { Start-Process ($url + '#/engineering') }
