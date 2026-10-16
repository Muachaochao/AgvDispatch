param([string]$TestIdentity = [Environment]::UserName, [ValidateSet('GetMapSwitch', 'GetExecutionRecovery')][string]$Operation = 'GetMapSwitch')
$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskHostFile = Join-Path $taskProjectRoot 'AgvDispatch.Service/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/AgvDispatch.Service.exe'
$taskTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('agv-map-switch-host-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskTestRoot | Out-Null
$taskPipe = 'AgvMapSwitchSmoke-' + [Guid]::NewGuid().ToString('N')
$taskArguments = @(
    "--Database:Path=$(Join-Path $taskTestRoot 'test.db')",
    "--Security:PipeName=$taskPipe",
    "--Security:Users:0:WindowsIdentity=$TestIdentity",
    '--Security:Users:0:Permissions:0=View',
    '--Agv:Enabled=false', '--Agv:AllowCommands=false', '--Plc:Enabled=false', '--Plc:AllowWrites=false',
    '--TaskCreation:Enabled=false', '--Dispatching:Enabled=false', '--AgvCommandOutbox:Enabled=false', '--DispatchTaskSteps:Enabled=false'
)
$taskHostProcess = Start-Process -FilePath $taskHostFile -ArgumentList $taskArguments -WorkingDirectory $taskTestRoot -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $taskTestRoot 'stdout.txt') -RedirectStandardError (Join-Path $taskTestRoot 'stderr.txt')
$taskPipeClient = [IO.Pipes.NamedPipeClientStream]::new('.', $taskPipe, [IO.Pipes.PipeDirection]::InOut)
try {
    $taskPipeClient.Connect(10000)
    $taskPayload = [Text.Encoding]::UTF8.GetBytes((@{ operation = $Operation; payload = @{} } | ConvertTo-Json -Compress))
    $taskPrefix = [BitConverter]::GetBytes([int]$taskPayload.Length)
    $taskPipeClient.Write($taskPrefix, 0, 4)
    $taskPipeClient.Write($taskPayload, 0, $taskPayload.Length)
    $taskPipeClient.Flush()
    $taskReader = [IO.BinaryReader]::new($taskPipeClient, [Text.Encoding]::UTF8, $true)
    $taskLength = $taskReader.ReadInt32()
    if ($taskLength -lt 1 -or $taskLength -gt 1000000) { throw '接口响应长度错误' }
    $taskResponse = [Text.Encoding]::UTF8.GetString($taskReader.ReadBytes($taskLength)) | ConvertFrom-Json
    if (-not $taskResponse.success) { throw $taskResponse.error }
    if ($Operation -eq 'GetMapSwitch' -and $taskResponse.payload.mode -ne 'Running') { throw "初始化状态不正确：$($taskResponse.payload.mode)" }
    if ($Operation -eq 'GetExecutionRecovery' -and (-not $taskResponse.payload.released -or [string]::IsNullOrWhiteSpace($taskResponse.payload.sessionId))) { throw "空现场启动恢复状态不正确：$($taskResponse.payload | ConvertTo-Json -Compress)" }
    Write-Output "PASS: 隔离服务启动与Named Pipe $Operation 接口；独立数据库、独立管道、车辆命令和PLC写入均禁用。"
    Write-Output "测试日志：$taskTestRoot"
} catch {
    Get-Content -LiteralPath (Join-Path $taskTestRoot 'stderr.txt') -Tail 15
    Get-Content -LiteralPath (Join-Path $taskTestRoot 'stdout.txt') -Tail 15
    throw
} finally {
    $taskPipeClient.Dispose()
    if (-not $taskHostProcess.HasExited) { Stop-Process -Id $taskHostProcess.Id }
    $taskHostProcess.Dispose()
}
