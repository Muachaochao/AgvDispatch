param([Parameter(Mandatory=$true)][string]$BundleDirectory,[Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$launchEvidence=[IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $launchEvidence -Force | Out-Null
$launchPrivate=Join-Path ([IO.Path]::GetTempPath()) ('agv-launcher-'+[Guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($BundleDirectory)) -Destination $launchPrivate -Recurse
$launchProbe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$launchProbe.Start();$launchPort=$launchProbe.LocalEndpoint.Port;$launchProbe.Stop()
$launchManifestPath=Join-Path $launchPrivate 'release-manifest.json'
$launchManifest=Get-Content -LiteralPath $launchManifestPath -Raw | ConvertFrom-Json
$launchManifest.port=$launchPort
$launchManifest.pipeName='AgvLauncherTest-'+[Guid]::NewGuid().ToString('N')
$launchManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $launchManifestPath -Encoding utf8
$launchExecutable=Join-Path $launchPrivate 'Web/host/AgvDispatch.WebHost.exe'
$launchChecks=[Collections.Generic.List[string]]::new()
function Check([bool]$pass,[string]$message){if(!$pass){throw $message};$launchChecks.Add($message);Write-Output "PASS $message"}
function Invoke-Launcher([string]$tag,[switch]$Stop,[string]$ProjectStorePath=''){
    $launchInfo=[Diagnostics.ProcessStartInfo]::new($launchExecutable)
    $launchInfo.UseShellExecute=$false;$launchInfo.CreateNoWindow=$true;$launchInfo.WindowStyle='Hidden'
    $launchInfo.RedirectStandardOutput=$true;$launchInfo.RedirectStandardError=$true
    foreach($launchArg in @('--launch-local','--no-browser','--bundle-root',$launchPrivate)){$launchInfo.ArgumentList.Add($launchArg)}
    if($Stop){$launchInfo.ArgumentList.Add('--stop')}
    if($ProjectStorePath){$launchInfo.ArgumentList.Add('--project-store-path');$launchInfo.ArgumentList.Add($ProjectStorePath)}
    $launchProcess=[Diagnostics.Process]::Start($launchInfo)
    $launchOut=$launchProcess.StandardOutput.ReadToEndAsync();$launchErr=$launchProcess.StandardError.ReadToEndAsync()
    if(!$launchProcess.WaitForExit(90000)){$launchProcess.Kill();throw 'Launcher timeout'}
    # Child processes have no redirected handles; these should finish after the launcher exits.
    if(!$launchOut.Wait(5000) -or !$launchErr.Wait(5000)){throw 'Launcher leaked its output pipe to a child process'}
    [IO.File]::WriteAllText((Join-Path $launchEvidence "$tag.log"),$launchOut.Result+"`n"+$launchErr.Result)
    $launchCode=$launchProcess.ExitCode;$launchProcess.Dispose();return $launchCode
}
try{
    Check ((Invoke-Launcher 'first') -eq 0) 'One click starts a complete private release and waits for backend readiness'
    $launchStatePath=Join-Path $launchPrivate 'Runtime/launcher-session.json'
    $launchFirst=Get-Content -LiteralPath $launchStatePath -Raw | ConvertFrom-Json
    $launchSnapshot=Invoke-RestMethod "http://127.0.0.1:$launchPort/api/snapshot"
    Check ($launchSnapshot.connected -and $launchSnapshot.source -eq 'backend') 'Ready page reads the actual private backend, not synthetic fallback'
    $launchReport=Invoke-RestMethod "http://127.0.0.1:$launchPort/api/commissioning" -Method Post -ContentType application/json -Body '{}'
    Check (!$launchReport.runtime.handshakeEnabled) 'New handshake remains disabled'
    $launchSettings=Get-Content -LiteralPath (Join-Path $launchPrivate 'Service/appsettings.json') -Raw | ConvertFrom-Json
    Check (!$launchSettings.Plc.Enabled -and !$launchSettings.Plc.AllowWrites -and !$launchSettings.Agv.Enabled -and !$launchSettings.Agv.AllowCommands -and !$launchSettings.Dispatching.Enabled) 'Private bundle contains disabled device and execution configuration'
    Check ((Invoke-Launcher 'repeat') -eq 0) 'Repeated click succeeds without starting another backend'
    $launchSecond=Get-Content -LiteralPath $launchStatePath -Raw | ConvertFrom-Json
    Check ($launchFirst.Service.Id -eq $launchSecond.Service.Id -and $launchFirst.Web.Id -eq $launchSecond.Web.Id) 'Repeated click preserves both recorded process identities'
    Check ((Invoke-Launcher 'stop' -Stop) -eq 0) 'Explicit stop affects only recorded private release processes'
    Check (!(Get-Process -Id $launchFirst.Service.Id,$launchFirst.Web.Id -ErrorAction SilentlyContinue)) 'Both owned processes stopped'
    $launchProbe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$launchPort);$launchProbe.Start()
    try{Check ((Invoke-Launcher 'occupied') -ne 0) 'Occupied port refuses startup without stopping the listener'}finally{$launchProbe.Stop()}
    $launchServiceHash=(Get-FileHash -LiteralPath (Join-Path $launchPrivate 'Service/AgvDispatch.Service.dll')).Hash
    Check ($launchServiceHash -eq (Get-FileHash -LiteralPath (Join-Path ([IO.Path]::GetFullPath($BundleDirectory)) 'Service/AgvDispatch.Service.dll')).Hash) 'Private launch leaves release binaries unchanged'
    $launchShared=Join-Path $launchPrivate 'SharedProjectData'
    Check ((Invoke-Launcher 'shared' -ProjectStorePath $launchShared) -eq 0) 'Explicit shared project directory starts without changing device controls'
    $launchProjectId=[Guid]::NewGuid().ToString()
    $launchPlan=@{schema='agv-web-plan';version=1;id=$launchProjectId;name='launcher project';updatedAt=[DateTimeOffset]::UtcNow.ToString('o');definition=@{name='draft';source='';coordinateSystem='TestCartesian';unit='m';nodes=@();edges=@();resources=@()};scene=@();vehicles=@();waypoints=@()}
    $launchSaved=Invoke-RestMethod "http://127.0.0.1:$launchPort/api/projects" -Method Post -ContentType application/json -Body (@{expectedRevision='';plan=$launchPlan} | ConvertTo-Json -Depth 15)
    Check ([IO.File]::Exists((Join-Path $launchShared ($launchProjectId.Replace('-','')+'/current.json')))) 'Project archive is written to the explicit shared directory'
    Check ((Invoke-Launcher 'different-store') -ne 0) 'Running release cannot be silently reused with a different project directory'
    Invoke-Launcher 'shared-stop' -Stop | Out-Null
    Check ((Invoke-Launcher 'shared-restart' -ProjectStorePath $launchShared) -eq 0) 'Restart reuses the shared project directory'
    $launchRestored=Invoke-RestMethod "http://127.0.0.1:$launchPort/api/projects/$launchProjectId"
    Check ($launchRestored.revision -eq $launchSaved.revision) 'Project content and revision survive a real WebHost process restart'
}finally{
    try{Invoke-Launcher 'cleanup' -Stop | Out-Null}catch{Write-Warning $_.Exception.Message}
    @{checks=$launchChecks;privateDirectory=$launchPrivate;port=$launchPort;scope='Copied release only; random loopback port, unique pipe, empty private database, no browser or site devices'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $launchEvidence 'results.json') -Encoding utf8
}
