#Requires -Version 5.1
<#
.SYNOPSIS
    中控服务版 —— 升级（覆盖安装），带"自动备份 + 验收 + 失败自动回滚"。

.DESCRIPTION
    一条命令走完 8 步，每一步失败都停下来说清楚原因，不做"继续看看"：

        0. 前置检查   磁盘余量、服务存在、数据目录可读、备份不能落在数据目录里
        1. 基线       停服后跑 --dbinfo，把"升级前长什么样"记下来
        2. 停服       等进程真的退出（端口不再接受连接）
        3. 备份       --backup 到 <BackupRoot>\pre-upgrade-<时间戳>；**失败就中止并重启旧服务**
        4. 安装       Installer /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
        5. 起服       等 /healthz 返回 200
        6. 验收       版本号、迁移条数、关键表行数、启动日志有没有 ERR/FTL
        7. 回滚       验收不过 -> 自动调 rollback.ps1（-NoRollback 可关）

.PARAMETER Installer
    新版本的安装包（ControlServer-Setup-x.y.z.exe）。

.PARAMETER SkipInstaller
    【演练用】跳过第 4 步的实际安装。用来在没有"两个版本"的情况下验证
    "停服 -> 备份 -> 起服 -> 验收"这条链本身。**生产升级不要加这个开关。**

.PARAMETER ProcessMode
    把"实例"当**控制台进程**管，而不是 Windows 服务（停/起走 PID 文件 + 进程）。

    🔥 这不是测试开关，是第二种真实部署形态（dist/win-x64 那种单文件 exe）。
       没有它，本脚本只能在"已经装好服务"的机器上跑 —— 也就是**只有真升级那天
       才会第一次执行**，脚本里的任何错误都只能在现场暴露。加上它，整条链
       （前置检查 -> 基线 -> 备份 -> 起服 -> 验收 -> 失败自动回滚）可以在
       任意一台开发机上真跑一遍，见 deploy/upgrade/rehearse-scripts.ps1。

.PARAMETER ProcessPort
    进程模式下的监听端口。**默认 0 = 挑一个当时空闲的端口。**
    🔴 不能默认 8899：开发机上常常真有一个装在 8899 的实例，那样"起服成功"
       其实是起在了别人的进程上，验收全绿而什么都没验。

.PARAMETER AllowSameVersion
    允许"装完之后版本号没变"。默认这是**失败** —— 启动自述里的版本行带构建标识
    （M6.4 加上的），同一个字符串就意味着同一个包，也就是根本没换成新版。
    只有在"故意重装同一个包"时才加这个开关。

.PARAMETER PreviousInstaller
    当前（旧）版本的安装包，自动回滚时用它把程序换回去。
    🔥 **不给的话，自动回滚只能恢复数据、换不回程序** —— 于是库会在下次启动时
    被仍然装着的这个版本重新 Migrate 上去，等于白回滚。升级前请把它留在手边。

.PARAMETER NoRollback
    验收失败也不自动回滚。只在"想保留现场排查"时用 —— 用了就等于放弃自动兜底。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\upgrade\upgrade.ps1 `
        -Installer \\share\ControlServer-Setup-0.2.0.exe

.EXAMPLE
    # 演练：不真装，只验"停服-备份-起服-验收"这条链
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\upgrade\upgrade.ps1 `
        -Installer x -SkipInstaller

.EXAMPLE
    # 完整演练（不需要管理员、不需要先装服务）：见 rehearse-scripts.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\upgrade\upgrade.ps1 `
        -Installer x -SkipInstaller -ProcessMode -DataDir D:\sandbox -ServerExe D:\sandbox\ControlServer.Server.exe

.NOTES
    🔥 本脚本的前提是"M6.1 的备份可用"。没有备份就没有回滚，第 3 步失败会直接中止。
    🔥 备份是**全量**的：现场文件仓库 1 GB 时，备份也要 1 GB、约 10 秒。磁盘余量检查就是为它。
    ⚠️ 输出只用中文与 GBK 里存在的符号（√ / × / ！），不用 emoji —— 中文控制台码页 936。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Installer,

    [string]$BackupRoot = 'C:\ControlServer-Backups',
    [string]$DataDir = '',
    [string]$ServiceName = 'ControlServer',
    [string]$ServerExe = '',
    [int]$HealthPort = 8899,
    [int]$HealthTimeoutSec = 180,
    [string]$PreviousInstaller = '',
    [switch]$SkipInstaller,
    [switch]$NoRollback,
    [switch]$ProcessMode,
    [int]$ProcessPort = 0,
    [switch]$AllowSameVersion
)

$ErrorActionPreference = 'Continue'

if (-not $DataDir) { $DataDir = Join-Path $env:ProgramData 'ControlServer' }
if (-not $ServerExe) { $ServerExe = Join-Path $env:ProgramFiles 'ControlServer\ControlServer.Server.exe' }

# 停/起实例收在共用模块里 —— 一份实现，upgrade 与 rollback 不会各修各的。
. (Join-Path $PSScriptRoot 'AppControl.ps1')

$script:AcPort = Initialize-AppControl `
    -Mode $(if ($ProcessMode) { 'process' } else { 'service' }) `
    -ServiceName $ServiceName -ServerExe $ServerExe -DataDir $DataDir -Port $ProcessPort

$script:Failures = @()
$script:BackupDir = $null
$script:Before = $null
$script:Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# ═══════════════════════════════════════════════════════════════════════════
# 输出与判定
# ═══════════════════════════════════════════════════════════════════════════

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

# ═══════════════════════════════════════════════════════════════════════════
# 数据目录 / 服务 / 健康检查
# ═══════════════════════════════════════════════════════════════════════════

function Get-DirSizeMb {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
    if (-not $sum) { return 0 }
    return [math]::Round($sum / 1MB, 1)
}

function Invoke-ServerExe {
    param([string[]]$Arguments)
    # 数据目录用环境变量传给 exe —— 与 install-service.ps1 用的是同一套解析顺序。
    #
    # 🔴 这里**不能**用 `& $ServerExe @Arguments 2>&1`：实测这台机器上那样调用
    #    什么也拿不到 —— 没有输出、$LASTEXITCODE 是空的，就像命令压根没执行过，
    #    而同一个 exe 在 cmd/bash 里跑 --dbinfo 完全正常。
    #    后果不是"少一行日志"，是第 1 步直接判"升级前基线读不出来"然后中止升级。
    $run = Invoke-Process -FilePath $ServerExe -Arguments $Arguments `
        -Environment @{ Server__Storage__DataDir = $DataDir }

    $lines = @(([string]$run.Out + "`n" + [string]$run.Err) -split "`r?`n" |
            Where-Object { $_ -ne '' })

    return @{ Code = $run.Code; Lines = $lines }
}

<#
    跑 --dbinfo 并解析成哈希表。键名是 ASCII（db / migrations / table.xxx / files.count），
    所以不怕控制台编码 —— 中文行乱码也不影响这里的正则。
#>
function Get-DbInfo {
    $result = Invoke-ServerExe -Arguments @('--dbinfo')
    if ($result.Code -ne 0) { return $null }

    $info = @{}
    foreach ($line in $result.Lines) {
        $text = [string]$line
        if ($text -match '^([a-z][a-zA-Z0-9\.]*)=(.+)$') {
            $info[$Matches[1]] = $Matches[2]
        }
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
        catch {
            Start-Sleep -Milliseconds 400
        }
    }
    return $false
}

<#
    从日志读"程序版本："那一行（启动自述）。升级前后对比就靠它 ——
    三段版本号分不出包新旧，一定要带构建标识（M6.4 才加上去的）。
#>
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

<#
    启动日志里有没有 ERR / FTL。只看**这一次启动**那一段（最后一次"启动中"之后）。
#>
function Get-StartupErrors {
    $logDir = Join-Path $DataDir 'logs'
    if (-not (Test-Path $logDir)) { return @() }

    $file = Get-ChildItem -Path $logDir -Filter 'server-*.log' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return @() }

    $lines = Get-Content -LiteralPath $file.FullName -Encoding UTF8 -ErrorAction SilentlyContinue
    $start = 0
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -like '*启动中*') { $start = $i; break }
    }

    $hits = @()
    for ($i = $start; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '\[(ERR|FTL)\]') { $hits += $lines[$i] }
    }
    return $hits
}

# ═══════════════════════════════════════════════════════════════════════════
# 主体
# ═══════════════════════════════════════════════════════════════════════════

Write-Host ('=' * 74)
Write-Host 'ControlServer 升级（覆盖安装）'
Write-Host ('=' * 74)
Write-Host ("  安装包    : {0}" -f $Installer)
Write-Host ("  数据目录  : {0}" -f $DataDir)
Write-Host ("  备份到    : {0}" -f $BackupRoot)
Write-Host ("  服务名    : {0}" -f $(if ($ProcessMode) { '(进程模式，不用服务)' } else { $ServiceName }))
Write-Host ("  实例来源  : {0}" -f $(if ($ProcessMode) { '控制台进程' } else { 'Windows 服务' }))

try {
    # ── 0. 前置检查 ────────────────────────────────────────────────────────
    Step 0 '前置检查'

    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if ($ProcessMode) {
        # 进程模式不碰服务、不写 Program Files，所以不需要提权。
        # 但如果还要真装包，写 Program Files 那一步仍然要提权 —— 这里提前说清楚。
        Write-Host ("  ！-ProcessMode：直接控制进程（端口 {0}），不碰 Windows 服务。" -f $script:AcPort)
        if (-not $SkipInstaller -and -not $isAdmin) {
            Write-Host '  ！仍要真装包（没加 -SkipInstaller），而当前不是管理员 —— 装到 Program Files 那步大概会失败。'
        }
    }
    elseif (-not (Ok $isAdmin '以管理员身份运行')) {
        throw '需要管理员权限（要停/起服务、写 Program Files）'
    }

    if (-not $SkipInstaller) {
        if (-not (Ok (Test-Path $Installer) '安装包存在' $Installer)) { throw '找不到安装包' }
    }
    else {
        Write-Host '  ！-SkipInstaller：跳过第 4 步的实际安装（演练模式）'
    }

    if (-not (Ok (Test-Path (Join-Path $DataDir 'control.db')) '数据目录里有 control.db' $DataDir)) {
        throw '这不是一份装好的现场，换 -DataDir'
    }

    if (-not (Ok (Test-Path $ServerExe) '找到服务端程序' $ServerExe)) {
        throw '找不到 ControlServer.Server.exe，用 -ServerExe 指定'
    }

    # 备份目录不能落在数据目录里面：同盘同毁，那不叫备份。
    $backupFull = [System.IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
    $dataFull = [System.IO.Path]::GetFullPath($DataDir).TrimEnd('\')
    if (-not (Ok (-not $backupFull.StartsWith($dataFull + '\', [StringComparison]::OrdinalIgnoreCase)) `
                 '备份目录不在数据目录里面' $backupFull)) {
        throw '备份目录不能放在数据目录里'
    }

    # 🔥 备份是全量的：现场文件仓库多大，备份就要多大。余量不够就别开始 ——
    #    备份写到一半失败，比"根本没开始"更难收拾。
    $sizeMb = Get-DirSizeMb -Path $DataDir
    $needMb = [math]::Round($sizeMb * 1.2, 0)
    $driveLetter = (Split-Path -Qualifier $backupFull)
    $freeMb = [math]::Round((Get-PSDrive -Name $driveLetter.TrimEnd(':')).Free / 1MB, 0)
    Write-Host ("  数据目录 {0} MB；备份预计需要 {1} MB；备份盘剩余 {2} MB" -f $sizeMb, $needMb, $freeMb)
    if (-not (Ok ($freeMb -ge $needMb) '备份盘余量够' ("需要 {0} MB，剩 {1} MB" -f $needMb, $freeMb))) {
        throw '磁盘余量不够放一份完整备份'
    }

    # ── 1. 停服 + 基线 ─────────────────────────────────────────────────────
    Step 1 '停服并记录升级前基线'

    if (-not (Ok (Stop-App) '服务已停止')) { throw '停服失败' }

    $script:Before = Get-DbInfo
    if (-not (Ok ($null -ne $script:Before) '升级前基线可读（--dbinfo）')) {
        throw '--dbinfo 读不出来，无法做升级后比对'
    }

    Write-Host ("    迁移条数 {0}（最新 {1}）" -f $script:Before['migrations'], $script:Before['migrations.latest'])
    foreach ($key in ($script:Before.Keys | Sort-Object)) {
        if ($key -like 'table.*' -or $key -like 'files.*') {
            Write-Host ("    {0,-34} {1}" -f $key, $script:Before[$key])
        }
    }

    $versionBefore = Get-RunningVersion
    Write-Host ("    升级前跑的版本：{0}" -f $(if ($versionBefore) { $versionBefore } else { '(日志里没有版本行)' }))

    # ── 2. 备份（fail-closed）──────────────────────────────────────────────
    Step 2 '升级前备份（没有这一份就没有回滚）'

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $script:BackupDir = Join-Path $BackupRoot ("pre-upgrade-" + $stamp)

    if (-not (Test-Path $BackupRoot)) {
        New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $backup = Invoke-ServerExe -Arguments @('--backup', $script:BackupDir)
    $sw.Stop()

    foreach ($line in $backup.Lines) { Write-Host ('    | ' + [string]$line) }

    $backupOk = ($backup.Code -eq 0) -and (Test-Path (Join-Path $script:BackupDir 'manifest.json'))
    if (-not (Ok $backupOk '备份成功' ("耗时 {0:N1}s" -f $sw.Elapsed.TotalSeconds))) {
        Write-Host ''
        Write-Host '  ！备份失败 —— 升级中止，正在把旧服务起回来。'
        Write-Host '    真实升级里这里必须中止：在"没有回滚路径"的情况下动现场库，是不可接受的风险。'
        Start-App | Out-Null
        throw '备份失败'
    }

    # ── 3. 安装 ────────────────────────────────────────────────────────────
    Step 3 '覆盖安装（Inno Setup，静默）'

    if ($SkipInstaller) {
        Write-Host '  ！-SkipInstaller：跳过。下面直接验收"当前装着的这套"能不能起来。'
    }
    else {
        # 安装包会自己停服、注册服务、起服、跑自检（见 ControlServer.iss 的 ssPostInstall）。
        # 所以这里不需要再手工停一次。
        # ⚠️ 变量名不能叫 $args：它是 PowerShell 的自动变量（脚本参数数组），
        #    赋值虽然能用，但一旦有人在本脚本后面加 param(...) 就会拿到一个怪东西。
        $installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        Write-Host ("  运行 {0} {1}" -f $Installer, ($installArgs -join ' '))
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        # ⚠️ 不能用 Start-Process：见 AppControl.ps1 里 New-ToolStartInfo 的注释 ——
        #    同一台机器上同时有 http_proxy 与 HTTP_PROXY 时它会直接抛异常，
        #    而这里正是"要装包"的那一步。
        $run = Invoke-Process -FilePath $Installer -Arguments $installArgs
        $sw.Stop()
        Write-Host ("  安装进程退出码 {0}，耗时 {1:N1}s" -f $run.Code, $sw.Elapsed.TotalSeconds)
        if (-not (Ok ($run.Code -eq 0) '安装包返回成功' ("退出码 {0}" -f $run.Code))) {
            throw '安装失败'
        }
    }

    # ── 4. 起服 ────────────────────────────────────────────────────────────
    Step 4 '启动实例并等它可用'

    Start-App | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $healthy = Wait-Health -Port $script:AcPort -TimeoutSec $HealthTimeoutSec
    $sw.Stop()
    if (-not (Ok $healthy "/healthz 返回 200" ("{0:N1}s" -f $sw.Elapsed.TotalSeconds))) {
        throw '服务没起来'
    }

    # ── 5. 验收 ────────────────────────────────────────────────────────────
    Step 5 '验收'

    $versionAfter = Get-RunningVersion
    Write-Host ("    升级后跑的版本：{0}" -f $(if ($versionAfter) { $versionAfter } else { '(日志里没有版本行)' }))

    # 🔥 版本行带构建标识（M6.4 加上的）。同一个字符串 = 同一个包 = 根本没换新版。
    #    原来这里只打一句"不是失败，但请确认" —— 那等于把最严重的一种失败
    #    （装了个寂寞）降级成一句提示，而结论段仍然报"升级成功"。
    $sameVersion = ($versionAfter -and $versionBefore -and $versionAfter -eq $versionBefore)
    if ($SkipInstaller) {
        Write-Host '    （-SkipInstaller 演练：没有换包，版本不变是预期的）'
    }
    else {
        Ok (-not $sameVersion -or $AllowSameVersion.IsPresent) '装完之后版本号变了' `
           $(if ($sameVersion) { "仍是 {0}（同一个包？确认安装包对不对）" -f $versionAfter }
             else { "{0} -> {1}" -f $versionBefore, $versionAfter })
    }
    Ok ($null -ne $versionAfter) '启动自述里有版本行（能确认跑的是哪个包）'

    $after = Get-DbInfo
    if (-not (Ok ($null -ne $after) '升级后 --dbinfo 可读')) { throw '升级后读不到数据库' }

    Ok ([int]$after['migrations'] -ge [int]$script:Before['migrations']) '迁移条数没有减少' `
       ("{0} -> {1}" -f $script:Before['migrations'], $after['migrations'])
    Ok ($after['migrations.latest'] -eq $script:Before['migrations.latest'] -or
        [string]::CompareOrdinal($after['migrations.latest'], $script:Before['migrations.latest']) -ge 0) `
       '迁移历史没有倒退' ("{0} -> {1}" -f $script:Before['migrations.latest'], $after['migrations.latest'])

    # 只对"配置类"表要求**完全相等**。
    # 🔴 不能要求"所有表都只增不减"：agent_metrics 有 30 天保留期，升级后第一次启动
    #    就可能裁掉旧行 —— 那会让一个完全正常的升级被判定成"数据丢了"（假 FAIL）。
    foreach ($table in @('users', 'config_documents')) {
        $key = 'table.' + $table
        if ($script:Before.ContainsKey($key) -and $after.ContainsKey($key)) {
            Ok ($after[$key] -eq $script:Before[$key]) ("{0} 没变" -f $table) `
               ("{0} -> {1}" -f $script:Before[$key], $after[$key])
        }
    }
    Ok ($after['files.count'] -eq $script:Before['files.count']) '文件仓库文件数没变' `
       ("{0} -> {1}" -f $script:Before['files.count'], $after['files.count'])

    $errors = Get-StartupErrors
    Ok ($errors.Count -eq 0) '本次启动日志没有 ERR/FTL' ("{0} 条" -f $errors.Count)
    foreach ($e in ($errors | Select-Object -First 5)) { Write-Host ('      ' + $e) }

    # ── 6. 结论 ────────────────────────────────────────────────────────────
    Step 6 '结论'

    if ($script:Failures.Count -eq 0) {
        Write-Host '  √ 升级成功'
        Write-Host ("    升级前版本：{0}" -f $(if ($versionBefore) { $versionBefore } else { '(未知)' }))
        Write-Host ("    升级后版本：{0}" -f $versionAfter)
        Write-Host ("    升级前备份：{0}" -f $script:BackupDir)
        Write-Host '    ！这份备份先留着，至少留到"升级后跑满一个班次"为止。'
        Write-Host '      确认没问题之前删掉它，就等于把回滚能力删掉了。'
        Write-Host ("    总耗时：{0:N1}s" -f $script:Stopwatch.Elapsed.TotalSeconds)
        exit 0
    }

    Write-Host ("  × {0} 项验收不通过：" -f $script:Failures.Count)
    foreach ($f in $script:Failures) { Write-Host ('    - ' + $f) }

    if ($NoRollback) {
        Write-Host ''
        Write-Host '  ！-NoRollback：不回滚。现场保持现状供排查。'
        Write-Host ("    （升级前备份仍在 {0}，随时可以手工回滚）" -f $script:BackupDir)
        exit 2
    }

    throw '验收不通过'
}
catch {
    $reason = $_.Exception.Message
    Write-Host ''
    Write-Host ('=' * 74)

    # 备份没做成：已经把旧服务起回来了，什么都不用回滚。
    if (-not $script:BackupDir -or -not (Test-Path (Join-Path $script:BackupDir 'manifest.json'))) {
        Write-Host ("升级中止：{0}" -f $reason)
        Write-Host '没有产生可用备份，未做回滚。旧版本仍在原位，服务已尝试重新启动。'
        Write-Host ('=' * 74)
        exit 1
    }

    if ($NoRollback) {
        Write-Host ("升级失败：{0}" -f $reason)
        Write-Host '（-NoRollback 已指定，不做自动回滚）'
        Write-Host ('=' * 74)
        exit 2
    }

    Write-Host ("升级失败：{0}" -f $reason)
    Write-Host '开始自动回滚 ——'
    Write-Host ('=' * 74)

    $rollback = Join-Path $PSScriptRoot 'rollback.ps1'
    if (-not (Test-Path $rollback)) {
        Write-Host ("  × 找不到回滚脚本：{0}" -f $rollback)
        Write-Host ("  手工回滚：停服 -> {0} --restore {1} --force -> 起服" -f $ServerExe, $script:BackupDir)
        exit 2
    }

    $rollbackArgs = @{
        BackupDir   = $script:BackupDir
        DataDir     = $DataDir
        ServiceName = $ServiceName
        ServerExe   = $ServerExe
        HealthPort  = $script:AcPort
    }

    # 🔥 进程模式必须传下去。否则"自动回滚"会去停一个根本不存在的 Windows 服务，
    #    停服失败 -> 回滚直接抛异常 -> 备份还在、数据没退回 —— 而那正是最需要它
    #    成功的时刻。（这种"两个脚本各管一段状态"的漏传，演练一次就会暴露。）
    if ($ProcessMode) {
        $rollbackArgs['ProcessMode'] = $true
        $rollbackArgs['ProcessPort'] = $script:AcPort
    }

    # 能换回旧程序就换；换不了就明说"只回滚了数据"，别让人以为退干净了。
    if ($PreviousInstaller -and (Test-Path $PreviousInstaller)) {
        $rollbackArgs['Installer'] = $PreviousInstaller
    }
    else {
        $rollbackArgs['SkipInstaller'] = $true
        Write-Host ''
        Write-Host '  ！没有提供 -PreviousInstaller（或路径不存在）：'
        Write-Host '    自动回滚只能恢复**数据**，换不回**程序**。'
        Write-Host '    库退回旧结构之后，下次用当前版本启动会把它重新迁移上去 —— 那等于没回滚。'
        Write-Host '    请手工装回旧版安装包，再启动验收。'
        Write-Host ''
    }

    & $rollback @rollbackArgs
    $rollbackCode = $LASTEXITCODE

    # 🔴 这里原来直接 `exit $LASTEXITCODE`。而回滚**成功**时它的退出码是 0 ——
    #    于是"升级失败、已退回旧版"这件事对外表现为"退出码 0"。
    #    任何自动化（CI、运维脚本、RMM）看到 0 就会认为"升好了"，而实际上一行代码都没升。
    #    这是最典型的假绿：脚本自己喊了"升级失败"，退出码却说成功。
    if ($rollbackCode -eq 0) {
        Write-Host ''
        Write-Host '退出码 3：升级**没有**成功，但已自动回滚到升级前的状态。'
        Write-Host '  机器是可用的（旧版本 + 旧数据），但"升级"这件事没做成 ——'
        Write-Host '  别把 0 当成升好了，这个分支永远不会返回 0。'
        exit 3
    }

    Write-Host ''
    Write-Host '退出码 2：升级失败，且自动回滚也没成功 —— 需要人工介入。'
    Write-Host ("  升级前备份仍在 {0}" -f $script:BackupDir)
    exit 2
}
