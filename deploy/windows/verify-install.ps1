#Requires -Version 5.1
<#
.SYNOPSIS
    中控服务版 —— 装完自检。一条命令回答"这台机器上到底能不能跑"。

.DESCRIPTION
    在**目标机**上运行（只读检查，不需要管理员）：

        powershell -NoProfile -ExecutionPolicy Bypass -File verify-install.ps1

    逐项核对安装包应该做好的 7 件事：
      1) 程序文件在位
      2) Windows 服务已注册、且是"自动启动"
      3) 服务当前 Running
      4) 端口在监听
      5) HTTP 真的能打开（这一条最能说明问题）
      6) 防火墙已放行
      7) 托盘助手已注册开机启动
      8) 数据目录已建立

    退出码：0 = 全部通过；1 = 有硬性失败项。

.NOTES
    用户可见输出**不使用** ✓/✗/⚠ 符号 —— 它们不在系统 GBK 代码页里，
    在默认控制台会显示成问号。统一用 [OK] / [!!] / [--]。
#>
param(
    [string]$ServiceName = 'ControlServer',
    [int]$HttpPort = 8899,
    [int]$PairingPort = 9999,
    [string]$DataDir = (Join-Path $env:ProgramData 'ControlServer'),
    [string]$InstallDir = 'C:\Program Files\ControlServer',
    [string]$TrayStartupName = 'ControlServer.TrayAgent'
)

$ErrorActionPreference = 'Continue'

$script:Pass = 0
$script:Fail = 0
$script:Info = 0

function Report {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Ok,
        [string]$Detail = '',
        [switch]$Soft          # 软性项：不通过只提示，不算失败
    )
    if ($Ok) {
        $script:Pass++
        Write-Host ("  [OK] {0,-22} {1}" -f $Name, $Detail)
    }
    elseif ($Soft) {
        $script:Info++
        Write-Host ("  [--] {0,-22} {1}" -f $Name, $Detail)
    }
    else {
        $script:Fail++
        Write-Host ("  [!!] {0,-22} {1}" -f $Name, $Detail) -ForegroundColor Red
    }
}

<#
.SYNOPSIS
    纯 socket 探测端口是否有人监听（不受系统代理影响）。
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
    ⚠️ Invoke-WebRequest 默认使用系统代理（WinINET /「Internet 选项」）。
    目标机装了 Clash / 公司代理时，访问 http://127.0.0.1:8899 会被代理接管返回
    502 —— 服务明明是好的，这里却报失败。用 HttpWebRequest + Proxy=$null 直连，
    拿到任何状态码（含 4xx/5xx）都算"服务在应答"。
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

# 检查一下本机代理设置 —— 若设了代理，报告里点明，免得下次又被同样的坑绊住。
$sysProxy = $null
try { $sysProxy = [System.Net.WebRequest]::DefaultWebProxy.GetProxy('http://127.0.0.1') } catch { }
if ($sysProxy -and $sysProxy.ToString() -notmatch '127\.0\.0\.1') {
    Write-Host ''
    Write-Host ('注意：本机设置了系统代理 ' + $sysProxy.ToString()) -ForegroundColor Yellow
    Write-Host '      下面的 HTTP 探活已强制直连，不受它影响。' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '中控服务 —— 安装自检'
Write-Host ("机器：{0}   时间：{1}" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Write-Host '────────────────────────────────────────────────────────────'

# ── 1. 程序文件 ─────────────────────────────────────────────────
$serverExe = Join-Path $InstallDir 'ControlServer.Server.exe'
$trayExe = Join-Path $InstallDir 'ControlServer.TrayAgent.exe'
$serverOk = Test-Path $serverExe
Report -Name '程序文件' -Ok $serverOk -Detail $(
    if ($serverOk) { $InstallDir } else { "缺少 $serverExe" }
)
if ($serverOk) {
    $mb = [math]::Round((Get-Item $serverExe).Length / 1MB, 1)
    Report -Name '  Server.exe' -Ok ($mb -gt 10) -Detail "$mb MB" -Soft
}
Report -Name '  托盘助手' -Ok (Test-Path $trayExe) -Detail $(
    if (Test-Path $trayExe) { '存在' } else { '不存在（服务本身仍可用，托盘需手工部署）' }
) -Soft

# ── 2/3. 服务注册与运行状态 ─────────────────────────────────────
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
Report -Name '服务已注册' -Ok ($null -ne $svc) -Detail $(
    if ($svc) { $svc.DisplayName } else { "找不到服务 $ServiceName" }
)

if ($svc) {
    $wmi = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    $startMode = if ($wmi) { $wmi.StartMode } else { $null }
    Report -Name '开机自启' -Ok ($startMode -eq 'Auto') -Detail "StartMode=$startMode"

    $running = ($svc.Status -eq 'Running')
    Report -Name '服务运行中' -Ok $running -Detail "Status=$($svc.Status)"

    if (-not $running) {
        # 注意：Write-Host 后面的字符串拼接必须整体加括号，否则 `+` 会被当成
        # Write-Host 的第二个位置参数，原样打进控制台（踩过一次）。
        Write-Host ('       提示：看日志找原因 -> ' + (Join-Path $DataDir 'logs'))
        Write-Host '       前台手动跑一次看报错：'
        Write-Host "         & `"$serverExe`" --Server:Http:Urls=http://127.0.0.1:$HttpPort"
    }
}

# ── 4. 端口监听（用 socket 直连判定，netstat 只作旁证） ─────────
$portOpen = Test-TcpPort -TargetHost '127.0.0.1' -Port $HttpPort
$listenLine = $null
try {
    $listenLine = (netstat.exe -ano -p tcp 2>$null | Select-String -Pattern ":$HttpPort\s" | Select-Object -First 1)
} catch { }
Report -Name '端口监听' -Ok $portOpen -Detail $(
    if ($portOpen) { "TCP $HttpPort（socket 直连成功）" }
    else { "TCP $HttpPort 无响应$(if ($listenLine) { '（netstat 却看到监听，可能是绑定地址不同）' } else { '' })" }
)

# ── 5. HTTP 真的能打开 ──────────────────────────────────────────
# 判据说明：端口通就已经是很强的"服务活着"证据（硬性项在上面）。
# HTTP 拿不到响应这里只作**提示**，不判失败 —— 代理、回环策略、
# 中间件启动慢都可能让它失败，而服务本身是好的。
$url = "http://127.0.0.1:$HttpPort/"
$httpStatus = $null
if ($portOpen) {
    for ($i = 0; $i -lt 10; $i++) {
        $httpStatus = Invoke-HttpProbeNoProxy -Url $url
        if ($httpStatus) { break }
        Start-Sleep -Milliseconds 500
    }
}
Report -Name 'HTTP 可访问' -Ok ($null -ne $httpStatus) -Soft -Detail $(
    if ($httpStatus) { "$url -> HTTP $httpStatus" }
    elseif ($portOpen) { "$url 未拿到 HTTP 响应（端口已通，服务通常仍是好的）" }
    else { "$url 无响应" }
)

# ── 6. 防火墙 ───────────────────────────────────────────────────
foreach ($r in @(
        @{ Name = 'ControlServer HTTP'; Proto = 'TCP' },
        @{ Name = 'ControlServer Pairing'; Proto = 'UDP' })) {
    $fw = $null
    try { $fw = Get-NetFirewallRule -DisplayName $r.Name -ErrorAction SilentlyContinue } catch { }
    Report -Name "防火墙 $($r.Proto)" -Ok ($null -ne $fw) -Detail $(
        if ($fw) { "$($fw.DisplayName) = $($fw.Action)" } else { '未找到规则（局域网其它设备可能连不上）' }
    ) -Soft
}

# ── 7. 托盘助手 ─────────────────────────────────────────────────
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runVal = $null
try { $runVal = (Get-ItemProperty -Path $runKey -Name $TrayStartupName -ErrorAction SilentlyContinue).$TrayStartupName } catch { }
$trayProc = @(Get-Process -Name 'ControlServer.TrayAgent' -ErrorAction SilentlyContinue)
Report -Name '托盘开机启动' -Ok ($null -ne $runVal) -Detail $(
    if ($runVal) { '已注册' } else { '未注册（右下角不会有图标）' }
) -Soft
Report -Name '托盘正在运行' -Ok ($trayProc.Count -gt 0) -Detail $(
    if ($trayProc.Count -gt 0) { "PID $(($trayProc | ForEach-Object Id) -join ',')" } else { '未运行' }
) -Soft

# ── 8. 数据目录 ─────────────────────────────────────────────────
Report -Name '数据目录' -Ok (Test-Path $DataDir) -Detail $DataDir
Report -Name '  数据库文件' -Ok (Test-Path (Join-Path $DataDir 'control.db')) -Detail 'control.db' -Soft

Write-Host '────────────────────────────────────────────────────────────'
Write-Host ("通过 {0} 项，失败 {1} 项，提示 {2} 项" -f $script:Pass, $script:Fail, $script:Info)

if ($script:Fail -eq 0) {
    Write-Host ''
    Write-Host "结果：正常。浏览器访问  http://<本机IP>:$HttpPort"
    Write-Host ('      本机可直接用  ' + $url)
    Write-Host '      默认账号 admin / admin123（登录后建议尽快自行修改）'
    Write-Host ''
    exit 0
}

Write-Host ''
Write-Host '结果：有失败项。常见原因与处置：'
Write-Host "  · 端口 $HttpPort 被占用        -> netstat -ano | findstr :$HttpPort"
Write-Host ('  · 服务起不来                    -> 看 ' + (Join-Path $DataDir 'logs') + ' 下的日志')
Write-Host '  · 服务没注册                    -> 重新运行安装包，或管理员执行：'
Write-Host "      $InstallDir\tools\install-service.ps1 -SkipCopy -Source `"$InstallDir`" -InstallDir `"$InstallDir`" -DataDir `"$DataDir`" -Verify"
Write-Host ''
exit 1
