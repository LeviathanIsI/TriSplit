@echo off
setlocal

echo TriSplit Development Runner
echo =========================
echo.

:: Build the solution
echo Building solution...
cd /d "%~dp0\.."
dotnet build --configuration Debug
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)
echo Build succeeded!
echo.

:: Run the desktop application
echo Starting TriSplit Desktop...
dotnet run --project src\TriSplit.Desktop\TriSplit.Desktop.csproj --no-build

echo.
echo Execution completed
pause