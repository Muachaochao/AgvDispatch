@echo off
setlocal
set "AGV_LAB_WORKSPACE=%~dp0"
set "AGV_LAB_ARGUMENT=%~1"
set "AGV_LAB_EXPLICIT_ROOT=%~2"
set "AGV_LAB_EXTRA_ARGUMENT=%~3"
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $base=$env:AGV_LAB_WORKSPACE; $exe=Join-Path $base 'artifacts/PlcHandshakeLab-live/PlcHandshakeLab.exe'; if (!(Test-Path -LiteralPath $exe)) { throw 'Publish tools/PlcHandshakeLab to artifacts/PlcHandshakeLab-live first.' }; if ($env:AGV_LAB_ARGUMENT) { if ($env:AGV_LAB_ARGUMENT -ne '--lab' -or [string]::IsNullOrWhiteSpace($env:AGV_LAB_EXPLICIT_ROOT) -or $env:AGV_LAB_EXTRA_ARGUMENT) { throw 'Usage: Start-PlcHandshakeLab.cmd [--lab device-directory]' }; $deviceRoot=$env:AGV_LAB_EXPLICIT_ROOT } else { $readyPath=Join-Path $base 'artifacts/web-single-vehicle-20261021-28stage/ready.json'; if (!(Test-Path -LiteralPath $readyPath)) { $readyPath=Join-Path $base 'artifacts/web-single-vehicle-20261020/ready.json' }; if (!(Test-Path -LiteralPath $readyPath)) { throw 'Start the WebSingleVehicleLab environment first, or pass --lab with its device directory.' }; $ready=Get-Content -LiteralPath $readyPath -Raw -Encoding UTF8 | ConvertFrom-Json; $deviceRoot=$ready.root }; if ([string]::IsNullOrWhiteSpace($deviceRoot) -or !(Test-Path -LiteralPath $deviceRoot -PathType Container)) { throw 'The selected device directory does not exist. Check ready.json or --lab.' }; & $exe --lab $deviceRoot"
if errorlevel 1 pause
