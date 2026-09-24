@echo off
echo ===================================================
echo  Turtle AI Usage Checker - Publish Single-File
echo ===================================================
echo.
echo Building single-file release package...
dotnet publish -c Release -r win-x64 --self-contained false -o bin\Release\publish
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Publish failed.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [SUCCESS] Publish completed!
echo Executable: bin\Release\publish\AIUsageChecker.exe
echo.
pause
