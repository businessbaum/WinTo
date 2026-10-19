; ============================================================================
;  WinTo installer (Inno Setup 6). Called MultiTool until 0.13.0.
;
;  Built by tools\publish.ps1, which creates dist\app and calls iscc with
;  /DAppVersion.
;
;  Why not MSIX: it runs the app in a container, and a toolbox that reads HKLM
;  autostart entries, uninstalls other programs and touches files anywhere is
;  exactly what that container prevents.
;
;  Updating: an installed version is closed and uninstalled silently before the
;  new one goes in (PrepareToInstall). Settings in %APPDATA% stay (the app moves
;  %APPDATA%\MultiTool to %APPDATA%\WinTo on its first start), and so does the
;  autostart entry. An install from the MultiTool days is replaced the same way:
;  the AppId is unchanged.
; ============================================================================

#define MyAppName      "WinTo"
#define MyAppPublisher "WinTo"
#define MyAppExeName   "WinTo.exe"

; The name until 0.13.0: its EXE, autostart value, task and Explorer keys are cleaned up.
#define FormerName     "MultiTool"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

; Identity of the installation (update vs. second install). Never change it.
#define MyAppId "7A6C1F84-3E2B-4A19-9C55-2E0B6D1A4F73"

; Always redirects to the current .NET 10 desktop runtime.
#define DotnetUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
; {{ is a literal brace: the preprocessor inserts the GUID, then Inno parses again.
AppId={{{#MyAppId}}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no

; Program Files: read-only for the user, which the elevated module search relies on.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

OutputDir=..\dist
OutputBaseFilename=WinTo-{#AppVersion}-Setup
SetupIconFile=..\src\MultiTool.Host\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; \
    GroupDescription: "Additional shortcuts"

Name: "autostart"; Description: "Start WinTo with Windows (tray only)"; \
    GroupDescription: "Startup"; Flags: unchecked

[Files]
; dist\app from tools\publish.ps1, including the modules\ tree.
Source: "..\dist\app\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Per user, not per machine: the app runs in the user's context.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --background"; \
    Flags: uninsdeletevalue; Tasks: autostart

; Never deleted on install: the app has its own autostart switch, and an update with
; the task unchecked would silently undo it. Still removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: none; ValueName: "{#MyAppName}"; Flags: uninsdeletevalue

; The former name's autostart value points at an EXE that no longer exists.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: none; ValueName: "{#FormerName}"; Flags: deletevalue uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Start {#MyAppName} now"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; %APPDATA%\WinTo (settings, module data) is kept on purpose for a reinstall.
Type: filesandordirs; Name: "{app}\logs"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  DotnetPage: TOutputProgressWizardPage;
  UpdatePage: TOutputProgressWizardPage;
  HadAutostart: Boolean;

function SetEnvironmentVariable(Name: String; Value: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

{ The entry Inno writes for this AppId. Concatenated so the preprocessor sees the
  inline directive on its own. }
function UninstallKey: String;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#MyAppId}' + '}_is1';
end;

{ Checks for a version folder rather than registry keys: the keys differ by
  install route (installer, winget, Visual Studio), the folders do not. }
function DesktopRuntimePresent: Boolean;
var
  Base: String;
  Found: TFindRec;
begin
  Result := False;
  Base := ExpandConstant('{commonpf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';
  if not DirExists(Base) then Exit;

  if FindFirst(Base + '\10.*', Found) then
  begin
    try
      repeat
        if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

procedure InitializeWizard;
var
  Dir: String;
begin
  DotnetPage := CreateOutputProgressPage(
    'Prerequisite', 'Installing the .NET 10 desktop runtime.');
  UpdatePage := CreateOutputProgressPage(
    'Update', 'Replacing the installed version of {#MyAppName}.');

  // Inno offers the previous folder for the same AppId. An install from the MultiTool days
  // moves from ...\MultiTool to ...\WinTo; a chosen parent folder stays.
  Dir := RemoveBackslashUnlessRoot(WizardForm.DirEdit.Text);
  if CompareText(ExtractFileName(Dir), '{#FormerName}') = 0 then
    WizardForm.DirEdit.Text := AddBackslash(ExtractFileDir(Dir)) + '{#MyAppName}';
end;

{ The installed version of the same kind, from its uninstall entry: per machine for an
  admin install, per user otherwise (PrivilegesRequiredOverridesAllowed). A per-user
  setup reaching for the machine-wide install would need an administrator prompt. }
function FindPrevious(var Root: Integer; var Version, Uninstaller, Location: String): Boolean;
begin
  Result := False;
  if IsAdminInstallMode then Root := HKLM else Root := HKCU;
  if not RegQueryStringValue(Root, UninstallKey, 'UninstallString', Uninstaller) then Exit;

  if not RegQueryStringValue(Root, UninstallKey, 'DisplayVersion', Version) then Version := '';
  if not RegQueryStringValue(Root, UninstallKey, 'InstallLocation', Location) then Location := '';
  Uninstaller := RemoveQuotes(Uninstaller);
  if Location = '' then Location := ExtractFileDir(Uninstaller);
  Result := FileExists(Uninstaller);
end;

function AtLeast(const Version, Minimum: String): Boolean;
var
  A, B: Int64;
begin
  Result := StrToVersion(Version, A) and StrToVersion(Minimum, B)
    and (ComparePackedVersion(A, B) >= 0);
end;

{ WinTo lives in the tray, so there is no window for Windows' restart manager
  to close. 0.12.0 and later end themselves on --quit, through the running instance
  and as the signed-in user; whatever still runs from the install folder ten seconds
  later is ended. Older versions don't know the switch and would come forward
  instead, so they are ended right away. Only that folder: a copy elsewhere (a
  development build) keeps running. The EXE has either name. }
procedure CloseRunningApp(const Location, Version: String);
var
  Exe, Script: String;
  Code, Wait: Integer;
begin
  Exe := AddBackslash(Location) + '{#MyAppExeName}';
  if not FileExists(Exe) then Exe := AddBackslash(Location) + '{#FormerName}.exe';

  { Waiting only makes sense for an instance that was asked to quit. }
  Wait := 0;
  if FileExists(Exe) and AtLeast(Version, '0.12.0') then
  begin
    ExecAsOriginalUser(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Wait := 10;
  end;

  Script :=
    '$folder = ''' + AddBackslash(Location) + '*'';' +
    '$deadline = (Get-Date).AddSeconds(' + IntToStr(Wait) + ');' +
    'function Running { Get-Process -Name {#MyAppName},{#FormerName} -ErrorAction SilentlyContinue | Where-Object { $_.Path -like $folder } };' +
    'while ((Running) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Milliseconds 250 };' +
    'Running | Stop-Process -Force -ErrorAction SilentlyContinue';

  // The sys constant is the 64-bit System32 in 64-bit mode; a 32-bit PowerShell couldn't
  // read the path of a 64-bit process and would match nothing.
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"',
       '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ Inno's uninstaller copies itself to a temporary file and may return before it is
  done; its registry entry and unins000.exe disappearing are the reliable signs. }
function UninstallPrevious(Root: Integer; const Uninstaller: String): Boolean;
var
  Code, Waited: Integer;
begin
  Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE,
       ewWaitUntilTerminated, Code);

  Waited := 0;
  while (RegKeyExists(Root, UninstallKey) or FileExists(Uninstaller)) and (Waited < 60000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;

  Result := not RegKeyExists(Root, UninstallKey);
end;

{ Runs after the wizard pages, so the previous folder and tasks were already
  offered. A failed uninstall is no reason to stop: installing over it still works. }
procedure ReplacePrevious;
var
  Root: Integer;
  Version, Uninstaller, Location, Value: String;
begin
  if not FindPrevious(Root, Version, Uninstaller, Location) then Exit;

  { Uninstalling removes the autostart entry; CurStepChanged writes it back. }
  HadAutostart := RegQueryStringValue(HKCU, RunKey, '{#MyAppName}', Value)
    or RegQueryStringValue(HKCU, RunKey, '{#FormerName}', Value);

  UpdatePage.Show;
  try
    UpdatePage.SetText('Closing {#MyAppName}...', '');
    CloseRunningApp(Location, Version);

    UpdatePage.SetText('Removing ' + Version + '...', 'Settings are kept.');

    { Inherited by the uninstaller: an update keeps the "Run as administrator" task. The
      name stays MULTITOOL_UPDATE: uninstallers from the MultiTool days read it. }
    SetEnvironmentVariable('MULTITOOL_UPDATE', '1');

    if UninstallPrevious(Root, Uninstaller) then
      Log('Removed ' + Version + ' from ' + Location + '.')
    else
      Log('The previous version could not be removed; installing over it.');
  finally
    UpdatePage.Hide;
  end;
end;

// The "Explorer menu entries" feature writes these; they would point at a missing EXE.
// Both names: entries from the MultiTool days and from WinTo.
procedure RemoveExplorerMenu;
var
  Shell: String;
begin
  Shell := 'Software\Classes\SystemFileAssociations\';
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + 'image\shell\MultiTool.Edit');
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + 'image\shell\MultiTool.Open');
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + 'video\shell\MultiTool.Open');
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + 'image\shell\MultiTool.Convert');
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + 'image\shell\MultiTool.Metadata');
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + '.pdf\shell\MultiTool.Metadata');
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + '.docx\shell\MultiTool.Metadata');
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + '.xlsx\shell\MultiTool.Metadata');
  RegDeleteKeyIncludingSubkeys(HKCU, Shell + '.pptx\shell\MultiTool.Metadata');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\shell\MultiTool.DiskUsage');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Drive\shell\MultiTool.DiskUsage');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Applications\MultiTool.exe');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Applications\WinTo.exe');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\WinTo.Picture');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\WinTo.Video');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\WinTo\Capabilities');
  RegDeleteValue(HKCU, 'Software\RegisteredApplications', 'WinTo');
end;

{ A real uninstall removes the scheduled task behind "Run as administrator"; an update,
  which uninstalls first (see ReplacePrevious), keeps it. The task points at the same EXE. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if (CurUninstallStep = usUninstall) and (GetEnv('MULTITOOL_UPDATE') <> '1') then
  begin
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "{#MyAppName}\Run as administrator" /f',
         '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "{#FormerName}\Run as administrator" /f',
         '', SW_HIDE, ewWaitUntilTerminated, Code);
    RemoveExplorerMenu;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;

  if HadAutostart and not WizardIsTaskSelected('autostart') then
    RegWriteStringValue(HKCU, RunKey, '{#MyAppName}',
      '"' + ExpandConstant('{app}\{#MyAppExeName}') + '" --background');

  // The former name's "Run as administrator" task starts an EXE that is gone; the app turns
  // its switch off and creates the new task with one prompt when switched on again.
  if IsAdminInstallMode then
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "{#FormerName}\Run as administrator" /f',
         '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function InstallDesktopRuntime: Boolean;
var
  Target, Message: String;
  Code: Integer;
begin
  Result := False;
  Target := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');

  DotnetPage.SetText('Downloading...', '{#DotnetUrl}');
  DotnetPage.Show;
  try
    { bitsadmin ships with every Windows and can resume. }
    if not Exec(ExpandConstant('{cmd}'),
                '/c bitsadmin /transfer WinToDotnet /download /priority FOREGROUND "{#DotnetUrl}" "'
                  + Target + '"',
                '', SW_HIDE, ewWaitUntilTerminated, Code) or not FileExists(Target) then
    begin
      MsgBox('The .NET 10 desktop runtime could not be downloaded.'#13#10#13#10
             + 'Install it manually:'#13#10
             + '{#DotnetUrl}'#13#10#13#10
             + 'Setup continues; WinTo starts once the runtime is installed.',
             mbInformation, MB_OK);
      Exit;
    end;

    DotnetPage.SetText('Installing...', '');
    if Exec(Target, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    begin
      { 3010 = success, restart recommended. }
      Result := (Code = 0) or (Code = 3010);
      if not Result then
      begin
        Message := 'The .NET 10 desktop runtime reported error ' + IntToStr(Code) + '.';
        MsgBox(Message, mbInformation, MB_OK);
      end;
    end;
  finally
    DotnetPage.Hide;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  ReplacePrevious;
  if not DesktopRuntimePresent then
    InstallDesktopRuntime;
end;
