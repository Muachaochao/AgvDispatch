@echo off
setlocal
title AGV Web Preview
pushd "%~dp0"
if not exist "host\AgvDispatch.WebHost.exe" (
  echo Missing host\AgvDispatch.WebHost.exe. Keep the complete preview folder.
  pause
  popd
  exit /b 1
)
echo Starting the local AGV Web adapter.
echo Open http://127.0.0.1:5188/ after "Now listening" appears.
echo Keep this window open. Press Ctrl+C to stop this Web adapter.
echo The dispatch backend is a separate process. Device execution is not enabled here.
"host\AgvDispatch.WebHost.exe" --Web:Port=5188 --Web:PipeName=AgvDispatch.Service --Web:EnableTaskSubmission=false --Web:EnableRecoveryActions=false --Web:StaticPath="%~dp0wwwroot"
set "previewExit=%errorlevel%"
popd
if not "%previewExit%"=="0" (
  echo Web adapter exited. If port 5188 is already in use, open the URL above.
  pause
)
exit /b %previewExit%
