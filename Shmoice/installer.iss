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
; If Shmoice is somehow still running when files are replaced, close it rather than fail.
CloseApplications=yes
RestartApplications=no
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
; An update started from inside Shmoice runs this installer silently with /relaunch=1,
; so open the new version again when it's done.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: RelaunchRequested

[Code]
function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// Shmoice closes itself right after starting an update. Give it up to 15 seconds to be gone
// before its exe is replaced. "Shmoice.Running" is created in App.xaml.cs; keep the names the same.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  I: Integer;
begin
  Result := '';
  if not RelaunchRequested then Exit;
  for I := 1 to 75 do
  begin
    if not CheckForMutexes('Shmoice.Running') then Break;
    Sleep(200);
  end;
end;
