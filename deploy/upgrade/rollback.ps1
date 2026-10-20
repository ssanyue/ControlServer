#Requires -Version 5.1
<#
.SYNOPSIS
    中控服务版 —— 回滚：停服 -> 恢复升级前的备份 -> 装回旧版程序 -> 起服验收。

.DESCRIPTION
    🔴 为什么不能"只把 exe 换回旧版"：
       EF 的迁移**不回退**。旧版程序读一个已经被新版升过结构的库，行为未定
       （多出来的表/列、迁移历史里对不上的条数、改过名的列）。
       所以回滚只有一条正确路径：**用升级前的备份把数据退回那个版本该有的形状**，
       再把程序换成那个版本。两件事必须一起做，缺一个都只是"半回滚"。

.PARAMETER BackupDir
    升级前的备份目录（upgrade.ps1 会打印它，形如 C:\ControlServer-Backups\pre-upgrade-20261017-223000）。

.PARAMETER Installer
    旧版本的安装包。**不给就只能恢复数据、不能恢复程序** —— 那种情况下库会在下次启动时
    被当前版本重新 Migrate 上去，等于没回滚。见 -SkipInstaller。

.PARAMETER SkipInstaller
    【演练用】不装旧版程序，只走"停服 -> 恢复数据 -> 起服 -> 验收"。
    会明确警告：程序仍是新版。

.PARAMETER ProcessMode
    把"实例"当**控制台进程**管，而不是 Windows 服务（停/起走 PID 文件 + 进程）。
    与 upgrade.ps1 的同名开关是同一套实现（见 AppControl.ps1）。
    🔥 自动回滚时由 upgrade.ps1 自动带上 —— 不传的话，回滚会去停一个根本不存在的
       Windows 服务，于是"最需要它成功的时刻"它反而直接抛异常。

.PARAMETER ProcessPort
    进程模式下的监听端口；默认 0 = 挑一个当时空闲的端口。
    🔴 端口必须是**升级时用的那一个**：换一个端口就等于换一个实例来验收，
       停的那个和验的那个不是同一个。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\upgrade\rollback.ps1 `
        -BackupDir C:\ControlServer-Backups\pre-upgrade-20261017-223000 `
        -Installer C:\ControlServer-Backups\ControlServer-Setup-0.1.0.exe

.NOTES
    🔥 恢复**必须先停服**。`--restore` 自己**不检查**服务是否在跑（它只要求 --force 覆盖确认），
       服务在跑的话，内存里的旧数据会在恢复之后写回去 —— 症状是"恢复成功但数据还是旧的"。
       这个脚本的第一件事就是停服并确认进程真的退出了。
    ⚠️ 输出只用中文与 GBK 里存在的符号（√ / × / ！），不用 emoji。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupDir,

    [string]$Installer = '',
    [string]$DataDir = '',
    [string]$ServiceName = 'ControlServer',
    [string]$ServerExe = '',
    [int]$HealthPort = 8899,
    [int]$HealthTimeoutSec = 180,
    [switch]$SkipInstaller,
    [switch]$ProcessMode,
    [int]$ProcessPort = 0
)

$ErrorActionPreference = 'Continue'

if (-not $DataDir) { $DataDir = Join-Path $env:ProgramData 'ControlServer' }
if (-not $ServerExe) { $ServerExe = Join-Path $env:ProgramFiles 'ControlServer\ControlServer.Server.exe' }

# 与 upgrade.ps1 共用同一份"停/起实例"实现 —— 回滚路径平时不跑，各写一份必然漂移。
. (Join-Path $PSScriptRoot 'AppControl.ps1')

$script:AcPort = Initialize-AppControl `
    -Mode $(if ($ProcessMode) { 'process' } else { 'service' }) `
    -ServiceName $ServiceName -ServerExe $ServerExe -DataDir $DataDir -Port $ProcessPort

$script:Failures = @()
$script:Watch = [System.Diagnostics.Stopwatch]::StartNew()

function Step {
    param([int]$Number, [string]$Text)
    Write-Host ''
    Write-Host ("[{0}] {1}" -f $Number, $Text)
    Write-Host ('-' * 74)
}

function Ok {
    param([bool]$Condition, [string]$Text, [string]$Detail = '')
    if ($Condition) {
        Write-Host ("  √ {0}{1}" -f $Text, $(if ($Detail) { "：" + $Detail } else { '' }))
    }
    else {
        Write-Host ("  × {0}{1}" -f $Text, $(if ($Detail) { "：" + $Detail } else { '' }))
        $script:Failures += $Text + $(if ($Detail) { "：" + $Detail } else { '' })
    }
    return $Condition
}

function Invoke-ServerExe {
    param([string[]]$Arguments)
    # 🔴 不用 `& $ServerExe @Arguments 2>&1`：实测那样调用什么都拿不到
    #    （无输出、$LASTEXITCODE 为空），而同一个 exe 在 cmd 里正常。
    #    这会让"恢复前 --dbinfo"静默失效，验收变成"两边都读不出来、看起来一致"。
    $run = Invoke-Process -FilePath $ServerExe -Arguments $Arguments `
        -Environment @{ Server__Storage__DataDir = $DataDir }

    $lines = @(([string]$run.Out + "`n" + [string]$run.Err) -split "`r?`n" |
            Where-Object { $_ -ne '' })

    return @{ Code = $run.Code; Lines = $lines }
}

function Get-DbInfo {
    $result = Invoke-ServerExe -Arguments @('--dbinfo')
    if ($result.Code -ne 0) { return $null }
    $info = @{}
    foreach ($line in $result.Lines) {
        $text = [string]$line
        if ($text -match '^([a-z][a-zA-Z0-9\.]*)=(.+)$') { $info[$Matches[1]] = $Matches[2] }
    }
    return $info
}

function Wait-Health {
    param([int]$Port, [int]$TimeoutSec)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/healthz" -f $Port) -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { return $true }
        }
        catch { Start-Sleep -Milliseconds 400 }
    }
    return $false
}

# ⚠️ Stop-App / Start-App 不在这里定义 —— 它们来自 AppControl.ps1（第 68 行点进来）。
#    这两个函数原来本脚本各写了一份，与 upgrade.ps1 的那份已经漂移过：
#    只有 upgrade 那份修了"停服要确认真的停了"，这份没有。回滚路径平时不跑，
#    坏了没人知道，所以只能留一份实现。

function Get-RunningVersion {
    $logDir = Join-Path $DataDir 'logs'
    if (-not (Test-Path $logDir)) { return $null }
    $file = Get-ChildItem -Path $logDir -Filter 'server-*.log' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return $null }
    $lines = Get-Content -LiteralPath $file.FullName -Encoding UTF8 -ErrorAction SilentlyContinue
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match '程序版本：(.+)$') { return $Matches[1].Trim() }
    }
    return $null
}

Write-Host ('=' * 74)
Write-Host 'ControlServer 回滚'
Write-Host ('=' * 74)
Write-Host ("  备份目录  : {0}" -f $BackupDir)
Write-Host ("  数据目录  : {0}" -f $DataDir)
Write-Host ("  旧版安装包: {0}" -f $(if ($Installer) { $Installer } else { '(未提供)' }))

try {
    # ── 0. 前置检查 ────────────────────────────────────────────────────────
    Step 0 '前置检查'

    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if ($ProcessMode) {
        Write-Host ("  ！-ProcessMode：直接控制进程（端口 {0}），不碰 Windows 服务。" -f $script:AcPort)
        if (-not $SkipInstaller -and -not $isAdmin) {
            Write-Host '  ！仍要真装旧版包（没加 -SkipInstaller），而当前不是管理员 —— 装到 Program Files 那步大概会失败。'
        }
    }
    elseif (-not (Ok $isAdmin '以管理员身份运行')) { throw '需要管理员权限' }

    if (-not (Ok (Test-Path (Join-Path $BackupDir 'manifest.json')) '备份目录完整（有 manifest.json）')) {
        throw '这不是一份可用的备份目录'
    }

    if (-not $SkipInstaller) {
        if (-not $Installer) {
            Write-Host ''
            Write-Host '  × 没有 -Installer：只能恢复数据，恢复不了程序。'
            Write-Host '    库会在下次启动时被当前（新版）程序重新 Migrate 上去 —— 那等于没回滚。'
            Write-Host '    要么给 -Installer，要么明确加 -SkipInstaller 表示"我知道程序没换回去"。'
            exit 1
        }
        if (-not (Ok (Test-Path $Installer) '旧版安装包存在' $Installer)) { throw '找不到旧版安装包' }
    }
    else {
        Write-Host '  ！-SkipInstaller：只恢复数据，不换程序（演练模式）'
    }

    # 先把"这份备份里应该是什么样"读出来 —— 恢复完之后拿它比对。
    $manifest = Get-Content -LiteralPath (Join-Path $BackupDir 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host ("    备份时间：{0}" -f $manifest.createdAt)
    Write-Host ("    备份来源：{0}" -f $manifest.sourceRoot)
    Write-Host ("    文件数  ：{0}" -f $manifest.files.Count)

    # ── 1. 停服 ────────────────────────────────────────────────────────────
    Step 1 '停服（恢复之前必须停，否则内存旧数据会被写回去）'
    if (-not (Ok (Stop-App) '服务已停止')) { throw '停服失败' }

    $before = Get-DbInfo
    if ($null -ne $before) {
        Write-Host ("    恢复前：迁移 {0} 条（最新 {1}）" -f $before['migrations'], $before['migrations.latest'])
    }

    # ── 2. 恢复数据 ────────────────────────────────────────────────────────
    Step 2 '恢复数据（--restore --force）'

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $restore = Invoke-ServerExe -Arguments @('--restore', $BackupDir, '--force')
    $sw.Stop()

    foreach ($line in $restore.Lines) { Write-Host ('    | ' + [string]$line) }
    if (-not (Ok ($restore.Code -eq 0) '数据恢复成功' ("耗时 {0:N1}s" -f $sw.Elapsed.TotalSeconds))) {
        throw '恢复失败'
    }

    # ── 3. 装回旧版程序 ────────────────────────────────────────────────────
    Step 3 '装回旧版程序'

    if ($SkipInstaller) {
        Write-Host '  ！跳过（-SkipInstaller）。程序仍是当前版本。'
        Write-Host '    ！注意：库已经退回旧结构，这个版本的程序下次启动会把它重新迁移上去。'
    }
    else {
        $installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        Write-Host ("  运行 {0} {1}" -f $Installer, ($installArgs -join ' '))
        # ⚠️ 不用 Start-Process：见 AppControl.ps1 里 New-ToolStartInfo 的注释。
        $run = Invoke-Process -FilePath $Installer -Arguments $installArgs
        if (-not (Ok ($run.Code -eq 0) '旧版安装包返回成功' ("退出码 {0}" -f $run.Code))) {
            throw '装旧版失败'
        }
    }

    # ── 4. 起服 ────────────────────────────────────────────────────────────
    Step 4 '启动服务并等它可用'

    Start-App | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $healthy = Wait-Health -Port $script:AcPort -TimeoutSec $HealthTimeoutSec
    $sw.Stop()
    if (-not (Ok $healthy '/healthz 返回 200' ("{0:N1}s" -f $sw.Elapsed.TotalSeconds))) {
        throw '服务没起来'
    }

    # ── 5. 验收 ────────────────────────────────────────────────────────────
    Step 5 '验收'

    $after = Get-DbInfo
    if (-not (Ok ($null -ne $after) '--dbinfo 可读')) { throw '读不到数据库' }

    # 恢复成功后，库的形状应当**回到备份那一天**。迁移条数是最直接的证据。
    if ($null -ne $before) {
        Ok ($after['migrations'] -eq $before['migrations']) '迁移条数回到恢复前那一份' `
           ("{0} -> {1}" -f $before['migrations'], $after['migrations'])
    }
    Write-Host ("    现在库里的迁移：{0} 条（最新 {1}）" -f $after['migrations'], $after['migrations.latest'])

    $versionNow = Get-RunningVersion
    Write-Host ("    当前跑的版本：{0}" -f $(if ($versionNow) { $versionNow } else { '(日志里没有版本行)' }))
    Ok ($null -ne $versionNow) '启动自述里有版本行'

    Step 6 '结论'

    if ($script:Failures.Count -eq 0) {
        Write-Host '  √ 回滚完成'
        Write-Host ''
        Write-Host '  ！回滚的代价（这是设计如此，不是缺陷）：'
        Write-Host '    升级之后、回滚之前产生的所有业务数据都没有了 ——'
        Write-Host '    配置改动、审计记录、遥测明细、上传的文件。'
        Write-Host '    备份只包含升级前那一刻的状态，恢复就是回到那一刻。'
        Write-Host ''
        Write-Host '  ！如果这段时间里有人改过配置，需要重新改一遍。'
        Write-Host ("    总耗时：{0:N1}s" -f $script:Watch.Elapsed.TotalSeconds)
        exit 0
    }

    Write-Host ("  × {0} 项不通过：" -f $script:Failures.Count)
    foreach ($f in $script:Failures) { Write-Host ('    - ' + $f) }
    Write-Host ''
    Write-Host ("  备份仍在 {0} —— 可以再试一次，或人工检查库。" -f $BackupDir)
    exit 2
}
catch {
    Write-Host ''
    Write-Host ('=' * 74)
    Write-Host ("回滚失败：{0}" -f $_.Exception.Message)
    Write-Host ("（备份仍在 {0}，没有被消耗掉）" -f $BackupDir)
    Write-Host ('=' * 74)
    exit 2
}
