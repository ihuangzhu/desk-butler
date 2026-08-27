#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define PublishDir "..\artifacts\publish\win-x64"

[Setup]
AppId=DeskButler
AppName=DeskButler
AppVersion={#AppVersion}
AppPublisher=DeskButler
DefaultDirName={localappdata}\Programs\DeskButler
DefaultGroupName=DeskButler
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=DeskButler-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
UninstallDisplayIcon={app}\DeskButler.Desktop.exe
WizardStyle=modern

[InstallDelete]
; 覆盖升级只登记已废弃的精确程序路径；禁止通配符、用户数据路径和 unins* 文件。
Type: files; Name: "{app}\installed-version.txt"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DeskButler"; Filename: "{app}\DeskButler.Desktop.exe"; WorkingDir: "{app}"

[UninstallRun]
Filename: "{app}\DeskButler.Desktop.exe"; Parameters: "--prepare-uninstall"; WorkingDir: "{app}"; StatusMsg: "正在安全退出 DeskButler..."; Flags: runhidden; RunOnceId: "PrepareDeskButlerUninstall"

[UninstallDelete]
Type: files; Name: "{app}\installed-version.txt"

[Code]
var
  DeleteUserData: Boolean;
  DeleteUserDataDecisionMade: Boolean;
  UninstallPrepared: Boolean;

function GetFileAttributesW(lpFileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

// 判断静默卸载是否显式要求删除用户数据。
function HasDeleteUserDataParameter: Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), '/DELETEUSERDATA=1') = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

// 通过卸载器命令行判断静默模式，避免调用仅在安装阶段可用的 WizardSilent。
function IsSilentUninstall: Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if (CompareText(ParamStr(Index), '/SILENT') = 0) or
       (CompareText(ParamStr(Index), '/VERYSILENT') = 0) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

// 规范化并确认候选路径精确等于当前用户 DeskButler 数据根，且根本身不是重解析点。
function IsExactUserDataPath(const Candidate: String): Boolean;
var
  ExpectedPath: String;
  CandidatePath: String;
  LocalAppDataPath: String;
  Attributes: LongWord;
begin
  Result := False;
  if Candidate = '' then
    Exit;

  ExpectedPath := RemoveBackslashUnlessRoot(
    ExpandFileName(ExpandConstant('{localappdata}\DeskButler')));
  CandidatePath := RemoveBackslashUnlessRoot(ExpandFileName(Candidate));
  LocalAppDataPath := RemoveBackslashUnlessRoot(
    ExpandFileName(ExpandConstant('{localappdata}')));

  if (CompareText(CandidatePath, ExpectedPath) <> 0) or
     (CompareText(CandidatePath, LocalAppDataPath) = 0) or
     (ExtractFileName(CandidatePath) = '') then
    Exit;

  Attributes := GetFileAttributesW(CandidatePath);
  if (Attributes <> $FFFFFFFF) and
     ((Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then
  begin
    Log('拒绝删除重解析点形式的 DeskButler 用户数据根：' + CandidatePath);
    Exit;
  end;

  Result := True;
end;

// 卸载初始化时只询问一次；静默模式仅接受显式删除参数。
function InitializeUninstall: Boolean;
begin
  Result := True;
  if DeleteUserDataDecisionMade then
    Exit;

  if IsSilentUninstall then
    DeleteUserData := HasDeleteUserDataParameter
  else
    DeleteUserData := MsgBox(
      '是否同时删除 DeskButler 的设置、快照和日志？',
      mbConfirmation, MB_YESNO) = IDYES;
  DeleteUserDataDecisionMade := True;
end;

// 覆盖升级前请求旧实例自然退出；失败时阻止替换仍在使用的程序文件。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  ExistingExecutable: String;
begin
  Result := '';
  ExistingExecutable := ExpandConstant('{app}\DeskButler.Desktop.exe');
  if not FileExists(ExistingExecutable) then
    Exit;

  if (not Exec(ExistingExecutable, '--prepare-upgrade', ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then
    Result := 'DeskButler 未能安全退出，安装程序不会替换正在运行的文件。';
end;

// 文件替换完成后写入独立版本证据，供覆盖升级验证并随卸载移除。
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and
     (not SaveStringToFile(ExpandConstant('{app}\installed-version.txt'),
       '{#AppVersion}' + #13#10, False)) then
    RaiseException('无法写入 DeskButler 安装版本标记。');
end;

// 在真正卸载开始时执行受控退出；失败则中止，绝不继续删除仍被锁定的文件。
procedure PrepareApplicationForUninstall;
var
  ExitCode: Integer;
  ExistingExecutable: String;
begin
  if UninstallPrepared then
    Exit;

  ExistingExecutable := ExpandConstant('{app}\DeskButler.Desktop.exe');
  if FileExists(ExistingExecutable) and
     ((not Exec(ExistingExecutable, '--prepare-uninstall', ExpandConstant('{app}'),
       SW_HIDE, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0)) then
  begin
    Log('DeskButler 受控卸载准备失败，卸载已中止。');
    if not IsSilentUninstall then
      SuppressibleMsgBox('DeskButler 未能安全退出，卸载已中止。请退出管家后重试。',
        mbError, MB_OK, IDOK);
    Abort;
  end;

  { 应用正常路径已经删除自身值；这里按精确名称幂等兜底，不接触其他 Run 值。 }
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DeskButler');
  UninstallPrepared := True;
end;

// 仅在用户明确选择且精确路径守卫通过时递归删除专属用户数据。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataPath: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    PrepareApplicationForUninstall;
    Exit;
  end;

  if (CurUninstallStep <> usPostUninstall) or (not DeleteUserData) then
    Exit;

  UserDataPath := ExpandConstant('{localappdata}\DeskButler');
  if not IsExactUserDataPath(UserDataPath) then
  begin
    Log('安全守卫拒绝删除 DeskButler 用户数据：' + UserDataPath);
    Exit;
  end;

  if not DelTree(UserDataPath, True, True, True) then
    Log('DeskButler 用户数据未能完全删除：' + UserDataPath);
end;
