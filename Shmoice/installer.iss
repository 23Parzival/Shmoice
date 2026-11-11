; Inno Setup script for Shmoice. Build it with publish.ps1 (which runs dotnet publish first).
; Installs per-user (no admin prompt) into %LOCALAPPDATA%\Programs\Shmoice.
; Uninstalling keeps the user's commands in %APPDATA%\Shmoice.

#define AppName "Shmoice"
#define AppExe "Shmoice.exe"
#define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "publish\" + AppExe)

[Setup]
; Keep this AppId the same forever so upgrades replace the old install.
AppId={{6F1C2B7E-4A3D-4E8B-9C5A-2D7F0E1B8A63}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=publish
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=Shmoice\Assets\Shmoice.ico

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start Shmoice when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
