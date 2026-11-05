; Miu Smart Storage installer (Inno Setup 6). Built by build/package-windows.ps1.
; Per-user install: no admin rights needed, nothing written outside your user profile.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion "0.0.0"
#endif

[Setup]
AppId={{7B0E5C61-2F4D-4C1E-9C3B-6D2E4A8F1A10}
AppName=Miu Smart Storage
AppVersion={#AppVersion}
VersionInfoVersion={#AppNumericVersion}
AppPublisher=Khang
AppPublisherURL=https://duykhang.com
AppSupportURL=https://github.com/khangkhangg/Smart-Drive-Storage
DefaultDirName={localappdata}\Programs\Miu Smart Storage
DefaultGroupName=Miu Smart Storage
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=Miu-Smart-Storage-{#AppVersion}-windows-x64-setup
SetupIconFile={#RootDir}\src\Miu.App\Assets\miu.ico
UninstallDisplayIcon={app}\Miu.exe
UninstallDisplayName=Miu Smart Storage
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Miu Smart Storage"; Filename: "{app}\Miu.exe"
Name: "{userdesktop}\Miu Smart Storage"; Filename: "{app}\Miu.exe"; Tasks: desktopicon

[Registry]
; Removed on uninstall if Miu created them (start at sign-in, "Analyze with Miu" in Explorer).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MiuSmartStorage"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\MiuAnalyze"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Drive\shell\MiuAnalyze"; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\Miu.exe"; Description: "Start Miu Smart Storage"; Flags: nowait postinstall skipifsilent
