@echo off
setlocal
cd /d "%~dp0"

rem 1) packaged app in dist\<folder>\*.exe
if exist "dist\*\*.exe" (
    for %%F in ("dist\*\*.exe") do (
        start "" "%%~fF"
        goto :eof
    )
)

rem 2) packaged single exe directly under dist\
if exist "dist\*.exe" (
    for %%F in ("dist\*.exe") do (
        start "" "%%~fF"
        goto :eof
    )
)

rem 3) python source
where python >nul 2>nul
if not errorlevel 1 (
    python server.py %*
    goto :eof
)
where py >nul 2>nul
if not errorlevel 1 (
    py -3 server.py %*
    goto :eof
)

echo [ERROR] Packaged exe not found and Python is not installed.
pause
