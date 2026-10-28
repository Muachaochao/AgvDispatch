# Pure launch-selection helpers. Reading or generating an entry never starts services.
function Resolve-FleetLaunchSelection {
    param([Parameter(Mandatory=$true)][string]$Root,[string[]]$VehicleIds=@(),[int]$MaxConcurrentTasks=0)
    $fleetIds=@($VehicleIds)
    $fleetConcurrency=$MaxConcurrentTasks
    if($fleetIds.Count -eq 0){
        foreach($fleetName in @('启动多车联调Web.cmd','启动双车联调Web.cmd')){
            $fleetEntry=Join-Path $Root $fleetName
            if(!(Test-Path -LiteralPath $fleetEntry)){continue}
            $fleetSource=[IO.File]::ReadAllText($fleetEntry)
            # CMD removes surrounding quotes from both option names and values.
            # Only literal configured IDs qualify; generated %~1 placeholders do not.
            $fleetMatch=[regex]::Match($fleetSource,'(?<![A-Za-z0-9_-])(?:"--fleet-test-vehicles"|--fleet-test-vehicles)\s+(?:"(?<ids>[A-Za-z0-9_-]{1,32}(?:,[A-Za-z0-9_-]{1,32})+)"|(?<ids>[A-Za-z0-9_-]{1,32}(?:,[A-Za-z0-9_-]{1,32})+))(?=\s|$)')
            if(!$fleetMatch.Success){continue}
            $fleetIds=@($fleetMatch.Groups['ids'].Value.Split(','))
            if($fleetConcurrency -eq 0){
                $fleetMaxMatch=[regex]::Match($fleetSource,'(?<![A-Za-z0-9_-])(?:"--fleet-test-max-concurrent"|--fleet-test-max-concurrent)\s+(?:"(?<max>[0-9]+)"|(?<max>[0-9]+))(?=\s|$)')
                if($fleetMaxMatch.Success){$fleetConcurrency=[int]$fleetMaxMatch.Groups['max'].Value}
            }
            break
        }
    }
    if($fleetIds.Count -gt 0){
        if($fleetIds.Count -lt 2 -or $fleetIds.Count -gt 1000 -or
            @($fleetIds | Where-Object {$_ -notmatch '^[A-Za-z0-9_-]{1,32}$'}).Count -gt 0 -or
            @($fleetIds | Sort-Object -Unique).Count -ne $fleetIds.Count){throw '多车联调清单必须是至少两辆编号不同的配置车辆。'}
        if($fleetConcurrency -eq 0){$fleetConcurrency=$fleetIds.Count}
        if($fleetConcurrency -lt 1 -or $fleetConcurrency -gt $fleetIds.Count){throw '多车联调并发上限必须在1至所选车辆数之间。'}
    }
    elseif($fleetConcurrency -ne 0){throw '指定并发上限时必须提供或保留车辆清单。'}
    return [pscustomobject]@{VehicleIds=$fleetIds;MaxConcurrentTasks=$fleetConcurrency}
}

function New-FleetLaunchEntryText {
    param(
        [Parameter(Mandatory=$true)]$Selection,
        [string]$LauncherEntryName='启动最新版Web.cmd',
        [string]$EntryName='启动多车联调Web.cmd'
    )
    $fleetDefault=if($Selection.VehicleIds.Count -gt 0){
        '--fleet-test-vehicles "'+($Selection.VehicleIds -join ',')+'" --fleet-test-max-concurrent "'+$Selection.MaxConcurrentTasks+'"'
    }else{''}
    $fleetText=@"
@echo off
chcp 65001 >nul
setlocal DisableDelayedExpansion
set fleetSelection=$fleetDefault
set fleetArguments=%*
if not defined fleetArguments goto fleet_require_selection
rem CMD splits unquoted commas in %%1, so parse only the raw positional prefix.
rem Expand the raw text after CMD metacharacter parsing; disable expansion again
rem before saving FOR tokens so literal exclamation marks in paths survive.
setlocal EnableDelayedExpansion
for /f "tokens=1,*" %%A in ("!fleetArguments!") do (
  endlocal
  set "fleetNext=%%~A"
  set fleetTail=%%B
)
if "%fleetNext:~0,2%"=="--" goto fleet_require_selection
set fleetSelection=--fleet-test-vehicles "%fleetNext%"
set fleetArguments=%fleetTail%
if not defined fleetArguments goto fleet_launch
setlocal EnableDelayedExpansion
for /f "tokens=1,*" %%A in ("!fleetArguments!") do (
  endlocal
  set "fleetNext=%%~A"
  set fleetTail=%%B
)
if "%fleetNext:~0,2%"=="--" goto fleet_launch
set fleetSelection=%fleetSelection% --fleet-test-max-concurrent "%fleetNext%"
set fleetArguments=%fleetTail%
goto fleet_launch
:fleet_require_selection
if defined fleetSelection goto fleet_launch
echo Usage: $EntryName VEHICLE-A,VEHICLE-B[,VEHICLE-C...] [max-concurrent] [launcher options]
echo Select configured vehicles explicitly. Startup does not send tasks.
exit /b 1
:fleet_launch
call "%~dp0$LauncherEntryName" %fleetSelection% %fleetArguments%
exit /b %errorlevel%
"@
    return $fleetText.Replace("`r`n","`n").Replace("`n","`r`n").TrimEnd()+"`r`n"
}
