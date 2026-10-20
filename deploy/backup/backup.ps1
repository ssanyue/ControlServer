<#
.SYNOPSIS
    M6.1 数据备份 —— 薄壳。真正的活在 ControlServer.Server.exe --backup 里。

.DESCRIPTION
    为什么只是一层薄壳：备份的核心是"数据库要拿到一致快照"（WAL 模式下必须 VACUUM INTO），
    那件事只有服务自己的 exe 做得对。脚本再实现一遍 SQLite 逻辑，就会有两个会分叉的版本。
    所以这里只做三件事：找 exe、拼一个带时间戳的目录、透传退出码。

    ⚠️ 备份**不需要**停服务 —— 这是它和恢复最重要的区别。

.PARAMETER Target
    备份的父目录，例如 D:\Backup。脚本会在里面建一个 ControlServer-20261017-1614 子目录。
    🔴 不要指向数据目录里面（脚本会拒绝），也不要和它同一块盘（脚本会警告）。

.PARAMETER IncludeLogs
    连 logs/ 一起备。默认不备：体积随运行天数线性增长。

.PARAMETER InstallDir
    服务安装目录，默认 %ProgramFiles%\ControlServer。

.EXAMPLE
    .\backup.ps1 -Target D:\Backup
    .\backup.ps1 -Target \\nas\share\controlserver -IncludeLogs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ControlServer'),
    [switch]$IncludeLogs,
    # 只做检查与打印，不真的调用程序。用于"先确认它会备到哪"，也用于没有安装环境时的自检。
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# 🔴 必须以管理员身份运行（M6.4）：数据目录的 ACL 收紧之后只留 SYSTEM 与 Administrators，
#    而 UAC 下"未提升"的管理员令牌里 Administrators 是 deny-only ⇒ 连读都读不了。
#    不在这里提前拦住的话，症状是程序跑到一半报"拒绝访问"，看起来像备份功能坏了。
#
#    注意 IsInRole(Administrator) 在"账号在管理员组、但当前窗口没提升"时返回 **False**
#    —— 正是我们要判的东西，不需要再去查完整性级别。
$__principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $__principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '【需要管理员权限】' -ForegroundColor Red
    Write-Host '  数据目录装好之后只允许 SYSTEM 与 Administrators 访问。' -ForegroundColor Red
    Write-Host '  请右键「以管理员身份运行」PowerShell，再执行本脚本。' -ForegroundColor Red
    Write-Host '  （未提升的窗口里，管理员组的权限是被系统屏蔽掉的，读不了数据目录）' -ForegroundColor DarkGray
    exit 1
}

$exe = Join-Path $InstallDir 'ControlServer.Server.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "【找不到程序】$exe" -ForegroundColor Red
    Write-Host "  用 -InstallDir 指定服务的安装目录。" -ForegroundColor Red
    exit 2
}

# ── 备份目录不能落在数据目录里面 ──
# 数据目录的来源与程序一致：环境变量优先，其次默认目录。
$dataRoot = $env:Server__Storage__DataDir
if ([string]::IsNullOrWhiteSpace($dataRoot)) {
    $dataRoot = Join-Path $env:ProgramData 'ControlServer'
}

$targetFull = [System.IO.Path]::GetFullPath($Target)
$dataFull = [System.IO.Path]::GetFullPath($dataRoot).TrimEnd('\') + '\'
if (($targetFull.TrimEnd('\') + '\').StartsWith($dataFull, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "【拒绝】备份目录不能放在数据目录里面：$targetFull" -ForegroundColor Red
    Write-Host "  放在一起的话，那块盘坏的时候两样一起没 —— 那不叫备份。" -ForegroundColor Red
    exit 2
}

# ── 同一块盘：只警告，不阻止 ──
# 有些现场只有一块盘，流程是"先备到本地，再拷到移动硬盘"。阻止它等于让人放弃备份。
try {
    $targetDrive = [System.IO.Path]::GetPathRoot($targetFull)
    $dataDrive = [System.IO.Path]::GetPathRoot($dataFull)
    if ($targetDrive -and $dataDrive -and
        ($targetDrive -eq $dataDrive) -and $dataDrive -notmatch '^\\\\') {
        Write-Host "注意：备份目录和数据目录在同一块盘（$dataDrive）。" -ForegroundColor Yellow
        Write-Host "      备份完成后请把它拷到另一块盘 / 另一台机器，否则盘坏时两样一起没。" -ForegroundColor Yellow
        Write-Host ""
    }
}
catch {
    # 网络路径取盘符会抛，忽略即可 —— 网络路径本来就满足"不同盘"。
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$destination = Join-Path $targetFull ("ControlServer-" + $stamp)

Write-Host "数据目录：$dataFull"
Write-Host "备份到：  $destination"
Write-Host ""

$arguments = @('--backup', $destination)
if ($IncludeLogs) { $arguments += '--include-logs' }

if ($DryRun) {
    Write-Host "[DryRun] 将要执行："
    Write-Host ("         `"" + $exe + "`" " + ($arguments -join ' '))
    Write-Host ""
    Write-Host "[DryRun] 这里只验了路径拼装与拒绝规则，没有真的调用程序。"
    exit 0
}

# 直接调 exe 并把它的输出原样交给运维 —— 不要在脚本里重新包装一遍文字，
# 否则"程序说的"和"脚本说的"迟早不一样。
#
# 🔴 这一行是本脚本里唯一"没在开发机验过"的地方：验证环境的安全策略不允许脚本
#    启动原生可执行文件。所以它刻意保持成最朴素的透传 —— 不解析输出、不改退出码，
#    错了也只可能错在"程序没被调起来"，而不是"脚本把结果解释错了"。
& $exe @arguments
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host ""
    Write-Host "【备份失败】退出码 $code（见上面的输出）" -ForegroundColor Red
    exit $code
}

Write-Host ""
Write-Host "下一步：把 $destination 整个目录拷到另一块盘或另一台机器。" -ForegroundColor Green
Write-Host "恢复：  $exe --restore <那个目录> --force    （先停服务）" -ForegroundColor Green
exit 0
