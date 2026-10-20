#Requires -Version 5.1
<#
.SYNOPSIS
    中控服务 —— 现场诊断信息收集（**只读**，不需要管理员）。

.DESCRIPTION
    装完之后"浏览器打不开"时跑这个，它会把所有相关现场证据打包成一个 txt，
    直接发回即可定位（比截图有用得多）。

        powershell -NoProfile -ExecutionPolicy Bypass -File collect-diagnostics.ps1

    脚本**不修改任何东西** —— 不建服务、不改注册表、不动文件。只是读。

    收集内容：
      1) 环境（OS / PowerShell / 当前账号 / 是否管理员 / 系统代理）
      2) 程序文件与版本
      3) 服务注册状态与 SCM 详情（含 PathName / ExitCode）
      4) 端口监听（socket 直连 + netstat 双证据）
      5) HTTP 探活（显式直连，不受系统代理影响）
      6) 数据目录与数据库
      7) 服务端日志尾部
      8) 系统事件日志中的错误
      9) 防火墙规则
     10) 托盘助手启动项与进程

.OUTPUTS
    报告写到 <DataDir>\logs\diagnostics-<时间戳>.txt，并在控制台打印路径。

.NOTES
    用户可见输出**不用** ✓/✗/⚠ —— 它们不在系统 GBK 代码页里，默认控制台会显示成问号。
    统一用 [OK] / [!!] / [--]。
#>
param(
    [string]$ServiceName = 'ControlServer',
    [int]$HttpPort = 8899,
    [int]$PairingPort = 9999,
    [string]$DataDir = (Join-Path $env:ProgramData 'ControlServer'),
    [string]$InstallDir = 'C:\Program Files\ControlServer',
    [string]$TrayStartupName = 'ControlServer.TrayAgent',
    [string]$OutFile
)

$ErrorActionPreference = 'Continue'

# ═══════════════════════════════════════════════════════════════════════════
# 辅助函数（PowerShell 不支持前向引用 —— 全部定义在主体之前）
# ═══════════════════════════════════════════════════════════════════════════

<#
.SYNOPSIS
    纯 socket 探测端口，不受系统代理影响。
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
    HTTP 探活，显式直连（Proxy = $null）。
.DESCRIPTION
    Invoke-WebRequest 默认走系统代理，目标机上有 Clash / 公司代理时会把
    127.0.0.1 的请求劫持成 502，造成"服务是好的却报错"的误判。
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

# ═══════════════════════════════════════════════════════════════════════════
# 主体
# ═══════════════════════════════════════════════════════════════════════════

$L = New-Object System.Collections.ArrayList
function Add-Line { param([string]$Text = '') [void]$L.Add($Text) }
function Add-Head { param([string]$Text) [void]$L.Add(''); [void]$L.Add('── ' + $Text + ' ' + ('─' * [Math]::Max(0, 58 - $Text.Length))) }

Add-Line '═══════════════════════════════════════════════════════════════'
Add-Line ' 中控服务 —— 现场诊断报告'
Add-Line (' 生成时间：' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Add-Line (' 机器名  ：' + $env:COMPUTERNAME)
Add-Line '═══════════════════════════════════════════════════════════════'

# ── 1. 环境 ──────────────────────────────────────────────────────
Add-Head '1. 环境'
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    Add-Line ('OS          : ' + $os.Caption + ' (Build ' + $os.BuildNumber + ', ' + $os.OSArchitecture + ')')
} catch { Add-Line ('OS          : 读取失败 - ' + $_.Exception.Message) }
Add-Line ('PowerShell  : ' + $PSVersionTable.PSVersion.ToString() + '  (' + $PSVersionTable.PSEdition + ')')
Add-Line ('机器名      : ' + $env:COMPUTERNAME)

$isAdmin = $false
try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    Add-Line ('当前账号    : ' + $id.Name)
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    $isAdmin = $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Add-Line ('管理员权限  : ' + $isAdmin)
} catch { Add-Line ('当前账号    : 读取失败 - ' + $_.Exception.Message) }

try {
    $cu = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName
    Add-Line ('控制台登录  : ' + $(if ($cu) { $cu } else { '(无人登录)' }))
} catch { }

# 系统代理 —— 这能直接解释"HTTP 探活误报"
$proxyText = '(未设置)'
try {
    $p = [System.Net.WebRequest]::DefaultWebProxy
    if ($p) {
        $gp = $p.GetProxy('http://127.0.0.1')
        if ($gp) { $proxyText = $gp.ToString() }
    }
} catch { }
Add-Line ('系统代理    : ' + $proxyText + '   <-- 若非空，旧版自检的 HTTP 探测会被劫持误报')
try {
    $ie = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
    if ($ie) {
        Add-Line ('IE 代理开关 : ProxyEnable=' + $ie.ProxyEnable + '  ProxyServer=' + $ie.ProxyServer + '  ProxyOverride=' + $ie.ProxyOverride)
    }
} catch { }

# ── 2. 程序文件 ──────────────────────────────────────────────────
Add-Head '2. 程序文件'
$serverExe = Join-Path $InstallDir 'ControlServer.Server.exe'
$trayExe = Join-Path $InstallDir 'ControlServer.TrayAgent.exe'
if (Test-Path $serverExe) {
    $fi = Get-Item $serverExe
    Add-Line ('[OK] ' + $serverExe)
    Add-Line ('     大小 ' + [math]::Round($fi.Length / 1MB, 2) + ' MB   修改时间 ' + $fi.LastWriteTime)
    try {
        Add-Line ('     文件版本 ' + $fi.VersionInfo.FileVersion + '   产品版本 ' + $fi.VersionInfo.ProductVersion)
    } catch { }
}
else { Add-Line ('[!!] 缺少 ' + $serverExe) }

if (Test-Path $trayExe) {
    $fi = Get-Item $trayExe
    Add-Line ('[OK] ' + $trayExe + '  (' + [math]::Round($fi.Length / 1MB, 2) + ' MB)')
}
else { Add-Line ('[--] 缺少托盘助手 ' + $trayExe) }

Add-Line ''
Add-Line '安装目录内容：'
try {
    if (Test-Path $InstallDir) {
        Get-ChildItem $InstallDir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            Add-Line ('     ' + $_.FullName.Replace($InstallDir, '.') + '   ' + [math]::Round($_.Length / 1KB, 0) + ' KB')
        }
    }
    else { Add-Line ('     ** 目录不存在：' + $InstallDir + ' **') }
} catch { Add-Line ('     枚举失败：' + $_.Exception.Message) }

# ── 3. 服务 ──────────────────────────────────────────────────────
Add-Head '3. 服务注册与状态'
$svc = $null
try { $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue } catch { }
if ($svc) {
    Add-Line ('[OK] 服务存在  Status=' + $svc.Status + '  StartType=' + $svc.StartType + '  DisplayName=' + $svc.DisplayName)
}
else {
    Add-Line ('[!!] 服务未注册：' + $ServiceName)
}

try {
    $wmi = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($wmi) {
        Add-Line ('     PathName    : ' + $wmi.PathName)
        Add-Line ('     StartMode   : ' + $wmi.StartMode)
        Add-Line ('     State       : ' + $wmi.State)
        Add-Line ('     ExitCode    : ' + $wmi.ExitCode + '  (0=正常)')
        Add-Line ('     ServiceSpecificExitCode : ' + $wmi.ServiceSpecificExitCode)
        Add-Line ('     StartName   : ' + $wmi.StartName)
        Add-Line ('     ProcessId   : ' + $wmi.ProcessId)
    }
} catch { Add-Line ('     Win32_Service 读取失败：' + $_.Exception.Message) }

Add-Line ''
Add-Line 'sc.exe query 原始输出：'
try {
    (sc.exe query $ServiceName 2>&1) | ForEach-Object { if ("$_".Trim()) { Add-Line ('     ' + "$_") } }
} catch { Add-Line ('     失败：' + $_.Exception.Message) }

# ── 4. 端口 ──────────────────────────────────────────────────────
Add-Head ('4. 端口（TCP ' + $HttpPort + ' / UDP ' + $PairingPort + '）')
$tcpOpen = Test-TcpPort -TargetHost '127.0.0.1' -Port $HttpPort
Add-Line ('socket 直连 127.0.0.1:' + $HttpPort + '  -> ' + $(if ($tcpOpen) { '[OK] 通' } else { '[!!] 不通' }))
$udpOpen = Test-TcpPort -TargetHost '127.0.0.1' -Port $PairingPort
Add-Line ('socket 直连 127.0.0.1:' + $PairingPort + '  -> ' + $(if ($udpOpen) { '[OK] 通' } else { '[--] 不通（UDP 端口用 TCP 探测不准，仅供参考）' }))

Add-Line ''
Add-Line 'netstat 原始输出：'
try {
    $hit = (netstat.exe -ano -p tcp 2>$null) | Where-Object { $_ -match "(^|\s|:)$HttpPort\s" }
    if ($hit) { $hit | ForEach-Object { Add-Line ('     ' + $_.Trim()) } }
    else { Add-Line ('     没有进程监听 TCP ' + $HttpPort) }
} catch { Add-Line ('     netstat 失败：' + $_.Exception.Message) }

# ── 5. HTTP ──────────────────────────────────────────────────────
Add-Head '5. HTTP 探活（直连，不走代理）'
$url = "http://127.0.0.1:$HttpPort/"
$code = Invoke-HttpProbeNoProxy -Url $url
if ($code) { Add-Line ('[OK] ' + $url + ' -> HTTP ' + $code) }
else {
    Add-Line ('[!!] ' + $url + ' -> 无响应')
    # 再用本地回环的 IPv6 试一下，排除"只绑了 ::1"或"只绑了 0.0.0.0"之类
    $code6 = Invoke-HttpProbeNoProxy -Url "http://[::1]:$HttpPort/"
    Add-Line ('     IPv6 回环 [::1]:' + $HttpPort + ' -> ' + $(if ($code6) { 'HTTP ' + $code6 } else { '无响应' }))
}

# ── 6. 数据目录 ──────────────────────────────────────────────────
Add-Head '6. 数据目录'
if (Test-Path $DataDir) {
    Add-Line ('[OK] ' + $DataDir)
    try {
        Get-ChildItem $DataDir -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object FullName | ForEach-Object {
                Add-Line ('     ' + $_.FullName.Replace($DataDir, '.') + '   ' + [math]::Round($_.Length / 1KB, 1) + ' KB   ' + $_.LastWriteTime.ToString('MM-dd HH:mm'))
            }
    } catch { Add-Line ('     枚举失败：' + $_.Exception.Message) }
    $db = Join-Path $DataDir 'control.db'
    Add-Line ''
    Add-Line ('数据库       : ' + $(if (Test-Path $db) { '[OK] 存在' } else { '[!!] 不存在 —— 服务没能初始化数据库' }))
}
else { Add-Line ('[!!] 数据目录不存在：' + $DataDir) }

# ── 7. 服务端日志 ────────────────────────────────────────────────
Add-Head '7. 服务端日志尾部'
try {
    $logDir = Join-Path $DataDir 'logs'
    $logs = @(Get-ChildItem $logDir -Filter 'server-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3)
    if ($logs.Count -eq 0) {
        Add-Line '(没有 server-*.log —— 服务很可能压根没启动到写日志这一步)'
    }
    foreach ($lf in $logs) {
        Add-Line ''
        Add-Line ('---- ' + $lf.Name + '  (' + [math]::Round($lf.Length / 1KB, 1) + ' KB, ' + $lf.LastWriteTime + ') ----')
        try {
            (Get-Content -Path $lf.FullName -Tail 60 -ErrorAction SilentlyContinue) | ForEach-Object { Add-Line ('     ' + $_) }
        } catch { Add-Line ('     (读取失败)') }
    }
} catch { Add-Line ('日志读取失败：' + $_.Exception.Message) }

# ── 8. 事件日志 ──────────────────────────────────────────────────
Add-Head '8. 系统事件日志（最近的错误）'
foreach ($logName in @('Application', 'System')) {
    Add-Line ''
    Add-Line ('---- ' + $logName + ' : 最近 20 条 Error ----')
    try {
        $evts = Get-EventLog -LogName $logName -EntryType Error -Newest 20 -ErrorAction SilentlyContinue
        if ($evts) {
            $evts | ForEach-Object {
                Add-Line ('     [' + $_.TimeGenerated.ToString('MM-dd HH:mm:ss') + '] ' + $_.Source + ' (ID ' + $_.EventID + ')')
                try { Add-Line ('          ' + (($_.Message -split "`r?`n")[0])) } catch { }
            }
        }
        else { Add-Line '     (无)' }
    } catch { Add-Line ('     (读取失败：' + $_.Exception.Message + ')') }
}
Add-Line ''
Add-Line '---- 与本服务相关的服务控制管理器日志 ----'
try {
    $scm = Get-EventLog -LogName System -Newest 200 -ErrorAction SilentlyContinue |
        Where-Object { $_.Source -eq 'Service Control Manager' -and ($_.Message -like "*$ServiceName*") } |
        Select-Object -First 15
    if ($scm) {
        $scm | ForEach-Object {
            Add-Line ('     [' + $_.TimeGenerated.ToString('MM-dd HH:mm:ss') + '] ' + $_.EntryType + ' ID=' + $_.EventID)
            try { Add-Line ('          ' + (($_.Message -split "`r?`n")[0])) } catch { }
        }
    }
    else { Add-Line '     (没找到提及该服务的事件)'; Add-Line '     ** 提示：SCM 日志里一条都没有，通常说明服务根本没被尝试启动过 **' }
} catch { Add-Line ('     (读取失败：' + $_.Exception.Message + ')') }

# ── 9. 防火墙 ────────────────────────────────────────────────────
Add-Head '9. 防火墙规则'
foreach ($r in @(
        @{ Name = 'ControlServer HTTP'; Proto = 'TCP'; Port = $HttpPort },
        @{ Name = 'ControlServer Pairing'; Proto = 'UDP'; Port = $PairingPort })) {
    try {
        $fw = Get-NetFirewallRule -DisplayName $r.Name -ErrorAction SilentlyContinue
        if ($fw) {
            Add-Line ('[OK] ' + $r.Name + '  Enabled=' + $fw.Enabled + ' Action=' + $fw.Action + ' Profile=' + $fw.Profile)
        }
        else { Add-Line ('[--] 未找到规则：' + $r.Name + '  (' + $r.Proto + ' ' + $r.Port + ')') }
    } catch {
        Add-Line ('[--] 查询防火墙失败：' + $_.Exception.Message)
        Add-Line '     （装有 360 / 火绒等安全软件时常见，它们会接管防火墙）'
    }
}
try {
    $fwSvc = Get-Service -Name 'MpsSvc' -ErrorAction SilentlyContinue
    Add-Line ('Windows Firewall 服务（MpsSvc）：' + $(if ($fwSvc) { $fwSvc.Status } else { '未找到' }))
    $prof = Get-NetFirewallProfile -ErrorAction SilentlyContinue
    if ($prof) {
        $prof | ForEach-Object { Add-Line ('     Profile ' + $_.Name + ' : Enabled=' + $_.Enabled + ' DefaultInboundAction=' + $_.DefaultInboundAction) }
    }
} catch { }

# ── 10. 托盘 ─────────────────────────────────────────────────────
Add-Head '10. 托盘助手'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
try {
    $val = (Get-ItemProperty -Path $runKey -Name $TrayStartupName -ErrorAction SilentlyContinue).$TrayStartupName
    if ($val) { Add-Line ('[OK] HKCU 启动项已注册：' + $val) }
    else { Add-Line ('[--] HKCU 启动项未注册（当前用户桌面右下角不会有图标）') }
} catch { Add-Line ('     读取启动项失败：' + $_.Exception.Message) }
try {
    $procs = @(Get-Process -Name 'ControlServer.TrayAgent' -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        $procs | ForEach-Object { Add-Line ('[OK] 托盘进程运行中 PID=' + $_.Id) }
    }
    else { Add-Line ('[--] 托盘进程未运行') }
} catch { }
try {
    $svrProcs = @(Get-Process -Name 'ControlServer.Server' -ErrorAction SilentlyContinue)
    if ($svrProcs.Count -gt 0) {
        $svrProcs | ForEach-Object {
            Add-Line ('[OK] 服务进程运行中 PID=' + $_.Id + '  内存 ' + [math]::Round($_.WorkingSet64 / 1MB, 0) + ' MB  启动于 ' + $(try { $_.StartTime } catch { '未知' }))
        }
    }
    else { Add-Line ('[!!] 服务进程未运行（服务不在跑）') }
} catch { }

Add-Line ''
Add-Line '═══════════════════════════════════════════════════════════════'
Add-Line ' 报告结束'
Add-Line '═══════════════════════════════════════════════════════════════'

# ── 落地 ─────────────────────────────────────────────────────────
if (-not $OutFile) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logDir = Join-Path $DataDir 'logs'
    if (-not (Test-Path $logDir)) {
        # 数据目录都没建起来时，退到临时目录，别因为写不出报告而白跑一趟
        try { New-Item -ItemType Directory -Path $logDir -Force | Out-Null } catch { $logDir = $env:TEMP }
    }
    $OutFile = Join-Path $logDir ("diagnostics-$stamp.txt")
}

$written = $false
try {
    Set-Content -Path $OutFile -Value $L -Encoding UTF8
    $written = $true
}
catch {
    try {
        $OutFile = Join-Path $env:TEMP ("diagnostics-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
        Set-Content -Path $OutFile -Value $L -Encoding UTF8
        $written = $true
    }
    catch { }
}

Write-Host ''
if ($written) {
    Write-Host '诊断报告已生成：' -ForegroundColor Green
    Write-Host ('  ' + $OutFile)
    Write-Host ''
    Write-Host '请把这个 txt 文件发回。'
}
else {
    Write-Host '无法写出报告文件，以下为全部内容：' -ForegroundColor Yellow
    Write-Host ''
    $L | ForEach-Object { Write-Host $_ }
}

# 有一个硬性失败项就返回 1，方便脚本化调用
$bad = @($L | Where-Object { $_ -match '^\[!!\]' }).Count
Write-Host ''
Write-Host ('---- 摘要：发现问题 ' + $bad + ' 处 ----')
if ($bad -gt 0) { exit 1 } else { exit 0 }
