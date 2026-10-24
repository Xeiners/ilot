; MyNotch's installer (Inno Setup 6). Built by installer\build.ps1, which publishes the app first and passes:
;   AppVersion  the version (Directory.Build.props)
;   SourceDir   the published app (self-contained: nothing else to install)
;   OutputDir   where MyNotch-Setup-<version>.exe goes
;
; Per user by default: no administrator prompt, installed under %LOCALAPPDATA%\Programs\MyNotch (the installer offers
; "for all users" too). The notch's data (%LOCALAPPDATA%\MyNotch: settings, notes, history) is left on uninstall.

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
AppName=MyNotch
AppVersion={#AppVersion}
AppVerName=MyNotch {#AppVersion}
AppPublisher=MyNotch
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\MyNotch
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=MyNotch-Setup-{#AppVersion}
SetupIconFile=..\src\MyNotch\Assets\MyNotch.ico
UninstallDisplayIcon={app}\MyNotch.exe
UninstallDisplayName=MyNotch
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The notch running (it holds this mutex) is closed before its files are replaced or removed.
AppMutex=Local\MyNotch.SingleInstance
CloseApplications=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "startup"; Description: "Lancer MyNotch avec Windows"; GroupDescription: "Au démarrage :"
Name: "desktopicon"; Description: "Un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MyNotch"; Filename: "{app}\MyNotch.exe"
Name: "{autodesktop}\MyNotch"; Filename: "{app}\MyNotch.exe"; Tasks: desktopicon

[Registry]
; The same entry as the app's own "Lancer avec Windows" switch (Settings\StartupLaunch.cs).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyNotch"; ValueData: """{app}\MyNotch.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\MyNotch.exe"; Description: "Lancer MyNotch maintenant"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Turned on from the app's settings rather than here: removed all the same.
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v MyNotch /f"; Flags: runhidden; RunOnceId: "RemoveStartup"
