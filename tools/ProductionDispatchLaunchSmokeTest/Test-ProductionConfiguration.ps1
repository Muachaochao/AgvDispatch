$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../ProductionLaunchConfiguration.ps1')
$checks=0
function Check([bool]$Pass,[string]$Message){if(!$Pass){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Message){$rejected=$false;try{& $Action | Out-Null}catch{$rejected=$true};Check $rejected $Message}
$old=[pscustomobject]@{directory='previous'}
Check (!(Resolve-ProductionLaunchSelection -Previous $old).Enabled) 'Older pointer grants no production permission'
$selected=Resolve-ProductionLaunchSelection -Previous $old -Enable -MaxConcurrentTasks 7
Check ($selected.Enabled -and $selected.MaxConcurrentTasks -eq 7) 'Authorization and concurrency are explicit'
$saved=[pscustomobject]@{productionDispatch=$true;maxConcurrentTasks=7}
$retained=Resolve-ProductionLaunchSelection -Previous $saved
Check ($retained.Enabled -and $retained.MaxConcurrentTasks -eq 7) 'Promotion retains authorized production mode and concurrency'
Check (!(Resolve-ProductionLaunchSelection -Previous $saved -Disable).Enabled) 'Explicit disable is persistent'
Check (!(Resolve-ProductionLaunchSelection -Previous $saved -NavigationSelected).Enabled) 'Explicit navigation default replaces production mode'
Check ((Resolve-ProductionLaunchSelection -Previous $saved -MaxConcurrentTasks 3).MaxConcurrentTasks -eq 3) 'Production concurrency can be changed without hardcoded vehicle count'
Reject {Resolve-ProductionLaunchSelection -Enable} 'Fresh authorization needs a concurrency limit'
Reject {Resolve-ProductionLaunchSelection -Enable -Disable -MaxConcurrentTasks 2} 'Conflicting authorization is rejected'
Reject {Resolve-ProductionLaunchSelection -Enable -NavigationSelected -MaxConcurrentTasks 2} 'Navigation cannot grant production permissions'
Reject {Resolve-ProductionLaunchSelection -MaxConcurrentTasks 2} 'Concurrency alone cannot authorize production'
foreach($bad in @('true',1,$null)){
    Reject {Resolve-ProductionLaunchSelection -Previous ([pscustomobject]@{productionDispatch=$bad;maxConcurrentTasks=2})} 'Invalid saved boolean fails closed'
}
foreach($bad in @(0,-1,1001,1.5,'2',$null)){
    Reject {Resolve-ProductionLaunchSelection -Previous ([pscustomobject]@{productionDispatch=$true;maxConcurrentTasks=$bad})} 'Invalid saved concurrency fails closed'
}
$tokens=$null;$errors=$null
[void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../Set-LatestWebRelease.ps1'),[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'Promotion script parses after adding production policy preservation'
Write-Output "PASS: $checks production policy checks; no release or live settings changed."
