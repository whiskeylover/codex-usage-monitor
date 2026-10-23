$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish .\codex-usage-monitor.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Write-Host "`nBuilt: $PSScriptRoot\publish\codex-usage-monitor.exe" -ForegroundColor Green
    Write-Host 'Run the EXE to start the tray icon. Right-click > Exit to quit.'
} finally { Pop-Location }
