@echo off
setlocal
set "AGV_LAB_WORKSPACE=%~dp0"
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $base=$env:AGV_LAB_WORKSPACE; $exe=Join-Path $base 'artifacts/PlcHandshakeLab-live/PlcHandshakeLab.exe'; if (!(Test-Path -LiteralPath $exe)) { throw 'Publish tools/PlcHandshakeLab to artifacts/PlcHandshakeLab-live first.' }; $readyPath=Join-Path $base 'artifacts/web-single-vehicle-20261020/ready.json'; if (!(Test-Path -LiteralPath $readyPath)) { throw 'Start the WebSingleVehicleLab environment first, or open the tool and select its device directory.' }; $ready=Get-Content -LiteralPath $readyPath -Raw -Encoding UTF8 | ConvertFrom-Json; & $exe --lab $ready.root"
if errorlevel 1 pause
