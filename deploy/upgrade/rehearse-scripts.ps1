#Requires -Version 5.1
<#
.SYNOPSIS
    upgrade.ps1 / rollback.ps1 的<b>端到端演练</b>：真跑这两个脚本，看它们是不是真的按
    "停服 -> 基线 -> 备份 -> 装 -> 起服 -> 验收 -> 失败自动回滚"办事。

.DESCRIPTION
    🔴 为什么需要它：这两个脚本原来**只能在"已经装好 Windows 服务"的机器上跑** ——
       也就是只有真升级那一天才会第一次执行。脚本里的任何错误（拼错变量、
       回滚没接上、退出码说反了）都只能在现场暴露，而现场那一次是不可重来的。

       本脚本用 -ProcessMode 把"实例"换成控制台进程，于是整条链可以在一台
       **没有管理员权限、也没装过服务**的开发机上真跑一遍。

    🔴 它验的是**脚本逻辑**，不是安装包。Inno 那一段（/VERYSILENT 装到 Program Files、
       注册服务）仍然只能在提权的真机上验 —— 这一条不因为本脚本存在就消失，
       只是从"整条链都没跑过"缩小成"只有安装那一步没跑过"。

.PARAMETER ServerExe
    单文件服务端 exe（默认取 dist\win-x64\ControlServer.Server.exe）。
    沙盒是**空目录**起家，所以不依赖现场那 1 GB 数据 —— 那条路在 rehearse-upgrade.py 里。

.PARAMETER KeepWork
    跑完不删沙盒（排查用）。

.NOTES
    ⚠️ 判据全部落在**退出码与数据**上，不解析中文输出 ——
       子进程的中文会按控制台码页写进日志，拿它做断言迟早会被编码问题搞成假红。
       upgrade.ps1 的退出码契约：0=升级成功 / 1=没开始就中止 / 2=需要人工 / 3=升级失败但已回滚。
    ⚠️ 输出只用中文与 GBK 里存在的符号（√ / × / ！），不用 emoji。
#>
[CmdletBinding()]
param(
    [string]$ServerExe = '',
    [switch]$KeepWork
)

$ErrorActionPreference = 'Continue'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $ServerExe) { $ServerExe = Join-Path $repo 'dist\win-x64\ControlServer.Server.exe' }

. (Join-Path $PSScriptRoot 'AppControl.ps1')

$Work = Join-Path $repo '.tools\upgrade-scripts'
$Sandbox = Join-Path $Work 'data'
$Backups = Join-Path $Work 'backups'
$Logs = Join-Path $Work 'logs'

$script:Failures = 0
$script:Checks = 0

function Judge {
    param([bool]$Condition, [string]$Text, [string]$Detail = '')

    $script:Checks++

    if ($Condition) {
        Write-Host ("  √ {0}{1}" -f $Text, $(if ($Detail) { "：" + $Detail } else { '' }))
    }
    else {
        Write-Host ("  × {0}{1}" -f $Text, $(if ($Detail) { "：" + $Detail } else { '' }))
        $script:Failures++
    }

    return $Condition
}

function Step {
    param([string]$Number, [string]$Text)

    Write-Host ''
    Write-Host ("[{0}] {1}" -f $Number, $Text)
    Write-Host ('-' * 74)
}

<#
    跑一次子脚本，返回退出码；子脚本的输出落到 $Logs\<名字>.log。

    🔴 这两件事在这里是**互相牵制**的，绕不开：

       · 要拿退出码，最稳的是 `-File` + `Process.ExitCode`；
       · 要拿完整输出，得接管道；而 upgrade.ps1 第 4 步会起一个服务端实例
         并留在那儿，那个孙进程会继承管道写端（CreateProcess 带重定向时
         bInheritHandles=TRUE，.NET 的匿名管道句柄是可继承的），
         于是子进程早退出了，管道却永不关闭 —— 父进程等 EOF 会一直等下去。

       ⇒ 解法：**子进程一退出就把沙盒实例停掉**，管道写端随之关闭，
         输出立刻收齐。反正这个实例在演练里本来就要停（场景 A 之后就是收尾步骤）。
         这条演练第一次连跑时卡了十分钟，就是卡在"等一个永远不会到来的 EOF"上。

    ⚠️ 也不用 `-Command "& script; exit $LASTEXITCODE"` 这种写法：实测 `& script.ps1`
       里 `exit 7` **不会**写进 $LASTEXITCODE，外层只能拿到 0 ——
       于是"升级失败已回滚（3）"和"需要人工（2）"全变成 0，判据集体失效。
#>
function Invoke-Script {
    param([string]$ScriptPath, [hashtable]$Params, [string]$LogName)

    $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath)

    foreach ($key in $Params.Keys) {
        $value = $Params[$key]

        if ($value -is [bool]) {
            if ($value) { $argumentList += ('-' + $key) }
            continue
        }

        $argumentList += @(('-' + $key), [string]$value)
    }

    # ⚠️ 不用 Start-Process：同一台机器上同时有 http_proxy 与 HTTP_PROXY 时它会抛异常
    #    （详见 AppControl.ps1 里 New-ToolStartInfo 的注释）。这条演练第一次跑就撞上了它。
    $psi = New-ToolStartInfo -FilePath 'powershell.exe' -Arguments $argumentList -CaptureOutput

    $proc = [System.Diagnostics.Process]::Start($psi)

    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    if (-not $proc.WaitForExit(600000)) {
        try { $proc.Kill() } catch { }
        Write-Host '  × 子脚本 10 分钟没有结束，已强杀。'

        return -1
    }

    $code = $proc.ExitCode

    # 子脚本可能留下实例（upgrade 成功、或回滚后起回来），它握着管道写端 ⇒ 先停掉
    $pidFile = Join-Path $env:TEMP ("controlserver-upgrade-{0}.pid" -f $port)

    if (Test-Path $pidFile) { $null = Stop-App }

    $null = $outTask.Wait(30000)
    $null = $errTask.Wait(5000)

    $text = $(if ($outTask.Status -eq 'RanToCompletion') { $outTask.Result } else { '（输出没收齐：管道仍被占用）' })
    $errText = $(if ($errTask.Status -eq 'RanToCompletion') { $errTask.Result } else { '' })

    Set-Content -LiteralPath (Join-Path $Logs $LogName) -Value ($text + $errText) -Encoding UTF8

    return $code
}

function Show-Log {
    param([string]$LogName, [int]$Head = 4, [int]$Tail = 22)

    $path = Join-Path $Logs $LogName

    if (-not (Test-Path $path)) { return }

    # 不指定 -Encoding：日志由 Set-Content 以 UTF8（带 BOM）落盘，Get-Content 会看 BOM。
    $lines = @(Get-Content -LiteralPath $path -ErrorAction SilentlyContinue)

    Write-Host ("    （{0} 行，摘录）" -f $lines.Count)

    foreach ($line in ($lines | Select-Object -First $Head)) { Write-Host ('    | ' + $line) }

    if ($lines.Count -gt ($Head + $Tail)) { Write-Host '    | ……' }

    foreach ($line in ($lines | Select-Object -Last $Tail)) { Write-Host ('    | ' + $line) }
}

function Get-DbInfo {
    param([string]$DataDir)

    # ⚠️ 不用 `& exe --dbinfo`：这台机器上那样调用拿不到任何输出，
    #    $LASTEXITCODE 也是空的（见 upgrade.ps1 里 Invoke-ServerExe 的注释）。
    $run = Invoke-Process -FilePath $ServerExe -Arguments @('--dbinfo') `
        -Environment @{ Server__Storage__DataDir = $DataDir }

    $info = @{}

    foreach ($line in @((([string]$run.Out + "`n" + [string]$run.Err) -split "`r?`n"))) {
        $text = [string]$line

        if ($text -match '^([a-z][a-zA-Z0-9\.]*)=(.+)$') { $info[$Matches[1]] = $Matches[2] }
    }

    return $info
}

<#
    取一次"库内容摘要"。

    🔴 必须走 --backup（VACUUM INTO）再哈希，**不能直接哈希 control.db**：
       库是 WAL 模式，直接哈希会把"还没 checkpoint 的 -wal"漏在外面，
       于是同一份数据在停服前后算出两个值 —— 假红。
       备份出来的那份是 checkpoint 过的，同一份逻辑内容永远是同一个字节流。
#>
function Get-DataDigest {
    param([string]$DataDir, [string]$BackupDir)

    if (Test-Path $BackupDir) { Remove-Item -LiteralPath $BackupDir -Recurse -Force }

    $run = Invoke-Process -FilePath $ServerExe -Arguments @('--backup', $BackupDir) `
        -Environment @{ Server__Storage__DataDir = $DataDir }

    $db = Join-Path $BackupDir 'control.db'

    if ($run.Code -ne 0 -or -not (Test-Path $db)) {
        Write-Host ("    ！--backup 失败（退出码 {0}）：{1}" -f $run.Code, ([string]$run.Err).Trim())

        return ''
    }

    return (Get-FileHash -LiteralPath $db -Algorithm SHA256).Hash
}

# ═══════════════════════════════════════════════════════════════════════════
# 主体
# ═══════════════════════════════════════════════════════════════════════════

<#
    比两份 --dbinfo 快照，返回不一致的项（空数组＝完全一致）。

    🔴 判"数据回来了"为什么用**全量 key 对比**而不是"备份库的字节哈希相等"：
       --restore 是把备份里的 control.db **逐字节**铺回去，但另一侧的基准是
       重新跑一次 --backup（VACUUM INTO）得到的 —— 那是一次**重写**，
       页布局/WAL checkpoint 时机会让同一份逻辑内容算出不同的字节
       （实测过：迁移条数、用户数、文件数全部相同，两个库的 SHA256 却不同）。
       拿字节当判据会给出一个"数据丢了"的假红，而那正是最不该出现的假红。
#>
function Compare-DbInfo {
    param([hashtable]$Before, [hashtable]$After)

    $diffs = @()

    foreach ($key in ($Before.Keys | Sort-Object)) {
        $a = [string]$Before[$key]
        $b = if ($After.ContainsKey($key)) { [string]$After[$key] } else { '（缺失）' }

        if ($a -ne $b) { $diffs += ('{0}: {1} -> {2}' -f $key, $a, $b) }
    }

    foreach ($key in ($After.Keys | Sort-Object)) {
        if (-not $Before.ContainsKey($key)) { $diffs += ('{0}: （原本没有） -> {1}' -f $key, $After[$key]) }
    }

    return $diffs
}

Write-Host ('=' * 74)
Write-Host 'upgrade.ps1 / rollback.ps1 端到端演练（进程模式，不需要管理员）'
Write-Host ('=' * 74)
Write-Host ("  程序    : {0}" -f $ServerExe)
Write-Host ("  沙盒    : {0}" -f $Work)

if (-not (Test-Path $ServerExe)) {
    Write-Host ''
    Write-Host ("× 找不到程序：{0}" -f $ServerExe)
    Write-Host '  先 dotnet publish 出单文件 exe（或 dotnet build，然后用 -ServerExe 指过去）'
    exit 1
}

if (Test-Path $Work) {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}

# 🔴 沙盒必须真的清干净。删不掉就说明有**上一次演练留下的实例**还开着沙盒里的库，
#    而那种情况下后面的判据会以极难查的方式出错：停服看起来成功（端口放开了），
#    但 --restore 会说"数据库正被占用"，于是"自动回滚失败"，而凶手根本不是这次的脚本。
if (Test-Path $Work) {
    Write-Host ''
    Write-Host ("× 清不掉旧沙盒：{0}" -f $Work)
    Write-Host '  多半是上一次演练（或手工试跑）留下的服务端进程还开着沙盒里的 control.db。'
    Write-Host '  先停掉它再重跑：Get-Process ControlServer.Server | Stop-Process -Force'
    exit 2
}

New-Item -ItemType Directory -Path $Sandbox -Force | Out-Null
New-Item -ItemType Directory -Path $Backups -Force | Out-Null
New-Item -ItemType Directory -Path $Logs -Force | Out-Null

# 端口由演练自己挑并**固定下来**：三个场景都用同一个端口，演练也能自己收尾。
$port = Initialize-AppControl -Mode process -ServerExe $ServerExe -DataDir $Sandbox -Port 0

$upgrade = Join-Path $PSScriptRoot 'upgrade.ps1'
$rollback = Join-Path $PSScriptRoot 'rollback.ps1'

$sw = [System.Diagnostics.Stopwatch]::StartNew()

try {
    # ── 0. 造沙盒：空目录起一次服务，让它自己建库 ──────────────────────
    Step 0 '造沙盒（空数据目录 -> 起一次服务 -> 停掉）'

    $started = Start-App
    Judge $started '第一次启动成功（空目录能自建库）' | Out-Null

    if ($started) {
        $deadline = (Get-Date).AddSeconds(90)
        $healthy = $false

        while ((Get-Date) -lt $deadline) {
            try {
                $r = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/healthz" -f $port) -UseBasicParsing -TimeoutSec 3

                if ($r.StatusCode -eq 200) { $healthy = $true; break }
            }
            catch { Start-Sleep -Milliseconds 400 }
        }

        Judge $healthy '/healthz 返回 200' ("{0}" -f $port) | Out-Null
    }

    # 停之前先等日志落盘（Serilog 有缓冲；立刻杀会把"程序版本"那行丢掉）
    Start-Sleep -Milliseconds 1500
    Judge (Stop-App) '沙盒实例已停（端口已释放）' | Out-Null

    $dbExists = Test-Path (Join-Path $Sandbox 'control.db')
    Judge $dbExists '沙盒里有了 control.db' | Out-Null

    if (-not $dbExists) { throw '沙盒没建成，后面的演练没有意义' }

    # ── 0b. 先证明"尺子是准的" ────────────────────────────────────────
    Step '0b' '校验尺子：同一份数据取两次摘要，必须完全一致'

    $digest1 = Get-DataDigest -DataDir $Sandbox -BackupDir (Join-Path $Work 'digest-1')
    $digest2 = Get-DataDigest -DataDir $Sandbox -BackupDir (Join-Path $Work 'digest-2')

    Judge ($digest1 -eq $digest2 -and $digest1.Length -gt 0) '同一份数据两次摘要相同' `
        ("{0} / {1}" -f $digest1.Substring(0, [Math]::Min(16, $digest1.Length)),
                     $digest2.Substring(0, [Math]::Min(16, $digest2.Length))) | Out-Null

    if ($digest1 -ne $digest2) {
        Write-Host '  ！摘要算法本身不稳定 —— 后面的"数据回到原样"判据全部作废，先停在这里。'
        throw '摘要不稳定'
    }

    # ── 1. 场景 A：正常升级 ────────────────────────────────────────────
    Step 1 '场景 A —— 正常升级（期望退出码 0）'

    $code = Invoke-Script -ScriptPath $upgrade -LogName 'A-upgrade.log' -Params @{
        Installer        = 'not-a-real-installer.exe'
        SkipInstaller    = $true
        ProcessMode      = $true
        ProcessPort      = $port
        DataDir          = $Sandbox
        ServerExe        = $ServerExe
        BackupRoot       = $Backups
        HealthTimeoutSec = 120
    }

    Show-Log -LogName 'A-upgrade.log'
    Judge ($code -eq 0) '退出码 0（升级成功）' ("实际 {0}" -f $code) | Out-Null

    $backupA = Get-ChildItem -LiteralPath $Backups -Directory -Filter 'pre-upgrade-*' -ErrorAction SilentlyContinue |
        Sort-Object Name | Select-Object -Last 1

    Judge ($null -ne $backupA -and (Test-Path (Join-Path $backupA.FullName 'manifest.json'))) '升级前备份确实产生了（有 manifest.json）' `
        $(if ($backupA) { $backupA.Name } else { '（没有）' }) | Out-Null

    # ── 2. 取基线② ────────────────────────────────────────────────────
    Step 2 '取基线②（A 留下的实例已在上一步收尾时停掉）'

    Judge (Test-PortReleased -Port $port -TimeoutSec 5) 'A 的实例已停（端口已释放）' | Out-Null

    $digestBefore = Get-DataDigest -DataDir $Sandbox -BackupDir (Join-Path $Work 'baseline-b')
    $infoBefore = Get-DbInfo -DataDir $Sandbox

    Write-Host ("    基线②：{0} 项 --dbinfo；迁移 {1} 条" -f $infoBefore.Count, $infoBefore['migrations'])

    # ── 3. 场景 B：注入"服务没起来"，必须自动回滚 ─────────────────────
    Step 3 '场景 B —— 升级后健康检查失败（-HealthTimeoutSec 0），期望：自动回滚且退出码 3'

    $code = Invoke-Script -ScriptPath $upgrade -LogName 'B-fail-rollback.log' -Params @{
        Installer        = 'not-a-real-installer.exe'
        SkipInstaller    = $true
        ProcessMode      = $true
        ProcessPort      = $port
        DataDir          = $Sandbox
        ServerExe        = $ServerExe
        BackupRoot       = $Backups
        # 🔥 注入点：0 秒健康超时 ⇒ "服务没起来" 必然成立。
        #    这是升级最常见的失败方式，而回滚不用这个值（它默认 180 秒），
        #    所以回滚本身能正常跑完 —— 正好完整验证一遍安全网。
        HealthTimeoutSec = 0
    }

    Show-Log -LogName 'B-fail-rollback.log'

    Judge ($code -eq 3) '退出码 3（升级失败，已自动回滚）' ("实际 {0}" -f $code) | Out-Null

    Judge (Stop-App) '回滚后留下的实例已停' | Out-Null

    # ── 4. 数据必须真的回到升级前 ──────────────────────────────────────
    Step 4 '回滚之后，数据必须等于基线②'

    $infoAfter = Get-DbInfo -DataDir $Sandbox
    $digestAfter = Get-DataDigest -DataDir $Sandbox -BackupDir (Join-Path $Work 'after-rollback-b')

    $diffs = Compare-DbInfo -Before $infoBefore -After $infoAfter

    Judge ($diffs.Count -eq 0) '全部 --dbinfo 项与基线②逐项一致（回滚把数据退回去了）' `
        $(if ($diffs.Count -eq 0) { '{0} 项' -f $infoBefore.Count } else { ($diffs -join '；') }) | Out-Null

    Write-Host ("    （仅供参考，不作判据）两次备份库的 SHA256：{0} / {1}" -f
        $digestBefore.Substring(0, [Math]::Min(16, $digestBefore.Length)),
        $digestAfter.Substring(0, [Math]::Min(16, $digestAfter.Length)))
    Write-Host '      两者不同是正常的：--restore 是逐字节铺回去，而基准那次 --backup 是 VACUUM INTO 重写。'

    # ── 5. 场景 C：回滚指向一份不存在的备份，必须拒绝且什么都不动 ──────
    Step 5 '场景 C —— 回滚到一份不存在的备份，期望：拒绝执行 + 数据不变'

    # 场景 C 的前后都用**同一种**取法（都跑一次 --backup），所以这里的字节对比是成立的。
    $digestBeforeC = Get-DataDigest -DataDir $Sandbox -BackupDir (Join-Path $Work 'before-c')

    $code = Invoke-Script -ScriptPath $rollback -LogName 'C-missing-backup.log' -Params @{
        BackupDir     = (Join-Path $Work 'no-such-backup')
        SkipInstaller = $true
        ProcessMode   = $true
        ProcessPort   = $port
        DataDir       = $Sandbox
        ServerExe     = $ServerExe
    }

    Show-Log -LogName 'C-missing-backup.log' -Head 6 -Tail 10

    Judge ($code -ne 0) '退出码非 0（拒绝执行）' ("实际 {0}" -f $code) | Out-Null

    $digestAfterC = Get-DataDigest -DataDir $Sandbox -BackupDir (Join-Path $Work 'after-c')
    Judge ($digestAfterC -eq $digestBeforeC) '数据没有被碰过' | Out-Null

    # ── 汇总 ─────────────────────────────────────────────────────────────
    Step 6 '结论'

    $sw.Stop()

    if ($script:Failures -eq 0) {
        Write-Host ("  √ 全部通过（{0} 项判据，{1:N1}s）" -f $script:Checks, $sw.Elapsed.TotalSeconds)
        Write-Host ''
        Write-Host '  已被这次演练证明的：'
        Write-Host '    · 停服 -> 基线 -> 备份 -> 起服 -> 验收 这条链能走通'
        Write-Host '    · 备份失败会中止（A 的备份确实产出了 manifest.json）'
        Write-Host '    · 验收失败会自动回滚，并且**数据真的回到升级前**'
        Write-Host '    · 回滚完成后退出码是 3 而不是 0（不会被当成"升好了"）'
        Write-Host '    · 备份缺失时回滚拒绝执行，且一个字节都不动'
        Write-Host ''
        Write-Host '  仍然没被证明的：'
        Write-Host '    · Inno 安装包那一步（/VERYSILENT 装到 Program Files、注册服务）'
        Write-Host '      —— 需要提权真机，这一条演练替代不了。'
        exit 0
    }

    Write-Host ("  × {0}/{1} 项不通过（日志在 {2}）" -f $script:Failures, $script:Checks, $Logs)
    exit 2
}
catch {
    Write-Host ''
    Write-Host ('=' * 74)
    Write-Host ("演练中断：{0}" -f $_.Exception.Message)
    Write-Host ("（沙盒与日志保留在 {0}）" -f $Work)
    Write-Host ('=' * 74)
    exit 2
}
finally {
    # 不管怎么退出，都别把实例留在机器上 —— 下一次演练会以为端口被占了。
    Initialize-AppControl -Mode process -ServerExe $ServerExe -DataDir $Sandbox -Port $port | Out-Null
    $null = Stop-App

    if (-not $KeepWork -and $script:Failures -eq 0) {
        Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
