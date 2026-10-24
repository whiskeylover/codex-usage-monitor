#ifndef AppVersion
  #error AppVersion must be supplied by release.ps1
#endif
#ifndef SourceDir
  #error SourceDir must point to the self-contained publish directory
#endif
#ifndef ReleaseDir
  #error ReleaseDir must point to the release output directory
#endif

[Setup]
AppId={{B3FDE745-55B5-4F72-8D63-CC4DC61485B4}
AppName=Codex Usage Monitor
AppVersion={#AppVersion}
AppPublisher=whiskeylover
AppPublisherURL=https://github.com/whiskeylover/codex-usage-monitor
AppSupportURL=https://github.com/whiskeylover/codex-usage-monitor/issues
DefaultDirName={localappdata}\Programs\Codex Usage Monitor
DefaultGroupName=Codex Usage Monitor
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#ReleaseDir}
OutputBaseFilename=codex-usage-monitor-v{#AppVersion}-win-x64-setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\codex-usage-monitor.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Local\CodexUsageMonitor
UsePreviousTasks=yes

[Tasks]
Name: "updates"; Description: "Check GitHub for updates daily (sends your IP address and app version)"; GroupDescription: "Preferences:"
Name: "autostart"; Description: "Start Codex Usage Monitor when I sign in to Windows"; GroupDescription: "Preferences:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Codex Usage Monitor"; Filename: "{app}\codex-usage-monitor.exe"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\whiskeylover\CodexUsageMonitor"; ValueType: dword; ValueName: "AutomaticUpdates"; ValueData: "1"; Tasks: updates; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\whiskeylover\CodexUsageMonitor"; ValueType: dword; ValueName: "AutomaticUpdates"; ValueData: "0"; Tasks: not updates; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexUsageMonitor"; ValueData: """{app}\codex-usage-monitor.exe"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "CodexUsageMonitor"; Tasks: not autostart; Flags: deletevalue

[Run]
Filename: "{app}\codex-usage-monitor.exe"; Description: "Launch Codex Usage Monitor"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent
