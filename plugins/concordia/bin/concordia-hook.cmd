@echo off
rem Concordia launcher for hosts that run hooks through cmd.exe instead of Git Bash.
setlocal
set "DIR=%~dp0"
set "EXE=%DIR%win-x64\concordia-hook.exe"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" if exist "%DIR%win-arm64\concordia-hook.exe" set "EXE=%DIR%win-arm64\concordia-hook.exe"
if not exist "%EXE%" (
  echo concordia-hook: no Windows binary found; Concordia hooks skipped. 1>&2
  exit /b 0
)
"%EXE%" %*
exit /b %ERRORLEVEL%
