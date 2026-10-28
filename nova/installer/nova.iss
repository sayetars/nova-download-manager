; Nova Download Manager — Inno Setup script (Windows installer)
; Builds in CI: ISCC.exe installer/nova.iss
; Produces NovaDownloadManager-Setup-1.0.0.exe

#define MyAppName "Nova Download Manager"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Nova Software"
#define MyAppExeName "nova.exe"
#define MyAppId "{{8A1B2C3D-4E5F-4A6B-8C9D-0E1F2A3B4C5D}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Nova Download Manager
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputBaseFilename=NovaDownloadManager-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fa"; MessagesFile: "compiler:Languages\Farsi.isl"

[CustomMessages]
en.CreateDesktopIcon=Create a &desktop shortcut
en.AssociateMagnet=Handle &magnet: links
fa.CreateDesktopIcon=ایجاد میان‌بر روی &میزکار
fa.AssociateMagnet=مدیریت لینک‌های &magnet:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "Shortcuts:"
Name: "magnetassoc"; Description: "{cm:AssociateMagnet}"; GroupDescription: "Integration:"

[Files]
; app + engine from the CI bundle
Source: "bundle\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "extensions\chrome\*"; DestDir: "{app}\extensions\chrome"; Flags: recursesubdirs
Source: "extensions\firefox\*"; DestDir: "{app}\extensions\firefox"; Flags: recursesubdirs

[Dirs]
Name: "{localappdata}\Nova"; Flags: uninsneveruninstall

[Registry]
; Native messaging host registration (Chrome/Edge/Brave — HKCU, no admin needed)
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.nova.downloadmanager"; ValueType: string; ValueData: "{localappdata}\Nova\com.nova.downloadmanager.chrome.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.nova.downloadmanager"; ValueType: string; ValueData: "{localappdata}\Nova\com.nova.downloadmanager.chrome.json"; Flags: uninsdeletekey
; Protocol handler novadl://
Root: HKCU; Subkey: "Software\Classes\novadl"; ValueType: string; ValueData: "URL:Nova Download Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\novadl"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\novadl\shell\open\command"; ValueType: string; ValueData: "\"{app}\{#MyAppExeName}\" \"%1\""; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; Write the native host manifest pointing at the installed engine
Filename: "{app}\tools\nova-host.exe"; Parameters: "install-chrome-host --exe ""{app}\tools\nova-host.exe"" --origins chrome-extension://nova/"; Flags: runhidden

[UninstallRun]
Filename: "{app}\tools\nova-host.exe"; Parameters: "--uninstall-host"; Flags: runhidden; RunOnceId: "UninstHost"
