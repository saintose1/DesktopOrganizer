; Per-user installer (no administrator rights). Built by scripts/publish.ps1 or CI:
;   ISCC.exe /DAppVersion=0.2.0 /DSourceDir=artifacts\publish\DesktopOrganizer installer\DesktopOrganizer.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\DesktopOrganizer"
#endif

[Setup]
AppId={{6E0A3C55-2B5D-4D2E-9F1B-8C4A7E1D2F90}
AppName=DesktopOrganizer
AppVersion={#AppVersion}
AppPublisher=saintose1
AppPublisherURL=https://github.com/saintose1/DesktopOrganizer
DefaultDirName={localappdata}\Programs\DesktopOrganizer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=DesktopOrganizer-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\DesktopOrganizer.App\Assets\Branding\application.ico
UninstallDisplayIcon={app}\DesktopOrganizer.App.exe
LicenseFile=..\LICENSE
; Close a running instance (it returns hidden icons on exit) before files are replaced or removed.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{userprograms}\DesktopOrganizer"; Filename: "{app}\DesktopOrganizer.App.exe"
Name: "{userdesktop}\DesktopOrganizer"; Filename: "{app}\DesktopOrganizer.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DesktopOrganizer.App.exe"; Description: "{cm:LaunchProgram,DesktopOrganizer}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Never leave a user's desktop items hidden: un-hide everything listed in hidden-icons.json first.
Filename: "{app}\DesktopOrganizer.App.exe"; Parameters: "--RestoreHidden=true --Quiet=true"; Flags: waituntilterminated runhidden; RunOnceId: "RestoreHidden"

[Registry]
; Autostart is written by the application itself (Settings); remove it with the program.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "DesktopOrganizer"; Flags: uninsdeletevalue dontcreatekey
