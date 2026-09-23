; Local release candidate. The self-contained payload and all vendor notices
; are built by tools/package-direct.ps1; no provider installation or authentication here.
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef TestIdentity
  #define ProductName "Llumi"
  #define ProductId "Llumi"
  #define RunValue "Llumi"
#else
  ; Test installs cannot overwrite the production identity, startup entry or shortcut.
  #define ProductName "Llumi-InstallerTest-" + TestIdentity
  #define ProductId ProductName
  #define RunValue ProductName
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher=Pranesh S
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=Llumi Setup
VersionInfoProductName=Llumi
VersionInfoProductVersion={#AppVersion}.0
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
UsePreviousTasks=no
UninstallDisplayIcon={app}\Llumi.exe
UninstallDisplayName={#ProductName}
SetupIconFile=..\src\AgentMeter\Assets\Llumi.ico
OutputDir={#OutputDir}
OutputBaseFilename=Llumi-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#ProductName}"; Filename: "{app}\Llumi.exe"; Parameters: ""; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValue}"; ValueData: """{app}\Llumi.exe"" --startup"; Flags: uninsdeletevalue; Check: StartupEnabled
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#RunValue}"; Flags: deletevalue uninsdeletevalue; Check: not StartupEnabled

#ifndef TestIdentity
[Run]
Filename: "{app}\Llumi.exe"; Parameters: ""; WorkingDir: "{app}"; Flags: nowait runasoriginaluser skipifsilent
#endif

[Code]
var
  OptionsPage: TWizardPage;
  StartupCheck: TNewCheckBox;

function StartupEnabled: Boolean;
begin
  Result := StartupCheck.Checked;
end;

function InitializeSetup: Boolean;
var Choice: String;
begin
  Choice := Lowercase(ExpandConstant('{param:STARTUP|auto}'));
  Result := (Choice = 'auto') or (Choice = 'yes') or (Choice = 'no');
  if not Result then
    MsgBox('The STARTUP option must be auto, yes or no.', mbError, MB_OK);
end;

procedure InitializeWizard;
var
  Choice: String;
  ExistingInstall: Boolean;
begin
  OptionsPage := CreateCustomPage(wpSelectDir, 'Install {#ProductName}',
    'Track your AI coding usage.');
  StartupCheck := TNewCheckBox.Create(WizardForm);
  StartupCheck.Parent := OptionsPage.Surface;
  StartupCheck.Left := 0;
  StartupCheck.Top := ScaleY(12);
  StartupCheck.Width := OptionsPage.SurfaceWidth;
  StartupCheck.Height := ScaleY(36);
  StartupCheck.Caption := 'Start Llumi with Windows';
  ExistingInstall := RegKeyExists(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1');
  // The current Run entry is authoritative, including an opt-out made in the app.
  StartupCheck.Checked := ((not ExistingInstall) and (not FileExists(ExpandConstant('{localappdata}\Llumi\v2-preferences.json')))) or RegValueExists(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run', '{#RunValue}');
  Choice := Lowercase(ExpandConstant('{param:STARTUP|auto}'));
  if Choice = 'yes' then StartupCheck.Checked := True;
  if Choice = 'no' then StartupCheck.Checked := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = OptionsPage.ID then
    WizardForm.NextButton.Caption := '&Install';
end;

function RequestQuietExit: Boolean;
#ifndef TestIdentity
var ExitCode, Attempt: Integer;
#endif
begin
  Result := True;
#ifndef TestIdentity
  if FileExists(ExpandConstant('{app}\Llumi.exe')) then begin
    Exec(ExpandConstant('{app}\Llumi.exe'), '--quit', ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, ExitCode);
  end;
    // Wait for the original instance to drain provider work and release its mutex.
    // Never force-kill an unrelated provider or recursively remove in-use files.
    for Attempt := 1 to 150 do begin
      if not CheckForMutexes('Local\Llumi.V1.' + GetUserNameString) then Exit;
      Sleep(100);
    end;
    Result := False;
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // Windows Run values accept a command line no longer than 260 characters.
  // Check before any file or registry installation, including silent /DIR overrides.
  if StartupEnabled and (Length(ExpandConstant('"{app}\Llumi.exe" --startup')) > 260) then begin
    Result := 'The installation path is too long for Start with Windows. Choose a shorter /DIR path or disable Start Llumi with Windows.';
    Exit;
  end;
  if CheckForMutexes('Local\AgentMeter.V0.1.' + GetUserNameString) and
     (not CheckForMutexes('Local\Llumi.V1.' + GetUserNameString)) then begin
    Result := 'Quit the existing AgentMeter application normally, then retry.';
    Exit;
  end;
  if not RequestQuietExit then
    Result := 'Llumi is still closing. Quit Llumi from its tray menu and retry.';
end;

function InitializeUninstall: Boolean;
begin
  Result := RequestQuietExit;
  if not Result then
    SuppressibleMsgBox('Llumi is still closing. Quit Llumi from its tray menu and retry uninstall.', mbError, MB_OK, IDOK);
end;

// The uninstall log removes only installed files and the one owned Run value.
// Preferences and sanitized rolling logs under LocalAppData\Llumi are kept.
// Never recursively delete user data or any provider-owned files.

#ifndef TestIdentity
procedure CurStepChanged(CurStep: TSetupStep);
var LegacyRun: String;
begin
  if CurStep = ssPostInstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AgentMeter', LegacyRun) then
      if CompareText(LegacyRun, ExpandConstant('"{localappdata}\Programs\AgentMeter\AgentMeter.exe" --startup')) = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AgentMeter');
end;

#endif
