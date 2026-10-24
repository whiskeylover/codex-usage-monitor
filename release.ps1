# Create a portable ZIP and self-contained installer. -Installer remains supported for compatibility.
param([switch]$ZipOnly, [switch]$Installer, [string]$IsccPath)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if ($ZipOnly -and $Installer) { throw 'Use -ZipOnly or -Installer, not both.' }
    if (-not $ZipOnly) {
        if (-not $IsccPath) {
            $candidates = @(
                (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe'),
                (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Inno Setup 6\ISCC.exe')
            )
            $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
            if ($command) { $candidates += $command.Source }
            $candidates += @(Get-ChildItem -Path (Join-Path $PSScriptRoot 'release\tools\inno-*\ISCC.exe') -File -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName)
            $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }
        if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
            throw 'Inno Setup compiler not found. Install Inno Setup 6, supply -IsccPath, or use -ZipOnly.'
        }
        $IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path
    }
    [xml]$project = Get-Content (Join-Path $PSScriptRoot 'codex-usage-monitor.csproj')
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Project version must be major.minor.patch.' }
    $releaseDir = Join-Path $PSScriptRoot 'release'
    $staging = Join-Path $releaseDir ("staging-" + [guid]::NewGuid().ToString('N'))
    $portable = Join-Path $staging 'portable'
    New-Item -ItemType Directory -Force $portable | Out-Null
    try {
        & dotnet publish .\codex-usage-monitor.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $portable
        if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
        $archive = Join-Path $releaseDir "codex-usage-monitor-v$version-win-x64.zip"
        Compress-Archive -Path "$portable\*" -DestinationPath $archive -Force
        $artifacts = @($archive)
        if (-not $ZipOnly) {
            $payload = Join-Path $staging 'installer'
            & dotnet publish .\codex-usage-monitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $payload
            if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
            & $IsccPath "/DAppVersion=$version" "/DSourceDir=$payload" "/DReleaseDir=$releaseDir" .\installer\setup.iss
            if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
            $setup = Join-Path $releaseDir "codex-usage-monitor-v$version-win-x64-setup.exe"
            if (-not (Test-Path -LiteralPath $setup)) { throw 'Installer output was not created.' }
            $artifacts += $setup
        }
        $artifacts | ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $(Split-Path $_ -Leaf)"
        } | Set-Content -Encoding ascii (Join-Path $releaseDir 'SHA256SUMS.txt')
        Write-Host "Release artifacts: $($artifacts -join ', ')" -ForegroundColor Green
    } finally {
        # Check the absolute cleanup target before removing the temporary payloads.
        $resolved = [IO.Path]::GetFullPath($staging)
        $allowed = [IO.Path]::GetFullPath($releaseDir) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
} finally { Pop-Location }
