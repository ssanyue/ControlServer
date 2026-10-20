#Requires -Version 5.1
<#
.SYNOPSIS
    客户端Server —— 在本机安装 / 重装（管理员），同时供图形安装包（Inno Setup）复用。
    （2026-10-16 显示名由「中控客户端 Agent」改为「客户端Server」；-DisplayName 是唯一出口。）

.DESCRIPTION
    做 6 件事：

      1) 复制 2 个 exe 到 $InstallDir（-SkipCopy 时跳过，安装包已放好）
      2) 预建状态目录，并写 appsettings.json（服务端地址 / 发现端口 / 服务名）
      3) 注册 Windows 服务（自启 + 崩溃自愈 + 授权交互用户启停）
      4) 放行「UDP 指令通道」的入站规则（**按程序授权**，见下）
      5) 为当前用户写 HKCU 启动项并拉起托盘伴侣
      6) （可选 -Verify）自检：服务是否 Running + 是否已上报状态

    ⚠️ 与 install-service.ps1 的**关键差异：这里的规则是按程序授权，不是按端口**。
       M4.5 起客户端有**一个**入站通道 —— 「UDP 指令通道」（远程关机 / 重启 / 音量）。
       它的端口**由中心服务端下发、可随时改**（默认 8090，允许范围 1024~65535），
       所以端口级规则没法预先写死：改一次端口就得重新写一次规则，改漏了的表现是
       "指令发出去了机器没反应" —— 一个要靠猜才能定位的故障。
       改成按 -Program 指向 Agent 服务 exe：一次装好，之后换端口不用再动。
       作用域确实比端口级略宽（该程序可以监听任意 UDP 端口），但**过滤在程序内部**
       （来源 IP 黑名单 + 明文指令解析）。
       🔥 2026-10-13 起这条通道是**纯明文**的：不再有 HMAC 验签，同网段任意主机
       都能关掉这台设备 —— 所以**防火墙这一层就是唯一的边界**，不要把该端口
       暴露到不可信网络；不想被控就在「客户端」页把通道关掉。
       用 -SkipFirewallRule 可整个跳过（例如本机根本不打算用 UDP 通道）。

       除这一个通道外，客户端的通信仍然是**它连出去**
       （UDP 广播发现 + SignalR 长连接），不需要任何入站规则。

    ⚠️ 服务注册**必须**用 New-Service（cmdlet），绝不能用 `sc.exe create` ——
       PowerShell 5.1 往原生程序传参时会吞掉内层引号，装到含空格的目录
       （如 C:\Program Files\...）时 binPath 会被拆断，报 1639 ERROR_INVALID_COMMAND_LINE。
       详见 install-service.ps1 里的实测记录。

.NOTES
    不用 $ErrorActionPreference='Stop'：原生工具的 stderr 警告不该被当成致命错误。
    统一用 $LASTEXITCODE + try/catch 收口，并用显式 exit 0/1/2/3 表达结果。
    退出码：0 成功 / 1 装配中途失败 / 2 服务没起来（自检不过） / 3 脚本被策略拒绝。

    ⚠️ 函数必须定义在调用点之前 —— PowerShell 不支持前向引用，
       而 Continue 模式会把 CommandNotFoundException 静默吞掉（脚本"成功"却少做了事）。

.PARAMETER Source
    发布产物目录（含 2 个 exe）。默认 <仓库根>\dist\agent

.PARAMETER InstallDir
    安装目录。默认 %ProgramFiles%\ControlServer Agent

.PARAMETER StateDir
    状态目录（凭据 / 状态文件 / 日志）。默认 %ProgramData%\ControlServer.Agent

.PARAMETER Server
    中心服务端地址（形如 http://10.0.0.5:8899）。留空 = 靠 UDP 广播自动发现（默认）。
    跨网段 / 广播被禁的网络里必须显式指定。

    ⚠️ 2026-10-13 起**不再需要任何注册令牌 / 加入码**：客户端能发现到中心服务端即自动注册。
    所以本脚本没有 -Token / -JoinCode 参数了。

.PARAMETER NoThermal
    关掉硬件温度监控（M4.3）。**不传 = 启用**（2026-10-14 起默认打开，需求①）。

    打开它意味着客户端启动时会释放并加载一个内核级驱动（LibreHardwareMonitor 内嵌的 WinRing0），
    这需要管理员 / SYSTEM 权限，可能被安全软件拦截，部分虚拟机上不支持。
    加载失败时客户端**不重试、不刷日志**，页面上如实显示「驱动加载失败」+ 原因，
    其余任何功能都不受影响。

    ⚠️ 关掉之后进程**一个字节都不会碰**驱动（驱动是 DLL 里的一段资源，只有真去调用才落盘），
       页面显示「未启用」。需要改这个开关就重跑本脚本 —— 它会重写 appsettings.json 并重启服务。

.PARAMETER ThermalEnabled
    ⚠️ **已废弃**（2026-10-14）。过去用它开启温度监控，现在默认就是开的，所以它是空操作。
    保留它只是为了**不破坏既有脚本**（群里发过的批处理、组策略里的命令行都还带着它）——
    带着它跑不会报"找不到参数"，行为与不带完全一致。

.PARAMETER SkipFirewallRule
    不创建「UDP 指令通道」的入站放行规则（M4.5）。用途：本机根本不打算用那条通道，
    或现场由组策略 / 安全软件统一下发防火墙策略、不接受安装脚本插手。

    ⚠️ 跳过的后果是**这个通道收不到包**（远程关机 / 重启 / 音量失效），
       其余功能一概不受影响。所以脚本会以警告形式写出来，而不是静默略过。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\install-agent.ps1

.EXAMPLE
    # 静默批量部署：指定服务端地址（跨网段 / 广播被禁时必填）
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\install-agent.ps1 `
        -Server http://10.0.0.5:8899

.EXAMPLE
    # 不想要温度监控（例如安全软件会反复弹窗）时显式关掉
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\install-agent.ps1 -NoThermal
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\..\dist\agent'),
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ControlServer Agent'),
    [string]$StateDir = (Join-Path $env:ProgramData 'ControlServer.Agent'),
    [string]$ServiceName = 'ControlServerAgent',
    # ⚠️ 显示名（services.msc / 程序与功能里看到的那个）—— 2026-10-16 起为「客户端Server」。
    #    ServiceName 是内部标识，**不要跟着改**（卸载脚本 / 自检 / 诊断都按它找服务）。
    [string]$DisplayName = '客户端Server',

    # ── 准入与寻址 ────────────────────────────────────────────────
    # ⚠️ 2026-10-13 起没有 -Token / -JoinCode：注册不需要任何凭据。
    [string]$Server = '',
    [int]$DiscoveryPort = 9998,

    # ── 可选能力 ──────────────────────────────────────────────────
    # 硬件温度监控（M4.3）：**2026-10-14 起默认打开**（需求①）。
    # 打开它意味着进程启动时会释放并加载一个内核级驱动（LibreHardwareMonitor
    # 内嵌的 WinRing0）—— 这需要管理员/SYSTEM 权限，可能被安全软件拦截，
    # 部分虚拟机根本不支持。加载失败**不影响别的任何功能**，页面如实显示原因。
    # 关掉时**一个字节都不会碰**：驱动是 DLL 里的一段资源，只有真的去调用才落盘。
    # 拿不到温度时页面显示「未启用」/「—」，绝不显示 0。
    [switch]$NoThermal,

    # ⚠️ 已废弃（2026-10-14）：默认就是开的，所以这个开关是空操作。
    #    保留只为不破坏既有脚本（批处理 / 组策略里还带着它）。
    [switch]$ThermalEnabled,

    # ── 入站防火墙（M4.5）────────────────────────────────────────
    # 不建「UDP 指令通道」的入站规则。用于：本机根本不打算用 UDP 通道；
    # 或者现场有统一的防火墙策略（由组策略 / 安全软件统一下发），不接受安装脚本插手。
    # ⚠️ 跳过的后果是**这个通道收不到包**（其余一切都正常）—— 所以它会以警告形式写出，
    #    而不是静默略过。见 Add-AgentUdpFirewallRule 的说明。
    [switch]$SkipFirewallRule,

    # ── 供图形安装包（Inno Setup）复用 ────────────────────────────
    [switch]$SkipCopy,               # 程序文件已由安装包放好，不要清空重铺
    [switch]$SkipConfig,             # 不写 appsettings.json（升级时保留现场配置）
    [string]$LogFile,                # 非空则写一份 transcript（安装失败时取证用）
    [switch]$Verify,                 # 收尾自检；服务没起来返回 2

    # ── 托盘伴侣 ─────────────────────────────────────────────────
    [switch]$SkipTray,               # 不部署托盘（只有服务）
    [switch]$SkipTrayAutostart,      # 不写 HKCU 启动项
    [switch]$SkipTrayLaunch,         # 写启动项但不立刻拉起
    [switch]$NoStart,                # 注册服务但不启动
    [string]$TrayStartupName = 'ControlServerAgent.Tray'
)

# 见 .NOTES：不要用 Stop。
$ErrorActionPreference = 'Continue'

$serviceExeName = 'ControlServer.Agent.Service.exe'
$trayExeName = 'ControlServer.Agent.Tray.exe'

# ═══════════════════════════════════════════════════════════════════════════
# 函数（必须全部前置于主体 —— 见 .NOTES）
# ═══════════════════════════════════════════════════════════════════════════

<#
.SYNOPSIS
    给 SDDL 权限串补上若干权限码，并按服务权限的规范顺序输出。
.DESCRIPTION
    规范顺序：CC DC LC SW RP WP DT LO CR RC SD WD WO。
    输入不是"干净的 2 字符码串"时退化为简单拼接 —— 宁可顺序不漂亮，
    也不能把别人的 DACL 改坏。
#>
function Add-ServiceRights {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Rights,
        [Parameter(Mandatory)][string[]]$Add
    )

    $order = @('CC', 'DC', 'LC', 'SW', 'RP', 'WP', 'DT', 'LO', 'CR', 'RC', 'SD', 'WD', 'WO')
    $fallback = $Rights + ($Add -join '')

    if ($Rights.Length % 2 -ne 0) { return $fallback }

    $pairs = @()
    for ($i = 0; $i -lt $Rights.Length; $i += 2) { $pairs += $Rights.Substring($i, 2) }

    foreach ($p in $pairs) {
        if ($order -notcontains $p) { return $fallback }
    }

    foreach ($a in $Add) {
        if ($pairs -notcontains $a) { $pairs += $a }
    }

    $result = ''
    foreach ($o in $order) {
        if ($pairs -contains $o) { $result += $o }
    }
    return $result
}

<#
.SYNOPSIS
    给"交互用户(IU)"补上 SERVICE_START / SERVICE_STOP，让托盘能启停客户端服务。
.DESCRIPTION
    sc create / New-Service 出来的默认 DACL 里，IU 拿到的是
    CC LC SW LO CR RC —— 正好缺 RP(启动) / WP(停止)。补上即可（最小授权：
    只加启停，不给改配置、不给删服务）。托盘菜单里的"启动/停止客户端服务"靠它。
#>
function Grant-InteractiveServiceControl {
    param([Parameter(Mandatory)][string]$ServiceName)

    $current = (sc.exe sdshow $ServiceName 2>$null | Where-Object { $_ -and $_.Trim() } | Select-Object -First 1)
    if (-not $current) {
        Write-Warning '    无法读取当前安全描述符（sc sdshow 无输出），跳过授权。托盘可能需要管理员权限才能启停服务。'
        return
    }
    $current = $current.Trim()

    if (($current -match '\(A;;[^)]*RP[^)]*;;;IU\)') -and ($current -match '\(A;;[^)]*WP[^)]*;;;IU\)')) {
        Write-Host '    交互用户已具备启停权限，跳过。'
        return
    }

    if ($current -notmatch ';;;IU\)') {
        Write-Warning "    安全描述符里没有交互用户(IU) ACE（可能是定制过 DACL）。跳过自动授权。"
        Write-Warning "    可手工执行：sc.exe sdset $ServiceName `<完整SDDL`>"
        return
    }

    $updated = $current -replace '\(A;;([^)]*);;;IU\)', { param($m) '(A;;' + (Add-ServiceRights -Rights $m.Groups[1].Value -Add @('RP', 'WP')) + ';;;IU)' }

    sc.exe sdset $ServiceName $updated 2>&1 | Out-Null

    # 带 S:(SACL) 段时 sdset 需要 SeSecurityPrivilege；失败就退化成"只设 DACL"。
    if ($LASTEXITCODE -ne 0 -and $updated -match 'S:') {
        $daclOnly = ($updated -split 'S:')[0]
        sc.exe sdset $ServiceName $daclOnly 2>&1 | Out-Null
    }

    if ($LASTEXITCODE -eq 0) {
        Write-Host '    [OK] 已授权交互用户启停服务（RP + WP）'
    }
    else {
        Write-Warning "    sc sdset 失败（退出码 $LASTEXITCODE）。托盘停服务时可能报「拒绝访问」。"
    }
}

<#
.SYNOPSIS
    放行客户端「UDP 指令通道」的入站规则（**按程序**授权）。
.DESCRIPTION
    M4.5 起客户端有**唯一一个**入站通道：UDP 指令通道（远程关机 / 重启 / 音量）。
    它的端口由中心服务端下发、可随时改（默认 8090，允许范围 1024~65535），
    因此**不能写成端口级规则** —— 改一次端口就要重新写一次规则，
    改漏了的表现是"指令发出去了机器没反应"，查起来极不直观。

    改成按 -Program 指向 Agent 服务 exe：一次装好，之后换端口不用再动。
    作用域比端口级略宽（该程序可以监听任意 UDP 端口），但**过滤在程序内部**
    （来源 IP 黑名单 + 明文指令解析）。
    🔥 2026-10-13 起这条通道是**纯明文**的：不再有 HMAC 验签 —— 因此**防火墙这层
    就是唯一的边界**，同网段任意主机都能关掉这台设备，切勿把端口暴露到不可信网络。

    规则显示名固定，重装时先删后建（幂等）。

    ⚠️ **失败不致命**：装了 360 / 火绒等接管防火墙的安全软件时，
    New-NetFirewallRule 会直接抛异常。处理方式与 install-service.ps1 一致 ——
    吞掉并给出手工放行提示。理由：防火墙只影响"这个通道能不能用"，
    服务注册、注册上报、网页可见、文件下发全都不受影响；
    若让它冒到最外层 catch，会变成"装完了服务没跑"，那才是真正的事故。
#>
function Add-AgentUdpFirewallRule {
    param([Parameter(Mandatory)][string]$ServiceExePath)

    $ruleName = 'ControlServer Agent UDP'

    try {
        if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
            Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
        }

        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
            -Protocol UDP -Program $ServiceExePath | Out-Null

        Write-Host ('    [OK] 已放行「UDP 指令通道」入站（按程序：' + $ServiceExePath + '）')
    }
    catch {
        Write-Warning ('    [--] 防火墙规则 ' + $ruleName + ' 创建失败：' + $_.Exception.Message)
        Write-Warning '         → UDP 指令通道（远程关机 / 重启 / 音量）在开了防火墙的机器上会收不到包。'
        Write-Warning ('         请手工放行【入站 UDP】，程序：' + $ServiceExePath)
        Write-Warning '         其余一切不受影响：注册、心跳、遥测、文件下发全都不用入站。'
    }
}

<#
.SYNOPSIS
    停掉并删除同名服务，等它真的从 SCM 消失。
.DESCRIPTION
    ⚠️ 删除是**异步**的：`sc delete` 立刻返回，服务却进入「标记为删除」状态，
    句柄未释放前同名服务建不起来（错误 1072）。所以这里必须轮询等它消失，
    否则重装必然失败在一个看着毫不相干的错误上。
#>
function Remove-ServiceIfPresent {
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [int]$TimeoutSec = 30
    )

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return $true }

    Write-Host ('    停止服务 ' + $ServiceName + ' …')
    sc.exe stop $ServiceName 2>&1 | ForEach-Object { if ("$_".Trim()) { Write-Host ('      ' + "$_") } }

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 700
    }

    Write-Host ('    删除服务 ' + $ServiceName + ' …')
    sc.exe delete $ServiceName 2>&1 | ForEach-Object { if ("$_".Trim()) { Write-Host ('      ' + "$_") } }

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 700
    }

    Write-Warning ('    服务 ' + $ServiceName + ' 在 ' + $TimeoutSec + ' 秒内仍未完全删除（可能被标记为「待删除」）。')
    return $false
}

<#
.SYNOPSIS
    写 / 合并 appsettings.json（客户端 exe 同目录）。
.DESCRIPTION
    为什么用配置文件而不是全塞进服务命令行：
      · 现场需要改服务端地址时，改一个 JSON 比 `sc config` 重写命令行安全得多；
      · 服务名 / 状态目录这类"两侧必须一致"的值放文件里更好核对。

    ⚠️ 已实测确认（2026-10-11）：`Host.CreateApplicationBuilder` + `AddWindowsService`
       会把 ContentRoot 钉在 **AppContext.BaseDirectory**，所以即使服务形态下
       cwd 是 C:\Windows\System32，exe 同目录的 appsettings.json **照样被读到**。
       （验证手法：外层放一份指向临时状态目录的 appsettings.json，从 System32 启动 exe，
        日志确认 Content root = exe 目录、状态目录 = 配置里那个、默认目录未被创建。）

    合并语义：只覆盖显式传进来的项，其余原样保留 —— 升级安装包时不至于把
    "手工改过的服务端地址"一起抹掉。
#>
function Write-AgentConfig {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][string]$StateDir,
        [string]$Server,
        [int]$DiscoveryPort,
        [bool]$ThermalEnabled
    )

    $existing = $null
    if (Test-Path $Path) {
        try {
            $existing = (Get-Content -Path $Path -Raw -Encoding UTF8) | ConvertFrom-Json
        }
        catch {
            Write-Warning "    现有 appsettings.json 无法解析（$($_.Exception.Message)），将重写。"
            $existing = $null
        }
    }

    $root = @{}
    if ($existing) {
        foreach ($p in $existing.PSObject.Properties) { $root[$p.Name] = $p.Value }
    }

    $agent = @{}
    if ($existing -and $existing.PSObject.Properties['Agent']) {
        foreach ($p in $existing.Agent.PSObject.Properties) { $agent[$p.Name] = $p.Value }
    }

    $agent['ServiceName'] = $ServiceName
    $agent['StateDirectory'] = $StateDir
    if ($DiscoveryPort -gt 0) { $agent['DiscoveryPort'] = $DiscoveryPort }
    if ($Server) { $agent['ServerUrl'] = $Server }

    # 硬件温度监控（M4.3）。**总是显式写出来**（包括 false）：现场改起来一目了然，
    # 也免得下次有人对着"没这个键"猜是关着还是老版本没这功能。
    # 嵌套键要**合并**而不是覆盖 —— Agent 段下面还有别的子对象时别把它们抹掉。
    $telemetry = @{}
    if ($existing -and $existing.PSObject.Properties['Agent'] -and
        $existing.Agent.PSObject.Properties['Telemetry']) {
        foreach ($p in $existing.Agent.Telemetry.PSObject.Properties) { $telemetry[$p.Name] = $p.Value }
    }
    $telemetry['ThermalEnabled'] = $ThermalEnabled
    $agent['Telemetry'] = $telemetry

    $root['Agent'] = $agent

    if (-not $root.ContainsKey('Logging')) {
        $root['Logging'] = @{ LogLevel = @{ Default = 'Information' } }
    }

    $json = $root | ConvertTo-Json -Depth 8

    # ⚠️ 不带 BOM 写出：JSON 配置提供程序虽然默认会剥掉 BOM，
    #    但现场难免有人拿记事本/脚本再改一遍，少一层隐形差异更省事。
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json, $utf8NoBom)
}

<#
.SYNOPSIS
    收紧 appsettings.json 的 ACL。
.DESCRIPTION
    去掉继承、只留 SYSTEM 与 Administrators。失败**不致命** ——
    装不上只是"本机普通用户可以改这份配置"，而目录 ACL 被现场改乱却是真会把服务装废的。

    ⚠️ 用 **SID** 而不是 `SYSTEM` / `Administrators` 这两个名字：
       组名在本地化系统上可能取不到（icacls 会报"找不到"），
       SID 则到处都是同一串。S-1-5-18 = 本地系统，S-1-5-32-544 = 本机管理员组。

    为什么还收紧（2026-10-13 起配置里**已经没有凭据**了）：
    这份文件决定"客户端连哪个中心服务端"。普通用户能改它，就能把这台机器
    悄悄指向另一个服务端 —— 那比读到一份令牌更麻烦（它连出去了，页面上还看不出异常）。
#>
function Protect-AgentConfig {
    param([Parameter(Mandatory)][string]$Path)

    try {
        & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host '    [OK] appsettings.json 已收紧权限（仅 SYSTEM / Administrators）'
        }
        else {
            Write-Warning '    收紧 appsettings.json 权限失败（不影响运行；本机普通用户可改它）。'
        }
    }
    catch {
        Write-Warning "    收紧 appsettings.json 权限失败：$($_.Exception.Message)"
    }
}

<#
.SYNOPSIS
    部署托盘伴侣：写 HKCU 启动项 + 立刻拉起一个。
.DESCRIPTION
    写 HKCU\...\Run 而不是 HKLM：托盘必须跑在"用户会话"（服务在 Session 0
    没有通知区域），而 Run 项天然是"当前用户登录时以普通权限启动"。
#>
function Install-AgentTray {
    param(
        [Parameter(Mandatory)][string]$InstallDir,
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][string]$StateDir,
        [Parameter(Mandatory)][string]$TrayStartupName,
        [switch]$SkipAutostart,
        [switch]$SkipLaunch
    )

    $trayPath = Join-Path $InstallDir $trayExeName

    if (-not (Test-Path $trayPath)) {
        Write-Warning "    托盘伴侣不存在：$trayPath（跳过）"
        return
    }

    Write-Host '==> 6/6 安装托盘伴侣（HKCU 启动项 + 立即启动）'

    # ⚠️ HKCU 属于"当前登录用户"。若脚本是用**另一个管理员账号**提权跑的，
    #    启动项会写到那个账号名下，真正用机器的人看不到托盘图标。
    $consoleUser = $null
    try { $consoleUser = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName } catch { }
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name

    if ($consoleUser -and $me -and ($consoleUser -ne $me)) {
        Write-Warning "    当前账号（$me）不是控制台登录用户（$consoleUser）。"
        Write-Warning "    启动项写进了 $me 的注册表 —— $consoleUser 登录后看不到托盘图标。"
        Write-Warning "    请用那个账号再执行一次："
        Write-Warning "      reg add `"HKCU\Software\Microsoft\Windows\CurrentVersion\Run`" /v $TrayStartupName /d `"`"$trayPath`" --service-name `"$ServiceName`" --state-dir `"$StateDir`"`" /f"
    }

    # 托盘只认这两个参数（服务名 + 状态目录）。状态目录必须与服务侧一致，
    # 否则托盘永远显示"未连接"而服务其实连着 —— 最难查的那种误判。
    $command = "`"$trayPath`" --service-name `"$ServiceName`" --state-dir `"$StateDir`""

    if ($SkipAutostart) {
        Write-Host '    已跳过启动项注册（-SkipTrayAutostart）。'
    }
    else {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
        New-ItemProperty -Path $runKey -Name $TrayStartupName -Value $command -PropertyType String -Force | Out-Null
        Write-Host "    启动项已注册：$TrayStartupName"
    }

    if ($SkipLaunch) {
        Write-Host '    已跳过立即启动（-SkipTrayLaunch）。注销 / 重登后图标会出现。'
        return
    }

    try {
        # ⚠️ Start-Process 的 -ArgumentList 是把数组用空格拼成命令行、**不自动加引号**，
        #    含空格的值必须自带引号（与 sc.exe 那个坑同源）。
        Start-Process -FilePath $trayPath -ArgumentList @(
            '--service-name', "`"$ServiceName`"", '--state-dir', "`"$StateDir`""
        ) -ErrorAction Stop | Out-Null
        Write-Host '    托盘伴侣已启动（右下角通知区域）。'
    }
    catch {
        Write-Warning "    托盘伴侣启动失败：$($_.Exception.Message)"
        Write-Warning "    注销 / 重登后再试，或手工双击：$trayPath"
    }
}

<#
.SYNOPSIS
    自检：服务是否 Running，以及客户端是否已经上报状态。
.DESCRIPTION
    ⚠️ 「状态文件还没出现」**不算失败**：客户机可能暂时连不上中心服务端
       （服务端没开 / 广播被禁 / 还没注册）。装完能不能连上是运行时的事，
       安装脚本的职责是"把东西正确放好并拉起来"。
       所以只有"服务没起来"才返回非 0。
#>
function Invoke-AgentVerify {
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][string]$StateDir,
        [int]$WaitSeconds = 30
    )

    Write-Host '==> 自检'

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host "[!!] 服务 $ServiceName 不存在。" -ForegroundColor Red
        return 2
    }

    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline) {
        $svc.Refresh()
        if ($svc.Status -eq 'Running') { break }
        Start-Sleep -Milliseconds 800
    }

    if ($svc.Status -ne 'Running') {
        Write-Host "[!!] 服务 $ServiceName 未进入 Running（当前：$($svc.Status)）。" -ForegroundColor Red
        Write-Host '     排查：事件查看器 → Windows 日志 → 应用程序；或客户端日志目录：'
        Write-Host "       $(Join-Path $StateDir 'logs')"
        return 2
    }
    Write-Host "    [OK] 服务 $ServiceName 正在运行"

    $statusFile = Join-Path $StateDir 'status.json'
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $snapshot = $null

    while ((Get-Date) -lt $deadline) {
        if (Test-Path $statusFile) {
            try {
                $snapshot = (Get-Content -Path $statusFile -Raw -Encoding UTF8) | ConvertFrom-Json
                if ($snapshot) { break }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 800
    }

    if (-not $snapshot) {
        Write-Warning "    客户端尚未写出状态文件（$statusFile）。"
        Write-Warning '    这不代表装错了 —— 多半是还没连上中心服务端（服务端没开 / 广播被禁 / 尚未注册）。'
        return 0
    }

    if ($snapshot.Connected) {
        Write-Host "    [OK] 客户端已连上中心服务端：$($snapshot.ServerUrl)"
    }
    else {
        Write-Warning "    客户端暂未连上中心服务端（状态：$($snapshot.Status)）。"
        if ($snapshot.LastError) { Write-Warning "    最近一次错误：$($snapshot.LastError)" }
    }

    return 0
}

# ═══════════════════════════════════════════════════════════════════════════
# 主体
# ═══════════════════════════════════════════════════════════════════════════

$sourceServiceExe = Join-Path $Source $serviceExeName

if (-not $SkipCopy -and -not (Test-Path $sourceServiceExe)) {
    Write-Host ''
    Write-Host "[!!] 未找到发布产物：$sourceServiceExe" -ForegroundColor Red
    Write-Host '  请先执行 deploy\windows\build-agent.ps1'
    exit 1
}

# 可选 transcript（图形安装包用：失败时留下证据）
$transcriptStarted = $false
if ($LogFile) {
    try {
        $logDir = Split-Path -Parent $LogFile
        if ($logDir -and -not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        Start-Transcript -Path $LogFile -Append -Force | Out-Null
        $transcriptStarted = $true
    }
    catch {
        Write-Warning "无法写入日志文件 $LogFile（$($_.Exception.Message)），改用控制台输出。"
    }
}

try {
    # ── 1/6 复制程序 ─────────────────────────────────────────────
    if ($SkipCopy) {
        Write-Host '==> 1/6 跳过复制（-SkipCopy：程序已由安装包放好）'
    }
    else {
        Write-Host "==> 1/6 复制程序到 $InstallDir"
        if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Copy-Item (Join-Path $Source '*') $InstallDir -Recurse -Force
    }

    $serviceExe = Join-Path $InstallDir $serviceExeName
    if (-not (Test-Path $serviceExe)) {
        throw "安装目录里没有客户端服务 exe：$serviceExe"
    }

    # ── 2/6 状态目录 + 配置 ──────────────────────────────────────
    Write-Host '==> 2/6 准备状态目录与配置'
    foreach ($sub in @('', 'logs')) {
        New-Item -ItemType Directory -Path (Join-Path $StateDir $sub) -Force | Out-Null
    }
    Write-Host "    状态目录：$StateDir"

    $configPath = Join-Path $InstallDir 'appsettings.json'

    # 🔥 温度开关（2026-10-14 起默认开）：只有显式 -NoThermal 才关。
    $thermal = -not $NoThermal.IsPresent

    if ($SkipConfig) {
        Write-Host '    已跳过配置写入（-SkipConfig：保留现场已有的 appsettings.json）'
    }
    else {
        Write-AgentConfig -Path $configPath -ServiceName $ServiceName -StateDir $StateDir `
            -Server $Server -DiscoveryPort $DiscoveryPort `
            -ThermalEnabled $thermal
        Protect-AgentConfig -Path $configPath
        Write-Host "    配置已写入：$configPath"
    }

    if ($thermal) {
        Write-Host '    硬件温度监控：已启用（默认）—— 客户端启动时会尝试加载温度驱动；加载失败不影响其它功能'
    }
    else {
        Write-Host '    硬件温度监控：未启用（-NoThermal）—— 不会加载任何驱动，页面显示「未启用」'
    }

    # ── 3/6 注册服务 ─────────────────────────────────────────────
    Write-Host '==> 3/6 注册 Windows 服务'
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host '    服务已存在，先停止并删除（会等到它真的从 SCM 消失）'
        [void](Remove-ServiceIfPresent -ServiceName $ServiceName -TimeoutSec 30)
        Start-Sleep -Milliseconds 800
    }

    # 服务名与状态目录同时走命令行：这两个值**服务侧与托盘侧必须一致**，
    # 放进 binPath 可以完全摆脱"配置文件读没读到"的疑问（它们不是凭据）。
    $svcBinPath = "`"$serviceExe`" --Agent:ServiceName=`"$ServiceName`" --Agent:StateDirectory=`"$StateDir`""

    # ⚠️⚠️ 必须用 New-Service（cmdlet）。理由见 install-service.ps1 的实测记录：
    #     PowerShell 5.1 给原生程序传参时会吞掉内层引号，装到含空格路径时
    #     sc.exe create 的 binPath 会被截断 ⇒ 1639 ERROR_INVALID_COMMAND_LINE。
    $created = $false
    $createError = ''

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            New-Service -Name $ServiceName `
                -BinaryPathName $svcBinPath `
                -DisplayName $DisplayName `
                -Description '客户端Server —— 被发现、注册、接收指令（局域网内）' `
                -StartupType Automatic -ErrorAction Stop | Out-Null
        }
        catch {
            $createError = $_.Exception.Message
            Write-Warning ('    服务注册失败（第 ' + $attempt + ' 次）：' + $createError)
        }

        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { $created = $true; break }

        if ($attempt -lt 3) {
            Write-Host '    先清一次残留服务，再重试…'
            [void](Remove-ServiceIfPresent -ServiceName $ServiceName -TimeoutSec 20)
            Start-Sleep -Seconds 2
        }
    }

    if (-not $created) {
        $hint = ''
        if ($createError -match '1072|标记为删除|marked for deletion') {
            $hint = ' 服务停在「待删除」、句柄未释放 —— **重启电脑**再装即可解决。'
        }
        elseif ($createError -match '1073|已存在|already exists') {
            $hint = ' 同名服务已存在却停不掉 —— 先在 services.msc 里删掉它。'
        }
        elseif ($createError -match '拒绝访问|denied|Denied') {
            $hint = ' 拒绝访问 —— 请确认是以管理员身份运行的。'
        }
        throw ('服务注册失败：连续 3 次未生效。最后一次错误：' + $createError + '。' + $hint)
    }
    Write-Host ('    [OK] 服务已注册：' + $ServiceName + '（显示名：' + $DisplayName + '）')

    # 回读 ImagePath，确认引号真的落进去了（SCM 靠它切分"路径 / 参数"）。
    $svcKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $ServiceName
    $imgPath = (Get-ItemProperty -Path $svcKey -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
    if ($imgPath -and ($imgPath -notmatch '^\s*"')) {
        Set-ItemProperty -Path $svcKey -Name ImagePath -Value $svcBinPath -ErrorAction SilentlyContinue
        Write-Warning ('    ImagePath 原为无引号形式（' + $imgPath + '），已自动修正为带引号形式。')
    }
    elseif ($imgPath) {
        Write-Host ('    [OK] ImagePath 校验通过：' + $imgPath)
    }

    # 崩溃后 5s / 10s / 30s 依次重启，一天后重置计数。
    # 客户端是常驻进程，"挂掉之后没人管"是最不该发生的事。
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 2>&1 |
        ForEach-Object { if ("$_".Trim()) { Write-Host ('      ' + "$_") } }

    # ── 4/6 授权交互用户启停 + 启动服务 ──────────────────────────
    Write-Host '==> 4/6 授权交互用户启停服务并启动'
    Grant-InteractiveServiceControl -ServiceName $ServiceName

    if ($NoStart) {
        Write-Host '    已按 -NoStart 跳过启动。'
    }
    else {
        try {
            Start-Service -Name $ServiceName -ErrorAction Stop
            Write-Host "    [OK] 服务已启动：$ServiceName"
        }
        catch {
            Write-Warning "    服务启动失败：$($_.Exception.Message)"
            Write-Warning '    服务已注册，可稍后用 services.msc 或 `sc start 手动启动。'
        }
    }

    # ── 5/6 入站防火墙：UDP 指令通道（M4.5）───────────────────────
    #
    # ⚠️ 这一步**必须放在"复制程序"之后**：规则是按 -Program 指向 exe 授权的，
    #    路径不存在时 New-NetFirewallRule 会直接报错。
    # ⚠️ 规则无条件创建（不看中心的配置）：通道启不启用是**中心下发**的、装的时候还不知道；
    #    而一条指向我们 exe 的 UDP 放行规则，在通道关闭时没有任何作用。
    if ($SkipFirewallRule) {
        Write-Warning '==> 5/6 已按 -SkipFirewallRule 跳过 UDP 指令通道入站规则'
        Write-Warning '     → 该通道在开了防火墙的机器上将收不到指令包（其余功能不受影响）。'
    }
    else {
        Write-Host '==> 5/6 放行「UDP 指令通道」入站规则'
        Add-AgentUdpFirewallRule -ServiceExePath $serviceExe
    }

    # ── 6/6 托盘伴侣 ─────────────────────────────────────────────
    if ($SkipTray) {
        Write-Host '==> 6/6 跳过托盘伴侣（-SkipTray）'
        Write-Host '    ⚠️ 托盘是会话侧唯一的执行者：跳过它 ⇒「音量 / 静音」与「延时启动程序」无处执行。'
        Write-Host '       客户端会一直等用户会话（并在日志里周期性提醒），而不是静默失败。'
    }
    else {
        Install-AgentTray -InstallDir $InstallDir -ServiceName $ServiceName -StateDir $StateDir `
            -TrayStartupName $TrayStartupName -SkipAutostart:$SkipTrayAutostart -SkipLaunch:$SkipTrayLaunch
    }

    # ── 自检 ─────────────────────────────────────────────────────
    $verifyCode = 0
    if ($Verify) {
        $verifyCode = Invoke-AgentVerify -ServiceName $ServiceName -StateDir $StateDir
    }

    Write-Host ''
    Write-Host '── 说明 ────────────────────────────────────────'
    Write-Host '  客户端会在下次广播发现到中心服务端时自动注册并出现在网页「客户端」页。'
    Write-Host '  ⚠️ **不需要任何注册令牌 / 加入码**（2026-10-13 起）—— 装完即上线。'
    Write-Host '     装完几分钟还没出现时，按顺序看两处：'
    Write-Host '       1) 这台机器与中心服务端是否在**同一个网段**（跨网段 / 广播被禁时须用 -Server 指定地址）；'
    Write-Host "       2) 本机客户端日志：$StateDir\logs"
    Write-Host '  ⚠️ 不要手工去改 appsettings.json —— 那个文件已收紧为仅 SYSTEM/管理员可读。'
    Write-Host '  · 硬件温度监控**默认已启用**（2026-10-14 起）：客户端会尝试加载一个内核驱动；'
    Write-Host '    拿不到温度时页面会如实显示「未启用」/「驱动加载失败」（绝不含糊成 0 ℃）。'
    Write-Host '    不想要这个副作用（例如安全软件反复弹窗）时重跑本脚本并加 -NoThermal 关掉。'
    Write-Host '  · 「UDP 指令通道」（远程关机 / 重启 / 音量）需要一条入站放行规则，本脚本已按**程序**建好。'
    Write-Host '    端口由中心下发（默认 8090），换端口不需要重跑本脚本。'
    Write-Host '    现场有统一防火墙策略（组策略 / 安全软件）时用 -SkipFirewallRule 跳过，由策略侧放行。'
    Write-Host '  · 「音量 / 静音」与「延时启动程序」由**用户会话里的托盘**执行（服务在 Session 0 里做这两件事，'
    Write-Host '    用户既听不到也看不见）。所以它们要等用户登录、托盘起来之后才生效 —— 这是正常的，不是故障。'
    Write-Host ''
    Write-Host '完成。'

    if ($transcriptStarted) { Stop-Transcript | Out-Null }
    exit $verifyCode
}
catch {
    Write-Host ''
    Write-Host "[!!] 客户端Server 安装失败：$($_.Exception.Message)" -ForegroundColor Red
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
    exit 1
}
