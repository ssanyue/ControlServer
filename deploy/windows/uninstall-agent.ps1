#Requires -Version 5.1
<#
.SYNOPSIS
    客户端Server —— 停止 / 注销 / 卸载（管理员）。

.DESCRIPTION
    顺序刻意是「先撤启动项 → 再杀托盘 → 再停服务 → 最后删文件」：

      · 先撤 HKCU 启动项：否则用户下次登录又拉起一个托盘，文件正被占着删不掉；
      · 先杀托盘：托盘进程会持有 exe 句柄，不杀它 Inno 卸载器删不掉文件；
      · 最后停服务并等它真的从 SCM 消失 —— `sc delete` 是异步的，
        "已删除"与"可重建"之间隔着句柄释放（错误 1072）。

    ⚠️ 默认**保留状态目录**（里面有 DPAPI 保护的凭据与日志）。
       要连数据一起清掉请显式加 -RemoveStateDir —— 那是不可逆的。

    ⚠️ 会一并删除「UDP 指令通道」的入站防火墙规则（M4.5，按程序授权的那条）。
       留下它就是一条**悬空规则**：它不会再放行任何东西（exe 已不存在），
       但下次有别的程序恰好装到同一个路径时会莫名其妙地"白得"一次入站放行。

    ⚠️ 这个脚本**不会**去动中心服务端上的台账：客户机上卸载了，
       服务端网页里那张记录还在（重新装上再连上就会复用/更新）。这是刻意的。

.NOTES
    不用 $ErrorActionPreference='Stop'；统一 try/catch + 显式 exit 0/1。

.PARAMETER SkipDeleteFiles
    只停服务 + 撤启动项，不删程序目录（供 Inno 卸载器调用：目录归它管）。

.PARAMETER RemoveStateDir
    连状态目录（凭据 / 状态文件 / 日志）一起删除。**不可逆**。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\uninstall-agent.ps1
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ControlServer Agent'),
    [string]$StateDir = (Join-Path $env:ProgramData 'ControlServer.Agent'),
    [string]$ServiceName = 'ControlServerAgent',
    [string]$TrayStartupName = 'ControlServerAgent.Tray',
    [string]$LogFile,

    [switch]$SkipDeleteFiles,
    [switch]$RemoveStateDir,
    [switch]$Quiet
)

$ErrorActionPreference = 'Continue'

$script:Report = @()
function Note([string]$message) {
    $script:Report += $message
    if (-not $Quiet) { Write-Host $message }
}

# ═══════════════════════════════════════════════════════════════════════════
# 主体
# ═══════════════════════════════════════════════════════════════════════════

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
    Note '==> 1/5 撤销托盘启动项'
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path $runKey) {
        $value = (Get-ItemProperty -Path $runKey -Name $TrayStartupName -ErrorAction SilentlyContinue)
        if ($value) {
            Remove-ItemProperty -Path $runKey -Name $TrayStartupName -Force -ErrorAction SilentlyContinue
            Note "    已删除启动项：$TrayStartupName"
        }
        else {
            Note '    启动项不存在，跳过。'
        }
    }

    Note '==> 2/5 结束托盘进程'
    $trayProcs = @(Get-Process -Name 'ControlServer.Agent.Tray' -ErrorAction SilentlyContinue)
    if ($trayProcs.Count -gt 0) {
        # 托盘可能属于别的登录会话；拿不到就记一笔而不是当失败。
        $killed = 0
        foreach ($p in $trayProcs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; $killed++ } catch { }
        }
        Note "    已结束 $killed / $($trayProcs.Count) 个托盘进程"
    }
    else {
        Note '    没有托盘进程在运行。'
    }

    Note '==> 3/5 停止并注销服务'
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        sc.exe stop $ServiceName 2>&1 | ForEach-Object { if ("$_".Trim()) { Note ('      ' + "$_") } }

        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            $svc.Refresh()
            if ($svc.Status -eq 'Stopped') { break }
            Start-Sleep -Milliseconds 700
        }

        sc.exe delete $ServiceName 2>&1 | ForEach-Object { if ("$_".Trim()) { Note ('      ' + "$_") } }

        $deadline = (Get-Date).AddSeconds(30)
        $gone = $false
        while ((Get-Date) -lt $deadline) {
            if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { $gone = $true; break }
            Start-Sleep -Milliseconds 700
        }

        if ($gone) { Note '    [OK] 服务已注销' }
        else { Note "    [--] 服务 $ServiceName 仍在「待删除」状态，重启后会自动消失。" }
    }
    else {
        Note '    服务不存在，跳过。'
    }

    # 服务注销后进程通常也会退出；仍活着的（被调试器挂着等）强杀，否则文件删不掉。
    $svcProcs = @(Get-Process -Name 'ControlServer.Agent.Service' -ErrorAction SilentlyContinue)
    if ($svcProcs.Count -gt 0) {
        # 给 SCM 一点时间把进程收掉。
        Start-Sleep -Seconds 2
        $svcProcs = @(Get-Process -Name 'ControlServer.Agent.Service' -ErrorAction SilentlyContinue)
        foreach ($p in $svcProcs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
        }
        Note "    已强制结束 $($svcProcs.Count) 个残留服务进程"
    }

    Note '==> 4/5 清理文件'
    if ($SkipDeleteFiles) {
        Note '    已跳过删除程序目录（-SkipDeleteFiles：目录归卸载器管）'
    }
    elseif (Test-Path $InstallDir) {
        try {
            Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop
            Note "    已删除 $InstallDir"
        }
        catch {
            Note "    [--] 删除程序目录失败：$($_.Exception.Message)"
            Note '         多半是还有进程占着 exe —— 注销 / 重启后再删。'
        }
    }
    else {
        Note "    程序目录不存在，跳过：$InstallDir"
    }

    # ── 防火墙规则（M4.5）────────────────────────────────────────
    #
    # 「UDP 指令通道」的入站放行是按**程序**建的（见 install-agent.ps1），
    # 一条规则指向一个 exe 路径 —— 删掉程序却留着规则，会留下一条**悬空规则**：
    # 它本身不会再放行任何东西（那个 exe 已经不存在了），但下次有别的程序
    # 恰好装到同一个路径时，会莫名其妙地"白得"一条入站放行。所以必须一并删掉。
    #
    # ⚠️ 这一步必须在**删除程序目录之后**做也无所谓（规则记的是路径字符串，
    #    不要求文件存在），但**绝不能放在最前面** —— 万一后面某步抛异常，
    #    规则已经删了、程序还在，结果是"装着的程序少了放行"。
    #
    # ⚠️ 与安装时同理：安全软件接管防火墙时 Remove-NetFirewallRule 也会抛异常。
    #    这里**不能吞掉**（与安装侧相反）：安装侧吞掉是为了不让服务装不上；
    #    卸载侧如果吞掉，就会留下一条谁也看不见、谁也想不起来的规则。
    #    所以失败时明确写一行"请手工删除"，把这件事交回给人。
    Note '==> 5/5 清理「UDP 指令通道」防火墙规则'
    $fwRuleName = 'ControlServer Agent UDP'
    try {
        if (Get-NetFirewallRule -DisplayName $fwRuleName -ErrorAction SilentlyContinue) {
            Remove-NetFirewallRule -DisplayName $fwRuleName -ErrorAction Stop
            Note "    已删除防火墙规则：$fwRuleName"
        }
        else {
            Note '    防火墙规则不存在，跳过。'
        }
    }
    catch {
        Note "    [--] 删除防火墙规则失败：$($_.Exception.Message)"
        Note "         请手工删除名为「$fwRuleName」的入站规则（或重跑本脚本）。"
    }

    if ($RemoveStateDir) {
        if (Test-Path $StateDir) {
            Remove-Item $StateDir -Recurse -Force -ErrorAction SilentlyContinue
            Note "    已删除状态目录（含凭据与日志）：$StateDir"
        }
    }
    else {
        Note "    状态目录已保留（凭据 / 日志）：$StateDir"
        Note '    下次重装会直接复用其中的注册凭据，不必重新注册。'
    }

    Note ''
    Note '完成。'
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
    exit 0
}
catch {
    Write-Host "[!!] 卸载失败：$($_.Exception.Message)" -ForegroundColor Red
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
    exit 1
}
