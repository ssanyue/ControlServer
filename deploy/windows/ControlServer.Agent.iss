; ═══════════════════════════════════════════════════════════════════════════
;  客户端Server —— Windows 安装包（Inno Setup 6）
;
;  ⚠️ 2026-10-16：产品显示名由「中控客户端 Agent」改为「客户端Server」。
;     改的是**显示名**（程序与功能 / 开始菜单组 / 服务显示名与服务描述），
;     以下内部标识**刻意保持不变**（改了就会变成"装两个"或找不到安装包）：
;       · AppId（升级/卸载的识别键）      · ServiceName=ControlServerAgent
;       · 安装目录 {autopf}\ControlServer Agent
;       · 状态目录 %ProgramData%\ControlServer.Agent
;       · 产物名 ControlServer-Agent-Setup-<版本>.exe
;         （服务端 AgentInstallerLocator 靠这个**文件名前缀**去找安装包）
;
;  构建入口：deploy\windows\build-agent-installer.ps1
;  手工编译：ISCC.exe /DAppVersion=0.1.0 deploy\windows\ControlServer.Agent.iss
;  静默安装：ControlServer-Agent-Setup-0.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
;            [/SERVER=http://10.0.0.5:8899] [/THERMAL=0]
;   ⚠️ /THERMAL 的语义 2026-10-14 起**反过来了**：不再有它 = 启用；只有给 0/false/no/off 才关。
;      （需求①：硬件温度监控改为默认启用。）
;   ⚠️ 2026-10-13 起**没有注册令牌 / 加入码**（/TOKEN= /JOINCODE= 已移除）：
;     客户端能发现到中心服务端即自动注册，"装完就出现在网页上"是默认行为。
;     因此本向导现在只剩一个输入框（服务端地址，可留空 = 靠广播自动发现）。
;
;  设计要点
;   · 管理员权限安装；程序装到 Program Files\ControlServer Agent，
;     状态在 %ProgramData%\ControlServer.Agent
;   · 程序本体是 self-contained 单文件 ⇒ 目标机**不需要**预装 .NET 运行时
;   · 服务注册 / 服务 DACL 授权 / HKCU 托盘启动项，全部复用 install-agent.ps1
;     （同一份已验证过的逻辑），避免在 .iss 里重写 sc sdset 那套
;   · ⚠️ 放行**一条**入站 UDP 规则（按**程序**授权，见 install-agent.ps1 的
;     Add-AgentUdpFirewallRule）：M4.5 的「UDP 指令通道」端口由中心下发、可随时改，
;     端口级规则没法预先写死。其余通信一律是客户端连出去，不需要任何入站规则。
;   · 收尾自检（-Verify）：服务真的 Running 才算"装好"
;   · 硬件温度监控（M4.3）**2026-10-14 起默认勾选**（需求①）：启动时释放并加载一个
;     内核级驱动（要管理员权限 / 可能被安全软件拦）。加载失败不影响任何其它功能，
;     网页上如实显示「驱动加载失败」。不想要的现场取消勾选（或 /THERMAL=0）。
;
;  与服务端安装包（ControlServer.iss）的对应关系：AppId 必须不同（否则互相顶掉），
;  目录 / 服务名 / 状态目录也各不相同 —— 两者可以装在同一台机器上。
; ═══════════════════════════════════════════════════════════════════════════

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

; 服务端地址在**构建时**可烘焙进安装包。
;   安装时仍可用 /SERVER= 覆盖，或在向导的输入框里手填；留空 = 靠 UDP 广播自动发现。
; ⚠️ 注册凭据（令牌 / 加入码）已于 2026-10-13 整体移除，这里不再有 EmbeddedToken / EmbeddedJoinCode。
#ifndef EmbeddedServer
  #define EmbeddedServer ""
#endif

#define AppName       "客户端Server"
#define ServiceExeName  "ControlServer.Agent.Service.exe"
#define TrayExeName     "ControlServer.Agent.Tray.exe"

; 两个 exe 的来源目录。
;
; 默认就是仓库里的 dist\agent。留出这个 #define 是为了**受限环境**：
; 有的机器上 dotnet 的单文件打包会在最后一步（HostWriter 回填 header，
; AdjustHeadersForBundle）被"拒绝访问已有 exe"挡住 —— 那时先把产物发布到
; 一个可写目录，再用 /DPayloadDir=<那个目录> 覆盖这里。
#ifndef PayloadDir
  #define PayloadDir "..\..\dist\agent"
#endif
#define ServiceName     "ControlServerAgent"
#define DiscoveryPort   "9998"

[Setup]
; AppId 一旦发布就不能改 —— 它是升级/卸载的识别键（改了就变成"装两个"）。
; ⚠️ 与服务端安装包刻意不同：两者是独立产品，可以并存在同一台机器上。
AppId={{8B4C5D2E-7A61-4F83-B9C4-5E6F708192A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ControlServer
DefaultDirName={autopf}\ControlServer Agent
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
OutputDir=..\..\dist\agent-setup
OutputBaseFilename=ControlServer-Agent-Setup-{#AppVersion}
SetupIconFile=..\..\assets\app.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#TrayExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
RestartIfNeededByRun=no
; 卸载时若托盘/服务仍占着 exe，Inno 自己删不掉 —— 交给 uninstall-agent.ps1 先杀进程
CloseApplications=no

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PayloadDir}\{#ServiceExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\{#TrayExeName}";    DestDir: "{app}"; Flags: ignoreversion
; 这两个脚本随包带走：一来安装时要用，二来出问题时可让现场人员手工重跑一次。
Source: "install-agent.ps1";   DestDir: "{app}\tools"; Flags: ignoreversion
Source: "uninstall-agent.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
; 再带一份"只解压不安装"的副本：升级/重装时要**在复制文件之前**先把正在跑的
; 服务停掉并注销，否则 exe 被占着，Inno 复制会直接失败。
Source: "uninstall-agent.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"

[UninstallRun]
; 卸载器删文件之前必须先撤启动项、杀托盘、停服务 —— 否则 exe 被占着删不掉。
; -SkipDeleteFiles：程序目录由 Inno 卸载器负责，脚本别抢。
; 注意：Inno 的 .iss **不支持** `\` 续行，这条必须写在一行里。
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\uninstall-agent.ps1"" -SkipDeleteFiles -InstallDir ""{app}"" -ServiceName ""{#ServiceName}"" -LogFile ""{commonappdata}\ControlServer.Agent\logs\uninstall.log"""; Flags: runhidden skipifdoesntexist; RunOnceId: "ControlServerAgentRemoveService"

[Messages]
; ⚠️ 2026-10-13 起完成页**不再提"凭据 / 加入码"** —— 注册已免凭据，那套自救流程不存在了。
;    现在唯一可能"装了却没出现"的原因是**网络**（跨网段 / 广播被禁），所以文案指向那里。
chinese.FinishedLabel=安装完成。%n%n服务「{#AppName}」已注册为开机自启，托盘图标已在右下角通知区域。%n状态目录：{commonappdata}\ControlServer.Agent%n%n它会自动发现同一局域网内的中心服务端并**自动注册**（不需要任何令牌或加入码），装完即出现在网页的「客户端」页。%n若几分钟后仍未出现：确认这台机器与中心服务端在同一网段；跨网段或广播被禁时，请用 -Server 指定服务端地址重跑安装，或查看 {commonappdata}\ControlServer.Agent\logs 下的客户端日志。
chinese.FinishedLabelNoIcons=安装完成。%n%n服务「{#AppName}」已注册为开机自启。%n状态目录：{commonappdata}\ControlServer.Agent%n%n它会自动发现同一局域网内的中心服务端并**自动注册**（不需要任何令牌或加入码），装完即出现在网页的「客户端」页。%n若几分钟后仍未出现：确认这台机器与中心服务端在同一网段；跨网段或广播被禁时，请用 -Server 指定服务端地址重跑安装，或查看 {commonappdata}\ControlServer.Agent\logs 下的客户端日志。
english.FinishedLabel=Setup has finished installing {#AppName}.%n%nThe client service has been registered to start automatically, and the tray icon is now in the notification area.%nState folder: {commonappdata}\ControlServer.Agent%n%nIt will automatically discover the central server on the same LAN and register itself (no token or join code needed) - it should appear on the Clients page right away.%nIf it still does not show up after a few minutes: make sure this machine and the server are on the same subnet; if they are not (or broadcast is blocked), re-run setup with -Server pointing at the server, or check the client log under {commonappdata}\ControlServer.Agent\logs.
english.FinishedLabelNoIcons=Setup has finished installing {#AppName}.%n%nThe client service has been registered to start automatically.%nState folder: {commonappdata}\ControlServer.Agent%n%nIt will automatically discover the central server on the same LAN and register itself (no token or join code needed) - it should appear on the Clients page right away.%nIf it still does not show up after a few minutes: make sure this machine and the server are on the same subnet; if they are not (or broadcast is blocked), re-run setup with -Server pointing at the server, or check the client log under {commonappdata}\ControlServer.Agent\logs.

[Code]
var
  ConnectionPage: TInputQueryWizardPage;
  ThermalPage: TWizardPage;
  ThermalCheck: TNewCheckBox;
  ServiceStepFailed: Boolean;
  ServiceFailReason: String;
  ServiceLogPath: String;
  RemoveStateDir: Boolean;

function PowerShellPath(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

{ 探一下 PowerShell 到底能不能跑脚本。
  ⚠️ 关键场景：目标机可能被**组策略**把执行策略锁成 AllSigned / Restricted ——
     这时命令行写 -ExecutionPolicy Bypass 是**没用的**（组策略优先级更高），
     install-agent.ps1 会被直接拒绝执行，服务根本不会注册。
     先探测，好在失败时给出准确原因。 }
function PowerShellUsable(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(PowerShellPath(), '-NoProfile -ExecutionPolicy Bypass -Command "exit 0"',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

function StateDirPath(): String;
begin
  Result := ExpandConstant('{commonappdata}\ControlServer.Agent');
end;

{ 命令行参数优先，其次构建时烘焙进去的值，最后空。
  /SERVER= 是静默批量部署的入口（配合组策略 / 批处理）。 }
function GetServerValue(): String;
begin
  Result := ExpandConstant('{param:SERVER|{#EmbeddedServer}}');
end;

{ 静默安装时的温度开关来源：/THERMAL=0（或 false / no / off）才会关。

  🔥 2026-10-14 起**默认启用**（需求①）：不传 /THERMAL 就是开。
  与图形安装里那个复选框的默认状态（勾上）必须一致 —— 两边不一致时，
  "图形装出来是开的、静默装出来是关的"会成为一类极难排查的现场差异。

  ⚠️ 所以这里判的是"**显式给了 0/false/no/off 才关**"，而不是"给了 1 才开"：
  参数缺失必须是"开"那一边。 }
function GetThermalValue(): Boolean;
var
  V: String;
begin
  V := LowerCase(Trim(ExpandConstant('{param:THERMAL|}')));
  if V = '' then
    Result := True
  else
    Result := not ((V = '0') or (V = 'false') or (V = 'no') or (V = 'off'));
end;

procedure InitializeWizard();
begin
  { 一个可选页：只问**服务端地址**。
    ⚠️ 2026-10-13 起**不再有令牌 / 加入码输入框**：注册不需要凭据，
       "留空也会自动出现"现在是**真的**（旧文案说它是错的 —— 那时确实会被拒）。
    · 服务端地址留空 = 靠 UDP 广播自动发现（默认路径，也是绝大多数现场）；
    · 跨网段 / 广播被禁的网络里，地址必须填。 }
  ConnectionPage := CreateInputQueryPage(wpSelectDir,
    '连接中心服务端',
    '一般可以留空 —— 客户端会自动发现同一局域网内的中心服务端，'
    + '发现到之后自动注册，装完即出现在网页的「客户端」页（不需要任何令牌或加入码）。',
    '跨网段或广播被禁的网络，请填写服务端地址（如 http://10.0.0.5:8899）。');

  ConnectionPage.Add('服务端地址（可留空 = 自动发现）：', False);

  ConnectionPage.Values[0] := GetServerValue();

  { 可选能力页：硬件温度监控（M4.3）。**2026-10-14 起默认勾选**（需求①）。
    ⚠️ 打开它意味着客户端启动时要释放并加载一个内核级驱动（LibreHardwareMonitor 内嵌的 WinRing0）：
       要管理员 / SYSTEM 权限、可能被安全软件拦截、部分虚拟机上没有温度传感器。
       这些都是真的，但"默认关着"的代价更大 —— 见下面那段说明的措辞。

    说明写在这里而不是放到复选框旁边：复选框本身要短，长句会让向导页挤成一团。 }
  ThermalPage := CreateCustomPage(ConnectionPage.ID,
    '可选能力（默认已勾选）',
    '硬件温度监控可以读取 CPU / 显卡 / 硬盘温度，用于排查过热与风扇故障。'
    + '它需要加载一个内核级驱动：要管理员权限、可能被安全软件拦截、部分虚拟机上不支持。'
    + '加载失败不影响其它任何功能，网页上会如实显示「驱动加载失败」或「未启用」（绝不会显示 0 ℃）。'
    + '不必需要的现场（例如安全软件会反复弹窗）取消勾选即可。');

  ThermalCheck := TNewCheckBox.Create(ThermalPage);
  ThermalCheck.Parent := ThermalPage.Surface;
  ThermalCheck.Left := 0;
  ThermalCheck.Top := 0;
  ThermalCheck.Width := ThermalPage.SurfaceWidth;
  ThermalCheck.Height := ScaleY(24);
  ThermalCheck.Caption := '启用硬件温度监控（默认已启用）';
  ThermalCheck.Checked := GetThermalValue();
end;

{ 静默安装时没有向导页 —— 值直接来自 /SERVER= /THERMAL= 或烘焙值。 }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := WizardSilent and ((PageID = ConnectionPage.ID) or (PageID = ThermalPage.ID));
end;

{ 复制文件**之前**：把上一次安装留下的服务/托盘清掉。
  · 首次安装时本机什么都没有，脚本自己会跳过；
  · 重装 / 升级时服务正在跑，会把 ControlServer.Agent.Service.exe 占住 ——
    不先停掉，Inno 复制文件必然失败（这是最容易踩的一个坑）。
  返回非空字符串 = 中止安装并显示该消息。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Cmd: String;
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;

  ExtractTemporaryFile('uninstall-agent.ps1');

  Cmd := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\uninstall-agent.ps1') + '"'
       + ' -SkipDeleteFiles'
       + ' -InstallDir "' + ExpandConstant('{app}') + '"'
       + ' -ServiceName "{#ServiceName}"';

  if not Exec(PowerShellPath(), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := '无法停止已有的客户端Server（PowerShell 未启动）。请先手工停止它再重试。';
end;

{ 调用 install-agent.ps1 完成"复制校验 → 状态目录与配置 → 注册服务 →
  授权托盘启停 → 启动服务 → 放行 UDP 指令通道入站 → 部署托盘 → 自检"。
  程序文件此时已经由 Inno 放好，所以传 -SkipCopy。 }
procedure RunAgentInstaller();
var
  Cmd: String;
  ResultCode: Integer;
  ServerValue: String;
  ThermalValue: Boolean;
begin
  ServiceLogPath := StateDirPath() + '\logs\install-agent.log';
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

  if WizardSilent then begin
    ServerValue := GetServerValue();
    ThermalValue := GetThermalValue();
  end
  else begin
    ServerValue := Trim(ConnectionPage.Values[0]);
    ThermalValue := ThermalCheck.Checked;
  end;

  Cmd := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\tools\install-agent.ps1') + '"'
       + ' -SkipCopy'
       + ' -Source "' + ExpandConstant('{app}') + '"'
       + ' -InstallDir "' + ExpandConstant('{app}') + '"'
       + ' -StateDir "' + StateDirPath() + '"'
       + ' -ServiceName "{#ServiceName}"'
       + ' -DiscoveryPort {#DiscoveryPort}'
       + ' -LogFile "' + ServiceLogPath + '"'
       + ' -Verify';

  if ServerValue <> '' then
    Cmd := Cmd + ' -Server "' + ServerValue + '"';

  { 🔥 2026-10-14 起**默认开**（需求①）：只有**取消勾选**才把 `-NoThermal` 传下去。
    不传 = 开 —— 与 GetThermalValue() 的默认值、与客户端自己的代码默认值三者一致。
    （过去是"勾了才传 -ThermalEnabled"，那个方向在默认值翻过来之后会**反过来**：不传就变成关。） }
  if not ThermalValue then
    Cmd := Cmd + ' -NoThermal';

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
      ServiceFailReason := '服务已注册，但没能进入 Running（自检退出码 2）。'
    else if ResultCode = 3 then
      ServiceFailReason := '脚本被拒绝执行（退出码 3）—— 执行策略或权限问题。'
    else
      ServiceFailReason := '装配脚本返回退出码 ' + IntToStr(ResultCode) + '。';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    RunAgentInstaller();
    if ServiceStepFailed and (not WizardSilent) then
      MsgBox('程序文件已复制到：' + #13#10 + ExpandConstant('{app}') + #13#10#13#10
           + '但客户端服务没有成功启动。' + #13#10#13#10
           + '原因：' + #13#10 + ServiceFailReason + #13#10#13#10
           + '排查：把这两个文件发回（比截图有用得多）：' + #13#10
           + '  ' + ServiceLogPath + #13#10
           + '  ' + ExpandConstant('{app}') + '\tools\install-agent.ps1' + #13#10#13#10
           + '手工重试（以管理员身份运行）：' + #13#10
           + '  ' + ExpandConstant('{app}') + '\tools\install-agent.ps1',
           mbError, MB_OK);
  end;
end;

{ 自检失败时，Finish 页不能再写"已注册为开机自启" —— 那是假话。 }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and ServiceStepFailed then
    WizardForm.FinishedLabel.Caption :=
      '程序文件已安装，但客户端服务没有成功启动。' + #13#10#13#10
      + '原因：' + ServiceFailReason + #13#10#13#10
      + '请在安装目录的 tools 子目录下运行 install-agent.ps1 重试，' + #13#10
      + '并把 logs 目录里的 install-agent.log 发回。';
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
  RemoveStateDir := False;

  { 静默卸载不弹窗：默认**保留**状态目录。
    ⚠️ 里面有 DPAPI 加密的注册凭据 —— 保留它意味着"重装后不必重新注册"，
       删掉它意味着"这台机器要重新准入一次"。宁可留，不可误删。 }
  if UninstallSilent then
    exit;

  Answer := MsgBox('是否同时删除客户端数据目录？' + #13#10#13#10
                 + '数据目录：' + StateDirPath() + #13#10
                 + '里面是注册凭据（DPAPI 加密）与运行日志。' + #13#10#13#10
                 + '选「否」则保留 —— 重新安装后不必重新注册。',
                 mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
  RemoveStateDir := (Answer = IDYES);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then begin
    if RemoveStateDir then begin
      { [UninstallRun] 里已经停服务了，但进程退出、文件句柄释放还要一点时间。 }
      Sleep(2000);
      DelTree(StateDirPath(), True, True, True);
    end;
  end;
end;
