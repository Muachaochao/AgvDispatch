@echo off
setlocal
set "AGV_PLC_LAB_EXE=%~dp0artifacts\PlcHandshakeLab-network\PlcHandshakeLab.exe"
if not exist "%AGV_PLC_LAB_EXE%" (
  echo Publish tools/PlcHandshakeLab to artifacts/PlcHandshakeLab-network first.
  pause
  exit /b 1
)
start "" "%AGV_PLC_LAB_EXE%" --network
