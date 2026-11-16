; Included by the generated Build-Installer entry point. No network or UI plug-ins.
#if Ver < EncodeVer(6, 6, 0)
  #error Inno Setup 6.6 or newer is required.
#endif
#ifndef CompilationMode
  #define CompilationMode "ReadyToRun"
#endif
#if (CompilationMode != "ReadyToRun") && (CompilationMode != "AotExperimental")
  #error Unsupported application compilation mode.
#endif
#if (CompilationMode == "AotExperimental") && (AppArch != "x64")
  #error AotExperimental supports only x64.
#endif

[Setup]
AppId=HeyMelody
#if CompilationMode == "AotExperimental"
AppName=HeyMelody（Native AOT 实验版）
AppVerName=HeyMelody {#AppVersion}（Native AOT 实验版）
UninstallDisplayName=HeyMelody {#AppVersion}（Native AOT 实验版）
#else
AppName=HeyMelody
#endif
AppVersion={#AppVersion}
AppPublisher=HeyMelody contributors
AppPublisherURL=https://github.com/swift-lee/heymelody-windows
AppSupportURL=https://github.com/swift-lee/heymelody-windows/issues
DefaultDirName={code:GetDefaultDirectory}
PrivilegesRequired=lowest
MinVersion=10.0.19041
#if AppArch == "x64"
ArchitecturesAllowed=x64os
#elif AppArch == "arm64"
ArchitecturesAllowed=arm64
#else
  #error Unsupported application architecture.
#endif
; Keep the shared per-user uninstall registration in HKCU32 on both architectures.
WizardStyle=modern
DisableWelcomePage=yes
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=no
DirExistsWarning=no
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\{#AppArch}\HeyMelody.exe
SetupIconFile={#AppIcon}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
Compression=lzma2
SolidCompression=yes
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#LanguageFile}"

[Files]
; List the temporary helper first to avoid decompressing the payload to reach it.
Source: "{#HelperExe}"; DestName: "HeyMelody.InstallerSupport.exe"; Flags: dontcopy noencryption
; Keep the independent helper and inventory until a successful uninstall. They
; must survive a failed or interrupted uninstall and do not use the app runtime.
Source: "{#HelperExe}"; DestDir: "{app}\.installer"; DestName: "HeyMelody.InstallerSupport.exe"; Flags: ignoreversion uninsneveruninstall
Source: "{#FilesManifest}"; DestDir: "{app}\.installer"; DestName: "payload-files.txt"; Flags: ignoreversion uninsneveruninstall
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; Both modes share installation identity and switch only after the previous
; payload has been uninstalled, so an AOT backend cannot retain CoreCLR files.
Root: HKCU32; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\HeyMelody_is1"; ValueType: string; ValueName: "CompilationMode"; ValueData: "{#CompilationMode}"; Flags: uninsdeletevalue

[Icons]
Name: "{userprograms}\HeyMelody"; Filename: "{app}\{#AppArch}\HeyMelody.exe"; WorkingDir: "{app}\{#AppArch}"

[Run]
Filename: "{app}\{#AppArch}\HeyMelody.exe"; WorkingDir: "{app}\{#AppArch}"; Description: "打开 HeyMelody"; Flags: nowait postinstall unchecked skipifsilent

[Code]
const
  LegacyKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\HeyMelody';
  InnoKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\HeyMelody_is1';
  HelperName = 'HeyMelody.InstallerSupport.exe';
  InventoryName = 'payload-files.txt';
  SetupLockName = 'Local\HeyMelody.Setup';
  UninstallLockName = 'Local\HeyMelody.Uninstall';
  ErrorAlreadyExists = 183;

var
  SetupLock, UninstallLock: LongWord;
  SetupHelper, UninstallHelper, PendingInnoCleanup: String;
  DesktopChoice, DesktopChoiceApplied, Installed, FinishShown: Boolean;
  UninstallDesktopOwned: Boolean;
  DesktopCheckbox: TNewCheckListBox;
  AdditionalExitCode: Integer;

function CreateOperationMutex(Attributes: LongWord; InitialOwner: Boolean;
  Name: String): LongWord;
  external 'CreateMutexW@kernel32.dll stdcall';
function CloseOperationHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

procedure ReportProblem(const Text: String);
var
  Quiet: Boolean;
begin
  Log(Copy(Text, 1, 1600));
  if IsUninstaller then
    Quiet := UninstallSilent
  else
    Quiet := WizardSilent;
  if not Quiet then
    SuppressibleMsgBox(Text, mbError, MB_OK, IDOK);
end;

procedure ReleaseLock(var Handle: LongWord);
begin
  if Handle <> 0 then begin
    CloseOperationHandle(Handle);
    Handle := 0;
  end;
end;

function AcquireLock(const Name, OtherName: String; SkipOther: Boolean;
  var Handle: LongWord): Boolean;
var
  LastError: LongWord;
begin
  Handle := CreateOperationMutex(0, False, Name);
  LastError := DLLGetLastError;
  Result := (Handle <> 0) and (LastError <> ErrorAlreadyExists);
  if Result and not SkipOther then
    Result := not CheckForMutexes(OtherName);
  if not Result then begin
    ReleaseLock(Handle);
    ReportProblem('另一个 HeyMelody 安装或卸载程序正在运行，请稍后重试。');
  end;
end;

function HasFromInstallerSwitch: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/FROMINSTALLER') = 0 then begin
      Result := True;
      Exit;
    end;
end;

function SameDirectory(const First, Second: String): Boolean;
begin
  Result := (First <> '') and (Second <> '') and
    (CompareText(RemoveBackslashUnlessRoot(ExpandFileName(First)),
      RemoveBackslashUnlessRoot(ExpandFileName(Second))) = 0);
end;

function GetDefaultDirectory(Param: String): String;
begin
  if not RegQueryStringValue(HKCU32, LegacyKey, 'InstallLocation', Result) or
      (Result = '') then
    Result := ExpandConstant('{localappdata}\Programs\HeyMelody');
end;

procedure AppendOutput(var Text: String; const Lines: TArrayOfString);
var
  I: Integer;
begin
  for I := 0 to GetArrayLength(Lines) - 1 do
    if (I < 3) and (Length(Text) < 1200) then begin
      if Text <> '' then Text := Text + #13#10;
      Text := Text + Copy(Lines[I], 1, 240);
    end;
end;

function RunHelper(const Filename, Parameters: String; var ExitCode: Integer;
  var Details: String): Boolean;
var
  Output: TExecOutput;
begin
  Details := '';
  ExitCode := -1;
  try
    Result := ExecAndCaptureOutput(Filename, Parameters, '', SW_HIDE,
      ewWaitUntilTerminated, ExitCode, Output);
    if Result then begin
      AppendOutput(Details, Output.StdErr);
      AppendOutput(Details, Output.StdOut);
      if Output.Error then begin
        Result := False;
        Details := '无法完整读取安装器辅助程序的结果。';
      end;
    end else
      Details := SysErrorMessage(ExitCode);
  except
    Result := False;
    Details := Copy(GetExceptionMessage, 1, 1200);
  end;
  Log('Installer helper exit: ' + IntToStr(ExitCode));
  if Details <> '' then Log(Copy(Details, 1, 1400));
end;

function InitializeSetup: Boolean;
var
  ExitCode: Integer;
  Details: String;
begin
  Result := AcquireLock(SetupLockName, UninstallLockName, False, SetupLock);
  if not Result then Exit;
  try
    ExtractTemporaryFile(HelperName);
    SetupHelper := ExpandConstant('{tmp}\') + HelperName;
    Result := RunHelper(SetupHelper, 'desktop-preference read', ExitCode, Details);
    if Result and ((ExitCode = 0) or (ExitCode = 20)) then
      DesktopChoice := ExitCode = 0
    else begin
      ReportProblem('无法读取上次的桌面快捷方式选择。原有安装偏好不会被重置。' + #13#10 +
        Details);
      Result := False;
    end;
  except
    ReportProblem('无法准备安装器辅助程序。' + #13#10 +
      Copy(GetExceptionMessage, 1, 1200));
    Result := False;
  end;
end;

function CheckInstallation(const Directory: String; InventoryRequired: Boolean;
  const Helper: String): String;
var
  Command, Details, NormalizedDirectory, DirectoryArgument: String;
  ExitCode: Integer;
begin
  Result := '';
  NormalizedDirectory := RemoveBackslashUnlessRoot(ExpandFileName(Directory));
  DirectoryArgument := NormalizedDirectory;
  { Windows argv treats a final backslash before the closing quote as an escape.
    Non-root paths are normalized above; preserve a root path's final backslash. }
  if Copy(DirectoryArgument, Length(DirectoryArgument), 1) = '\' then
    DirectoryArgument := DirectoryArgument + '\';
  Command := 'check-running';
  if InventoryRequired or FileExists(AddBackslash(NormalizedDirectory) +
      '.installer\' + InventoryName) then
    Command := 'check-installation';
  Log('Installation preflight ' + Command + ': ' + Copy(NormalizedDirectory, 1, 600));
  if not RunHelper(Helper, Command + ' "' + DirectoryArgument + '"', ExitCode, Details) or
      (ExitCode <> 0) then
    Result := '请关闭所有 HeyMelody 窗口，并从托盘菜单退出后台后重试。' + #13#10 +
      Details;
end;

function RegisteredInnoUninstaller(const Directory: String): String;
var
  Value, Base: String;
  I: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKCU32, InnoKey, 'UninstallString', Value) then Exit;
  Value := RemoveQuotes(Trim(Value));
  Base := ExtractFileName(Value);
  if not SameDirectory(ExtractFileDir(Value), Directory) or
      (CompareText(Copy(Base, 1, 5), 'unins') <> 0) or
      (CompareText(ExtractFileExt(Base), '.exe') <> 0) then Exit;
  if Length(Base) <= 9 then Exit;
  for I := 6 to Length(Base) - 4 do
    if (Base[I] < '0') or (Base[I] > '9') then Exit;
  if FileExists(Value) then Result := Value;
end;

function WaitForInnoCleanup(const Uninstaller: String): Boolean;
var
  Attempt: Integer;
begin
  { The original uninstaller can return before its temporary copy finishes. }
  for Attempt := 1 to 100 do begin
    Result := not FileExists(Uninstaller) and not CheckForMutexes(UninstallLockName);
    if Result then Exit;
    Sleep(100);
  end;
  Result := not FileExists(Uninstaller) and not CheckForMutexes(UninstallLockName);
  if not Result then Log('Timed out waiting for the previous Inno uninstall to finish.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Target, PreviousInno, PreviousLegacy, Uninstaller, PreviousCompilationMode: String;
  ExitCode: Integer;
begin
  { Keep waiting on a retry even if the old uninstall registration is already gone. }
  if PendingInnoCleanup <> '' then begin
    if not WaitForInnoCleanup(PendingInnoCleanup) then begin
      Result := '旧版卸载程序仍在完成清理，请稍后重试。';
      Exit;
    end;
    PendingInnoCleanup := '';
  end;
  Target := WizardDirValue;
  Result := CheckInstallation(Target, False, SetupHelper);
  if Result <> '' then Exit;
  { Existing installers predate this value and always contained ReadyToRun. }
  if not RegQueryStringValue(HKCU32, InnoKey, 'CompilationMode',
      PreviousCompilationMode) then
    PreviousCompilationMode := 'ReadyToRun';
  if RegQueryStringValue(HKCU32, InnoKey, 'InstallLocation', PreviousInno) and
      (PreviousInno <> '') and
      (not SameDirectory(PreviousInno, Target) or
       (CompareText(PreviousCompilationMode, '{#CompilationMode}') <> 0)) then begin
    Result := CheckInstallation(PreviousInno, False, SetupHelper);
    if Result <> '' then Exit;
    Uninstaller := RegisteredInnoUninstaller(PreviousInno);
    if Uninstaller = '' then begin
      Result := '旧版卸载程序缺失或路径无效，请修复旧安装后重试。';
      Exit;
    end;
    if not Exec(Uninstaller,
        '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FROMINSTALLER', '',
        SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then begin
      Result := '无法迁移旧安装，请关闭 HeyMelody 后重试。';
      Exit;
    end;
    PendingInnoCleanup := Uninstaller;
    if not WaitForInnoCleanup(PendingInnoCleanup) then begin
      Result := '旧版卸载程序仍在完成清理，请稍后重试。';
      Exit;
    end;
    PendingInnoCleanup := '';
  end;
  if RegQueryStringValue(HKCU32, LegacyKey, 'InstallLocation', PreviousLegacy) and
      (PreviousLegacy <> '') then begin
    Result := CheckInstallation(PreviousLegacy, False, SetupHelper);
    if Result <> '' then Exit;
    Uninstaller := AddBackslash(PreviousLegacy) + 'Uninstall.exe';
    if not FileExists(Uninstaller) then begin
      Result := '旧版 NSIS 卸载程序缺失，请修复旧安装后重试。';
      Exit;
    end;
    if not Exec(Uninstaller, '/S /FROMINSTALLER _?=' + PreviousLegacy, '',
        SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then begin
      Result := '无法移除旧版 NSIS 安装，请关闭 HeyMelody 后重试。';
      Exit;
    end;
    DeleteFile(Uninstaller);
    RemoveDir(PreviousLegacy);
  end;
  Result := '';
end;

procedure UpdateDesktopShortcut;
var
  Owned: Cardinal;
  Link, Target: String;
begin
  Link := ExpandConstant('{userdesktop}\HeyMelody.lnk');
  try
    if DesktopChoice then begin
      Target := ExpandConstant('{app}\{#AppArch}\HeyMelody.exe');
      CreateShellLink(Link, 'HeyMelody', Target, '', ExtractFileDir(Target),
        Target, 0, SW_SHOWNORMAL);
      if not RegWriteDWordValue(HKCU32, InnoKey, 'DesktopShortcut', 1) then
        RaiseException('无法登记桌面快捷方式。');
    end else begin
      Owned := 0;
      RegQueryDWordValue(HKCU32, InnoKey, 'DesktopShortcut', Owned);
      if (Owned = 1) and FileExists(Link) and not DeleteFile(Link) then
        RaiseException('无法移除桌面快捷方式。');
      if not RegWriteDWordValue(HKCU32, InnoKey, 'DesktopShortcut', 0) then
        RaiseException('无法登记桌面快捷方式选择。');
    end;
  except
    AdditionalExitCode := 1;
    ReportProblem('无法完成桌面快捷方式设置，可通过开始菜单打开 HeyMelody。' + #13#10 +
      Copy(GetExceptionMessage, 1, 1000));
  end;
end;

procedure ApplyDesktopChoiceOnce;
var
  Details, Value: String;
  ExitCode: Integer;
begin
  if DesktopChoiceApplied or not Installed or not FinishShown or WizardSilent then Exit;
  DesktopChoiceApplied := True;
  DesktopChoice := DesktopCheckbox.Checked[0];
  if DesktopChoice then Value := 'true' else Value := 'false';
  if not RunHelper(SetupHelper, 'desktop-preference write ' + Value,
      ExitCode, Details) or (ExitCode <> 0) then begin
    AdditionalExitCode := 1;
    ReportProblem('无法保存桌面快捷方式选择。原有安装偏好未被重置。' + #13#10 +
      Details);
  end;
  UpdateDesktopShortcut;
end;

procedure InitializeWizard;
begin
  DesktopCheckbox := TNewCheckListBox.Create(WizardForm);
  DesktopCheckbox.Parent := WizardForm.FinishedPage;
  { Use the same item renderer as RunList so glyph and caption insets match at every DPI. }
  DesktopCheckbox.BorderStyle := WizardForm.RunList.BorderStyle;
  DesktopCheckbox.Color := WizardForm.RunList.Color;
  DesktopCheckbox.ParentColor := WizardForm.RunList.ParentColor;
  DesktopCheckbox.Font := WizardForm.RunList.Font;
  DesktopCheckbox.Flat := WizardForm.RunList.Flat;
  DesktopCheckbox.ShowLines := WizardForm.RunList.ShowLines;
  DesktopCheckbox.MinItemHeight := WizardForm.RunList.MinItemHeight;
  DesktopCheckbox.WantTabs := WizardForm.RunList.WantTabs;
  DesktopCheckbox.AddCheckBox('创建桌面快捷方式', '', 0,
    DesktopChoice, True, False, False, nil);
  DesktopCheckbox.Anchors := [akLeft, akTop, akRight];
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Top, Remaining: Integer;
begin
  if (CurPageID = wpFinished) and not FinishShown then begin
    Top := WizardForm.RunList.Top;
    if Top + ScaleY(58) > WizardForm.FinishedPage.ClientHeight then
      Top := WizardForm.FinishedPage.ClientHeight - ScaleY(58);
    if WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height +
        ScaleY(8) > Top then begin
      WizardForm.FinishedLabel.AutoSize := False;
      WizardForm.FinishedLabel.Height := Top - WizardForm.FinishedLabel.Top - ScaleY(8);
    end;
    DesktopCheckbox.SetBounds(WizardForm.RunList.Left, Top,
      WizardForm.RunList.Width, ScaleY(23));
    WizardForm.RunList.Top := Top + ScaleY(30);
    Remaining := WizardForm.FinishedPage.ClientHeight - WizardForm.RunList.Top - ScaleY(4);
    WizardForm.RunList.Height := Remaining;
    DesktopCheckbox.Checked[0] := DesktopChoice;
    FinishShown := True;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = wpFinished then ApplyDesktopChoiceOnce;
  Result := True;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
var
  I: Integer;
begin
  if CurPageID = wpFinished then begin
    ApplyDesktopChoiceOnce;
    Confirm := False;
    { Closing the completed wizard must not start a checked post-install program. }
    for I := 0 to WizardForm.RunList.Items.Count - 1 do
      WizardForm.RunList.Checked[I] := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Details: String;
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    Installed := True;
    if not RunHelper(SetupHelper, 'compatibility', ExitCode, Details) or
        (ExitCode <> 0) then begin
      AdditionalExitCode := 1;
      ReportProblem('程序文件已安装，但无法完成 Basic Render 兼容模式设置。' +
        '原有设置未被清空，请手动设置后重新打开界面。' + #13#10 + Details);
    end;
    if WizardSilent then UpdateDesktopShortcut;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := AdditionalExitCode;
end;

procedure DeinitializeSetup;
begin
  if Installed and FinishShown and not DesktopChoiceApplied and not WizardSilent then
    ApplyDesktopChoiceOnce;
  ReleaseLock(SetupLock);
end;

function InitializeUninstall: Boolean;
var
  InstalledHelper, RegisteredDirectory, Details: String;
  Owned: Cardinal;
begin
  Result := AcquireLock(UninstallLockName, SetupLockName,
    HasFromInstallerSwitch, UninstallLock);
  if not Result then Exit;
  Owned := 0;
  UninstallDesktopOwned := RegQueryStringValue(HKCU32, InnoKey,
    'InstallLocation', RegisteredDirectory) and
    SameDirectory(RegisteredDirectory, ExpandConstant('{app}')) and
    RegQueryDWordValue(HKCU32, InnoKey, 'DesktopShortcut', Owned) and (Owned = 1);
  InstalledHelper := ExpandConstant('{app}\.installer\') + HelperName;
  UninstallHelper := ExpandConstant('{tmp}\') + HelperName;
  if not FileCopy(InstalledHelper, UninstallHelper, False) then begin
    ReportProblem('独立卸载辅助程序缺失或不可读取。请先修复当前安装后重试。');
    Result := False;
    Exit;
  end;
  Details := CheckInstallation(ExpandConstant('{app}'), True, UninstallHelper);
  Result := Details = '';
  if not Result then ReportProblem(Details);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then begin
    if UninstallDesktopOwned then
      DeleteFile(ExpandConstant('{userdesktop}\HeyMelody.lnk'));
    DeleteFile(ExpandConstant('{app}\.installer\') + HelperName);
    DeleteFile(ExpandConstant('{app}\.installer\') + InventoryName);
    RemoveDir(ExpandConstant('{app}\.installer'));
    RemoveDir(ExpandConstant('{app}'));
  end;
  if CurUninstallStep = usDone then RemoveDir(ExpandConstant('{app}'));
end;

procedure DeinitializeUninstall;
begin
  ReleaseLock(UninstallLock);
end;
