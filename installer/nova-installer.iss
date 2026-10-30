; Nova Download Manager — Inno Setup installer script
; Build (on Windows):  iscc installer\nova-installer.iss
; Prereqs: run scripts\build-release.ps1 first to produce artifacts under installer\payload\
;
; Notes:
;  - Per-machine install to Program Files (admin required ONCE at install; the app
;    itself runs at user level and needs no elevation afterwards).
;  - Native messaging host registration is HKCU per-user (Chrome/Brave). Firefox
;    uses the user-profile manifest dir. Uninstall offers to KEEP settings/history.

#define MyAppName "Nova Download Manager"
#define MyAppVersion "1.8.0"
#define MyAppPublisher "Nova Engineering"
#define MyAppExeName "NovaDesktop.exe"

[Setup]
AppId={{8E4A3B71-9C6D-4E1F-B2A8-3F5D9E7C1A42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Nova
DefaultGroupName=Nova Download Manager
UninstallDisplayName={#MyAppName}
OutputDir=..\dist
OutputBaseFilename=nova-setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
DisableProgramGroupPage=yes
LicenseFile=LICENSE.txt

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
; Vendored from the official jrsoftware translations (Files/Languages/Unofficial,
; "Inno Setup version 6.5.0+ Farsi messages") — Inno Setup does not ship Farsi.
Name: "fa"; MessagesFile: "languages\Farsi.isl"

[CustomMessages]
en.CreateDesktopIcon=Create a &desktop shortcut
en.LaunchAfter=Launch Nova after installation
fa.CreateDesktopIcon=ایجاد میان‌بر روی دسکتاپ
fa.LaunchAfter=اجرای نوا پس از نصب

[Files]
; Application binaries (produced by scripts/build-release.ps1)
Source: "payload\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Engine + browser host
Source: "payload\engine\NovaEngine.exe"; DestDir: "{app}\engine"; Flags: ignoreversion
Source: "payload\engine\*.dll"; DestDir: "{app}\engine"; Flags: ignoreversion
Source: "payload\engine\NovaBrowserHost.exe"; DestDir: "{app}\engine"; Flags: ignoreversion
Source: "payload\engine\NovaBrowserHost.dll"; DestDir: "{app}\engine"; Flags: ignoreversion
; Native messaging host manifest (path patched at install time)
Source: "payload\hosts\com.nova.downloadmanager.chromium.json"; DestDir: "{app}\hosts"; Flags: ignoreversion
; Browser extension packages
Source: "payload\ext\nova-chromium-1.0.0.zip"; DestDir: "{app}\extensions"; Flags: ignoreversion
Source: "payload\ext\nova-firefox-1.0.0.zip"; DestDir: "{app}\extensions"; Flags: ignoreversion
Source: "payload\ext\chromium\*"; DestDir: "{app}\extensions\chromium"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\ext\firefox\*"; DestDir: "{app}\extensions\firefox"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; User-level data dirs (created on demand by the engine as well)
Name: "{localappdata}\Nova"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall Nova"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:CreateDesktopIcon}"
Name: "autostart"; Description: "Start Nova engine with Windows (user level)"; GroupDescription: "Startup"
Name: "registerchromium"; Description: "Register native messaging host for Chrome/Brave (current user)"; GroupDescription: "Browser integration"; Flags: checkedonce
Name: "registerfirefox"; Description: "Install manifest for Firefox (manual reload)"; GroupDescription: "Browser integration"

[Registry]
; --- Chrome/Brave native messaging host (per user) — path written below points to {app}\engine\NovaBrowserHost.exe
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.nova.downloadmanager"; \
    ValueType: string; ValueName: ""; ValueData: "{localappdata}\Nova\com.nova.downloadmanager.json"; \
    Flags: uninsdeletekey; Tasks: registerchromium
Root: HKCU; Subkey: "Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.nova.downloadmanager"; \
    ValueType: string; ValueName: ""; ValueData: "{localappdata}\Nova\com.nova.downloadmanager.json"; \
    Flags: uninsdeletekey; Tasks: registerchromium
; --- Run at login (user level, optional)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "NovaDownloadManager"; ValueData: """{app}\engine\NovaEngine.exe"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Code]
procedure PatchChromiumHostManifest();
var
  content: String;
  path: String;
begin
  path := ExpandConstant('{localappdata}\Nova\com.nova.downloadmanager.json');
  // copy template from {app}\hosts and rewrite the exe path
  if LoadStringFromFile(ExpandConstant('{app}\hosts\com.nova.downloadmanager.chromium.json'), content) then
  begin
    StringChangeEx(content, 'C:\Program Files\Nova\NovaBrowserHost.exe',
                   ExpandConstant('{app}\engine\NovaBrowserHost.exe'), True);
    SaveStringToFile(path, content, False);
  end;
end;

procedure InstallFirefoxManifest();
var
  dir: String;
begin
  dir := ExpandConstant('{userappdata}') + '\mozilla\NativeMessagingHosts';
  ForceDirectories(dir);
  FileCopy(ExpandConstant('{app}\hosts\com.nova.downloadmanager.chromium.json'),
           dir + '\com.nova.downloadmanager.json', False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    PatchChromiumHostManifest();
    if WizardIsTaskSelected('registerfirefox') then
      InstallFirefoxManifest();
  end;
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}\extensions"
Type: files; Name: "{localappdata}\Nova\com.nova.downloadmanager.json"

[UninstallRun]
; stop the background engine (graceful: it persists state)
Filename: "{app}\engine\NovaEngine.exe"; Parameters: ""; RunOnceId: "StopEngine"; Flags: runhidden; BeforeInstall: none

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchAfter}"; Flags: nowait postinstall skipifsilent

[Messages]
; Friendly, honest messaging — never claim unsigned binaries are signed.
WinVersionTooLowError=This application requires Windows 10 or later.
