#Requires -RunAsAdministrator
<#
.SYNOPSIS
    中控服务版 —— Windows 服务安装脚本（含托盘助手）。

.DESCRIPTION
    把已发布的服务程序注册为 Windows 服务，并完成：
      1) 复制程序到安装目录（Server + 托盘助手）
      2) 预建数据目录并授权
      3) 注册服务 + 配置"崩溃自动重启"恢复策略（进程自愈）
      4) 授权交互用户启停服务（sc sdset）—— 托盘助手靠它才能停服务
      5) 放行防火墙端口
      6) 启动服务 + 安装托盘助手（HKCU 启动项）

    为什么需要第 4 步：
      Windows 服务默认 DACL 只给"交互用户"(IU) 查询权限，**不给** SERVICE_START /
      SERVICE_STOP。托盘助手运行在用户会话（非管理员），所以 admin 直接通过
      services.msc 停服务没问题，但普通用户点托盘菜单会 Access Denied。
      这里把 RP(SERVICE_START) + WP(SERVICE_STOP) 补进 IU 的 ACE。

    为什么托盘助手是**独立进程**：
      服务跑在 Session 0，那里没有 explorer、没有通知区域 —— 服务进程自己
      **不可能**显示托盘图标。所以托盘放到用户会话，见
      docs/M3-托盘驻留与宿主形态方案.md 方案 D。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-portable-exe.ps1
    powershell -ExecutionPolicy Bypass -File deploy\windows\install-service.ps1
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\..\dist\win-x64-service'),
    [string]$InstallDir = 'C:\Program Files\ControlServer',
    [string]$DataDir = (Join-Path $env:ProgramData 'ControlServer'),
    [string]$ServiceName = 'ControlServer',
    [string]$DisplayName = '中控服务',
    [int]$HttpPort = 8899,
    [int]$PairingPort = 9999,

    # ── 供图形安装包（Inno Setup）复用 ────────────────────────────────
    # 安装包已经把关文件放到 $InstallDir 了，就不该再"清空 + 从 $Source 复制"
    # —— 那会把安装包自己刚铺好的文件连同 $Source 一起删掉。
    [switch]$SkipCopy,
    [string]$LogFile,                # 非空则把全过程写一份 transcript（安装包失败时取证用）
    [switch]$Verify,                 # 收尾自检：服务是否 Running + HTTP 是否响应；不通过返回 2

    # ── 托盘助手（方案 D） ────────────────────────────────────────────
    [switch]$SkipTrayAgent,          # 不部署托盘助手（只有服务）
    [switch]$SkipTrayLaunch,         # 注册启动项，但不立刻拉起
    [string]$TrayStartupName = 'ControlServer.TrayAgent',

    # ── 数据目录权限收紧（M6.4 / 审计清单 #40）────────────────────────
    # 默认收紧：只留 SYSTEM 与 Administrators。传 -HardenDataDir:$false 可关。
    # 收还是不收的取舍见下面 2b/6 那段注释。
    [bool]$HardenDataDir = $true
)

# 见 deploy\windows\build-portable-exe.ps1 的 .NOTES：原生工具的 stderr 警告
# 不该被当成致命错误。这里统一用 try/catch + 显式 exit 收口。
$ErrorActionPreference = 'Continue'

# ═══════════════════════════════════════════════════════════════════════════
# 函数
#
# ⚠️ PowerShell **不支持前向引用**：函数必须定义在调用点之前，否则会
#    CommandNotFoundException（且 Continue 模式下会被静默吞掉，脚本"成功"
#    却少做了事）。所以这里全部前置，主体代码统一放到最下面。
# ═══════════════════════════════════════════════════════════════════════════

<#
.SYNOPSIS
    给 SDDL 权限串补上若干权限码，并按服务权限的规范顺序输出。

.DESCRIPTION
    权限码表（顺序即 Windows 的规范顺序）：
      CC LC SW RP WP DT LO CR RC  —— 服务专用权限
      SD WD WO                   —— 标准权限
    输入不是"干净的 2 字符码串"（例如含 0x0001 这类十六进制写法）时，
    退化为简单拼接 —— 宁可顺序不漂亮，也不能把别人的 DACL 改坏。
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

    # 有无法识别的码就退回拼接，避免打乱未知语义。
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
    给"交互用户"补上 SERVICE_START / SERVICE_STOP，让托盘助手能启停服务。

.DESCRIPTION
    sc create 出来的服务，默认 DACL 是：
      D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)
        (A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)
    映射（服务专用权限的 SDDL 缩写）：
      CC=SERVICE_QUERY_CONFIG  DC=SERVICE_CHANGE_CONFIG  LC=SERVICE_QUERY_STATUS
      SW=SERVICE_ENUMERATE_DEPENDENTS  RP=SERVICE_START  WP=SERVICE_STOP
      DT=SERVICE_PAUSE_CONTINUE  LO=SERVICE_INTERROGATE  CR=SERVICE_USER_DEFINED_CONTROL
      RC=READ_CONTROL  SD=DELETE  WD=WRITE_DAC  WO=WRITE_OWNER
    ⇒ IU 拿到的是 CC LC SW LO CR RC，**正好缺 RP/WP**。补上即可（最小授权：
      只加启停，不给改配置、不给删服务）。
#>
function Grant-InteractiveServiceControl {
    param([Parameter(Mandatory)][string]$ServiceName)

    $current = (sc.exe sdshow $ServiceName 2>$null | Where-Object { $_ -and $_.Trim() } | Select-Object -First 1)
    if (-not $current) {
        Write-Warning '    无法读取当前安全描述符（sc sdshow 无输出），跳过授权。托盘助手可能需要管理员权限才能停服务。'
        return
    }
    $current = $current.Trim()

    # 已经授权过？（IU 的 ACE 里同时有 RP 和 WP）
    if ($current -match '\(A;;[^)]*RP[^)]*;;;IU\)' -and $current -match '\(A;;[^)]*WP[^)]*;;;IU\)') {
        Write-Host '    交互用户已具备启停权限，跳过。'
        return
    }

    if ($current -notmatch ';;;IU\)') {
        Write-Warning "    安全描述符里没有交互用户(IU) ACE（可能是定制过 DACL）。跳过自动授权。"
        Write-Warning "    可手工执行：sc.exe sdset $ServiceName `<完整SDDL`>"
        return
    }

    # 给 IU 的 ACE 追加 RP(启动) + WP(停止)，其余权限保持原样。
    # 注意：不是简单字符串拼接 —— 服务权限码有规范顺序（CC LC SW RP WP DT LO CR RC），
    # 拼在后面会得到 "...LOCRRCRPWP" 这种非规范串。这里按规范表重建，避免 sc sdset
    # 对顺序挑剔时踩坑。
    $updated = [regex]::Replace(
        $current,
        '\(A;;([^;]*);;;IU\)',
        { param($m) '(A;;' + (Add-ServiceRights -Rights $m.Groups[1].Value -Add @('RP', 'WP')) + ';;;IU)' })

    if ($updated -eq $current) {
        Write-Warning '    未能改写 IU 的 ACE，跳过自动授权。'
        return
    }

    sc.exe sdset $ServiceName $updated 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host '    已授予交互用户「启动 / 停止」服务权限。'
        return
    }

    # 带 S:(SACL) 段时 sdset 需要 SeSecurityPrivilege；失败就退化成"只设 DACL"。
    $daclOnly = ($updated -split 'S:')[0]
    sc.exe sdset $ServiceName $daclOnly 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host '    已授予交互用户「启动 / 停止」服务权限（仅设 DACL）。'
    }
    else {
        Write-Warning "    sc sdset 失败（退出码 $LASTEXITCODE）。托盘助手停服务时可能报「拒绝访问」。"
    }
}

<#
.SYNOPSIS
    纯 socket 探测端口是否有人监听。
.DESCRIPTION
    刻意不用 Test-NetConnection / Invoke-WebRequest —— 这两个都会受系统代理
    与 WinHTTP 配置影响。这里只做一次 TCP 连接，判据最干净。
#>
function Test-TcpPort {
    param(
        [Parameter(Mandatory)][string]$TargetHost,
        [Parameter(Mandatory)][int]$Port,
        [int]$TimeoutMs = 1500
    )
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.BeginConnect($TargetHost, $Port, $null, $null)
        if (-not $task.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) { return $false }
        $client.EndConnect($task)
        return $true
    }
    catch { return $false }
    finally { try { $client.Close() } catch { } }
}

<#
.SYNOPSIS
    HTTP 探活 —— **显式直连**（Proxy = $null）。
.DESCRIPTION
    ⚠️ 为什么不用 Invoke-WebRequest：
      它默认使用系统代理（WinINET /「Internet 选项」里的设置）。目标机上一旦装了
      Clash、v2ray、公司上网代理之类，访问 http://127.0.0.1:8899 会被代理接管，
      返回 502 / 无响应 —— 服务明明是好的，自检却报失败，安装包于是弹窗。
      本机 Git Bash 的 curl 就踩过同一个坑（必须加 --noproxy '*'）。

    这里用 HttpWebRequest 并把 Proxy 显式置空，绕开一切代理，直接连本地端口。
    拿到任何 HTTP 状态码（含 4xx/5xx）都算"服务在应答"。
#>
function Invoke-HttpProbeNoProxy {
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$TimeoutMs = 3000
    )
    $req = $null
    $resp = $null
    try {
        $req = [System.Net.HttpWebRequest]::Create($Url)
        $req.Proxy = $null
        $req.Timeout = $TimeoutMs
        $req.ReadWriteTimeout = $TimeoutMs
        $req.AllowAutoRedirect = $true
        $req.KeepAlive = $false
        $req.Method = 'GET'
        $resp = $req.GetResponse()
        return [int]$resp.StatusCode
    }
    catch [System.Net.WebException] {
        if ($_.Exception.Response) {
            try { return [int]$_.Exception.Response.StatusCode } catch { return $null }
        }
        return $null
    }
    catch { return $null }
    finally { if ($resp) { try { $resp.Close() } catch { } } }
}

<#
.SYNOPSIS
    自检失败时收集现场证据，写成一个文件方便发回排查。
.DESCRIPTION
    输出：服务状态、端口占用、数据目录内容、服务端日志尾部、系统事件日志报错。
    图形安装包场景下 transcript 已经在写，这里额外留一份**独立**的证据文件，
    路径直接打在弹窗/控制台里，省得来回问"日志在哪"。
#>
function Write-InstallDiagnostics {
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][string]$DataDir,
        [Parameter(Mandatory)][string]$InstallDir,
        [Parameter(Mandatory)][int]$HttpPort,
        [Parameter(Mandatory)][int]$PairingPort
    )

    $diagPath = Join-Path $DataDir 'logs\install-diagnostics.log'
    try {
        $dir = Split-Path -Parent $diagPath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }
    catch { return '' }

    $lines = @()
    $lines += '═══════════════════════════════════════════════════════════'
    $lines += ' 中控服务 —— 安装自检失败诊断'
    $lines += (' 时间：' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '   机器：' + $env:COMPUTERNAME)
    $lines += '═══════════════════════════════════════════════════════════'
    $lines += ''
    $lines += '[1] 当前身份与提权状态'
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $lines += ('    用户：' + $id.Name)
        $pr = New-Object Security.Principal.WindowsPrincipal($id)
        $lines += ('    管理员：' + $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    }
    catch { $lines += ('    (读取失败: ' + $_.Exception.Message + ')') }
    $lines += ''

    $lines += '[2] 服务状态'
    try {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc) { $lines += ('    存在，Status=' + $svc.Status + ' StartType=' + $svc.StartType) }
        else { $lines += '    ** 服务未注册 **' }
    }
    catch { $lines += ('    查询失败: ' + $_.Exception.Message) }
    try {
        $wmi = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
        if ($wmi) {
            $lines += ('    PathName : ' + $wmi.PathName)
            $lines += ('    StartMode: ' + $wmi.StartMode + '   State: ' + $wmi.State)
            $lines += ('    ExitCode : ' + $wmi.ExitCode)
        }
    }
    catch { }
    $lines += ''

    $lines += '[3] 端口占用情况'
    try {
        $net = (netstat.exe -ano -p tcp 2>$null) | Where-Object { $_ -match ":$HttpPort\s" }
        if ($net) { $lines += ($net | ForEach-Object { '    ' + $_.Trim() }) }
        else { $lines += "    没有任何进程在监听 TCP $HttpPort" }
    }
    catch { $lines += ('    netstat 失败: ' + $_.Exception.Message) }
    $lines += ''

    $lines += '[4] 安装目录'
    try {
        if (Test-Path $InstallDir) {
            $lines += ((Get-ChildItem $InstallDir -Recurse -File -ErrorAction SilentlyContinue |
                ForEach-Object { '    ' + $_.FullName.Replace($InstallDir, '') + '  ' + [math]::Round($_.Length / 1MB, 2) + ' MB' }))
        }
        else { $lines += ('    ** 目录不存在: ' + $InstallDir + ' **') }
    }
    catch { }
    $lines += ''

    $lines += '[5] 数据目录'
    try {
        if (Test-Path $DataDir) {
            $lines += ((Get-ChildItem $DataDir -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -ne '.log' -and $_.Extension -ne '.ndjson' } |
                ForEach-Object { '    ' + $_.FullName.Replace($DataDir, '') }))
        }
        else { $lines += ('    ** 目录不存在: ' + $DataDir + ' **') }
    }
    catch { }
    $lines += ''

    $lines += '[6] 服务端日志尾部（各取最后 40 行）'
    try {
        $logDir = Join-Path $DataDir 'logs'
        $serverLogs = @(Get-ChildItem $logDir -Filter 'server-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 2)
        if ($serverLogs.Count -eq 0) { $lines += '    (还没有任何服务端日志 —— 服务很可能压根没能启动到写日志这一步)' }
        foreach ($lf in $serverLogs) {
            $lines += ('    ---- ' + $lf.Name + ' ----')
            try {
                $lines += ((Get-Content -Path $lf.FullName -Tail 40 -ErrorAction SilentlyContinue) |
                    ForEach-Object { '    ' + $_ })
            }
            catch { $lines += '    (读取失败)' }
        }
    }
    catch { }
    $lines += ''

    $lines += '[7] 系统事件日志 —— 最近的错误（Application + System，各 15 条）'
    foreach ($logName in @('Application', 'System')) {
        $lines += ('    ---- ' + $logName + ' ----')
        try {
            $evts = Get-EventLog -LogName $logName -EntryType Error -Newest 15 -ErrorAction SilentlyContinue
            if ($evts) {
                $lines += ($evts | ForEach-Object {
                    '    [' + $_.TimeGenerated.ToString('MM-dd HH:mm:ss') + '] ' +
                    $_.Source + ' (ID ' + $_.EventID + '): ' +
                    (($_.Message -split "`r?`n")[0])
                })
            }
            else { $lines += '    (无)' }
        }
        catch { $lines += ('    (读取失败: ' + $_.Exception.Message + ')') }
    }
    $lines += ''
    $lines += '═══════════════════════════════════════════════════════════'

    try {
        Set-Content -Path $diagPath -Value $lines -Encoding UTF8
        return $diagPath
    }
    catch { return '' }
}

<#
.SYNOPSIS
    停掉并删除已存在的服务，**并等到它真的从 SCM 里消失**才返回。

.DESCRIPTION
    ⚠️⚠️ 这是**重装必踩**的一个坑，且失败形态极具迷惑性：

      Windows 的 `sc delete` 只是**标记删除**（ERROR_SERVICE_MARKED_FOR_DELETE = 1072），
      真正的清除是**异步**的 —— 要等该服务的所有句柄都关闭才完成。与此同时：

        · 紧接着调 `sc create` ⇒ 撞上「指定的服务已标记为删除」1072，创建失败；
        · 而此时 `Get-Service` **也可能查不到它** ⇒ 于是"回查 SCM"的保护逻辑
          把它误判成"注册失败"，整个安装报 exit 1；
        · 更糟的是，连着报"服务不存在"，让人以为是权限问题，往完全错误的方向查。

    所以这里**不能**用 `sc stop` + 睡 2 秒 + `sc delete` + 睡 1 秒 这种估算式等待，
    必须**轮询到真的没了**再返回。本函数就是干这个的。

.OUTPUTS
    $true  = 服务已确认为不存在（或本来就不存在）
    $false = 超时仍未消失（调用方应据此给出明确提示，而不是硬着头皮往下走）
#>
function Remove-ServiceIfPresent {
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [int]$TimeoutSec = 30
    )

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return $true }   # 本来就没有，直接成功

    $deadline = (Get-Date).AddSeconds($TimeoutSec)

    # ── 1) 停服务，并等它真的到 Stopped ──────────────────────────
    Write-Host ('    停止服务 ' + $ServiceName + ' …')
    sc.exe stop $ServiceName 2>&1 | ForEach-Object { if ("$_".Trim()) { Write-Host ('      ' + "$_") } }

    while ((Get-Date) -lt $deadline) {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $svc) { return $true }
        if ($svc.Status -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 500
    }

    # ── 2) 删服务，并轮询到 SCM 里查不到为止 ─────────────────────
    Write-Host ('    删除服务 ' + $ServiceName + ' …')
    sc.exe delete $ServiceName 2>&1 | ForEach-Object { if ("$_".Trim()) { Write-Host ('      ' + "$_") } }

    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 500
    }

    Write-Warning ('    服务 ' + $ServiceName + ' 在 ' + $TimeoutSec + ' 秒内仍未完全删除（可能被标记为「待删除」）。')
    return $false
}

<#
.SYNOPSIS
    部署托盘助手：写 HKCU 启动项 + 立刻拉起一个。
.DESCRIPTION
    写 HKCU\...\Run 而不是 HKLM，是因为托盘必须跑在"用户会话"，而 Run 项
    天然是"当前用户登录时启动、非管理员权限"。代价：每个要用托盘的账号
    各装一次（只需管理员装一次服务，托盘按用户装）。
#>
function Install-TrayAgent {
    $trayPath = Join-Path $InstallDir $trayExeName

    if (-not (Test-Path $trayPath)) {
        Write-Warning "    托盘助手不存在：$trayPath（跳过）"
        return
    }

    Write-Host '==> 6b  安装托盘助手（HKCU 启动项 + 立即启动）'

    # ⚠️ HKCU 是"当前登录用户"的注册表；若脚本是用**另一个管理员账号**提权跑的，
    #    启动项会写到那个账号名下，托盘不会出现在使用者桌面上。
    $consoleUser = $null
    try { $consoleUser = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName } catch { }
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name

    if ($consoleUser -and $me -and ($consoleUser -ne $me)) {
        Write-Warning "    当前账号（$me）不是控制台登录用户（$consoleUser）。"
        Write-Warning "    启动项写进了 $me 的注册表 —— $consoleUser 登录后看不到托盘图标。"
        Write-Warning "    请在 $consoleUser 会话里再跑一次本脚本，或用那个账号执行："
        Write-Warning "      reg add `"HKCU\Software\Microsoft\Windows\CurrentVersion\Run`" /v $TrayStartupName /d `"`"$trayPath`" --service-name $ServiceName --port $HttpPort --data-dir `"$DataDir`"`" /f"
    }

    $command = "`"$trayPath`" --service-name `"$ServiceName`" --port $HttpPort --data-dir `"$DataDir`""
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

    if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    New-ItemProperty -Path $runKey -Name $TrayStartupName -Value $command `
        -PropertyType String -Force | Out-Null
    Write-Host "    启动项已注册：$TrayStartupName"

    if ($SkipTrayLaunch) {
        Write-Host '    已跳过立即启动（-SkipTrayLaunch）。注销/重登后图标会出现。'
        return
    }

    # 立刻拉起一个，省得用户还要重新登录。
    # 注意：此处以**管理员**身份启动，是在当前会话里 —— 图标会出现在通知区域。
    # 下次登录时 HKCU 启动项会以普通权限再启一个，靠"每会话单实例互斥"自动去重。
    try {
        # ⚠️ Start-Process 的 -ArgumentList 是把数组**用空格拼成命令行、不自动加引号**，
        #    所以含空格的值必须自己带引号 —— 否则又被拆成多个参数（与 sc.exe 那个坑同源）。
        Start-Process -FilePath $trayPath -ArgumentList @(
            '--service-name', $ServiceName, '--port', $HttpPort, '--data-dir', "`"$DataDir`""
        ) -ErrorAction Stop | Out-Null
        Write-Host '    托盘助手已启动（右下角通知区域）。'
    }
    catch {
        Write-Warning "    托盘助手启动失败：$($_.Exception.Message)"
        Write-Warning '    注销/重登后再试，或手工双击：'
        Write-Warning "      $trayPath"
    }
}

# ═══════════════════════════════════════════════════════════════════════════
# 主体
# ═══════════════════════════════════════════════════════════════════════════

$serverExe = Join-Path $Source 'ControlServer.Server.exe'
$trayExeName = 'ControlServer.TrayAgent.exe'
$sourceTrayExe = Join-Path $Source $trayExeName

if (-not (Test-Path $serverExe)) {
    Write-Host ''
    Write-Host "[!!] 未找到发布产物：$serverExe" -ForegroundColor Red
    Write-Host '  请先执行 deploy\windows\build-portable-exe.ps1'
    exit 1
}

if (-not $SkipTrayAgent -and -not (Test-Path $sourceTrayExe)) {
    Write-Warning "未找到托盘助手产物：$sourceTrayExe"
    Write-Warning '  服务将**没有**托盘图标，只能用 services.msc / sc stop 停服务。'
    Write-Warning '  若要托盘：重跑 build-portable-exe.ps1（它会一并发布 TrayAgent），或用 -SkipTrayAgent 显式跳过。'
    $SkipTrayAgent = $true
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

    # ── 2/6 预建数据目录 ─────────────────────────────────────────
    Write-Host "==> 2/6 预建数据目录 $DataDir"
    foreach ($sub in @('', 'config', 'logs', 'files', 'images')) {
        New-Item -ItemType Directory -Path (Join-Path $DataDir $sub) -Force | Out-Null
    }

    # ── 2b/6 收紧数据目录权限 ────────────────────────────────────
    #
    # 🔴 为什么必须先做这一步（审计清单 #40，M6.4 实测）：
    #    %ProgramData% 给子目录继承下来的是
    #        BUILTIN\Users:(I)(OI)(CI)(RX)          → 本机**任何**普通用户可读
    #        BUILTIN\Users:(I)(CI)(WD,AD,WEA,WA)    → 还能在子目录里创建文件/目录
    #    也就是说 https\ca.pfx（私钥）与 control.db（口令哈希）对普通用户是可读的。
    #    第二条（可创建）比审计清单原来写的更严重：不是只读，是能往里塞东西。
    #
    # 🔴 为什么必须"目录还空着的时候收紧"：
    #    icacls 先切断父目录的继承、再去打开子对象时，会被刚设上去的 ACL 挡住
    #    （实测：icacls /inheritance:r /grant:r ... /T 在有子对象时报"拒绝访问"、退出码 5）。
    #    在空目录上做，之后写进来的东西会自动继承。
    #
    # 🔴 收紧之后，"未提升"的管理员也会被挡在外面（实测：UAC 中等完整性下 Get-ChildItem
    #    就报拒绝访问）。所以 --backup / --restore / --dbinfo 与 deploy\backup\*.ps1
    #    从此都**必须以管理员身份运行**。服务本身以 LocalSystem 跑，不受影响。
    if ($HardenDataDir) {
        Write-Host '    收紧数据目录权限（只留 SYSTEM 与 Administrators）'
        $icaclsArgs = @(
            $DataDir,
            '/inheritance:r',
            '/grant:r', '*S-1-5-18:(OI)(CI)(F)',
            '/grant:r', '*S-1-5-32-544:(OI)(CI)(F)'
        )
        $icaclsOut = & icacls @icaclsArgs 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host '      已完成：普通用户读不到私钥与库，服务（LocalSystem）不受影响'
        }
        else {
            # ⚠️ 收紧失败**不中止安装**：装不上服务比"权限没收紧"严重得多。
            #    但必须喊出来，而且给出可手工执行的命令 —— 静默失败等于假安全。
            Write-Warning ("      收紧失败（退出码 {0}）—— 数据目录仍然对所有本机用户可读。" -f $LASTEXITCODE)
            $icaclsOut | ForEach-Object { if ("$_".Trim()) { Write-Warning ('      ' + $_) } }
            Write-Warning ('      手工执行：icacls "{0}" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)(F)" /grant:r "*S-1-5-32-544:(OI)(CI)(F)"' -f $DataDir)
        }
    }
    else {
        Write-Warning '    已跳过权限收紧（-HardenDataDir:$false）。数据目录对所有本机用户可读。'
    }

    # ── 3/6 注册服务 + 恢复策略 ──────────────────────────────────
    Write-Host '==> 3/6 注册服务并配置恢复策略（进程自愈）'
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host '    服务已存在，先停止并删除（会等到它真的从 SCM 消失）'
        # 删不干净也**不在这里中止** —— 让下面 sc create 的真实报错说话，比在这儿瞎猜强。
        [void](Remove-ServiceIfPresent -ServiceName $ServiceName -TimeoutSec 30)
        Start-Sleep -Milliseconds 800
    }

    $binPath = Join-Path $InstallDir 'ControlServer.Server.exe'

    # 服务跑在 Session 0 —— 那里没有桌面，「自动打开浏览器」只会白拉一个进程。
    # 打开管理界面由托盘助手（用户会话）或安装包负责。
    $svcBinPath = "`"$binPath`" --Server:Startup:AutoOpenBrowser=false"

    # ⚠️⚠️ 注册服务**必须**用 New-Service（cmdlet），绝不能用 `sc.exe create`。
    #
    #   实测（2026-10-08，本机 DESKTOP-9VFHRMA，装到 C:\Program Files\ControlServer）：
    #   PowerShell 5.1 往**原生程序**传参时，会把参数里的**内层引号整个吞掉**，只在
    #   空格处断成新的 argv。用探针抓到的真实 argv 是：
    #       sc.exe create X binPath= "C:\Program Files\...\ControlServer.Server.exe" --flag
    #       → ['create','X','binPath=','C:\Program','Files\...\ControlServer.Server.exe --flag','start=','auto',...]
    #   `binPath=` 只拿到 "C:\Program" ⇒ sc.exe 参数解析失败 ⇒ 打印帮助 + 退出码
    #   **1639 (ERROR_INVALID_COMMAND_LINE)**，服务根本建不起来。
    #   装到**不含空格**的目录（如 D:\ControlServer）时反而侥幸能过 —— 这就是它长期
    #   没被发现的原因，也是为什么这个坑只会在默认路径上现形。
    #
    #   对照证据（同一台机器，同一个含空格字符串）：
    #       cmdlet 参数绑定 → 96 字符完整到达，引号保留  ✅
    #       原生程序 argv   → 空格处被拆开，引号丢失      ❌
    #   cmdlet 走的是 .NET 参数绑定，不是命令行字符串拼接，所以整串（含空格和引号）
    #   会原样写进注册表 ImagePath。
    $created = $false
    $createError = ''
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            New-Service -Name $ServiceName `
                -BinaryPathName $svcBinPath `
                -DisplayName $DisplayName `
                -Description '中控服务版 —— 局域网中控 Web 服务' `
                -StartupType Automatic -ErrorAction Stop | Out-Null
        }
        catch {
            $createError = $_.Exception.Message
            Write-Warning ('    服务注册失败（第 ' + $attempt + ' 次）：' + $createError)
        }

        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { $created = $true; break }

        if ($attempt -lt 3) {
            # 绝大多数"重试就好了"的情形，根子都是上一次删除还没完成 ⇒ 再删一次 + 等一会儿
            Write-Host '    先清一次残留服务，再重试…'
            [void](Remove-ServiceIfPresent -ServiceName $ServiceName -TimeoutSec 20)
            Start-Sleep -Seconds 2
        }
    }

    if (-not $created) {
        # 把错误翻译成"人话 + 下一步"，别让现场人员对着数字猜。
        $hint = ''
        if ($createError -match '1072|标记为删除|marked for deletion') {
            $hint = ' 该状态表示服务停在「待删除」、句柄尚未释放 —— **重启电脑**再装即可解决。'
        }
        elseif ($createError -match '1073|已存在|already exists') {
            $hint = ' 该状态表示同名服务已存在却停不掉 —— 先在 services.msc 里删掉它再装。'
        }
        elseif ($createError -match '拒绝访问|denied|Denied') {
            $hint = ' 该状态表示拒绝访问 —— 请确认是以管理员身份运行安装包的。'
        }
        # ⚠️ 续行的 `+` 必须留在**上一行末尾** —— 若行尾是完整的字符串字面量，
        #    PowerShell 会认为语句已结束，下一行开头的 `+` 就成了新的语句 ⇒ 语法错误。
        throw ('服务注册失败：连续 3 次未生效。最后一次错误：' + $createError + '。' + $hint)
    }
    Write-Host ('    [OK] 服务已注册：' + $ServiceName + '（显示名：' + $DisplayName + '）')

    # 回读注册表 ImagePath，确认**引号真的落进去了**。
    # SCM 靠这对引号切分"程序路径 / 启动参数"；缺失时它会把
    # C:\Program Files\ControlServer\ControlServer.Server.exe 当成 C:\Program.exe 去找
    # （著名的 unquoted service path 问题），表现为"服务注册成功但一启动就 1053/1067"。
    # 理论上 New-Service 会原样写入，这里只是不让它在静默中失守。
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
    # New-Service 不支持恢复策略，这一步仍需 sc.exe —— 好在它的参数（reset= 86400、
    # restart/5000/...）**不含引号**，不会触发上面那个 argv 拆分问题。
    # 但输出不再静默：失败时这也是线索。
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 2>&1 |
        ForEach-Object { if ("$_".Trim()) { Write-Host ('      ' + "$_") } }

    # ── 4/6 授权交互用户启停服务 ─────────────────────────────────
    Write-Host '==> 4/6 授权交互用户启停服务（sc sdset）'
    Grant-InteractiveServiceControl -ServiceName $ServiceName

    # ── 5/6 防火墙 ───────────────────────────────────────────────
    Write-Host "==> 5/6 放行防火墙端口 TCP/$HttpPort UDP/$PairingPort"
    foreach ($rule in @(
            @{ Name = 'ControlServer HTTP'; Protocol = 'TCP'; Port = $HttpPort },
            @{ Name = 'ControlServer Pairing'; Protocol = 'UDP'; Port = $PairingPort })) {
        try {
            if (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue) {
                Remove-NetFirewallRule -DisplayName $rule.Name
            }
            New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow `
                -Protocol $rule.Protocol -LocalPort $rule.Port | Out-Null
            Write-Host "    [OK] $($rule.Protocol)/$($rule.Port) 已放行"
        }
        catch {
            # ⚠️ 装了 360 / 火绒等接管防火墙的安全软件时，New-NetFirewallRule 会直接抛异常。
            #    放在这里**必须**吞掉：防火墙只是"局域网其它设备能否连进来"的问题，
            #    本机 http://127.0.0.1 照样能开。若不吞，异常会一路冒到最外层 catch，
            #    脚本 exit 1、后面"启动服务"根本不执行 —— 结果就是装完了服务没跑、浏览器打不开。
            Write-Warning "    [--] 防火墙规则 $($rule.Name) 创建失败：$($_.Exception.Message)"
            Write-Warning "        本机访问不受影响；局域网其它设备可能连不上，请手工放行 $($rule.Protocol) $($rule.Port)"
        }
    }

    # ── 6/6 启动服务 + 托盘助手 ──────────────────────────────────
    Write-Host '==> 6/6 启动服务'
    # ⚠️ 不要把 sc start 的输出吞掉 —— 服务起不来时这一行是**最有价值**的证据：
    #    1053 = 服务没有及时响应启动请求（多半卡在初始化，例如建库/迁移）
    #    1067 = 进程意外终止（启动时抛异常直接退出，去数据目录看服务端日志）
    #    1069 = 由于登录失败服务未能启动
    $startOut = sc.exe start $ServiceName 2>&1
    foreach ($l in $startOut) { if ("$l".Trim()) { Write-Host ('    ' + "$l") } }
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "    sc start 退出码 $LASTEXITCODE —— 服务可能没起来，详见上方输出。"
    }
    Start-Sleep -Seconds 2
    Get-Service -Name $ServiceName | Format-List Name, Status, StartType

    # ⚠️ 托盘失败**不该**让整个安装报错：服务此时已经启动好了，没托盘只是
    #    "少个右下角图标、停服务要走 services.msc"而已。而 Install-TrayAgent 里
    #    写 HKCU 启动项（New-ItemProperty）在"提权账号 ≠ 控制台登录账号"、
    #    注册表被组策略锁定等情况下是会抛异常的 —— 一旦冒到最外层 catch，
    #    exit 1，安装包就弹"服务注册或启动自检没有通过"，可服务明明是好的。
    if (-not $SkipTrayAgent) {
        try { Install-TrayAgent }
        catch {
            Write-Warning "    托盘助手部署失败（不影响服务运行）：$($_.Exception.Message)"
            Write-Warning ('    服务已在运行；停止它可用 services.msc 或管理员执行 sc.exe stop ' + $ServiceName)
        }
    }

    # ── 收尾自检（-Verify）：装完就确认"真的能跑" ─────────────────
    #
    # 判据刻意分成两级，避免"服务其实是好的、自检却误报失败"：
    #   硬性 = 服务 Running + TCP 端口真的在监听（纯 socket，不受任何代理影响）
    #   软性 = HTTP 能应答（中间件就绪的额外确认；拿不到只提示，不判失败）
    #
    # 为什么不用 Invoke-WebRequest 当硬判据：它默认走系统代理，目标机上有
    # Clash / 公司代理时会把 127.0.0.1 的请求劫持成 502，导致误报。
    if ($Verify) {
        Write-Host '==> 自检：服务状态 / 端口监听 / HTTP 探活'
        $healthy = $true

        # 1) 服务是否进入 Running。首次启动要建库 + 跑 EF 迁移 + 初始化 admin，
        #    慢机器上十几秒很正常 —— 给足 60 秒（120 x 500ms）。
        $running = $false
        for ($i = 0; $i -lt 120; $i++) {
            $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if ($svc -and $svc.Status -eq 'Running') { $running = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if ($running) { Write-Host '    [OK] 服务状态：Running' }
        else {
            Write-Host '    [!!] 服务未进入 Running 状态（已等待 60 秒）' -ForegroundColor Red
            $healthy = $false
        }

        # 2) 端口是否真的在监听 —— 纯 TCP 连接，绕开一切代理与 WinHTTP 配置。
        $portOpen = $false
        if ($running) {
            for ($i = 0; $i -lt 120; $i++) {
                if (Test-TcpPort -TargetHost '127.0.0.1' -Port $HttpPort) { $portOpen = $true; break }
                Start-Sleep -Milliseconds 500
            }
        }
        if ($portOpen) { Write-Host "    [OK] 端口监听：TCP $HttpPort" }
        else {
            Write-Host "    [!!] 端口未监听：TCP $HttpPort（已等待 60 秒）" -ForegroundColor Red
            $healthy = $false
        }

        # 3) HTTP 应答确认（软性）。显式直连，不走系统代理。
        $url = "http://127.0.0.1:$HttpPort/"
        $httpStatus = $null
        if ($portOpen) {
            for ($i = 0; $i -lt 20; $i++) {
                $httpStatus = Invoke-HttpProbeNoProxy -Url $url
                if ($httpStatus) { break }
                Start-Sleep -Milliseconds 500
            }
        }
        if ($httpStatus) {
            Write-Host "    [OK] HTTP 可访问：$url -> HTTP $httpStatus"
        }
        else {
            Write-Host "    [--] HTTP 探活未拿到响应（端口已通，服务通常仍是好的）" -ForegroundColor Yellow
        }

        if (-not $healthy) {
            Write-Host ''
            Write-Host '自检未通过 —— 正在收集诊断信息…'
            $diagPath = Write-InstallDiagnostics -ServiceName $ServiceName -DataDir $DataDir -InstallDir $InstallDir -HttpPort $HttpPort -PairingPort $PairingPort

            Write-Host ''
            Write-Host '排查方向：'
            Write-Host "  · 端口 $HttpPort 是否被别的程序占用：netstat -ano | findstr :$HttpPort"
            Write-Host "  · 服务端日志：$DataDir\logs"
            Write-Host "  · 前台手动跑一次看报错：& `"$InstallDir\ControlServer.Server.exe`" --Server:Http:Urls=http://127.0.0.1:$HttpPort"
            if ($diagPath) {
                Write-Host ''
                Write-Host "诊断报告已写出：$diagPath"
            }
            exit 2
        }
    }

    Write-Host ''
    Write-Host "完成。浏览器访问：http://<本机IP>:$HttpPort"
    Write-Host '默认账号：admin / admin123（登录后建议尽快自行修改）'
    Write-Host "日志目录：$DataDir\logs"
    if ($SkipTrayAgent) {
        Write-Host ''
        Write-Host "停止服务：services.msc → 中控服务 → 停止；或管理员执行 sc.exe stop $ServiceName"
    }
    else {
        Write-Host '停止服务：右键点击右下角托盘图标 →「停止服务」'
    }
    exit 0
}
catch {
    Write-Host ''
    Write-Host "[!!] 安装失败：$($_.Exception.Message)" -ForegroundColor Red

    # ⚠️ 这条路径先前**不生成诊断报告**，可安装包的弹窗却让人去看 install-diagnostics.log
    #    —— 用户拿到的是一个根本不存在的路径。现在补上：报错也要留下证据。
    Write-Host ''
    Write-Host '正在收集诊断信息…'
    try {
        $diagPath = Write-InstallDiagnostics -ServiceName $ServiceName -DataDir $DataDir -InstallDir $InstallDir -HttpPort $HttpPort -PairingPort $PairingPort
        if ($diagPath) { Write-Host ('诊断报告：' + $diagPath) }
        else { Write-Warning '诊断报告未能生成（数据目录不可写）。' }
    }
    catch { Write-Warning ('诊断信息收集也失败了：' + $_.Exception.Message) }

    exit 1
}
finally {
    if ($transcriptStarted) { try { Stop-Transcript | Out-Null } catch { } }
}
