#Requires -Version 5.1
<#
.SYNOPSIS
    upgrade.ps1 / rollback.ps1 共用的"停一个实例 / 起一个实例"。

.DESCRIPTION
    🔥 为什么单独一份：这两个脚本原来各自写了一份 Stop-App / Start-App。
       "停服必须确认真的停了"这条经验只在 upgrade 里修过，rollback 里那份还是老的
       —— 各写一份，迟早漂移，而且漂移的是**回滚路径**（平时不跑，坏了没人知道）。
       所以收在这里，一处修改两边生效。

    🔥 两种模式，不是"测试开关"，是两种真实部署形态：

       service  Windows 服务（安装包装出来的）。停/起走 sc.exe，需要管理员。
       process  控制台进程（dist/win-x64 那种单文件 exe）。停/起走 PID 文件 + 进程，
                **不需要管理员，也不需要先把程序装进 Program Files** ——
                这是唯一能在开发机上把升级脚本真跑一遍的方式。

    ⚠️ 进程模式下 PID 文件写在哪里：%TEMP%。不写数据目录，因为数据目录会被备份/恢复，
       一份"升级前的 PID"被恢复出来只会误导人。

    ⚠️ 按 PID 杀之前必须核对**进程镜像路径**：PID 会复用，一个几天前的 PID 文件
       很可能已经指向别人的进程 —— 照着杀就是杀掉现场某个无关程序。

.NOTES
    ⚠️ 输出只用中文与 GBK 里存在的符号（√ / × / ！），不用 emoji —— 中文控制台码页 936。
#>

$script:AcMode = 'service'
$script:AcServiceName = 'ControlServer'
$script:AcServerExe = ''
$script:AcDataDir = ''
$script:AcPort = 8899
$script:AcPidFile = Join-Path $env:TEMP 'controlserver-upgrade.pid'
$script:AcStdoutLog = Join-Path $env:TEMP 'controlserver-upgrade.stdout.log'
$script:AcStderrLog = Join-Path $env:TEMP 'controlserver-upgrade.stderr.log'

<#
    进程模式下的"端口"由 AppControl 决定：传 0 就挑一个当时空闲的端口。
    🔴 不能默认 8899：开发机上往往真的有一个装在 8899 上的实例在跑，
       于是升级脚本"起服成功"其实是起在了别人的进程上，验收全绿但什么都没验。
#>
function Initialize-AppControl {
    param(
        [ValidateSet('service', 'process')]
        [string]$Mode = 'service',
        [string]$ServiceName = 'ControlServer',
        [string]$ServerExe = '',
        [string]$DataDir = '',
        [int]$Port = 8899
    )

    $script:AcMode = $Mode
    $script:AcServiceName = $ServiceName
    $script:AcServerExe = $ServerExe
    $script:AcDataDir = $DataDir
    $script:AcPort = $Port

    if ($Mode -eq 'process' -and $Port -le 0) {
        $script:AcPort = Get-FreePort
    }

    # 控制文件名里带端口：同一台机器上可能同时开着两个沙盒（演练 / 手工排查），
    # 共用一个 PID 文件就会出现"停 A 结果停了 B"。
    if ($Mode -eq 'process') {
        $script:AcPidFile = Join-Path $env:TEMP ("controlserver-upgrade-{0}.pid" -f $script:AcPort)
        $script:AcStdoutLog = Join-Path $env:TEMP ("controlserver-upgrade-{0}.stdout.log" -f $script:AcPort)
        $script:AcStderrLog = Join-Path $env:TEMP ("controlserver-upgrade-{0}.stderr.log" -f $script:AcPort)
    }

    return $script:AcPort
}

function Get-FreePort {
    $listener = $null
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    catch {
        return 18080
    }
    finally {
        if ($listener) { $listener.Stop() }
    }
}

# 判"端口还有人在听"。用 Get-NetTCPConnection 而不是去 connect 一下：
# 连一下就走的探针会在服务日志里留一条来路不明的连接，排障时没人认得出它是谁。
function Test-PortInUse {
    param([int]$Port)

    try {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop
        return ($null -ne $conn)
    }
    catch {
        # 老系统没有这个 cmdlet，退回 connect 探测（宁可多一条日志，也不要误判"已停"）
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $client.Connect('127.0.0.1', $Port)
            return $true
        }
        catch {
            return $false
        }
        finally {
            try { $client.Dispose() } catch { }
        }
    }
}

function Test-PortReleased {
    param([int]$Port, [int]$TimeoutSec = 20)

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-PortInUse -Port $Port)) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return (-not (Test-PortInUse -Port $Port))
}

<#
    🔥 判"服务真的停了"的最终依据是**库不再被占用**，不是端口释放。

    踩过一次：进程已经杀掉、端口也已经放开，但 control.db 还被某个句柄占着，
    于是下一步 --restore 回一句"数据库正被占用，现在不能恢复"，
    自动回滚在**最需要它成功**的那一刻失败 —— 而错误信息里完全没有
    "刚刚那个进程还没退干净"这条线索。

    所以停服的最后一步是：真的试着独占打开一次 control.db。
    打得开 = 释放了；打不开就重试到超时，再由调用方决定怎么办。
#>
function Test-DatabaseReleased {
    param([int]$TimeoutSec = 15)

    $db = Join-Path $script:AcDataDir 'control.db'

    if (-not (Test-Path -LiteralPath $db)) { return $true }

    $deadline = (Get-Date).AddSeconds($TimeoutSec)

    while ((Get-Date) -lt $deadline) {
        $stream = $null

        try {
            $stream = [System.IO.File]::Open(
                $db,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)

            return $true
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
        finally {
            if ($stream) { $stream.Dispose() }
        }
    }

    return $false
}

# ═══════════════════════════════════════════════════════════════════════════
# 起进程（不走 Start-Process）
# ═══════════════════════════════════════════════════════════════════════════

<#
    🔴 <b>为什么不用 <c>Start-Process</c></b>：

    PowerShell 5.1 的 <c>Start-Process</c> 在构造子进程环境块时，
    遇到<b>同一台机器上同时存在 <c>http_proxy</c> 与 <c>HTTP_PROXY</c></b>
    （或任何只有大小写不同的重名变量）会直接抛
    <c>ArgumentException：已添加项。字典中的关键字:“http_proxy”所添加的关键字:“HTTP_PROXY”</c>。

    这个环境在装了代理工具 / 云客户端 / 某些国产安全软件的机器上很常见，
    而它炸的地方是"升级脚本第 3 步要装包"和"起服"—— 正是最不该炸的时刻，
    且错误信息里没有一个字提到 Start-Process。

    <c>[Environment]::GetEnvironmentVariables()</c> 在 Windows 上返回的是
    <b>大小写不敏感</b>的集合，天然把重名折叠成一个；而
    <c>System.Diagnostics.ProcessStartInfo</c> 允许我们自己灌这个已经折叠过的环境块。
    于是这条路既不挑机器，也能顺手做到"只给这个子进程必要的环境变量"。

    ⚠️ 但实测（.NET Framework 4.8 / PowerShell 5.1）：<c>ProcessStartInfo.EnvironmentVariables</c>
       在这里<b>读出来是 null，而且是只读属性</b>，想自己灌都灌不进去。
       所以环境变量改走 <see cref="Push-ProcessEnvironment"/>：临时改进本进程的 $env:，
       起完立刻还原 —— <c>Process.Start</c> 在 EnvironmentVariables 为 null 时
       走的是"继承父进程环境块"，正好就是我们想要的。
       这条路也**不经过** Start-Process 那个会抛重复键异常的字典，所以一并绕开了那个坑。
#>
function New-ToolStartInfo {
    param(
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [switch]$CaptureOutput
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $psi.Arguments = (($Arguments | ForEach-Object {
                if ($_ -match '[\s"]') { '"{0}"' -f ($_ -replace '"', '\"') } else { $_ }
            }) -join ' ')

    if ($CaptureOutput) {
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
    }

    return $psi
}

<#
    临时把若干环境变量改进本进程，返回原值（用来还原）。
    ⚠️ 原值可能是"本来就没有"，此时 SetEnvironmentVariable(null) 正好把它删掉。
#>
function Push-ProcessEnvironment {
    param([hashtable]$Environment)

    $saved = @{}

    foreach ($key in $Environment.Keys) {
        $name = [string]$key
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, [string]$Environment[$key])
    }

    return $saved
}

function Pop-ProcessEnvironment {
    param([hashtable]$Saved)

    foreach ($key in $Saved.Keys) {
        [Environment]::SetEnvironmentVariable([string]$key, $Saved[$key])
    }
}

<#
    跑一个进程并等它结束，返回 @{ Code; Out; Err }。
    ⚠️ 必须在 WaitForExit <b>之前</b>开始读输出：先等再读，输出把管道缓冲区撑满时
       双方会互等到超时（症状是"装包卡住"，而安装包其实早就装完了）。

    🔴 但"读完"这件事不能无限等。实测踩到的坑：
       子进程如果**又起了别人**（比如 upgrade.ps1 起服务端实例），那个孙进程会
       继承子进程的 stdout/stderr 管道写端（CreateProcess 带重定向时
       bInheritHandles=TRUE，而 .NET 的匿名管道句柄是可继承的）。
       于是子进程早就退出了，管道却因为孙进程还活着而**永远不关**，
       `ReadToEndAsync().Result` 一直等下去 —— 症状是"演练跑到一半不动了"，
       而那一步其实早就成功了。
       ⇒ 退出之后只**限时**等输出，拿不到就按空处理（关键判据都在退出码与文件里，
         不该因为收不齐日志而卡死）。
#>
function Invoke-Process {
    param(
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [hashtable]$Environment = @{},
        [int]$TimeoutMs = 0
    )

    $psi = New-ToolStartInfo -FilePath $FilePath -Arguments $Arguments -CaptureOutput

    $saved = Push-ProcessEnvironment -Environment $Environment

    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
    }
    finally {
        Pop-ProcessEnvironment -Saved $saved
    }

    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    if ($TimeoutMs -gt 0) {
        if (-not $proc.WaitForExit($TimeoutMs)) {
            try { $proc.Kill() } catch { }
            return @{ Code = -1; Out = ''; Err = ('超时（{0} ms）' -f $TimeoutMs) }
        }
    }
    else {
        $proc.WaitForExit()
    }

    $null = $outTask.Wait(8000)
    $null = $errTask.Wait(8000)

    return @{
        Code = $proc.ExitCode
        Out  = $(if ($outTask.Status -eq 'RanToCompletion') { $outTask.Result } else { '' })
        Err  = $(if ($errTask.Status -eq 'RanToCompletion') { $errTask.Result } else { '' })
    }
}

<#
    起一个**不等它结束**的进程（服务端实例）。
    输出仍然要读：不读就会在管道写满时把子进程卡住，而"卡住"看起来像"起不来"。
#>
function Start-BackgroundProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [hashtable]$Environment = @{}
    )

    $psi = New-ToolStartInfo -FilePath $FilePath -Arguments $Arguments -CaptureOutput

    $saved = Push-ProcessEnvironment -Environment $Environment

    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
    }
    finally {
        Pop-ProcessEnvironment -Saved $saved
    }

    # 挂着读，不 await —— 只为把管道抽干
    $null = $proc.StandardOutput.ReadToEndAsync()
    $null = $proc.StandardError.ReadToEndAsync()

    return $proc
}

# ═══════════════════════════════════════════════════════════════════════════
# 停
# ═══════════════════════════════════════════════════════════════════════════

function Stop-App {
    if ($script:AcMode -eq 'process') { return (Stop-AppProcess) }
    return (Stop-AppService)
}

<#
    停 Windows 服务。

    🔴 返回值不是"我发过停止命令"，是"它真的停了"。原来这里等 30 秒之后
       无条件 return $true —— 服务卡住没退时，备份会对着一个**还在被写的库**跑，
       而脚本报"服务已停止"。后面 --restore 也有同样的问题。
#>
function Stop-AppService {
    $svc = Get-Service -Name $script:AcServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host ("  服务 {0} 不存在 —— 这不是一份装好的现场。" -f $script:AcServiceName)
        return $false
    }
    if ($svc.Status -ne 'Running') {
        Write-Host ("  服务当前状态：{0}（已经停了）" -f $svc.Status)
        return $true
    }

    Write-Host ("  停止服务 {0} …" -f $script:AcServiceName)
    sc.exe stop $script:AcServiceName 2>&1 | Out-Null

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $svc = Get-Service -Name $script:AcServiceName -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -eq 'Stopped') { break }
    }

    $svc = Get-Service -Name $script:AcServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Write-Host ("  × 服务没有在 30 秒内停下，当前状态：{0}" -f $svc.Status)
        return $false
    }

    # 服务 Stopped 不等于"库没人用了"：进程退出、文件句柄释放还要一点时间。
    Start-Sleep -Seconds 1

    if (-not (Test-DatabaseReleased)) {
        Write-Host ("  × 服务已停，但 {0}\control.db 仍被占用 —— 不能在这上面做恢复。" -f $script:AcDataDir)
        return $false
    }

    return $true
}

function Stop-AppProcess {
    if (-not (Test-Path -LiteralPath $script:AcPidFile)) {
        Write-Host '  没有进程控制文件 —— 之前没起过（或已经停干净了）。'
        return (Test-PortReleased -Port $script:AcPort)
    }

    $raw = (Get-Content -LiteralPath $script:AcPidFile -Raw -ErrorAction SilentlyContinue)
    $procId = 0
    if (-not [int]::TryParse(([string]$raw).Trim(), [ref]$procId)) {
        Write-Host '  ！进程控制文件里不是数字，当作没有（不猜 PID）。'
        Remove-Item -LiteralPath $script:AcPidFile -Force -ErrorAction SilentlyContinue
        return (Test-PortReleased -Port $script:AcPort)
    }

    $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue

    # 🔴 PID 会复用。不核对镜像路径就可能杀掉一个与本项目无关的进程 ——
    #    而且是在"升级"这种最不该出意外的时刻。
    if ($proc) {
        $expected = $null
        try { $expected = (Resolve-Path -LiteralPath $script:AcServerExe -ErrorAction Stop).Path } catch { }
        $actual = $null
        try { $actual = $proc.Path } catch { }

        if ($expected -and $actual -and -not [string]::Equals($expected, $actual, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host ("  ！PID {0} 现在是 {1}，不是我们的程序 —— 不动它（PID 已被复用）。" -f $procId, $actual)
            Remove-Item -LiteralPath $script:AcPidFile -Force -ErrorAction SilentlyContinue
            return (Test-PortReleased -Port $script:AcPort)
        }

        Write-Host ("  停止进程 {0}（PID {1}）…" -f $script:AcServerExe, $procId)
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        try { $proc.WaitForExit(20000) | Out-Null } catch { }
    }
    else {
        Write-Host ("  PID {0} 已经不在了。" -f $procId)
    }

    Remove-Item -LiteralPath $script:AcPidFile -Force -ErrorAction SilentlyContinue

    # 进程退了还不够：端口要真的不再有人听，否则下一步的 restore 可能写进"还活着的库"。
    if (-not (Test-PortReleased -Port $script:AcPort)) {
        Write-Host ("  × 进程已退，但端口 {0} 仍有人在听。" -f $script:AcPort)
        return $false
    }

    # 端口放开也不等于库放开（这两个是两回事）。--restore 的前提是库能独占打开。
    if (-not (Test-DatabaseReleased)) {
        Write-Host ("  × 进程已退、端口已放开，但 {0}\control.db 仍被占用。" -f $script:AcDataDir)
        Write-Host '    多半是还有另一个实例（或它的子进程）开着这个库 —— 用 handle/资源监视器查一下再重试。'
        return $false
    }

    return $true
}

# ═══════════════════════════════════════════════════════════════════════════
# 起
# ═══════════════════════════════════════════════════════════════════════════

function Start-App {
    if ($script:AcMode -eq 'process') { return (Start-AppProcess) }
    return (Start-AppService)
}

function Start-AppService {
    $svc = Get-Service -Name $script:AcServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return $false }
    if ($svc.Status -ne 'Running') {
        Write-Host ("  启动服务 {0} …" -f $script:AcServiceName)
        sc.exe start $script:AcServiceName 2>&1 | Out-Null
    }
    return $true
}

<#
    起控制台进程。

    ⚠️ 每个配置项都必须显式给：进程继承的是**当前这个 shell 的环境**，
       而现场机器上很可能已经设过 Server__Https__Enabled=true 之类的变量。
       不覆盖就会"起是起来了，但起来的是另一个形态"（HTTPS、弹浏览器、单实例抢锁）。

    ⚠️ 而且这些变量只在**这个子进程**里生效 —— 不再写进 $env:。
       早先是直接改 $env: 的，于是后面每一次 --dbinfo / --backup 都顺带继承了
       "Http__Urls""Startup__SingleInstance=false" 这些只有服务端实例才有意义的东西。
       现在它们各走各的。
#>
function Start-AppProcess {
    if (Test-PortInUse -Port $script:AcPort) {
        Write-Host ("  × 端口 {0} 已经有人在听 —— 先停掉它，否则等一下验收验的是别人。" -f $script:AcPort)
        return $false
    }

    $childEnv = @{
        Server__Storage__DataDir         = $script:AcDataDir
        Server__Http__Urls               = ("http://127.0.0.1:{0}" -f $script:AcPort)
        Server__Https__Enabled           = 'false'
        Server__Startup__AutoOpenBrowser = 'false'
        Server__Startup__SingleInstance  = 'false'
        Server__Startup__AttachConsole   = 'false'
        Server__Time__AutoSync           = 'false'
        Server__Timer__Enabled           = 'false'
        Server__Discovery__Port          = '0'
    }

    Write-Host ("  启动进程 {0}（端口 {1}）…" -f $script:AcServerExe, $script:AcPort)

    try {
        $proc = Start-BackgroundProcess -FilePath $script:AcServerExe -Environment $childEnv
    }
    catch {
        Write-Host ("  × 进程起不来：{0}" -f $_.Exception.Message)
        return $false
    }

    if (-not $proc) {
        Write-Host '  × 进程起不来（Process.Start 返回空）'
        return $false
    }

    $proc.Id | Set-Content -LiteralPath $script:AcPidFile -Encoding ASCII

    # 起来之后**先看一眼它还在不在**：配置写错、端口被占、端口权限不足这些
    # 都会在几百毫秒内退出。等健康检查超时再报错（默认 180 秒）会把
    # "起不来"和"起得慢"混成同一条消息，排障时最费时间。
    Start-Sleep -Milliseconds 500

    if ($proc.HasExited) {
        Write-Host ("  ！进程刚起来就退出了（退出码 {0}）" -f $proc.ExitCode)
        Write-Host ("    原因看数据目录的日志：{0}" -f (Join-Path $script:AcDataDir 'logs'))
        return $false
    }

    return $true
}
