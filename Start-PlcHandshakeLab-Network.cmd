@echo off
setlocal
set "AGV_LAB_WORKSPACE=%~dp0"
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $base=$env:AGV_LAB_WORKSPACE; $pointer=Join-Path $base 'artifacts/PlcHandshakeLab-current.json'; if (!(Test-Path -LiteralPath $pointer)) { throw 'Run tools/PlcHandshakeLab/Publish-Lab.ps1 to publish the current carrying package first.' }; $release=Get-Content -LiteralPath $pointer -Raw -Encoding UTF8 | ConvertFrom-Json; $package=[IO.Path]::GetFullPath($release.packageDirectory); $prefix=[IO.Path]::GetFullPath((Join-Path $base 'artifacts'))+[IO.Path]::DirectorySeparatorChar; if (!$package.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Release outside workspace artifacts.' }; $exe=Join-Path $package 'PlcHandshakeLab.exe'; if (!(Test-Path -LiteralPath $exe)) { throw 'Current release is missing.' }; Start-Process -FilePath $exe -ArgumentList '--network'"
if errorlevel 1 pause
