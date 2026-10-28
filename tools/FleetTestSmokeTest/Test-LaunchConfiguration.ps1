$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../FleetLaunchConfiguration.ps1')
$fleetRoot=Join-Path ([IO.Path]::GetTempPath()) ('fleet-launch-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fleetRoot) | Out-Null
$script:fleetChecks=0
function Check-Fleet($Condition,[string]$Message){if(!$Condition){throw $Message};$script:fleetChecks++}
function Reject-Fleet([scriptblock]$Action,[string]$Message){try{& $Action | Out-Null}catch{$script:fleetChecks++;return};throw $Message}
$fleetOldCapture=$env:AGV_FLEET_LAUNCH_CAPTURE
$fleetOldStubExit=$env:AGV_FLEET_LAUNCH_STUB_EXIT
function Write-FleetTestFile([string]$Path,[string]$Text){
    [IO.File]::WriteAllText($Path,$Text.Replace("`r`n","`n").Replace("`n","`r`n"),[Text.UTF8Encoding]::new($false))
}
function Invoke-FleetStub {
    param([string]$Root,[string]$EntryName,[string]$Arguments='',[string[]]$ExpectedArguments=@(),[int]$ExpectedExitCode=0,[switch]$NoLaunch)
    $capture=Join-Path $Root 'received.json'
    if(Test-Path -LiteralPath $capture){Remove-Item -LiteralPath $capture -Force}
    $env:AGV_FLEET_LAUNCH_CAPTURE=$capture
    $driver=Join-Path $Root 'invoke-test.cmd'
    Write-FleetTestFile $driver "@echo off`r`nchcp 65001 >nul`r`ncall `"%~dp0$EntryName`" $Arguments`r`nexit /b %errorlevel%`r`n"
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$env:ComSpec
    $start.Arguments='/d /v:off /c ""'+$driver+'""'
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    try{
        if(!$process.WaitForExit(30000)){$process.Kill();throw 'Isolated CMD argument receiver timed out.'}
        $output=$process.StandardOutput.ReadToEnd()+$process.StandardError.ReadToEnd()
        Check-Fleet ($process.ExitCode -eq $ExpectedExitCode) ("Unexpected isolated CMD exit code for ${EntryName}: "+$output)
    }finally{$process.Dispose()}
    if($NoLaunch){
        Check-Fleet (!(Test-Path -LiteralPath $capture)) 'An unconfigured entry must not reach even the isolated receiver.'
    }else{
        Check-Fleet (Test-Path -LiteralPath $capture) ("CMD did not reach the isolated receiver: "+$output)
        $actual=@(Get-Content -LiteralPath $capture -Raw | ConvertFrom-Json)
        $actualJson=ConvertTo-Json -InputObject $actual -Compress
        $expectedJson=ConvertTo-Json -InputObject @($ExpectedArguments) -Compress
        Check-Fleet ($actualJson -ceq $expectedJson) ("CMD changed or dropped arguments. Expected $expectedJson; received $actualJson")
    }
}
try{
    $empty=Resolve-FleetLaunchSelection -Root $fleetRoot
    Check-Fleet ($empty.VehicleIds.Count -eq 0 -and $empty.MaxConcurrentTasks -eq 0) 'No defaults may invent vehicle identifiers.'
    Check-Fleet ((New-FleetLaunchEntryText $empty).Contains('exit /b 1')) 'Unconfigured double-click must show usage without a launch.'
    $legacy=Join-Path $fleetRoot '启动双车联调Web.cmd'
    [IO.File]::WriteAllText($legacy,'call Start.cmd --fleet-test-vehicles OLD-1,OLD-7')
    $preserved=Resolve-FleetLaunchSelection -Root $fleetRoot
    Check-Fleet (($preserved.VehicleIds -join ',') -eq 'OLD-1,OLD-7' -and $preserved.MaxConcurrentTasks -eq 2) 'Legacy literal IDs must remain compatible.'
    [IO.File]::WriteAllText($legacy,'call Start.cmd "--fleet-test-vehicles" "QUOTED-A,QUOTED-B,QUOTED-C" "--fleet-test-max-concurrent" "2" %*')
    $quoted=Resolve-FleetLaunchSelection -Root $fleetRoot
    Check-Fleet (($quoted.VehicleIds -join ',') -eq 'QUOTED-A,QUOTED-B,QUOTED-C' -and $quoted.MaxConcurrentTasks -eq 2) 'Quoted legacy option names, vehicle lists and concurrency must be preserved.'
    [IO.File]::WriteAllText($legacy,'call Start.cmd --fleet-test-vehicles "VALUE-A,VALUE-B,VALUE-C" --fleet-test-max-concurrent "1" %*')
    $quotedValues=Resolve-FleetLaunchSelection -Root $fleetRoot
    Check-Fleet (($quotedValues.VehicleIds -join ',') -eq 'VALUE-A,VALUE-B,VALUE-C' -and $quotedValues.MaxConcurrentTasks -eq 1) 'Quoted legacy values with ordinary option names must be preserved.'
    $multi=Resolve-FleetLaunchSelection -Root $fleetRoot -VehicleIds @('BOT-A','BOT-B','BOT-C','BOT-D') -MaxConcurrentTasks 3
    Check-Fleet ($multi.VehicleIds.Count -eq 4 -and $multi.MaxConcurrentTasks -eq 3) 'Explicit subset and concurrency must be kept.'
    [IO.File]::WriteAllText((Join-Path $fleetRoot '启动多车联调Web.cmd'),(New-FleetLaunchEntryText $multi))
    $roundTrip=Resolve-FleetLaunchSelection -Root $fleetRoot
    Check-Fleet (($roundTrip.VehicleIds -join ',') -eq 'BOT-A,BOT-B,BOT-C,BOT-D' -and $roundTrip.MaxConcurrentTasks -eq 3) 'Canonical entry must win and preserve concurrency on subsequent update.'
    $override=Resolve-FleetLaunchSelection -Root $fleetRoot -VehicleIds @('CUSTOM-X','CUSTOM-Y','CUSTOM-Z')
    Check-Fleet ($override.MaxConcurrentTasks -eq 3 -and ($override.VehicleIds -join ',') -eq 'CUSTOM-X,CUSTOM-Y,CUSTOM-Z') 'Explicit vehicles override prior identifiers with a new size-based default.'
    Reject-Fleet {Resolve-FleetLaunchSelection -Root $fleetRoot -VehicleIds @('same','SAME')} 'Duplicate IDs must be refused.'
    Reject-Fleet {Resolve-FleetLaunchSelection -Root $fleetRoot -VehicleIds @('ONE')} 'Single-vehicle list is not a multi-vehicle mode.'
    Reject-Fleet {Resolve-FleetLaunchSelection -Root $fleetRoot -VehicleIds @('VALID','bad id')} 'Invalid identifiers must be refused.'
    Reject-Fleet {Resolve-FleetLaunchSelection -Root $fleetRoot -VehicleIds @('A','B') -MaxConcurrentTasks 3} 'Concurrency outside the selected fleet must be refused.'
    # Exercise the generated CMDs against a receiver created only in this temporary
    # tree. Never invoke Build/Set-Latest, a real WebHost, a service or port 5199.
    $receiverPowerShell=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $stubRoots=@((Join-Path $fleetRoot 'root entry 空格'),(Join-Path $fleetRoot 'bundle entry 空格'))
    foreach($stubRoot in $stubRoots){
        [IO.Directory]::CreateDirectory($stubRoot) | Out-Null
        Write-FleetTestFile (Join-Path $stubRoot 'receive.ps1') @'
$ErrorActionPreference='Stop'
ConvertTo-Json -InputObject @($args) -Compress | Set-Content -LiteralPath $env:AGV_FLEET_LAUNCH_CAPTURE -Encoding utf8
exit ([int]$env:AGV_FLEET_LAUNCH_STUB_EXIT)
'@
        $stubText="@echo off`r`n`"$receiverPowerShell`" -NoProfile -File `"%~dp0receive.ps1`" %*`r`nexit /b %errorlevel%`r`n"
        Write-FleetTestFile (Join-Path $stubRoot '启动最新版Web.cmd') $stubText
        Write-FleetTestFile (Join-Path $stubRoot 'Start-Latest-Web.cmd') $stubText
    }
    $env:AGV_FLEET_LAUNCH_STUB_EXIT='0'
    $rootStub=$stubRoots[0];$bundleStub=$stubRoots[1]
    Write-FleetTestFile (Join-Path $rootStub '启动多车联调Web.cmd') (New-FleetLaunchEntryText $empty)
    Invoke-FleetStub $rootStub '启动多车联调Web.cmd' -NoLaunch -ExpectedExitCode 1
    Invoke-FleetStub $rootStub '启动多车联调Web.cmd' '--daily-data' -NoLaunch -ExpectedExitCode 1
    $bundleEntry=New-FleetLaunchEntryText -Selection $empty -LauncherEntryName 'Start-Latest-Web.cmd' -EntryName 'Start-Fleet-Test.cmd'
    Write-FleetTestFile (Join-Path $bundleStub 'Start-Fleet-Test.cmd') $bundleEntry
    Invoke-FleetStub $bundleStub 'Start-Fleet-Test.cmd' -NoLaunch -ExpectedExitCode 1
    Write-FleetTestFile (Join-Path $rootStub '启动多车联调Web.cmd') (New-FleetLaunchEntryText $multi)
    $defaultArgs=@('--fleet-test-vehicles','BOT-A,BOT-B,BOT-C,BOT-D','--fleet-test-max-concurrent','3')
    Invoke-FleetStub $rootStub '启动多车联调Web.cmd' -ExpectedArguments $defaultArgs
    $spacePath='C:\隔离参数 path\工程方案 & (!保留)'
    $extraText='--daily-data --project-store-path "'+$spacePath+'" --probe-1 a --probe-2 "two words" --probe-3 c --probe-4 d --empty "" --last end'
    $extraArgs=@('--daily-data','--project-store-path',$spacePath,'--probe-1','a','--probe-2','two words','--probe-3','c','--probe-4','d','--empty','','--last','end')
    Invoke-FleetStub $rootStub '启动多车联调Web.cmd' $extraText -ExpectedArguments ($defaultArgs+$extraArgs)
    $selected='CUSTOM-1,CUSTOM_2,CUSTOM-3,CUSTOM-4,CUSTOM-5'
    $selectedArgs=@('--fleet-test-vehicles',$selected)
    Invoke-FleetStub $rootStub '启动多车联调Web.cmd' ($selected+' '+$extraText) -ExpectedArguments ($selectedArgs+$extraArgs)
    Invoke-FleetStub $rootStub '启动多车联调Web.cmd' ('"'+$selected+'" "2" '+$extraText) -ExpectedArguments ($selectedArgs+@('--fleet-test-max-concurrent','2')+$extraArgs)
    Invoke-FleetStub $bundleStub 'Start-Fleet-Test.cmd' $selected -ExpectedArguments $selectedArgs
    Invoke-FleetStub $bundleStub 'Start-Fleet-Test.cmd' ($selected+' '+$extraText) -ExpectedArguments ($selectedArgs+$extraArgs)
    Invoke-FleetStub $bundleStub 'Start-Fleet-Test.cmd' ('"'+$selected+'" 4 '+$extraText) -ExpectedArguments ($selectedArgs+@('--fleet-test-max-concurrent','4')+$extraArgs)
    Write-FleetTestFile (Join-Path $rootStub '启动双车联调Web.cmd') "@echo off`r`nchcp 65001 >nul`r`ncall `"%~dp0启动多车联调Web.cmd`" %*`r`n"
    Invoke-FleetStub $rootStub '启动双车联调Web.cmd' $extraText -ExpectedArguments ($defaultArgs+$extraArgs)
    $env:AGV_FLEET_LAUNCH_STUB_EXIT='23'
    Invoke-FleetStub $bundleStub 'Start-Fleet-Test.cmd' ($selected+' 2 --daily-data') -ExpectedArguments ($selectedArgs+@('--fleet-test-max-concurrent','2','--daily-data')) -ExpectedExitCode 23
    foreach($file in @('../Build-CommissioningBundle.ps1','../Set-LatestWebRelease.ps1','../FleetLaunchConfiguration.ps1')){
        $parseTokens=$null;$parseErrors=$null
        [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$parseTokens,[ref]$parseErrors) | Out-Null
        Check-Fleet ($parseErrors.Count -eq 0) ('PowerShell source must parse: '+$file)
    }
    Write-Output ('PASS '+$script:fleetChecks+' launch preservation, isolated CMD argument forwarding and source syntax checks; no service started.')
}
finally{
    $env:AGV_FLEET_LAUNCH_CAPTURE=$fleetOldCapture
    $env:AGV_FLEET_LAUNCH_STUB_EXIT=$fleetOldStubExit
    # A fresh test-owned temporary directory only; never touch deployment/runtime data.
    $resolvedFleetRoot=[IO.Path]::GetFullPath($fleetRoot)
    if($resolvedFleetRoot.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolvedFleetRoot).StartsWith('fleet-launch-')){
        Remove-Item -LiteralPath $resolvedFleetRoot -Recurse -Force
    }
}
