#Requires -Version 5.1
<#
.SYNOPSIS
    中控服务版 —— Windows 安装包「复测」一键驱动（在**目标机**以管理员运行）。

.DESCRIPTION
    把「安装 → 自检 → **重装** → 自检 → 卸载 → 清理核对」整条链一次跑完，
    输出一张判定矩阵，并把全过程写进报告文件。

    为什么需要它：
      · 第三轮定位到的真根因是 `sc.exe create` 的 argv 被 PowerShell 拆烂
        （退出码 1639 = ERROR_INVALID_COMMAND_LINE），已改为 `New-Service`
        （cmdlet 参数绑定，不走命令行字符串拼接）并加了 ImagePath 回读兜底；
      · 那个 bug **只在「装到含空格的路径（默认 Program Files）+ 重装」时才现形**——
        静态检查、编译、单测全都拦不住，只能在真机上跑一次；
      · 手工按向导点两遍既慢又容易漏看，还常忘记「重装」这一步（恰恰是关键）。

    每一轮都会查这 7 件事：
      1) 安装器退出码（0 = 服务步骤成功）
      2) 服务已注册 / 开机自启(Auto) / Running
      3) 注册表 ImagePath **以引号开头且指向正确的 exe**（1639 与 unquoted path 两道坑的判据）
      4) 本轮 install-service.log **不再出现 1639**、且出现 `ImagePath 校验通过`
      5) HTTP 端口在监听
      6) HTTP 真的能打开
      7) 卸载后：服务 / 程序目录 / 防火墙规则 / 托盘启动项都清掉，数据目录保留

.PARAMETER Check
    只读模式：不安装、不卸载，只把**当前机器**的状态查一遍。
    不需要管理员 —— 先拿它确认脚本能跑，顺便看清现场现状。

.PARAMETER SetupExe
    安装包路径。默认自动取 dist\installer 下最新的 ControlServer-Setup-*.exe。

.PARAMETER Rounds
    安装轮数，默认 2（第 1 轮净装、第 2 轮**重装** —— 重装才是 1639 的触发场景）。

.PARAMETER SkipUninstall
    跑完不卸载（现场想留着用）。

.PARAMETER SkipVerifyScript
    不额外调用随包的 verify-install.ps1。

.EXAMPLE
    # 只读看一眼现状（不需要管理员）
    powershell -NoProfile -ExecutionPolicy Bypass -File retest-install.ps1 -Check

.EXAMPLE
    # 完整复测（管理员）；安装包路径可省略，自动取最新
    powershell -NoProfile -ExecutionPolicy Bypass -File retest-install.ps1

.NOTES
    用户可见输出**不使用** ✓/✗/⚠（不在系统 GBK 代码页里，控制台显示成问号），
    统一用 [OK] / [!!] / [--]。退出码：0 = 全部通过；1 = 有硬性失败。
#>
[CmdletBinding()]
param(
    [string]$SetupExe,
    [switch]$Check,
    [int]$Rounds = 2,
    [switch]$SkipUninstall,
    [switch]$SkipVerifyScript,
    [string]$ServiceName = 'ControlServer',
    [int]$HttpPort = 8899,
    [int]$PairingPort = 9999,
    [string]$InstallDir = 'C:\Program Files\ControlServer',
    [string]$DataDir = (Join-Path $env:ProgramData 'ControlServer'),
    [string]$TrayStartupName = 'ControlServer.TrayAgent',
    [string]$ReportPath
)

$ErrorActionPreference = 'Continue'

$script:Pass = 0
$script:Fail = 0
$script:Info = 0

# 安装器自身的 /LOG 放这里（DataDir 可能要等安装中间才建出来，不稳妥）
$script:Work = Join-Path $env:TEMP 'ControlServer-retest'
if (-not (Test-Path $script:Work)) { New-Item -ItemType Directory -Path $script:Work -Force | Out-Null }

# ═══════════════════════════════════════════════════════════════════════════
# 函数（PowerShell 不支持前向引用，必须全部前置）
# ═══════════════════════════════════════════════════════════════════════════

function Write-Section {
    param([Parameter(Mandatory)][string]$Text)
    Write-Host ''
    Write-Host ('── ' + $Text + ' ' + ('─' * [Math]::Max(0, 58 - $Text.Length)))
}

<#
.SYNOPSIS
    记录一项判定。$Soft 为软性项：不通过只提示，不计失败。
#>
function Report {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Ok,
        [string]$Detail = '',
        [switch]$Soft
    )
    if ($Ok) {
        $script:Pass++
        Write-Host ('  [OK] {0,-26} {1}' -f $Name, $Detail)
    }
    elseif ($Soft) {
        $script:Info++
        Write-Host ('  [--] {0,-26} {1}' -f $Name, $Detail)
    }
    else {
        $script:Fail++
        Write-Host ('  [!!] {0,-26} {1}' -f $Name, $Detail) -ForegroundColor Red
    }
}

function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch { return $false }
}

<#
.SYNOPSIS
    纯 socket 探测端口是否有监听（完全不受系统代理影响）。
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
    HTTP 探活 —— 显式直连（Proxy = $null），免得被 Clash / 公司代理劫持成 502。
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

function Get-ServiceImagePath {
    param([Parameter(Mandatory)][string]$Name)
    $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $Name
    try { return (Get-ItemProperty -Path $key -Name ImagePath -ErrorAction SilentlyContinue).ImagePath }
    catch { return $null }
}

function Get-ExpectedImagePath {
    return ('"' + (Join-Path $InstallDir 'ControlServer.Server.exe') + '" --Server:Startup:AutoOpenBrowser=false')
}

<#
.SYNOPSIS
    ImagePath 判定：ok = 带引号且指向本安装目录下的 Server.exe。
    返回 'ok' / 'unquoted' / 'wrong-path' / 'missing'。
#>
function Get-ImagePathVerdict {
    param([string]$ImagePath)
    if (-not $ImagePath) { return 'missing' }
    if ($ImagePath -notmatch '^\s*"') { return 'unquoted' }
    $plain = $ImagePath -replace '"', ''
    $exe = Join-Path $InstallDir 'ControlServer.Server.exe'
    if ($plain -notlike ($exe + '*')) { return 'wrong-path' }
    return 'ok'
}

function Get-LogLineCount {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0 }
    try { return @(Get-Content -Path $Path -ErrorAction SilentlyContinue).Count }
    catch { return 0 }
}

<#
.SYNOPSIS
    取日志文件新增行（用于只看"本轮安装"写了什么）。
#>
function Get-NewLogText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$SkipLines
    )
    if (-not (Test-Path $Path)) { return '' }
    try {
        $all = @(Get-Content -Path $Path -ErrorAction SilentlyContinue)
        if ($all.Count -le $SkipLines) { return '' }
        return (($all | Select-Object -Skip $SkipLines) -join "`n")
    }
    catch { return '' }
}

function Wait-ServiceState {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Want,       # 'Running' | 'Gone'
        [int]$TimeoutSec = 40
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $s = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($Want -eq 'Gone') {
            if (-not $s) { return $true }
        }
        elseif ($s -and $s.Status.ToString() -eq $Want) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Wait-PortOpen {
    param(
        [Parameter(Mandatory)][int]$Port,
        [int]$TimeoutSec = 25
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-TcpPort -TargetHost '127.0.0.1' -Port $Port) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Resolve-SetupExe {
    param([string]$Explicit)
    if ($Explicit) {
        if (Test-Path $Explicit) { return (Resolve-Path $Explicit).Path }
        throw "指定的安装包不存在：$Explicit"
    }
    # ① 脚本同目录 —— 把 retest-install.ps1 与 Setup.exe 拷到同一个文件夹，
    #    在目标机上直接就能跑（不必把整个仓库搬过去）。
    $here = @(Get-ChildItem -Path $PSScriptRoot -Filter 'ControlServer-Setup-*.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($here.Count -gt 0) { return $here[0].FullName }

    # ② 仓库里的 dist\installer（开发机上的默认位置）
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $dir = Join-Path $repoRoot 'dist\installer'
    $cands = @(Get-ChildItem -Path $dir -Filter 'ControlServer-Setup-*.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($cands.Count -eq 0) { return $null }
    return $cands[0].FullName
}

<#
.SYNOPSIS
    静默跑安装包。返回退出码（0 = 服务步骤成功；1 = 服务步骤失败）。
.DESCRIPTION
    ⚠️ -ArgumentList 传单个字符串（不要传数组）：数组是"用空格拼接、不自动加引号"，
       /LOG 的路径一旦含空格就会被拆成两个参数（与 sc.exe 那个坑同源）。
#>
function Invoke-SilentInstall {
    param(
        [Parameter(Mandatory)][string]$Setup,
        [Parameter(Mandatory)][string]$Tag
    )
    $log = Join-Path $script:Work ('setup-' + $Tag + '.log')
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $argLine = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $log + '"'
    Write-Host ("  执行：" + (Split-Path -Leaf $Setup) + ' ' + $argLine)
    $p = Start-Process -FilePath $Setup -ArgumentList $argLine -Wait -PassThru
    return [int]$p.ExitCode
}

<#
.SYNOPSIS
    静默卸载（Inno 卸载器）。返回退出码。
.DESCRIPTION
    ⚠️ Inno 的卸载器会把自己拷到临时目录再运行，**最初启动的那个进程会先退出** ——
      所以不能只靠 Start-Process -Wait，必须轮询到"卸载真的完成"（见调用处）。
#>
function Invoke-SilentUninstall {
    $unins = Join-Path $InstallDir 'unins000.exe'
    if (-not (Test-Path $unins)) {
        Write-Host ('  未找到卸载器：' + $unins)
        return -1
    }
    Write-Host '  执行：unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    try {
        $p = Start-Process -FilePath $unins -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
        return [int]$p.ExitCode
    }
    catch {
        Write-Host ('  卸载器启动失败：' + $_.Exception.Message)
        return -1
    }
}

<#
.SYNOPSIS
    一轮安装后的全部核对。
#>
function Invoke-InstallChecks {
    param(
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][int]$InstallExit,
        [Parameter(Mandatory)][int]$LogSkipBefore
    )

    Write-Section ($Phase + ' 自检')

    Report -Name ($Phase + ': 安装器退出码') -Ok ($InstallExit -eq 0) -Detail ('exit=' + $InstallExit)

    $exePath = Join-Path $InstallDir 'ControlServer.Server.exe'
    Report -Name ($Phase + ': 程序文件') -Ok (Test-Path $exePath) -Detail $(
        if (Test-Path $exePath) { ([math]::Round((Get-Item $exePath).Length / 1MB, 1)).ToString() + ' MB' } else { '缺少 ' + $exePath }
    )

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Report -Name ($Phase + ': 服务已注册') -Ok ($null -ne $svc) -Detail $(
        if ($svc) { $svc.DisplayName } else { '找不到服务 ' + $ServiceName }
    )

    if ($svc) {
        $wmi = Get-CimInstance Win32_Service -Filter ("Name='" + $ServiceName + "'") -ErrorAction SilentlyContinue
        $startMode = if ($wmi) { $wmi.StartMode } else { $null }
        Report -Name ($Phase + ': 开机自启') -Ok ($startMode -eq 'Auto') -Detail ('StartMode=' + $startMode)

        $ran = Wait-ServiceState -Name $ServiceName -Want 'Running' -TimeoutSec 40
        $now = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
        $nowStatus = if ($now) { $now.Status.ToString() } else { 'NotFound' }
        Report -Name ($Phase + ': 服务运行中') -Ok $ran -Detail ('Status=' + $nowStatus)
    }

    # ── 本轮最关键：ImagePath（1639 / unquoted path 的判据）─────────
    $img = Get-ServiceImagePath -Name $ServiceName
    $verdict = Get-ImagePathVerdict -ImagePath $img
    $imgOk = ($verdict -eq 'ok')
    $imgDetail = $img
    if (-not $img) { $imgDetail = '(读不到注册表 ImagePath)' }
    Report -Name ($Phase + ': ImagePath 带引号') -Ok ($img -and ($img -match '^\s*"')) -Detail $imgDetail
    Report -Name ($Phase + ': ImagePath 指向正确') -Ok $imgOk -Detail $(
        if ($imgOk) { 'verdict=ok' } else { 'verdict=' + $verdict + '，期望：' + (Get-ExpectedImagePath) }
    )

    # ── 本轮安装日志：不得再出现 1639 ─────────────────────────────
    $svcLog = Join-Path $DataDir 'logs\install-service.log'
    $newText = Get-NewLogText -Path $svcLog -SkipLines $LogSkipBefore
    if ($newText) {
        Report -Name ($Phase + ': 日志无 1639') -Ok (-not ($newText -match '1639')) -Detail 'install-service.log 本轮新增内容'
        $hasImg = ($newText -match 'ImagePath 校验通过')
        $rewritten = ($newText -match 'ImagePath 原为无引号')
        Report -Name ($Phase + ': ImagePath 校验') -Ok ($hasImg -or $rewritten) -Detail $(
            if ($hasImg) { '日志：ImagePath 校验通过' }
            elseif ($rewritten) { '日志：曾无引号，已自动修正' }
            else { '日志中未见 ImagePath 校验行' }
        )
        if ($newText -match '1639') {
            Write-Host '  --- 本轮 install-service.log 新增内容（节选）---'
            ($newText -split "`n" | Select-Object -Last 25) | ForEach-Object { Write-Host ('    ' + $_) }
            Write-Host '  ---------------------------------------------'
        }
    }
    else {
        Report -Name ($Phase + ': 日志无 1639') -Ok $false -Detail ('本轮没有产生日志（' + $svcLog + '）')
    }

    # ── 端口 + HTTP ────────────────────────────────────────────────
    $portOpen = Wait-PortOpen -Port $HttpPort -TimeoutSec 25
    Report -Name ($Phase + ': 端口监听') -Ok $portOpen -Detail ('TCP ' + $HttpPort)

    $httpStatus = $null
    if ($portOpen) {
        $url = 'http://127.0.0.1:' + $HttpPort + '/'
        for ($i = 0; $i -lt 10; $i++) {
            $httpStatus = Invoke-HttpProbeNoProxy -Url $url
            if ($httpStatus) { break }
            Start-Sleep -Milliseconds 500
        }
    }
    Report -Name ($Phase + ': HTTP 可访问') -Ok ($null -ne $httpStatus) -Soft -Detail $(
        if ($httpStatus) { 'HTTP ' + $httpStatus } elseif ($portOpen) { '端口已通、HTTP 未取到响应（服务通常仍是好的）' } else { '无响应' }
    )
}

<#
.SYNOPSIS
    额外跑一次随包自检（verify-install.ps1），它的输出整段进报告。
#>
function Invoke-VerifyScript {
    if ($SkipVerifyScript) { return }
    $candidates = @(
        (Join-Path $InstallDir 'tools\verify-install.ps1'),
        (Join-Path $PSScriptRoot 'verify-install.ps1')
    )
    $scriptPath = $null
    foreach ($c in $candidates) { if (Test-Path $c) { $scriptPath = $c; break } }
    if (-not $scriptPath) {
        Write-Host '  [--] 未找到 verify-install.ps1，跳过随包自检'
        $script:Info++
        return
    }
    Write-Section '随包自检 verify-install.ps1'
    try {
        # 用**独立进程**跑：verify-install.ps1 结尾是 exit 0 / exit 1，
        # 独立进程既能干净地拿到退出码，也不会让它的 exit 反过来影响本驱动。
        $argLine = '-NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath + '"'
        $argLine += ' -ServiceName "' + $ServiceName + '" -HttpPort ' + $HttpPort + ' -PairingPort ' + $PairingPort
        $argLine += ' -DataDir "' + $DataDir + '" -InstallDir "' + $InstallDir + '"'
        $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -NoNewWindow -Wait -PassThru
        Write-Host ('  (verify-install.ps1 退出码 ' + $p.ExitCode + ')')
        if ($p.ExitCode -ne 0) { Write-Host '  [--] verify-install.ps1 报告有失败项，详见上方输出' }
    }
    catch {
        Write-Host ('  [!!] verify-install.ps1 执行异常：' + $_.Exception.Message)
        $script:Fail++
    }
}

<#
.SYNOPSIS
    卸载后的清理核对。
#>
function Invoke-UninstallChecks {
    param([Parameter(Mandatory)][int]$UninstallExit)

    Write-Section '卸载后清理核对'

    # 卸载器是异步收尾的（先把自身拷到 temp 再跑），所以必须等到"真的没了"。
    $gone = Wait-ServiceState -Name $ServiceName -Want 'Gone' -TimeoutSec 90
    Report -Name '服务已移除' -Ok $gone -Detail $(
        if ($gone) { 'SCM 中已无此服务' } else { '仍在 SCM 中（可能停在「待删除」，重启后清除）' }
    )

    $exePath = Join-Path $InstallDir 'ControlServer.Server.exe'
    Report -Name '程序目录已清理' -Ok (-not (Test-Path $exePath)) -Detail $InstallDir

    foreach ($r in @('ControlServer HTTP', 'ControlServer Pairing')) {
        $fw = $null
        try { $fw = Get-NetFirewallRule -DisplayName $r -ErrorAction SilentlyContinue } catch { }
        Report -Name ('防火墙已移除: ' + $r) -Ok ($null -eq $fw) -Detail $(
            if ($fw) { '规则仍在' } else { '已删除' }
        ) -Soft
    }

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runVal = $null
    try { $runVal = (Get-ItemProperty -Path $runKey -Name $TrayStartupName -ErrorAction SilentlyContinue).$TrayStartupName } catch { }
    Report -Name '托盘启动项已移除' -Ok ($null -eq $runVal) -Detail $(
        if ($runVal) { 'HKCU 启动项仍在' } else { '已删除' }
    ) -Soft

    Report -Name '数据目录保留' -Ok (Test-Path $DataDir) -Detail ($DataDir + '（卸载默认保留现场配置）') -Soft
}

<#
.SYNOPSIS
    -Check 只读模式：把当前机器状态查一遍（不需要管理员）。
#>
function Invoke-CurrentStateReport {
    Write-Section '当前机器状态（只读）'

    Report -Name '管理员权限' -Ok (Test-IsAdmin) -Detail $(
        if (Test-IsAdmin) { '是（可跑完整复测）' } else { '否 —— 完整复测需要管理员' }
    ) -Soft

    $ep = ''
    try { $ep = (Get-ExecutionPolicy).ToString() } catch { }
    Report -Name 'PowerShell 执行策略' -Ok ($ep -ne 'Restricted') -Detail $ep -Soft

    $exePath = Join-Path $InstallDir 'ControlServer.Server.exe'
    Report -Name '程序文件' -Ok (Test-Path $exePath) -Detail $(
        if (Test-Path $exePath) { $InstallDir } else { '未安装（' + $exePath + ' 不存在）' }
    ) -Soft

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Report -Name '服务已注册' -Ok ($null -ne $svc) -Detail $(
        if ($svc) { $svc.DisplayName } else { '未注册' }
    ) -Soft

    if ($svc) {
        $wmi = Get-CimInstance Win32_Service -Filter ("Name='" + $ServiceName + "'") -ErrorAction SilentlyContinue
        $startMode = if ($wmi) { $wmi.StartMode } else { $null }
        Report -Name '开机自启' -Ok ($startMode -eq 'Auto') -Detail ('StartMode=' + $startMode) -Soft
        Report -Name '服务状态' -Ok ($svc.Status -eq 'Running') -Detail ('Status=' + $svc.Status) -Soft
    }

    $img = Get-ServiceImagePath -Name $ServiceName
    $verdict = Get-ImagePathVerdict -ImagePath $img
    Report -Name 'ImagePath 带引号' -Ok ($img -and ($img -match '^\s*"')) -Detail $img -Soft
    Report -Name 'ImagePath 指向正确' -Ok ($verdict -eq 'ok') -Detail ('verdict=' + $verdict) -Soft

    Report -Name '端口监听' -Ok (Test-TcpPort -TargetHost '127.0.0.1' -Port $HttpPort) -Detail ('TCP ' + $HttpPort) -Soft

    $httpStatus = $null
    if (Test-TcpPort -TargetHost '127.0.0.1' -Port $HttpPort) {
        $httpStatus = Invoke-HttpProbeNoProxy -Url ('http://127.0.0.1:' + $HttpPort + '/')
    }
    Report -Name 'HTTP 可访问' -Ok ($null -ne $httpStatus) -Detail $(
        if ($httpStatus) { 'HTTP ' + $httpStatus } else { '无响应' }
    ) -Soft

    Report -Name '数据目录' -Ok (Test-Path $DataDir) -Detail $DataDir -Soft

    $setup = $null
    try { $setup = Resolve-SetupExe -Explicit $SetupExe } catch { $setup = $null }
    Report -Name '安装包就位' -Ok ($null -ne $setup) -Detail $(
        if ($setup) { $setup } else { '未在 dist\installer 下找到（可用 -SetupExe 指定）' }
    ) -Soft

    if ($setup) {
        $hash = (Get-FileHash -Path $setup -Algorithm SHA256).Hash
        Write-Host ('       SHA256 = ' + $hash)
        Write-Host ('       大小   = ' + ([math]::Round((Get-Item $setup).Length / 1MB, 1)).ToString() + ' MB')
    }
}

# ═══════════════════════════════════════════════════════════════════════════
# 主体
# ═══════════════════════════════════════════════════════════════════════════

if (-not $ReportPath) {
    $logDir = Join-Path $DataDir 'logs'
    if (-not (Test-Path $logDir)) { $logDir = $env:TEMP }
    $ReportPath = Join-Path $logDir ('retest-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
}

$transcriptStarted = $false
try {
    $reportDir = Split-Path -Parent $ReportPath
    if ($reportDir -and -not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }
    Start-Transcript -Path $ReportPath -Force | Out-Null
    $transcriptStarted = $true
}
catch { }

Write-Host ''
Write-Host '中控服务版 —— 安装包复测驱动'
Write-Host ('机器：{0}   管理员：{1}   时间：{2}' -f $env:COMPUTERNAME, (Test-IsAdmin), (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Write-Host ('报告：{0}' -f $ReportPath)

$exitCode = 1
try {
    if ($Check) {
        Invoke-CurrentStateReport
        Write-Host ''
        Write-Host ('通过 {0} 项，失败 {1} 项，提示 {2} 项' -f $script:Pass, $script:Fail, $script:Info)
        $exitCode = 0
        if ($script:Fail -gt 0) { $exitCode = 1 }
    }
    else {
        if (-not (Test-IsAdmin)) {
            Write-Host ''
            Write-Host '[!!] 完整复测需要管理员权限（要注册 Windows 服务）。' -ForegroundColor Red
            Write-Host '     请用「以管理员身份运行」打开 PowerShell 再执行；只想看现状请加 -Check。'
            # ⚠️ 必须计入 Fail：否则 finally 里的「Fail -eq 0 ⇒ 全部通过」会把退出码覆写回 0，
            #    结果是"明明没跑成却报成功" —— 这类假成功比报错更危险。
            $script:Fail++
        }
        else {
            $setup = Resolve-SetupExe -Explicit $SetupExe
            if (-not $setup) { throw '未找到安装包。请先跑 deploy\windows\build-installer.ps1，或用 -SetupExe 指定。' }

            Write-Section '被测安装包'
            $hash = (Get-FileHash -Path $setup -Algorithm SHA256).Hash
            $sizeMb = ([math]::Round((Get-Item $setup).Length / 1MB, 1)).ToString()
            Write-Host ('  ' + $setup)
            Write-Host ('  大小   : ' + $sizeMb + ' MB')
            Write-Host ('  SHA256 : ' + $hash)

            $svcLog = Join-Path $DataDir 'logs\install-service.log'

            # ── 基线：若已有安装，先卸载，保证第 1 轮是"净装" ─────────
            if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
                Write-Section '基线清理（检测到已有安装，先卸载）'
                [void](Invoke-SilentUninstall)
                [void](Wait-ServiceState -Name $ServiceName -Want 'Gone' -TimeoutSec 90)
                Start-Sleep -Seconds 1
            }

            # ── 逐轮安装（第 2 轮就是 1639 的触发场景：重装）─────────
            $roundNames = @()
            for ($i = 1; $i -le $Rounds; $i++) {
                $label = '第' + $i + '轮'
                if ($i -eq 1) { $label = $label + '(净装)' } else { $label = $label + '(重装)' }

                Write-Section ($label + ' 安装')
                $skipBefore = Get-LogLineCount -Path $svcLog
                $exit = Invoke-SilentInstall -Setup $setup -Tag ('r' + $i)
                Start-Sleep -Seconds 1

                Invoke-InstallChecks -Phase $label -InstallExit $exit -LogSkipBefore $skipBefore
                $roundNames += $label + '=' + $exit

                if ($i -eq 1) { Invoke-VerifyScript }
            }

            # ── 卸载 + 清理核对 ───────────────────────────────────────
            if ($SkipUninstall) {
                Write-Section '跳过卸载（-SkipUninstall）'
            }
            else {
                Write-Section '卸载'
                $uexit = Invoke-SilentUninstall
                Invoke-UninstallChecks -UninstallExit $uexit
            }
        }
    }
}
catch {
    Write-Host ''
    Write-Host ('[!!] 复测驱动异常：' + $_.Exception.Message) -ForegroundColor Red
    $script:Fail++
}
finally {
    Write-Host ''
    Write-Host '════════════════════════════════════════════════════════════'
    Write-Host ('汇总：通过 {0} 项，失败 {1} 项，提示 {2} 项' -f $script:Pass, $script:Fail, $script:Info)
    if ($script:Fail -eq 0) {
        Write-Host '结果：全部通过。'
        $exitCode = 0
    }
    else {
        Write-Host '结果：有失败项 —— 请把报告文件整份发回。' -ForegroundColor Red
        Write-Host ('      报告：' + $ReportPath)
        $exitCode = 1
    }
    if ($transcriptStarted) { try { Stop-Transcript | Out-Null } catch { } }
}

exit $exitCode
