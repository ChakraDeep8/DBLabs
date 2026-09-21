; Inno Setup script — builds a single one-click Setup.exe from the self-contained
; `dotnet publish` output: Start Menu shortcut, optional desktop icon, proper uninstaller,
; and versioned upgrades in place.
;
; Build order:
;   1. dotnet publish ..\DBLabs -c Release -r win-x64 --self-contained true -o ..\publish
;   2. "ISCC.exe" DBLabs.iss
;   Output lands in ..\dist\DBLabs-Setup-<version>.exe
;
; Both steps are wrapped by installer\build-installer.ps1 — run that instead of doing them
; by hand.
;
; Note: the YOLOv8 weights are NOT part of this installer. They are Ultralytics' and
; AGPL-3.0, so the app fetches them to the user's app-data folder on first use instead —
; see Services/ModelStore.cs. That also keeps this installer small.

#define MyAppName "DBLabs"
#define MyAppFullName "DBLabs — Dataset Builder"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ChakraDeep8"
#define MyAppURL "https://github.com/ChakraDeep8/DBLabs"
#define MyAppExeName "DBLabs.exe"

[Setup]
; Fixed GUID — kept stable across versions so Windows treats later releases as upgrades
; rather than installing a second copy alongside.
AppId={{F10CB3E8-F375-428A-B255-2DBDFC608C2D}
AppName={#MyAppFullName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppFullName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Installs per-user with no admin prompt, or elevates if the person chooses — which is what
; makes this genuinely one-click for someone without admin rights.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=DBLabs-Setup-{#MyAppVersion}
SetupIconFile=..\DBLabs\Assets\logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppFullName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete] for %AppData%\DBLabs on purpose — the label library and the downloaded
; model live there, and wiping them on uninstall would force a 43MB re-download and lose the
; user's labels if they ever reinstall.
