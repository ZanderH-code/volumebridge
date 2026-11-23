; VolumeBridge installer: VolumeBridge.exe + bundled usbip-win2 (virtual USB host driver).
; Build (CI): iscc /DAppVersion=x.y.z /DPayloadDir=<dir with VolumeBridge.exe and USBip-*.exe> installer\VolumeBridge.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
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
; A running VolumeBridge is handled in InitializeSetup/InitializeUninstall (wait for it to exit),
; not with AppMutex, so one-click updates (silent) do not abort while the app is shutting down.
UninstallDisplayName=VolumeBridge
; Language dialog (English / 简体中文); silent updates reuse the previous choice.
ShowLanguageDialog=yes
UsePreviousLanguage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; Official translation from jrsoftware/issrc (not yet shipped with Inno Setup 6.7.1), vendored.
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Messages]
english.WelcomeLabel2=This will install VolumeBridge {#AppVersion}.%n%nZero-latency mode adds a small volume plugin to your speaker's audio effects. To load it, Windows' protected audio path is turned off (a few DRM players may be affected), and the Windows Audio service restarts once (sound stops for ~2 seconds). Everything is restored on uninstall.
chinesesimplified.WelcomeLabel2=将安装 VolumeBridge {#AppVersion}。%n%n零延迟模式会在音箱的音效设置中加入一个小型音量插件。为了让 Windows 加载它，会关闭“受保护音频路径”（少数 DRM 播放可能受影响），并重启一次 Windows 音频服务（声音中断约 2 秒）。卸载时全部恢复。

[CustomMessages]
english.TaskApo=Enable the zero-latency volume plugin for the speaker now (recommended)
chinesesimplified.TaskApo=现在为音箱启用零延迟音量插件（推荐）
english.TaskUsbip=Install the usbip-win2 driver for the compatible mode (virtual speaker); USB devices disconnect briefly
chinesesimplified.TaskUsbip=安装兼容模式（虚拟音箱）所需的 usbip-win2 驱动（USB 设备会短暂断开）
english.StartApp=Start VolumeBridge now
chinesesimplified.StartApp=立即启动 VolumeBridge
english.AppRunning=VolumeBridge is running. Right-click its tray icon, choose "Quit", then click Retry.
chinesesimplified.AppRunning=VolumeBridge 正在运行。请右键托盘图标选择“退出”，然后点“重试”。
english.InstallingUsbip=Installing usbip-win2 {#UsbipVersion} (USB devices will briefly disconnect)...
chinesesimplified.InstallingUsbip=正在安装 usbip-win2 {#UsbipVersion}（USB 设备会短暂断开）……
english.UsbipFailed=usbip-win2 could not be installed: %1%n%nVolumeBridge is installed, but the virtual speaker needs usbip-win2. Please report this with a screenshot.
chinesesimplified.UsbipFailed=usbip-win2 未能安装：%1%n%nVolumeBridge 已安装，但虚拟音箱需要 usbip-win2 才能工作。请截图告知开发者。
english.UsbipCannotStart=its installer could not be started (%1)
chinesesimplified.UsbipCannotStart=无法启动安装程序（%1）
english.UsbipExitCode=exit code %1. Code 1 may mean the driver requires test signing.
chinesesimplified.UsbipExitCode=退出码 %1。若为 1，可能是该驱动要求开启测试签名。
english.UsbipVersionMismatch=the installer finished, but the installed version is still not {#UsbipVersion} (found: %1).
chinesesimplified.UsbipVersionMismatch=安装程序已结束，但检测到的版本仍不是 {#UsbipVersion}（当前：%1）。
english.RemoveUsbipAsk=VolumeBridge installed the usbip-win2 driver. Remove it too?%n%n(Choose No if other software uses usbip-win2. Its uninstaller asks for a restart when done.)
chinesesimplified.RemoveUsbipAsk=VolumeBridge 安装时一并安装了 usbip-win2 驱动。是否也卸载它？%n%n（如果其他软件也在使用 usbip-win2，请选“否”。它的卸载程序会在完成后提示重启。）
english.RemoveUsbipFailed=Could not start the usbip-win2 uninstaller (%1). You can remove "USBip" in Settings → Apps.
chinesesimplified.RemoveUsbipFailed=无法启动 usbip-win2 卸载程序（%1）。可在“设置 → 应用”中手动卸载 USBip。

[Tasks]
Name: "apo"; Description: "{cm:TaskApo}"
Name: "usbip"; Description: "{cm:TaskUsbip}"; Flags: unchecked

[Dirs]
; Shared state between the tray app (user) and the plugin (audiodg, LOCAL SERVICE).
Name: "{commonappdata}\VolumeBridge"; Permissions: everyone-modify

[Files]
Source: "{#PayloadDir}\VolumeBridge.exe"; DestDir: "{app}"; Flags: ignoreversion
; Loaded by audiodg.exe; PrepareToInstall stops the audio service so it can be replaced.
Source: "{#PayloadDir}\VolumeBridgeApo.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace uninsrestartdelete
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
; Installed right after extraction (AfterInstall) so NeedRestart, evaluated at the end of the copy phase, sees it.
Source: "{#PayloadDir}\{#UsbipSetup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Tasks: usbip; Check: UsbipNeeded; AfterInstall: InstallUsbip

[Icons]
Name: "{group}\VolumeBridge"; Filename: "{app}\VolumeBridge.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,VolumeBridge}"; Filename: "{uninstallexe}"

[Run]
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\VolumeBridgeApo.dll"""; Flags: runhidden waituntilterminated
; Windows Audio was stopped for the upgrade: start it first, otherwise --apo-setup sees no speakers.
Filename: "{sys}\net.exe"; Parameters: "start audiosrv"; Flags: runhidden waituntilterminated
Filename: "{app}\VolumeBridge.exe"; Parameters: "--apo-setup"; Flags: runhidden waituntilterminated; Tasks: apo
Filename: "{app}\VolumeBridge.exe"; WorkingDir: "{app}"; Description: "{cm:StartApp}"; Flags: postinstall nowait skipifsilent runasoriginaluser
; One-click update from the app runs this installer with /SILENT; reopen the app afterwards.
Filename: "{app}\VolumeBridge.exe"; Parameters: "--updated"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallDelete]
Type: files; Name: "{app}\language.txt"

[UninstallRun]
; Restore the speakers' original effect settings and DisableProtectedAudioDG, then unregister the plugin.
Filename: "{app}\VolumeBridge.exe"; Parameters: "--apo-remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveApo"
Filename: "{sys}\regsvr32.exe"; Parameters: "/u /s ""{app}\VolumeBridgeApo.dll"""; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterApo"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN VolumeBridge /F"; Flags: runhidden; RunOnceId: "DeleteAutostartTask"

[Code]
var
  UsbipInstalledNow: Boolean;
  AudioStopped: Boolean;

const
  RunningMutex = 'Global\VolumeBridge.Running';
  { Tells the app (waiting after starting this installer) that UAC was accepted, so it can quit. }
  UpdatingMutex = 'Global\VolumeBridge.Updating';

{ Waits up to TimeoutMs for a running VolumeBridge to exit; interactive runs may retry. }
function WaitForAppExit(TimeoutMs: Integer; Silent: Boolean): Boolean;
var
  Waited: Integer;
begin
  repeat
    Waited := 0;
    while CheckForMutexes(RunningMutex) and (Waited < TimeoutMs) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
    Result := not CheckForMutexes(RunningMutex);
    if Result or Silent then
      Exit;
  until MsgBox(CustomMessage('AppRunning'), mbError, MB_RETRYCANCEL) <> IDRETRY;
end;

function InitializeSetup(): Boolean;
begin
  { Runs in the elevated setup process, i.e. after UAC was accepted. }
  CreateMutex(UpdatingMutex);
  { Silent = one-click update: the app is exiting by itself, give it time. Interactive: ask quickly. }
  if WizardSilent() then
    Result := WaitForAppExit(30000, True)
  else
    Result := WaitForAppExit(2000, False);
end;

function InitializeUninstall(): Boolean;
begin
  Result := WaitForAppExit(5000, UninstallSilent());
end;

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
  SuppressibleMsgBox(FmtMessage(CustomMessage('UsbipFailed'), [Msg]), mbCriticalError, MB_OK, IDOK);
end;

procedure InstallUsbip();
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingUsbip');
  if not Exec(ExpandConstant('{tmp}\{#UsbipSetup}'),
      '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /COMPONENTS="main,client"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    UsbipFailed(FmtMessage(CustomMessage('UsbipCannotStart'), [SysErrorMessage(ResultCode)]));
    Exit;
  end;
  if ResultCode <> 0 then
  begin
    UsbipFailed(FmtMessage(CustomMessage('UsbipExitCode'), [IntToStr(ResultCode)]));
    Exit;
  end;
  if UsbipNeeded() then
  begin
    UsbipFailed(FmtMessage(CustomMessage('UsbipVersionMismatch'), [InstalledUsbipVersion()]));
    Exit;
  end;
  UsbipInstalledNow := True;
  RegWriteDWordValue(HKLM64, '{#MarkerKey}', 'InstalledUsbip', 1);
end;

{ Upgrades: audiodg.exe keeps VolumeBridgeApo.dll loaded; stop the audio service so it can be
  replaced. The Run section starts it again. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\VolumeBridgeApo.dll')) then
    AudioStopped := Exec(ExpandConstant('{sys}\net.exe'), 'stop audiosrv /y', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Whatever happened (finished, cancelled, failed), never leave Windows Audio stopped. }
procedure DeinitializeSetup();
var
  ResultCode: Integer;
begin
  if AudioStopped then
    Exec(ExpandConstant('{sys}\net.exe'), 'start audiosrv', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ The app's UI follows the installer language unless the user picks another one in the app. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Lang: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if ActiveLanguage() = 'chinesesimplified' then
    Lang := 'zh'
  else
    Lang := 'en';
  SaveStringToFile(ExpandConstant('{app}\language.txt'), Lang, False);
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
    if SuppressibleMsgBox(CustomMessage('RemoveUsbipAsk'), mbConfirmation, MB_YESNO, IDNO) = IDYES then
    begin
      if not Exec(Exe, '/SILENT', '', SW_SHOW, ewNoWait, ResultCode) then
        SuppressibleMsgBox(FmtMessage(CustomMessage('RemoveUsbipFailed'), [SysErrorMessage(ResultCode)]), mbError, MB_OK, IDOK);
    end;
  end;
  RegDeleteKeyIncludingSubkeys(HKLM64, '{#MarkerKey}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { usDone: after our "removed" dialog is closed, so our uninstaller no longer blocks the
    restart that usbip-win2's uninstaller offers. }
  if CurUninstallStep = usDone then
  begin
    MaybeRemoveUsbip();
    RegDeleteKeyIfEmpty(HKLM64, '{#MarkerKey}');
  end;
end;
