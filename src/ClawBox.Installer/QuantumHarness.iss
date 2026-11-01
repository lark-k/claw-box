; Build through scripts/release/Publish-Installer.ps1. Never include user data.
#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef ProductVersion
  #error ProductVersion is required
#endif
#ifndef InstallerOutput
  #error InstallerOutput is required
#endif
#ifndef InstallerId
  #define InstallerId "QuantumHarness"
#endif
#ifndef ProductName
  #define ProductName "Quantum Harness"
#endif
#ifndef LegacyWindows
  #define LegacyWindows "0"
#endif
#if LegacyWindows == "1"
  #define PackageSuffix "-legacy"
  #define VersionDirectory ProductVersion + "-legacy"
#else
  #define PackageSuffix ""
  #define VersionDirectory ProductVersion
#endif

[Setup]
AppId={#InstallerId}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppPublisher=ClawBox
VersionInfoVersion={#ProductVersion}
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Deployment baseline: Windows 11 24H2 (26100) or newer, with current patches.
; Do not admit 21H2 (22000), where CLR startup can fail before managed UI exists.
#if LegacyWindows == "1"
MinVersion=10.0.22000
AppVerName={#ProductName} {#ProductVersion} 旧版系统兼容版
#else
MinVersion=10.0.26100
#endif
DisableDirPage=no
AlwaysShowDirOnReadyPage=yes
AllowRootDirectory=no
AllowUNCPath=no
AllowNetworkDrive=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
WizardStyle=modern
SetupIconFile=..\ClawBox.Desktop\Assets\QuantumHarness.ico
UninstallDisplayIcon={app}\versions\{#VersionDirectory}\QuantumHarness.exe
UninstallDisplayName={#ProductName}
OutputDir={#InstallerOutput}
OutputBaseFilename=QuantumHarness-{#ProductVersion}-win-x64{#PackageSuffix}-Setup
Compression=lzma2/fast
SolidCompression=yes
AppMutex=Local\QuantumHarness-{username}
SetupMutex=Local\QuantumHarness-Maintenance-{username}
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
; Inno records installed files individually. Never recursively delete {app} or AppData.

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
#if LegacyWindows == "1"
WindowsVersionNotSupported=本兼容包需要 Windows 11 21H2（内部版本 22000）或更新的 x64 系统。
WelcomeLabel2=此向导将为当前 Windows 用户安装 [name/ver]。%n%n这是旧版 Windows 兼容包。为兼容旧系统，本程序不启用 CET 硬件栈保护；不会修改 Windows 全局安全设置。%n%n升级前请等待任务结束并退出 Quantum Harness。聊天、配对和工作区文件会保留。连接盒子仍需 Windows OpenSSH 客户端。
#else
WindowsVersionNotSupported=Quantum Harness 需要 Windows 11 24H2（内部版本 26100）或更新版本，并安装当前系统补丁。请先通过 Windows 更新升级到仍受支持的系统版本，然后重新运行安装包。无需安装开发 SDK。
WelcomeLabel2=此向导将为当前 Windows 用户安装 [name/ver]。%n%n安装包已包含 .NET 运行时，无需安装开发 SDK。连接盒子需要 Windows OpenSSH 客户端；启动后直接进入首页，可从设置中手动连接。%n%n升级或卸载前请等待任务结束并退出 Quantum Harness（包括旧便携版）。聊天、配对及工作区文件会保留。
#endif
SetupAppRunningError=Quantum Harness 正在运行。请等待任务结束并退出软件，再点击“确定”。安装程序不会强制结束任务。
UninstallAppRunningError=Quantum Harness 正在运行。请等待任务结束并退出软件，再点击“确定”。卸载程序不会强制结束任务。
ConfirmUninstall=确定卸载 %1 及其程序组件？%n%n聊天记录、设置、配对凭据和工作区文件将保留。重新安装后可继续使用。板子配置及 Windows OpenSSH 客户端不会被移除。
FinishedLabel=Quantum Harness 已安装完成。%n%n请从开始菜单启动软件；已有便携版数据会继续使用。请按说明书从首页或设置中连接盒子。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}\versions\{#VersionDirectory}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#ProductName}\{#ProductName}"; Filename: "{app}\versions\{#VersionDirectory}\QuantumHarness.exe"; WorkingDir: "{app}\versions\{#VersionDirectory}"
Name: "{userprograms}\{#ProductName}\卸载 {#ProductName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\versions\{#VersionDirectory}\QuantumHarness.exe"; WorkingDir: "{app}\versions\{#VersionDirectory}"; Tasks: desktopicon

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#InstallerId}_is1';

function DowngradeError(): String;
var
  Installed: String;
  PreviousVersion, IncomingVersion: Int64;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Installed) then
  begin
    if not StrToVersion(Installed, PreviousVersion) then
      Result := '无法识别已安装版本，请先通过 Windows 设置卸载旧程序；用户数据会保留。'
    else if StrToVersion('{#ProductVersion}', IncomingVersion) then
      if ComparePackedVersion(PreviousVersion, IncomingVersion) > 0 then
        Result := '已安装较新版本 ' + Installed + '，不能覆盖为旧版本 {#ProductVersion}。';
  end;
end;

function InitializeSetup(): Boolean;
var
  Error: String;
begin
  Error := DowngradeError();
  Result := Error = '';
  if not Result then SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
end;

function InstallDirectoryError(): String;
var
  PreviousDirectory: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', PreviousDirectory) then
    if CompareText(AddBackslash(ExpandFileName(PreviousDirectory)),
      AddBackslash(ExpandFileName(WizardDirValue()))) <> 0 then
      Result := '升级或修复请沿用原安装位置：' + PreviousDirectory + #13#10 +
        '如需更换位置，请先卸载旧程序，再重新安装；聊天、配对和工作区文件会保留。';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Error := InstallDirectoryError();
    Result := Error = '';
    if not Result then SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := DowngradeError();
  if Result <> '' then exit;
  Result := InstallDirectoryError();
  if Result <> '' then exit;
  if CheckForMutexes(ExpandConstant('Local\QuantumHarness-{username}')) then
    Result := '请等待任务结束并退出 Quantum Harness，然后重试。';
end;

function InitializeUninstall(): Boolean;
begin
  Result := not CheckForMutexes(ExpandConstant('Local\QuantumHarness-Maintenance-{username}'));
  if not Result then
    SuppressibleMsgBox('另一个安装或卸载程序正在运行，请完成后再试。', mbError, MB_OK, IDOK)
  else
    CreateMutex(ExpandConstant('Local\QuantumHarness-Maintenance-{username}'));
end;
