Write-Host "TriSplit Debug Log Viewer" -ForegroundColor Cyan
Write-Host "=========================" -ForegroundColor Cyan

$debugPath = "$env:LOCALAPPDATA\TriSplit\Debug"

Write-Host "`nDebug logs are saved to: $debugPath" -ForegroundColor Yellow

if (Test-Path $debugPath) {
    $latestLog = Get-ChildItem "$debugPath\*.txt" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if ($latestLog) {
        Write-Host "`nLatest debug log: $($latestLog.Name)" -ForegroundColor Green
        Write-Host "Created: $($latestLog.LastWriteTime)" -ForegroundColor Gray
        Write-Host "`nOpening in notepad..." -ForegroundColor White
        notepad $latestLog.FullName
    } else {
        Write-Host "`nNo debug logs found yet. Run the processing first!" -ForegroundColor Red
    }
} else {
    Write-Host "`nDebug directory doesn't exist yet. Run the processing first!" -ForegroundColor Red
}