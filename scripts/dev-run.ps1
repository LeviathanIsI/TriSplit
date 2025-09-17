param(
    [switch]$Watch = $false,
    [switch]$Clean = $false
)

$ErrorActionPreference = "Stop"

# Get script directory and project root
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir

Write-Host "TriSplit Development Runner" -ForegroundColor Cyan
Write-Host "=========================" -ForegroundColor Cyan

# Clean build artifacts if requested
if ($Clean) {
    Write-Host "`nCleaning build artifacts..." -ForegroundColor Yellow

    Get-ChildItem -Path $RootDir -Include bin,obj -Recurse -Directory |
        ForEach-Object {
            Write-Host "  Removing: $_" -ForegroundColor DarkGray
            Remove-Item $_ -Recurse -Force
        }

    Write-Host "Clean completed!" -ForegroundColor Green
}

# Build the solution
Write-Host "`nBuilding solution..." -ForegroundColor Yellow
Push-Location $RootDir
try {
    dotnet build --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed!"
    }
    Write-Host "Build succeeded!" -ForegroundColor Green
}
finally {
    Pop-Location
}

# Run the desktop application
$DesktopProject = Join-Path $RootDir "src\TriSplit.Desktop\TriSplit.Desktop.csproj"

if ($Watch) {
    Write-Host "`nStarting in watch mode (hot reload enabled)..." -ForegroundColor Yellow
    Write-Host "Press Ctrl+C to stop`n" -ForegroundColor DarkGray

    dotnet watch run --project $DesktopProject --no-build
}
else {
    Write-Host "`nStarting TriSplit Desktop..." -ForegroundColor Yellow

    dotnet run --project $DesktopProject --no-build
}

Write-Host "`nExecution completed" -ForegroundColor Green