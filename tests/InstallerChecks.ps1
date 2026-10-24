# Runs only on the disposable Windows release runner; installs/uninstalls for its user.
$ErrorActionPreference = 'Stop'
[xml]$project = Get-Content (Join-Path $PSScriptRoot '..\codex-usage-monitor.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$setup = (Resolve-Path (Join-Path $PSScriptRoot "..\release\codex-usage-monitor-v$version-win-x64-setup.exe")).Path
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Codex Usage Monitor'
$preferenceKey = 'HKCU:\Software\whiskeylover\CodexUsageMonitor'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Install-App([string]$Tasks) {
    $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/TASKS=`"$Tasks`"") -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer exited with $($process.ExitCode)." }
    if (-not (Test-Path (Join-Path $installDir 'codex-usage-monitor.exe'))) { throw 'Per-user executable is missing.' }
}

try {
    Install-App 'updates,autostart'
    if ((Get-ItemProperty $preferenceKey).AutomaticUpdates -ne 1) { throw 'Update opt-in was not saved.' }
    $expectedRun = '"' + (Join-Path $installDir 'codex-usage-monitor.exe') + '"'
    if ((Get-ItemProperty $runKey).CodexUsageMonitor -ne $expectedRun) { throw 'Autostart path is missing or unquoted.' }

    Install-App ''
    if ((Get-ItemProperty $preferenceKey).AutomaticUpdates -ne 0) { throw 'Update opt-out was not saved.' }
    if (Get-ItemPropertyValue $runKey -Name CodexUsageMonitor -ErrorAction SilentlyContinue) { throw 'Autostart opt-out did not remove the Run value.' }
} finally {
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (Test-Path $uninstaller) {
        $process = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Uninstaller exited with $($process.ExitCode)." }
    }
}
if (Test-Path (Join-Path $installDir 'codex-usage-monitor.exe')) { throw 'Uninstall left the executable behind.' }
if (Get-ItemPropertyValue $runKey -Name CodexUsageMonitor -ErrorAction SilentlyContinue) { throw 'Uninstall left autostart enabled.' }
if (Get-ItemPropertyValue $preferenceKey -Name AutomaticUpdates -ErrorAction SilentlyContinue) { throw 'Uninstall left the installer preference behind.' }
Write-Host 'Passed: per-user installation, update preference, autostart opt-in/opt-out, upgrade, and uninstall.'
