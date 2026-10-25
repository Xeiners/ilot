; Ilot's installer (Inno Setup 6). Built by installer\build.ps1, which publishes the app first and passes:
;   AppVersion  the version (Directory.Build.props)
;   SourceDir   the published app (self-contained: nothing else to install)
;   OutputDir   where Ilot-Setup-<version>.exe goes
;
; Per user by default: no administrator prompt, installed under %LOCALAPPDATA%\Programs\Ilot (the installer offers
; "for all users" too). The notch's data (%LOCALAPPDATA%\Ilot: settings, notes, history) is left on uninstall.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{96759160-A82D-4F30-B9A8-8FEBD96293E8}
AppName=Ilot
AppVersion={#AppVersion}
AppVerName=Ilot {#AppVersion}
AppPublisher=Xeiners
AppPublisherURL=https://github.com/Xeiners
AppSupportURL=https://github.com/Xeiners
AppCopyright=Copyright © 2026 Xeiners
VersionInfoCompany=Xeiners
VersionInfoCopyright=Copyright © 2026 Xeiners
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Ilot
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Ilot-Setup-{#AppVersion}
SetupIconFile=..\src\Ilot\Assets\Ilot.ico
UninstallDisplayIcon={app}\Ilot.exe
UninstallDisplayName=Ilot
WizardStyle=modern
; The licence (MIT), shown before installing.
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
; The notch running (it holds this mutex) is closed before its files are replaced or removed.
AppMutex=Local\Ilot.SingleInstance
CloseApplications=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "startup"; Description: "Lancer Ilot avec Windows"; GroupDescription: "Au démarrage :"
Name: "desktopicon"; Description: "Un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Ilot"; Filename: "{app}\Ilot.exe"
Name: "{autodesktop}\Ilot"; Filename: "{app}\Ilot.exe"; Tasks: desktopicon

[Registry]
; The same entry as the app's own "Lancer avec Windows" switch (Settings\StartupLaunch.cs).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Ilot"; ValueData: """{app}\Ilot.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Ilot.exe"; Description: "Lancer Ilot maintenant"; Flags: nowait postinstall skipifsilent
; An update (Ilot runs this installer silently, then quits): Ilot starts again, as the user, once it is done.
Filename: "{app}\Ilot.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
; Turned on from the app's settings rather than here: removed all the same.
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v Ilot /f"; Flags: runhidden; RunOnceId: "RemoveStartup"

[Code]
// An update started by Ilot itself: it quits right after starting this installer. Give it a few seconds to be gone
// (its single-instance mutex) before Setup checks for it (AppMutex).
function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while CheckForMutexes('Local\Ilot.SingleInstance') and (Waited < 10000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := True;
end;
