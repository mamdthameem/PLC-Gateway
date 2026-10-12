; ShotSense Gateway - setup wizard.
;
; Do not compile this directly: run installer\build.ps1, which publishes the app, stages PostgreSQL,
; cloudflared and the Visual C++ runtime into installer\staging, and passes AppVersion and Staging.
;
; The wizard collects the answers and copies files; installer\scripts\Install-Gateway.ps1 then does
; the real work (database, settings, services, firewall, tunnel) and logs it to
; C:\ProgramData\ShotSense\logs. Secrets are handed over in environment variables, never on a
; command line and never in a file.
;
; Three modes, decided when setup starts:
;   Fresh    - first install (also a re-run after a first install that did not finish)
;   Restore  - installing again after an uninstall: the data in ProgramData\ShotSense is reused
;   Upgrade  - a newer version over a working install: no questions, services restarted

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Staging
  #define Staging "staging"
#endif

#define DashboardPort "5200"

[Setup]
; Never change AppId: it is how Windows and later versions of this setup recognise the install.
AppId={{8E5B7C2A-3F1D-4B6E-9A07-5C2D1E8F4A36}
AppName=ShotSense Gateway
AppVersion={#AppVersion}
AppVerName=ShotSense Gateway {#AppVersion}
AppPublisher=Shot Sense Technologies
VersionInfoVersion={#AppVersion}
VersionInfoProductName=ShotSense Gateway
DefaultDirName={autopf}\ShotSense Gateway
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=output
OutputBaseFilename=ShotSense-Gateway-Setup-{#AppVersion}
SetupIconFile=assets\shotsense.ico
UninstallDisplayIcon={app}\shotsense.ico
UninstallDisplayName=ShotSense Gateway
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The services are stopped and started by the scripts, not by the "close applications" feature.
CloseApplications=no
SetupLogging=yes

[Files]
Source: "{#Staging}\app\*";                       DestDir: "{app}\app";         Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Staging}\pgsql\*";                     DestDir: "{app}\pgsql";       Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Staging}\cloudflared\cloudflared.exe"; DestDir: "{app}\cloudflared"; Flags: ignoreversion
Source: "scripts\*.ps1";                          DestDir: "{app}\scripts";     Flags: ignoreversion
Source: "assets\shotsense.ico";                   DestDir: "{app}";             Flags: ignoreversion
Source: "{#Staging}\redist\vc_redist.x64.exe";    DestDir: "{tmp}";             Flags: deleteafterinstall
; Unpacked early, while the wizard is still asking questions: checking the installation code, and
; stopping services before an upgrade copies anything.
Source: "scripts\Common.ps1";               Flags: dontcopy
Source: "scripts\Describe-InstallCode.ps1"; Flags: dontcopy
Source: "scripts\Stop-Services.ps1";        Flags: dontcopy

[Run]
Filename: "http://localhost:{#DashboardPort}"; Description: "Open the dashboard"; Flags: postinstall shellexec nowait skipifsilent; Check: InstallSucceeded

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Uninstall-Gateway.ps1"" -AppDir ""{app}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveServices"

[Code]
var
  InstallMode: String;
  CodePage: TInputQueryWizardPage;
  PlcPage: TInputQueryWizardPage;
  AdminPage: TInputQueryWizardPage;
  NetworkPage: TInputOptionWizardPage;
  ClientName: String;
  PublicHostname: String;
  InstallOk: Boolean;

function SetEnvironmentVariable(lpName: String; lpValue: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

function PowerShellExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\ShotSense');
end;

function InstallSucceeded: Boolean;
begin
  Result := InstallOk;
end;

{ ---- Mode ---------------------------------------------------------------------------------- }

function InitializeSetup: Boolean;
var
  HasSettings, HasService, Completed: Boolean;
begin
  HasSettings := FileExists(DataDir + '\appsettings.Production.json');
  HasService  := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\ShotSenseGateway');
  Completed   := FileExists(DataDir + '\install-complete.txt');

  if HasSettings and HasService then
    InstallMode := 'Upgrade'
  else if HasSettings and Completed then
    InstallMode := 'Restore'
  else
    InstallMode := 'Fresh';

  Log('Install mode: ' + InstallMode);
  Result := True;
end;

{ ---- Pages --------------------------------------------------------------------------------- }

procedure InitializeWizard;
begin
  CodePage := CreateInputQueryPage(wpWelcome,
    'Installation code',
    'Paste the code the ShotSense cloud showed when this client was registered.',
    'The code carries this client''s licence, its cloud keys and its internet address. ' +
    'It starts with SSG1. Treat it like a password.');
  CodePage.Add('Installation code:', False);

  PlcPage := CreateInputQueryPage(CodePage.ID,
    'PLC connection',
    'Where the gateway finds the Siemens S7-1200 on the plant network.',
    '');
  PlcPage.Add('PLC IP address:', False);
  PlcPage.Add('Rack:', False);
  PlcPage.Add('Slot:', False);
  PlcPage.Values[0] := '192.168.0.180';
  PlcPage.Values[1] := '0';
  PlcPage.Values[2] := '1';

  AdminPage := CreateInputQueryPage(PlcPage.ID,
    'Dashboard login',
    'The account for signing in to the dashboard.',
    'Choose the username and password the plant will use. ' +
    'The password needs at least 12 characters. It can be changed later.');
  AdminPage.Add('Username:', False);
  AdminPage.Add('Password:', True);
  AdminPage.Add('Type the password again:', True);

  NetworkPage := CreateInputOptionPage(AdminPage.ID,
    'Plant network access',
    'Whether other computers at the plant can open the dashboard.',
    'The dashboard always works on this PC (http://localhost:{#DashboardPort}) and, ' +
    'when the code includes a tunnel, on the internet at its public address.',
    False, False);
  NetworkPage.Add('Let other computers on the plant network open the dashboard (port {#DashboardPort}). ' +
    'Applies to Private and Domain networks only, never Public ones.');
  NetworkPage.Values[0] := True;

  if InstallMode = 'Restore' then
    PlcPage.SubCaptionLabel.Caption :=
      'An earlier installation''s data was found and will be kept. Check the PLC details.';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if InstallMode = 'Upgrade' then
    Result := (PageID = CodePage.ID) or (PageID = PlcPage.ID) or
              (PageID = AdminPage.ID) or (PageID = NetworkPage.ID)
  else if InstallMode = 'Restore' then
    { The logins are already in the kept database. }
    Result := (PageID = AdminPage.ID);
end;

{ ---- Validation ---------------------------------------------------------------------------- }

function ReadKeyValue(Lines: TArrayOfString; Key: String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Pos(Key + '=', Lines[I]) = 1 then
    begin
      Result := Copy(Lines[I], Length(Key) + 2, MaxInt);
      Exit;
    end;
end;

function CheckInstallCode(Code: String): Boolean;
var
  ResultCode: Integer;
  OutFile: String;
  Lines: TArrayOfString;
  Started: Boolean;
begin
  Result := False;
  ExtractTemporaryFile('Common.ps1');
  ExtractTemporaryFile('Describe-InstallCode.ps1');
  OutFile := ExpandConstant('{tmp}\install-code-check.txt');
  DeleteFile(OutFile);

  SetEnvironmentVariable('SHOTSENSE_INSTALL_CODE', Code);
  try
    Started := Exec(PowerShellExe,
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\Describe-InstallCode.ps1') +
      '" -OutFile "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  finally
    SetEnvironmentVariable('SHOTSENSE_INSTALL_CODE', '');
  end;

  if (not Started) or (not LoadStringsFromFile(OutFile, Lines)) then
  begin
    MsgBox('Setup could not check the installation code (PowerShell did not run).', mbError, MB_OK);
    Exit;
  end;
  if ReadKeyValue(Lines, 'ok') <> '1' then
  begin
    MsgBox(ReadKeyValue(Lines, 'error'), mbError, MB_OK);
    Exit;
  end;

  ClientName := ReadKeyValue(Lines, 'clientName');
  PublicHostname := ReadKeyValue(Lines, 'publicHostname');
  Result := True;
end;

function IsValidIPv4(S: String): Boolean;
var
  Part: String;
  I, Count, P: Integer;
begin
  Result := False;
  Count := 0;
  S := S + '.';
  while S <> '' do
  begin
    P := Pos('.', S);
    Part := Copy(S, 1, P - 1);
    Delete(S, 1, P);
    if (Length(Part) < 1) or (Length(Part) > 3) then Exit;
    for I := 1 to Length(Part) do
      if (Part[I] < '0') or (Part[I] > '9') then Exit;
    if StrToInt(Part) > 255 then Exit;
    Count := Count + 1;
  end;
  Result := (Count = 4);
end;

function IsValidUsername(S: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := (Length(S) >= 3) and (Length(S) <= 64);
  if not Result then Exit;
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if not (((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or
            ((C >= '0') and (C <= '9')) or (C = '.') or (C = '_') or (C = '@') or (C = '-')) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Rack, Slot: Integer;
  Where: String;
begin
  Result := True;

  if CurPageID = CodePage.ID then
  begin
    Result := CheckInstallCode(Trim(CodePage.Values[0]));
    if Result then
    begin
      if PublicHostname <> '' then
        Where := ', on the internet at https://' + PublicHostname
      else
        Where := ' (no internet address in this code)';
      PlcPage.Description := 'Installing for ' + ClientName + Where + '.';
    end;
  end

  else if CurPageID = PlcPage.ID then
  begin
    if not IsValidIPv4(Trim(PlcPage.Values[0])) then
    begin
      MsgBox('Enter the PLC''s IP address, for example 192.168.0.180.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    Rack := StrToIntDef(Trim(PlcPage.Values[1]), -1);
    Slot := StrToIntDef(Trim(PlcPage.Values[2]), -1);
    if (Rack < 0) or (Rack > 7) or (Slot < 0) or (Slot > 31) then
    begin
      MsgBox('Rack is a number from 0 to 7 and Slot from 0 to 31. For an S7-1200 they are normally 0 and 1.',
        mbError, MB_OK);
      Result := False;
    end;
  end

  else if CurPageID = AdminPage.ID then
  begin
    if not IsValidUsername(Trim(AdminPage.Values[0])) then
    begin
      MsgBox('A username is 3 to 64 characters: letters, digits and . _ @ - only.', mbError, MB_OK);
      Result := False;
    end
    else if Length(AdminPage.Values[1]) < 12 then
    begin
      MsgBox('The password needs at least 12 characters.', mbError, MB_OK);
      Result := False;
    end
    else if AdminPage.Values[1] <> AdminPage.Values[2] then
    begin
      MsgBox('The two passwords are different. Type them again.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ ---- Install ------------------------------------------------------------------------------- }

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if InstallMode <> 'Upgrade' then Exit;

  { A running exe cannot be replaced: stop the services before any file is copied. }
  ExtractTemporaryFile('Stop-Services.ps1');
  if not Exec(PowerShellExe,
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\Stop-Services.ps1') +
      '" -AppDir "' + ExpandConstant('{app}') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     or (ResultCode <> 0) then
    Result := 'Setup could not stop the running ShotSense services. ' +
              'Stop them in Services (services.msc) and run setup again.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Params, OpenFirewall: String;
  ResultCode: Integer;
  Started: Boolean;
begin
  if CurStep <> ssPostInstall then Exit;

  WizardForm.StatusLabel.Caption := 'Setting up the database and services. This can take a few minutes...';
  WizardForm.ProgressGauge.Style := npbstMarquee;

  if NetworkPage.Values[0] then OpenFirewall := '1' else OpenFirewall := '0';

  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\scripts\Install-Gateway.ps1') + '"' +
    ' -Mode ' + InstallMode +
    ' -AppDir "' + ExpandConstant('{app}') + '"' +
    ' -OpenFirewall ' + OpenFirewall +
    ' -VcRedist "' + ExpandConstant('{tmp}\vc_redist.x64.exe') + '"' +
    ' -SummaryFile "' + ExpandConstant('{tmp}\install-summary.txt') + '"';
  if InstallMode <> 'Upgrade' then
    Params := Params +
      ' -PlcIp "' + Trim(PlcPage.Values[0]) + '"' +
      ' -PlcRack ' + Trim(PlcPage.Values[1]) +
      ' -PlcSlot ' + Trim(PlcPage.Values[2]);

  SetEnvironmentVariable('SHOTSENSE_INSTALL_CODE', Trim(CodePage.Values[0]));
  SetEnvironmentVariable('SHOTSENSE_ADMIN_USERNAME', Trim(AdminPage.Values[0]));
  SetEnvironmentVariable('SHOTSENSE_ADMIN_PASSWORD', AdminPage.Values[1]);
  try
    Started := Exec(PowerShellExe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  finally
    SetEnvironmentVariable('SHOTSENSE_INSTALL_CODE', '');
    SetEnvironmentVariable('SHOTSENSE_ADMIN_USERNAME', '');
    SetEnvironmentVariable('SHOTSENSE_ADMIN_PASSWORD', '');
  end;

  WizardForm.ProgressGauge.Style := npbstNormal;
  InstallOk := Started and (ResultCode = 0);
  Log('Install-Gateway.ps1 exit code: ' + IntToStr(ResultCode));

  if not InstallOk then
    MsgBox('The program files were copied, but setting up the gateway did not finish.' + #13#10#13#10 +
      'The reason is in the newest install log in ' + DataDir + '\logs.' + #13#10#13#10 +
      'Fix the cause and run this setup again. It picks up from where it stopped and keeps ' +
      'anything already set up.', mbError, MB_OK);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Summary: AnsiString;
begin
  if CurPageID <> wpFinished then Exit;

  if InstallOk then
  begin
    if LoadStringFromFile(ExpandConstant('{tmp}\install-summary.txt'), Summary) then
      WizardForm.FinishedLabel.Caption :=
        'ShotSense Gateway is installed and running. It starts by itself whenever this PC starts.' + #13#10#13#10 +
        String(Summary)
    else
      WizardForm.FinishedLabel.Caption := 'ShotSense Gateway is installed and running.';
  end
  else
    WizardForm.FinishedLabel.Caption :=
      'Setup did not finish. The reason is in the newest install log in ' + DataDir + '\logs. ' +
      'Run this setup again after fixing it.';
end;

{ ---- Uninstall ----------------------------------------------------------------------------- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    MsgBox('ShotSense Gateway was removed.' + #13#10#13#10 +
      'Its settings, logs and database, with all recorded history, were kept in ' +
      ExpandConstant('{commonappdata}') + '\ShotSense. Installing again picks them up.' + #13#10#13#10 +
      'Delete that folder only if this data is never needed again.', mbInformation, MB_OK);
end;
