function Resolve-ProductionLaunchSelection {
    param($Previous, [switch]$Enable, [switch]$Disable, [int]$MaxConcurrentTasks=0,
        [switch]$NavigationSelected)
    if($Enable -and ($Disable -or $NavigationSelected)){throw 'Production and isolated navigation defaults cannot be selected together.'}
    if($MaxConcurrentTasks -lt 0 -or $MaxConcurrentTasks -gt 1000){throw 'Production concurrency must be between 1 and 1000.'}
    $productionEnabled=$false
    $productionMaximum=0
    if($Previous -and $Previous.PSObject.Properties['productionDispatch']){
        if($Previous.productionDispatch -isnot [bool]){throw 'Saved productionDispatch must be a boolean.'}
        $productionEnabled=$Previous.productionDispatch
        if($productionEnabled){
            $value=$Previous.maxConcurrentTasks
            if(($value -isnot [int] -and $value -isnot [long]) -or $value -lt 1 -or $value -gt 1000){throw 'Saved production concurrency must be an integer between 1 and 1000.'}
            $productionMaximum=[int]$value
        }
    }
    if($Disable -or $NavigationSelected){$productionEnabled=$false;$productionMaximum=0}
    if($Enable){$productionEnabled=$true}
    if($MaxConcurrentTasks -gt 0){
        if(!$productionEnabled){throw 'Production concurrency requires an enabled production policy.'}
        $productionMaximum=$MaxConcurrentTasks
    }
    if($productionEnabled -and $productionMaximum -lt 1){throw 'Specify a production concurrency limit before enabling production dispatch.'}
    return [pscustomobject]@{Enabled=$productionEnabled;MaxConcurrentTasks=$productionMaximum}
}
