@echo off
REM VideoCompare build wrapper - runs build.ps1 in the same folder.
REM Optional argument: zip
setlocal
set "HERE=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%build.ps1" %*
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)
echo.
echo BUILD OK
