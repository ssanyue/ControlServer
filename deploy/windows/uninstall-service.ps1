#Requires -RunAsAdministrator
<#
.SYNOPSIS
    中控服务版 —— Windows 服务卸载脚本（保留数据目录）。

.DESCRIPTION
    反向清理 install-service.ps1 做过的事：
      1) 停止并删除托盘助手（含 HKCU 启动项）
      2) 停止并删除 Windows 服务
      3) 删除程序目录
      4) 删除防火墙规则

    刻意**不**保留任何东西在系统里；只有数据目录（%ProgramData%\ControlServer）保留。

.NOTES
    HKCU 启动项属于"当前登录用户"。若当初是用别的管理员账号装的，或是给多个
    用户装过托盘，需要在各账号下分别清理（见 -TrayStartupName / 手工 reg delete）。
#>
param(
    [string]$ServiceName = 'ControlServer',
    [string]$InstallDir = 'C:\Program Files\ControlServer',
    [string]$TrayStartupName = 'ControlServer.TrayAgent',

    # ── 供图形安装包（Inno Setup）复用 ────────────────────────────────
    # 安装包自带的卸载器会删程序目录；脚本再删一次会与卸载器抢文件（且可能
    # 因为"文件正被占用"而报错），所以由卸载器调用时传 -SkipDeleteFiles。
    # 默认仍是"删" —— 手工单独调用的行为与以前完全一致。
    [switch]$SkipDeleteFiles,
    [string]$LogFile                 # 非空则写一份 transcript
)

$ErrorActionPreference = 'Continue'

$transcriptStarted = $false
if ($LogFile) {
    try {
        $logDir = Split-Path -Parent $LogFile
        if ($logDir -and -not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        Start-Transcript -Path $LogFile -Append -Force | Out-Null
        $transcriptStarted = $true
    }
    catch { Write-Warning "无法写入日志文件 $LogFile（$($_.Exception.Message)），改用控制台输出。" }
}

try {
    # ── 1/4 托盘助手 ─────────────────────────────────────────────
    Write-Host '==> 1/4 移除托盘助手'
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path $runKey) {
        $existing = Get-ItemProperty -Path $runKey -Name $TrayStartupName -ErrorAction SilentlyContinue
        if ($existing) {
            Remove-ItemProperty -Path $runKey -Name $TrayStartupName -Force
            Write-Host '    已删除 HKCU 启动项'
        }
        else {
            Write-Host '    未找到 HKCU 启动项，跳过'
        }
    }

    # 先关托盘助手再删目录，否则 exe 被占用会删不掉。
    $agents = @(Get-Process -Name 'ControlServer.TrayAgent' -ErrorAction SilentlyContinue)
    if ($agents.Count -gt 0) {
        foreach ($p in $agents) {
            Write-Host "    结束托盘助手进程 PID=$($p.Id)"
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500
    }
    else {
        Write-Host '    托盘助手未在运行'
    }

    # ── 2/4 服务 ─────────────────────────────────────────────────
    Write-Host '==> 2/4 停止并删除服务'
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {

        # ⚠️⚠️ 这里**不能**用「停 2 秒 → 删 → 就算完」这种估算式等待。
        #
        #    Windows 的 `sc delete` 只是**标记删除**（ERROR_SERVICE_MARKED_FOR_DELETE = 1072），
        #    真正的清除是**异步**的 —— 要等该服务的所有句柄关闭才完成。
        #    而本脚本正是被安装包的 `PrepareToInstall` 调用的（用来在复制文件前先停掉旧服务）：
        #    若它删完立刻返回，紧跟着的 `sc create` 就会撞上 1072；
        #    更坑的是此时 `Get-Service` 也可能查不到该服务，于是被误判成"服务注册失败"，
        #    把排查方向整个带偏（看着像权限问题，实际只是没等够）。
        #
        #    所以必须**轮询到它真的从 SCM 消失**再往下走。

        sc.exe stop $ServiceName 2>&1 | ForEach-Object { if ("$_".Trim()) { Write-Host ('    ' + "$_") } }

        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            $s = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if (-not $s -or $s.Status -eq 'Stopped') { break }
            Start-Sleep -Milliseconds 500
        }

        sc.exe delete $ServiceName 2>&1 | ForEach-Object { if ("$_".Trim()) { Write-Host ('    ' + "$_") } }

        $deadline = (Get-Date).AddSeconds(30)
        $gone = $false
        while ((Get-Date) -lt $deadline) {
            if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { $gone = $true; break }
            Start-Sleep -Milliseconds 500
        }

        if ($gone) { Write-Host '    服务已删除（已确认从 SCM 消失）' }
        else { Write-Warning '    服务在 30 秒内仍未完全删除（可能停在「待删除」状态）—— 重启电脑后可清除。' }
    }
    else {
        Write-Host '    服务不存在，跳过'
    }

    # ── 3/4 程序目录 ─────────────────────────────────────────────
    if ($SkipDeleteFiles) {
        Write-Host '==> 3/4 跳过删除程序目录（-SkipDeleteFiles：由安装包卸载器负责）'
    }
    else {
        Write-Host '==> 3/4 删除程序目录'
        if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
        Write-Host "    已删除 $InstallDir"
    }

    # ── 4/4 防火墙规则 ───────────────────────────────────────────
    Write-Host '==> 4/4 删除防火墙规则'
    foreach ($name in @('ControlServer HTTP', 'ControlServer Pairing')) {
        if (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue) {
            Remove-NetFirewallRule -DisplayName $name
            Write-Host "    已删除规则：$name"
        }
    }

    Write-Host ""
    Write-Host "完成。数据目录（%ProgramData%\ControlServer）已保留，如需彻底清除请手动删除。"
    exit 0
}
catch {
    Write-Host ''
    Write-Host "[!!] 卸载失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ($transcriptStarted) { try { Stop-Transcript | Out-Null } catch { } }
}
