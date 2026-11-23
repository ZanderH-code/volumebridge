; VolumeBridge installer: VolumeBridge.exe + bundled usbip-win2 (virtual USB host driver).
; Build (CI): iscc /DAppVersion=x.y.z /DPayloadDir=<dir with VolumeBridge.exe and USBip-*.exe> installer\VolumeBridge.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\out"
#endif
#define UsbipVersion "0.9.8.1"
#define UsbipSetup "USBip-" + UsbipVersion + "-x64.exe"
; AppId of usbip-win2's own Inno Setup installer (userspace/innosetup/setup.iss).
#define UsbipUninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1"
#define MarkerKey "Software\VolumeBridge"

[Setup]
AppId={{6F1C2B7E-4A0B-4D8E-9C55-5642B0A1D001}
AppName=VolumeBridge
AppVersion={#AppVersion}
AppPublisher=VolumeBridge
DefaultDirName={autopf}\VolumeBridge
DefaultGroupName=VolumeBridge
DisableProgramGroupPage=yes
; Fixed location: the exe decides where config/logs go by checking for Program Files (AppPaths.DataDir).
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; usbip-win2 requires Windows 10 1903+.
MinVersion=10.0.18362
OutputBaseFilename=VolumeBridgeSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Ask the user to close a running VolumeBridge (it detaches the virtual speaker cleanly on exit).
AppMutex=VolumeBridge.Running,Global\VolumeBridge.Running
UninstallDisplayName=VolumeBridge

[Messages]
WelcomeLabel2=This will install VolumeBridge {#AppVersion}.%n%nIt also installs usbip-win2 {#UsbipVersion} (signed virtual USB host driver) if needed.%n%n安装驱动时，所有 USB 3.0 Hub 会重启，USB 设备（包括音箱、键盘、鼠标）会短暂断开几秒，属于正常现象。

[Files]
Source: "{#PayloadDir}\VolumeBridge.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
; Installed right after extraction (AfterInstall) so NeedRestart, evaluated at the end of the copy phase, sees it.
Source: "{#PayloadDir}\{#UsbipSetup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: UsbipNeeded; AfterInstall: InstallUsbip

[Icons]
Name: "{group}\VolumeBridge"; Filename: "{app}\VolumeBridge.exe"; Parameters: "--speaker"; WorkingDir: "{app}"
Name: "{group}\VolumeBridge 设备诊断"; Filename: "{app}\VolumeBridge.exe"; Parameters: "--probe"; WorkingDir: "{app}"
; Config and logs live in %LOCALAPPDATA%\VolumeBridge of whoever runs it (Program Files stays read-only).
Name: "{group}\打开日志文件夹"; Filename: "{app}\VolumeBridge.exe"; Parameters: "--open-logs"
Name: "{group}\卸载 VolumeBridge"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\VolumeBridge.exe"; Parameters: "--speaker"; WorkingDir: "{app}"; Description: "立即启动 VolumeBridge"; Flags: postinstall nowait skipifsilent runasoriginaluser unchecked

[Code]
var
  UsbipInstalledNow: Boolean;

function InstalledUsbipVersion(): String;
begin
  if not RegQueryStringValue(HKLM64, '{#UsbipUninstallKey}', 'DisplayVersion', Result) then
    Result := '';
end;

{ True when usbip-win2 is missing, unparsable or older than the bundled version. }
function UsbipNeeded(): Boolean;
var
  Installed: String;
  A, B: Int64;
begin
  Installed := InstalledUsbipVersion();
  if (Installed <> '') and StrToVersion(Installed, A) and StrToVersion('{#UsbipVersion}', B) then
    Result := ComparePackedVersion(A, B) < 0
  else
    Result := True;
end;

procedure UsbipFailed(const Msg: String);
begin
  SuppressibleMsgBox('usbip-win2 未能安装：' + Msg + #13#10#13#10 +
    'VolumeBridge 已安装，但虚拟音箱需要 usbip-win2 才能工作。请截图告知开发者。',
    mbCriticalError, MB_OK, IDOK);
end;

procedure InstallUsbip();
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing usbip-win2 {#UsbipVersion} (USB devices will briefly disconnect)...';
  if not Exec(ExpandConstant('{tmp}\{#UsbipSetup}'),
      '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /COMPONENTS="main,client"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    UsbipFailed('无法启动安装程序（' + SysErrorMessage(ResultCode) + '）');
    Exit;
  end;
  if ResultCode <> 0 then
  begin
    UsbipFailed('退出码 ' + IntToStr(ResultCode) + '。若为 1，可能是该驱动要求开启测试签名。');
    Exit;
  end;
  if UsbipNeeded() then
  begin
    UsbipFailed('安装程序已结束，但检测到的版本仍不是 {#UsbipVersion}（当前：' + InstalledUsbipVersion() + '）。');
    Exit;
  end;
  UsbipInstalledNow := True;
  RegWriteDWordValue(HKLM64, '{#MarkerKey}', 'InstalledUsbip', 1);
end;

{ usbip-win2 asks for a reboot (AlwaysRestart=yes); offer it, but the user may test first. }
function NeedRestart(): Boolean;
begin
  Result := UsbipInstalledNow;
end;

{ "C:\...\unins000.exe" /LOG  ->  C:\...\unins000.exe }
function ExecutableFromCommandLine(const Cmd: String): String;
var
  S: String;
  P: Integer;
begin
  S := Trim(Cmd);
  if (Length(S) > 0) and (S[1] = '"') then
  begin
    Delete(S, 1, 1);
    P := Pos('"', S);
    if P > 0 then
      S := Copy(S, 1, P - 1);
    Result := S;
  end
  else
  begin
    P := Pos(' /', S);
    if P > 0 then
      Result := Copy(S, 1, P - 1)
    else
      Result := S;
  end;
end;

{ Optionally hands off to usbip-win2's own uninstaller (with its progress window). It is not waited
  for: its second phase outlives the first, and it shows its own restart prompt only after the
  driver packages are removed, so we never offer a reboot that could interrupt it. }
procedure MaybeRemoveUsbip();
var
  Marker: Cardinal;
  Uninstaller, Exe: String;
  ResultCode: Integer;
begin
  if not RegQueryDWordValue(HKLM64, '{#MarkerKey}', 'InstalledUsbip', Marker) or (Marker <> 1) then
    Exit;
  if RegQueryStringValue(HKLM64, '{#UsbipUninstallKey}', 'UninstallString', Uninstaller)
     and not UninstallSilent() then
  begin
    Exe := ExecutableFromCommandLine(Uninstaller);
    if SuppressibleMsgBox('VolumeBridge 安装时一并安装了 usbip-win2 驱动。' + #13#10 +
        '是否也卸载它？（如果其他软件也在使用 usbip-win2，请选“否”。它的卸载程序会在完成后提示重启。）',
        mbConfirmation, MB_YESNO, IDNO) = IDYES then
    begin
      if not Exec(Exe, '/SILENT', '', SW_SHOW, ewNoWait, ResultCode) then
        SuppressibleMsgBox('无法启动 usbip-win2 卸载程序（' + SysErrorMessage(ResultCode) + '）。' + #13#10 +
          '可在“设置 → 应用”中手动卸载 USBip。', mbError, MB_OK, IDOK);
    end;
  end;
  RegDeleteKeyIncludingSubkeys(HKLM64, '{#MarkerKey}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { usDone: after our "removed" dialog is closed, so our uninstaller no longer blocks the
    restart that usbip-win2's uninstaller offers. }
  if CurUninstallStep = usDone then
    MaybeRemoveUsbip();
end;
