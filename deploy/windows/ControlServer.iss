; ═══════════════════════════════════════════════════════════════════════════
;  中控服务 (ControlServer) —— Windows 安装包（Inno Setup 6）
;
;  构建入口：deploy\windows\build-installer.ps1
;  手工编译：ISCC.exe /DAppVersion=0.1.0 deploy\windows\ControlServer.iss
;  静默安装：ControlServer-Setup-0.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
;
;  设计要点
;   · 管理员权限安装；程序装到 Program Files\ControlServer，数据在 %ProgramData%\ControlServer
;   · 程序本体是 self-contained 单文件 ⇒ 目标机**不需要**预装 .NET 运行时
;   · 服务注册/防火墙/托盘这一整套，直接复用 install-service.ps1（同一份已被冒烟验证过的
;     逻辑），避免在 .iss 里重写 sc sdset 那套服务 DACL 处理
;   · 收尾自检（-Verify）：服务真的 Running + HTTP 真的能打开，才算"装好就能跑"
;   · 升级/回滚（M6.4）：AppId 不变即"覆盖升级"；AppVersion 由 build-installer.ps1
;     从 Directory.Build.props 读出来传入，**别在这里写死** —— 写死就一定会与程序集不同步
;     （此处曾默认 1.0.0，而程序集是 0.1.0，两个号对不上）。
; ═══════════════════════════════════════════════════════════════════════════

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName       "中控服务"
#define AppExeName    "ControlServer.Server.exe"
#define TrayExeName   "ControlServer.TrayAgent.exe"
#define ServiceName   "ControlServer"
#define HttpPort      "8080"
#define PairingPort   "9999"

[Setup]
; AppId 一旦发布就不能改 —— 它是升级/卸载的识别键（改了就变成"装两个"）。
AppId={{5A2550C4-2693-4DA5-86A2-86326A8DBAA2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ControlServer
DefaultDirName={autopf}\ControlServer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
AllowNoIcons=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
VersionInfoVersion={#AppVersion}
OutputDir=..\..\dist\installer
OutputBaseFilename=ControlServer-Setup-{#AppVersion}
SetupIconFile=..\..\assets\app.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 服务由脚本注册，安装包本身不请求重启
RestartIfNeededByRun=no
; 卸载时若托盘助手仍占着 exe，Inno 自己删不掉 —— 交给 uninstall-service.ps1 先杀进程
CloseApplications=no

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式（打开管理控制台）"; GroupDescription: "附加快捷方式:"; Flags: unchecked

[Files]
Source: "..\..\dist\win-x64-service\{#AppExeName}";  DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\dist\win-x64-service\{#TrayExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 这两个脚本随包带走：一来安装时要用，二来出问题时可让现场人员手工重跑一次。
Source: "install-service.ps1";   DestDir: "{app}\tools"; Flags: ignoreversion
Source: "uninstall-service.ps1";     DestDir: "{app}\tools"; Flags: ignoreversion
Source: "verify-install.ps1";        DestDir: "{app}\tools"; Flags: ignoreversion
Source: "collect-diagnostics.ps1";   DestDir: "{app}\tools"; Flags: ignoreversion
; 再带一份"只解压不安装"的副本：升级/重装时要**在复制文件之前**先把正在跑的
; 服务停掉并注销，否则 exe 被服务占着，Inno 复制会直接失败。
Source: "uninstall-service.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\打开管理控制台"; Filename: "http://localhost:{#HttpPort}/"
Name: "{group}\数据目录";       Filename: "{commonappdata}\ControlServer"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\管理控制台"; Filename: "http://localhost:{#HttpPort}/"; Tasks: desktopicon

[Run]
Filename: "http://localhost:{#HttpPort}/"; Description: "打开管理控制台"; Flags: postinstall shellexec nowait skipifsilent

[UninstallRun]
; 卸载器删文件之前必须先停服务、杀托盘、撤防火墙 —— 否则 exe 被占着删不掉。
; -SkipDeleteFiles：程序目录由 Inno 卸载器负责，脚本别抢。
; 注意：Inno 的 .iss **不支持** `\` 续行，这条必须写在一行里。
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\uninstall-service.ps1"" -SkipDeleteFiles -InstallDir ""{app}"" -ServiceName ""{#ServiceName}"" -LogFile ""{commonappdata}\ControlServer\logs\uninstall.log"""; Flags: runhidden skipifdoesntexist; RunOnceId: "ControlServerRemoveService"

[Messages]
; FinishedLabel 不接受 %1 之类的参数 —— 路径用常量在运行时展开。
chinese.FinishedLabel=安装完成。%n%n服务「{#AppName}」已注册为开机自启。%n数据目录：{commonappdata}\ControlServer%n%n默认账号 admin / admin123 —— 登录后建议尽快自行修改。
chinese.FinishedLabelNoIcons=安装完成。%n%n服务「{#AppName}」已注册为开机自启。%n数据目录：{commonappdata}\ControlServer%n%n默认账号 admin / admin123 —— 登录后建议尽快自行修改。
english.FinishedLabel=Setup has finished installing {#AppName}.%n%nThe service has been registered to start automatically.%nData folder: {commonappdata}\ControlServer%n%nDefault account: admin / admin123 (we recommend changing it after sign-in).
english.FinishedLabelNoIcons=Setup has finished installing {#AppName}.%n%nThe service has been registered to start automatically.%nData folder: {commonappdata}\ControlServer%n%nDefault account: admin / admin123 (we recommend changing it after sign-in).

[Code]
var
  ServiceStepFailed: Boolean;
  ServiceFailReason: String;
  ServiceLogPath: String;
  RemoveDataDir: Boolean;

function PowerShellPath(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

{ 探一下 PowerShell 到底能不能跑脚本。
  ⚠️ 关键场景：目标机可能被**组策略**把执行策略锁成 AllSigned / Restricted ——
     这时命令行写 -ExecutionPolicy Bypass 是**没用的**（组策略优先级更高），
     install-service.ps1 会被直接拒绝执行，服务根本不会注册。
     先探测，好在失败时给出准确原因，而不是让人对着"服务没起来"干瞪眼。 }
function PowerShellUsable(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(PowerShellPath(), '-NoProfile -ExecutionPolicy Bypass -Command "exit 0"',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

function DataDirPath(): String;
begin
  Result := ExpandConstant('{commonappdata}\ControlServer');
end;

{ 复制文件**之前**：把上一次安装留下的服务/托盘/防火墙清掉。
  · 首次安装时本机什么都没有，脚本自己会跳过；
  · 重装 / 升级时服务正在跑，会把 ControlServer.Server.exe 占住 ——
    不先停掉，Inno 复制文件必然失败（这是最容易踩的一个坑）。
  返回非空字符串 = 中止安装并显示该消息。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Cmd: String;
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;

  ExtractTemporaryFile('uninstall-service.ps1');

  Cmd := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\uninstall-service.ps1') + '"'
       + ' -SkipDeleteFiles'
       + ' -InstallDir "' + ExpandConstant('{app}') + '"'
       + ' -ServiceName "{#ServiceName}"';

  if not Exec(PowerShellPath(), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := '无法停止已有的中控服务（PowerShell 未启动）。请先手工停止 ControlServer 服务再重试。';
end;

{ 调用 install-service.ps1 完成"建数据目录 → 注册服务 → 授权托盘启停 → 放行防火墙 →
  启动服务 → 部署托盘助手 → 自检"。程序文件此时已经由 Inno 放好，所以传 -SkipCopy。 }
procedure RunServiceInstaller();
var
  Cmd: String;
  ResultCode: Integer;
begin
  ServiceLogPath := DataDirPath() + '\logs\install-service.log';
  ServiceFailReason := '';

  { 先确认 PowerShell 可用。组策略把执行策略锁成 AllSigned/Restricted 时，
    下面的调用会失败得**毫无痕迹** —— 服务没被创建，日志也不会生成。 }
  if not PowerShellUsable() then begin
    ServiceStepFailed := True;
    ServiceFailReason := '本机的 PowerShell 被安全策略禁止运行脚本。' + #13#10
      + '命令行上的 -ExecutionPolicy Bypass 会被组策略覆盖，所以脚本压根没跑起来。'
      + #13#10 + '解决：以管理员身份执行 Set-ExecutionPolicy -Scope LocalMachine RemoteSigned';
    ServiceLogPath := '(PowerShell 被策略禁止，脚本未执行)';
    exit;
  end;

  Cmd := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\tools\install-service.ps1') + '"'
       + ' -SkipCopy'
       + ' -Source "' + ExpandConstant('{app}') + '"'
       + ' -InstallDir "' + ExpandConstant('{app}') + '"'
       + ' -DataDir "' + DataDirPath() + '"'
       + ' -ServiceName "{#ServiceName}"'
       + ' -HttpPort {#HttpPort} -PairingPort {#PairingPort}'
       + ' -LogFile "' + ServiceLogPath + '"'
       + ' -Verify';

  if not Exec(PowerShellPath(), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
    ServiceStepFailed := True;
    ServiceFailReason := '无法启动 PowerShell 进程。';
    ServiceLogPath := '(无法启动 PowerShell)';
    exit;
  end;

  ServiceStepFailed := (ResultCode <> 0);

  if ServiceStepFailed then begin
    if ResultCode = 1 then
      ServiceFailReason := '部署脚本中途抛错（退出码 1）—— 多半卡在「注册服务」或「启动服务」那一步。'
    else if ResultCode = 2 then
      ServiceFailReason := '服务已注册，但没能进入 Running 或端口没起来（自检退出码 2）。'
    else if ResultCode = 3 then
      ServiceFailReason := '脚本被拒绝执行（退出码 3）—— 执行策略或权限问题。'
    else
      ServiceFailReason := '装配脚本返回退出码 ' + IntToStr(ResultCode) + '。';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    RunServiceInstaller();
    if ServiceStepFailed and (not WizardSilent) then
      MsgBox('程序文件已复制到：' + #13#10 + ExpandConstant('{app}') + #13#10#13#10
           + '但服务没有成功启动 —— 浏览器现在多半打不开。' + #13#10#13#10
           + '原因：' + #13#10 + ServiceFailReason + #13#10#13#10
           + '排查（任选其一）：' + #13#10
           + '  · 跑一次现场诊断（只读，不需要管理员）：' + #13#10
           + '    ' + ExpandConstant('{app}') + '\tools\collect-diagnostics.ps1' + #13#10
           + '  · 或把这两个文件发回（比截图有用得多）：' + #13#10
           + '    ' + ServiceLogPath + #13#10
           + '    ' + DataDirPath() + '\logs\install-diagnostics.log' + #13#10#13#10
           + '手工重试（以管理员身份运行）：' + #13#10
           + '  ' + ExpandConstant('{app}') + '\tools\install-service.ps1',
           mbError, MB_OK);
  end;
end;

{ 自检失败时，Finish 页不能再写"服务已注册为开机自启" —— 那是假话。 }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and ServiceStepFailed then
    WizardForm.FinishedLabel.Caption :=
      '程序文件已安装，但服务没有成功启动，浏览器现在打不开。' + #13#10#13#10
      + '原因：' + ServiceFailReason + #13#10#13#10
      + '请在安装目录的 tools 子目录下运行 collect-diagnostics.ps1 生成诊断报告，' + #13#10
      + '连同 logs 目录里的 install-service.log 一起发回。';
end;

function GetCustomSetupExitCode(): Integer;
begin
  if ServiceStepFailed then
    Result := 1
  else
    Result := 0;
end;

function InitializeUninstall(): Boolean;
var
  Answer: Integer;
begin
  Result := True;
  RemoveDataDir := False;

  { 静默卸载不弹窗：默认保留数据目录（宁可留，不可误删现场配置）。 }
  if UninstallSilent then
    exit;

  Answer := MsgBox('是否同时删除数据目录？' + #13#10#13#10
                 + '数据目录：' + DataDirPath() + #13#10
                 + '里面是按钮配置、指令库、图片与日志。' + #13#10#13#10
                 + '选「否」则保留 —— 重新安装后配置还在。',
                 mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
  RemoveDataDir := (Answer = IDYES);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then begin
    if RemoveDataDir then begin
      { [UninstallRun] 里已经停服务了，但进程退出、文件句柄释放还要一点时间。 }
      Sleep(2000);
      DelTree(DataDirPath(), True, True, True);
    end;
  end;
end;
