Write-Host "Starting TriSplit with console output capture..." -ForegroundColor Green
Write-Host "===========================================" -ForegroundColor Green

# Run the application with console output visible
& dotnet run --project src/TriSplit.Desktop/TriSplit.Desktop.csproj 2>&1

Write-Host "`nNote: Check the console output for [DEBUG] messages" -ForegroundColor Yellow
Write-Host "These will show:" -ForegroundColor Cyan
Write-Host "- Which columns are being processed" -ForegroundColor White
Write-Host "- Whether owners are being created" -ForegroundColor White
Write-Host "- Phone extraction attempts" -ForegroundColor White
Write-Host "- Why phone records might be skipped" -ForegroundColor White