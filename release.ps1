# Create a local, unsigned Windows release package and SHA-256 manifest.
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $version = '0.2.1'
    & dotnet publish .\codex-usage-monitor.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    New-Item -ItemType Directory -Force .\release | Out-Null
    $archive = Join-Path $PWD "release\codex-usage-monitor-v$version-win-x64.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    Compress-Archive -Path .\publish\* -DestinationPath $archive
    $hash = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $archive -Leaf)" | Set-Content -Encoding ascii .\release\SHA256SUMS.txt
    Write-Host "Release: $archive" -ForegroundColor Green
    Write-Host "Checksums: $PWD\release\SHA256SUMS.txt"
} finally { Pop-Location }
