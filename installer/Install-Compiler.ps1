# Install the pinned official compiler in portable mode; no machine-wide changes.
param([Parameter(Mandatory = $true)][string]$ToolsDirectory)
$ErrorActionPreference = 'Stop'
$version = '6.7.1'
$expectedHash = '4d11e8050b6185e0d49bd9e8cc661a7a59f44959a621d31d11033124c4e8a7b0'
New-Item -ItemType Directory -Force $ToolsDirectory | Out-Null
$tools = (Resolve-Path -LiteralPath $ToolsDirectory).Path
$download = Join-Path $tools "innosetup-$version.exe"
$destination = Join-Path $tools "inno-$version"
$log = Join-Path $tools 'inno-install.log'

Write-Host "Downloading Inno Setup $version from the official release..."
Invoke-WebRequest "https://github.com/jrsoftware/issrc/releases/download/is-6_7_1/innosetup-$version.exe" -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'Inno Setup download failed SHA-256 verification.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid') { throw "Inno Setup signature verification failed: $($signature.Status)" }
Write-Host "Verified compiler download; extracting portable compiler to $destination"
$process = Start-Process -FilePath $download -ArgumentList @(
    '/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS',
    ('/DIR="' + $destination + '"'), ('/LOG="' + $log + '"')
) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log | Write-Host }
    throw "Inno Setup compiler installation failed with exit code $($process.ExitCode)."
}
$compiler = Join-Path $destination 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Compiler not found after installation: $compiler" }
Write-Host "Compiler ready: $compiler"
Write-Output $compiler
