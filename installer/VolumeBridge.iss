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
CloseApplications=no
UninstallDisplayName=VolumeBridge

[Messages]
WelcomeLabel2=This will install VolumeBridge {#AppVersion}.%n%nIt also installs usbip-win2 {#UsbipVersion} (signed virtual USB host driver) if needed.%n%n安装驱动时，所有 USB 3.0 Hub 会重启，USB 设备（包括音箱、键盘、鼠标）会短暂断开几秒，属于正常现象。

[Dirs]
; config.json and volumebridge.log are written next to the exe by a normal (non-admin) user.
Name: "{app}"; Permissions: users-modify

[Files]
Source: "{#PayloadDir}\VolumeBridge.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\{#UsbipSetup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: UsbipNeeded

[Icons]
Name: "{group}\VolumeBridge 测试（虚拟音箱）"; Filename: "{app}\VolumeBridge.exe"; Parameters: "--speaker"; WorkingDir: "{app}"
Name: "{group}\VolumeBridge 设备诊断"; Filename: "{app}\VolumeBridge.exe"; Parameters: "--probe"; WorkingDir: "{app}"
Name: "{group}\打开日志文件夹"; Filename: "{app}"
Name: "{group}\卸载 VolumeBridge"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\VolumeBridge.exe"; Parameters: "--speaker"; WorkingDir: "{app}"; Description: "立即启动 VolumeBridge 测试（虚拟音箱）"; Flags: postinstall nowait skipifsilent runasoriginaluser unchecked

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
  if InstalledUsbipVersion() = '' then
  begin
    UsbipFailed('安装程序已结束，但未检测到安装结果。');
    Exit;
  end;
  UsbipInstalledNow := True;
  RegWriteDWordValue(HKLM64, '{#MarkerKey}', 'InstalledUsbip', 1);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and UsbipNeeded() then
    InstallUsbip();
end;

{ usbip-win2 asks for a reboot (AlwaysRestart=yes); offer it, but the user may test first. }
function NeedRestart(): Boolean;
begin
  Result := UsbipInstalledNow;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Marker: Cardinal;
  Uninstaller: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if not RegQueryDWordValue(HKLM64, '{#MarkerKey}', 'InstalledUsbip', Marker) or (Marker <> 1) then
    Exit;
  RegDeleteKeyIncludingSubkeys(HKLM64, '{#MarkerKey}');
  if not RegQueryStringValue(HKLM64, '{#UsbipUninstallKey}', 'UninstallString', Uninstaller) then
    Exit;
  if SuppressibleMsgBox('VolumeBridge 安装时一并安装了 usbip-win2 驱动。' + #13#10 +
      '是否也卸载它？（如果其他软件也在使用 usbip-win2，请选“否”。卸载后需要重启。）',
      mbConfirmation, MB_YESNO, IDNO) = IDYES then
    Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
