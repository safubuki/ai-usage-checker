@echo off
echo ===================================================
echo  Turtle AI Usage Checker - Publish Single-File
echo ===================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Publish failed.
    pause
    exit /b %ERRORLEVEL%
)

pause
