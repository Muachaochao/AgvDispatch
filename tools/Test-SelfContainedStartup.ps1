param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$taskProjectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskReleaseRoot=[IO.Path]::GetFullPath((Join-Path $taskProjectRoot '../发布包'))
$taskEvidence=[IO.Path]::GetFullPath($EvidenceDirectory)
$taskPrivate=Join-Path ([IO.Path]::GetTempPath()) ('agv-selfcontained-'+[Guid]::NewGuid().ToString('N'))
$taskPipe='agv-selfcontained-'+[Guid]::NewGuid().ToString('N')
$taskProcesses=@()
$taskChecks=@()
function Check([bool]$condition,[string]$name){if(-not $condition){throw $name};$script:taskChecks+=$name;Write-Output "PASS $name"}
function Copy-Isolated([string]$folder,[string]$target){
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach($taskItem in Get-ChildItem -LiteralPath $folder){
        if($taskItem.Name -in @('data','logs') -or $taskItem.Name -like 'appsettings*.json'){continue}
        Copy-Item -LiteralPath $taskItem.FullName -Destination $target -Recurse
    }
}
function Start-Isolated([string]$exe,[string]$directory,[string]$label){
    $taskInfo=[Diagnostics.ProcessStartInfo]::new($exe)
    $taskInfo.WorkingDirectory=$directory
    $taskInfo.UseShellExecute=$false
    $taskInfo.CreateNoWindow=$true
    $taskInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $taskInfo.Environment['DOTNET_ROOT']=Join-Path $taskPrivate 'no-global-runtime'
    $taskInfo.Environment['DOTNET_ROOT_X64']=Join-Path $taskPrivate 'no-global-runtime'
    $taskInfo.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    $taskInfo.Environment['DOTNET_ENVIRONMENT']='Production'
    $taskInfo.Environment['AGV_DISPATCH_DATABASE_PATH']=Join-Path $taskPrivate 'private.db'
    $taskInfo.Environment['COREHOST_TRACE']='1'
    $taskInfo.Environment['COREHOST_TRACEFILE']=Join-Path $taskEvidence "$label-host.log"
    return [Diagnostics.Process]::Start($taskInfo)
}
function Read-Exact([IO.Stream]$stream,[int]$count){
    $taskBytes=[byte[]]::new($count);$taskOffset=0
    while($taskOffset -lt $count){
        $taskRead=$stream.ReadAsync($taskBytes,$taskOffset,$count-$taskOffset)
        if(-not $taskRead.Wait(10000)){throw 'Private pipe response timeout'}
        if($taskRead.Result -le 0){throw 'Private pipe response EOF'}
        $taskOffset+=$taskRead.Result
    }
    return ,$taskBytes
}
try{
    $taskBackend=Join-Path $taskPrivate 'backend';$taskFrontend=Join-Path $taskPrivate 'frontend'
    Copy-Isolated (Join-Path $taskReleaseRoot '调度Windows服务') $taskBackend
    Copy-Isolated (Join-Path $taskReleaseRoot 'WPF上位机') $taskFrontend
    $taskConfig=Get-Content -LiteralPath (Join-Path $taskProjectRoot 'AgvDispatch.Service/appsettings.json') -Raw | ConvertFrom-Json
    $taskConfig.Database.Path=Join-Path $taskPrivate 'private.db'
    $taskConfig.Security.PipeName=$taskPipe
    $taskConfig.Security.Users=@(@{Enabled=$true;WindowsIdentity=[Security.Principal.WindowsIdentity]::GetCurrent().Name;DisplayName='isolated startup test';Permissions=@('View')})
    $taskConfig.Plc.Enabled=$false;$taskConfig.Plc.AllowWrites=$false
    $taskConfig.Agv.Enabled=$false;$taskConfig.Agv.AllowCommands=$false;$taskConfig.Agv.Robots=@()
    $taskConfig.Dispatching.Enabled=$false;$taskConfig.TaskCreation.Enabled=$false
    $taskConfig.Map.Enabled=$false;$taskConfig.ExecutionIdentityAcquisition.Enabled=$false
    $taskConfig | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $taskBackend 'appsettings.json') -Encoding utf8
    @{Service=@{Transport='NamedPipe';PipeName=$taskPipe}} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskFrontend 'appsettings.json') -Encoding utf8
    foreach($taskApp in @(@{Directory=$taskBackend;Name='AgvDispatch.Service'},@{Directory=$taskFrontend;Name='AgvDispatch.Wpf'})){
        $taskRuntime=Get-Content -LiteralPath (Join-Path $taskApp.Directory ($taskApp.Name+'.runtimeconfig.json')) -Raw | ConvertFrom-Json
        Check (-not $taskRuntime.runtimeOptions.frameworks -and -not $taskRuntime.runtimeOptions.framework -and $taskRuntime.runtimeOptions.includedFrameworks.Count -gt 0) ($taskApp.Name+' uses included frameworks')
    }
    $taskService=Start-Isolated (Join-Path $taskBackend 'AgvDispatch.Service.exe') $taskBackend 'backend'
    $taskProcesses+=$taskService
    $taskStream=[IO.Pipes.NamedPipeClientStream]::new('.',$taskPipe,[IO.Pipes.PipeDirection]::InOut)
    try{
        $taskStream.Connect(20000)
        $taskRequest=[Text.Encoding]::UTF8.GetBytes('{"operation":"GetSystemStatus","payload":{}}')
        $taskHeader=[BitConverter]::GetBytes([int]$taskRequest.Length)
        $taskStream.Write($taskHeader,0,4);$taskStream.Write($taskRequest,0,$taskRequest.Length);$taskStream.Flush()
        $taskLength=[BitConverter]::ToInt32((Read-Exact $taskStream 4),0)
        if($taskLength -le 0 -or $taskLength -gt 16777216){throw 'Invalid private response length'}
        $taskResponse=[Text.Encoding]::UTF8.GetString((Read-Exact $taskStream $taskLength)) | ConvertFrom-Json
        Check ($taskResponse.success) 'Private backend responds over unique Named Pipe'
        $taskSafety=$taskResponse.payload.safety
        Check ($null -ne $taskSafety -and -not $taskSafety.agvCommunicationEnabled -and -not $taskSafety.agvCommandsAllowed -and -not $taskSafety.plcCommunicationEnabled -and -not $taskSafety.plcWritesAllowed -and -not $taskSafety.dispatchingEnabled) 'Actual backend diagnostics confirm device communication/writes/dispatch disabled'
        $taskResponse | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $taskEvidence 'private-system-status.json') -Encoding utf8
    }finally{$taskStream.Dispose()}
    $taskGeometryStream=[IO.Pipes.NamedPipeClientStream]::new('.',$taskPipe,[IO.Pipes.PipeDirection]::InOut)
    try{
        $taskGeometryStream.Connect(10000)
        $taskRequest=[Text.Encoding]::UTF8.GetBytes('{"operation":"GetTrafficGeometryPreview","payload":{"firstStepId":"NO-STEP-A","secondStepId":"NO-STEP-B"}}')
        $taskHeader=[BitConverter]::GetBytes([int]$taskRequest.Length)
        $taskGeometryStream.Write($taskHeader,0,4);$taskGeometryStream.Write($taskRequest,0,$taskRequest.Length);$taskGeometryStream.Flush()
        $taskLength=[BitConverter]::ToInt32((Read-Exact $taskGeometryStream 4),0)
        if($taskLength -le 0 -or $taskLength -gt 16777216){throw 'Invalid geometry response length'}
        $taskGeometryResponse=[Text.Encoding]::UTF8.GetString((Read-Exact $taskGeometryStream $taskLength)) | ConvertFrom-Json
        Check ($taskGeometryResponse.success -and -not $taskGeometryResponse.payload.available -and -not $taskGeometryResponse.payload.resourcesLocked -and -not $taskGeometryResponse.payload.resourcesReleased) 'Production geometry endpoint registered and unavailable without authoritative context'
        $taskGeometryResponse | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $taskEvidence 'private-geometry-status.json') -Encoding utf8
    }finally{$taskGeometryStream.Dispose()}
    $taskPreviewStream=[IO.Pipes.NamedPipeClientStream]::new('.',$taskPipe,[IO.Pipes.PipeDirection]::InOut)
    try{
        $taskPreviewStream.Connect(10000)
        $taskRequest=[Text.Encoding]::UTF8.GetBytes('{"operation":"GetTaskRoutePreview","payload":{"stationId":"NO-STATION","vehicleId":"NO-VEHICLE","mapId":"NO-MAP","mapVersionId":"NO-VERSION","parameterRevision":"initial"}}')
        $taskHeader=[BitConverter]::GetBytes([int]$taskRequest.Length)
        $taskPreviewStream.Write($taskHeader,0,4);$taskPreviewStream.Write($taskRequest,0,$taskRequest.Length);$taskPreviewStream.Flush()
        $taskLength=[BitConverter]::ToInt32((Read-Exact $taskPreviewStream 4),0)
        if($taskLength -le 0 -or $taskLength -gt 16777216){throw 'Invalid task preview response length'}
        $taskPreviewResponse=[Text.Encoding]::UTF8.GetString((Read-Exact $taskPreviewStream $taskLength)) | ConvertFrom-Json
        Check ($taskPreviewResponse.success -and -not $taskPreviewResponse.payload.verified -and $taskPreviewResponse.payload.legs.Count -eq 0 -and $taskPreviewResponse.payload.segments.Count -eq 0) 'Production readonly task preview endpoint refuses missing active map'
        $taskPreviewResponse | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $taskEvidence 'private-task-route-preview.json') -Encoding utf8
    }finally{$taskPreviewStream.Dispose()}
    $taskService.Refresh()
    $taskCore=$taskService.Modules | Where-Object ModuleName -EQ 'coreclr.dll' | Select-Object -First 1
    Check ($taskCore.FileName -eq (Join-Path $taskBackend 'coreclr.dll')) 'Backend loaded local coreclr rather than installed runtime'
    $taskWpf=Start-Isolated (Join-Path $taskFrontend 'AgvDispatch.Wpf.exe') $taskFrontend 'frontend'
    $taskProcesses+=$taskWpf
    $taskDeadline=[DateTime]::UtcNow.AddSeconds(30)
    do{
        Start-Sleep -Milliseconds 250;$taskWpf.Refresh()
        if($taskWpf.HasExited){throw "WPF startup exited: $($taskWpf.ExitCode)"}
    }while($taskWpf.MainWindowTitle -ne '铝屑回收智能调度平台' -and [DateTime]::UtcNow -lt $taskDeadline)
    Check ($taskWpf.MainWindowHandle -ne [IntPtr]::Zero -and $taskWpf.MainWindowTitle -eq '铝屑回收智能调度平台') 'WPF actual main window initialized'
    $taskCore=$taskWpf.Modules | Where-Object ModuleName -EQ 'coreclr.dll' | Select-Object -First 1
    Check ($taskCore.FileName -eq (Join-Path $taskFrontend 'coreclr.dll')) 'WPF loaded local coreclr rather than installed runtime'
    $taskDesktop=$taskWpf.Modules | Where-Object ModuleName -EQ 'wpfgfx_cor3.dll' | Select-Object -First 1
    Check ($taskDesktop.FileName -eq (Join-Path $taskFrontend 'wpfgfx_cor3.dll')) 'WPF loaded bundled desktop rendering runtime'
    @{Checks=$taskChecks;PrivateDirectory=$taskPrivate;Pipe=$taskPipe;WpfTitle=$taskWpf.MainWindowTitle;WpfProcessId=$taskWpf.Id;ServiceProcessId=$taskService.Id;
        Scope='actual copied Release executables; unique pipe, empty private DB, devices disabled; local runtime module verification'} |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskEvidence 'startup-result.json') -Encoding utf8
}finally{
    foreach($taskProcess in $taskProcesses){
        try{if(-not $taskProcess.HasExited){if($taskProcess.MainWindowHandle -ne [IntPtr]::Zero){$null=$taskProcess.CloseMainWindow();$null=$taskProcess.WaitForExit(3000)}
            if(-not $taskProcess.HasExited){$taskProcess.Kill();$null=$taskProcess.WaitForExit(10000)}}}finally{$taskProcess.Dispose()}
    }
}
