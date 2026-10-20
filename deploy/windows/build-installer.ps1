#Requires -Version 5.1
<#
.SYNOPSIS
    中控服务版 —— 生成 Windows 安装包（Inno Setup），一个 exe 双击即装、装完就能跑。

.DESCRIPTION
    与 build-portable-exe.ps1 的分工：
      · build-portable-exe.ps1  → 绿色版单文件 exe + 服务版两个 exe（**原始产物**）
      · 本脚本                  → 把服务版那两个 exe 封进一个标准安装包（**分发产物**）

    安装包（ControlServer-Setup-<版本>.exe）会做：
      1) 复制程序到 C:\Program Files\ControlServer（管理员权限）
      2) 建数据目录 %ProgramData%\ControlServer
      3) 注册 Windows 服务并设为开机自启 + 崩溃自愈
      4) 授权交互用户启停服务（sc sdset，托盘助手要靠它）
      5) 放行防火墙端口 TCP/8899、UDP/9999
      6) 启动服务 + 部署托盘助手（HKCU 启动项）
      7) 自检：服务真的 Running 且 HTTP 真的能打开，否则报错并指出日志路径

    目标机**不需要**预装 .NET —— 程序本体是 self-contained 单文件。

    ISCC.exe（Inno 编译器）不要求预装：本脚本会按需从 NuGet 拉一份
    Tools.InnoSetup 到 <仓库>\.tools\innosetup（该目录已在 .gitignore 里）。

.NOTES
    沿用本目录其它脚本的约定：**不用** $ErrorActionPreference='Stop'
    （npm/vite 的警告走 stderr，会被 Stop 误判成致命错误），统一用
    $LASTEXITCODE + try/catch 收口。用户可见输出**禁用** ✓/✗/⚠ 符号
    （不在系统 GBK 代码页里，控制台会显示成问号）。

.PARAMETER AppVersion
    写进安装包的版本号。**不传则从 Directory.Build.props 的 <Version> 读**（M6.4）——
    版本号只留一处真源，此处与 .iss 里都不再写死（曾出现 iss 默认 1.0.0 / 程序集 0.1.0 对不上）。

.PARAMETER BuildStamp
    构建标识（形如 build.20261017-2230），注入程序集的 InformationalVersion 并写进
    dist\installer\BUILD-STAMP.txt。不传则按当前时间自动生成。

.PARAMETER InnoPath
    直接指定 ISCC.exe；给了就不去自动下载。

.PARAMETER SkipPayloadBuild
    跳过 build-portable-exe.ps1，直接用 dist\win-x64-service 里现成的 payload。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-installer.ps1
.EXAMPLE
    # 版本号 1.2.0，且不重跑程序发布（复用现成的 payload）
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-installer.ps1 -AppVersion 1.2.0 -SkipPayloadBuild
#>
param(
    [string]$DotNetRoot = 'C:\Users\IYLONG\.workbuddy\binaries\dotnet8',
    [string]$NodeRoot = 'C:\Users\IYLONG\.workbuddy\binaries\node\versions\22.22.2-6',
    [string]$AppVersion = '',
    [string]$BuildStamp = '',
    [string]$InnoPath,
    [switch]$SkipPayloadBuild,
    [switch]$SkipWebBuild
)

$ErrorActionPreference = 'Continue'

$InnoPackageVersion = '6.7.3'
$IslUrls = @(
    'https://raw.githubusercontent.com/kira-96/Inno-Setup-Chinese-Simplified-Translation/main/ChineseSimplified.isl',
    'https://raw.githubusercontent.com/kira-96/Inno-Setup-Chinese-Simplified-Translation/master/ChineseSimplified.isl'
)

# ═══════════════════════════════════════════════════════════════════════════
# 函数（PowerShell 不支持前向引用，必须全部前置）
# ═══════════════════════════════════════════════════════════════════════════

function Invoke-Download {
    param(
        [Parameter(Mandatory)][string[]]$Urls,
        [Parameter(Mandatory)][string]$OutFile
    )
    foreach ($u in $Urls) {
        try {
            Write-Host "    下载：$u"
            Invoke-WebRequest -Uri $u -OutFile $OutFile -UseBasicParsing -TimeoutSec 180
            if ((Test-Path $OutFile) -and ((Get-Item $OutFile).Length -gt 0)) { return $true }
        }
        catch {
            Write-Host "    失败：$($_.Exception.Message)"
        }
    }
    return $false
}

<#
.SYNOPSIS
    找到一个可用的 ISCC.exe，找不到就自动下载 Tools.InnoSetup 到 .tools\innosetup。
#>
function Resolve-ISCC {
    param(
        [string]$Explicit,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    if ($Explicit) {
        if (Test-Path $Explicit) { return (Resolve-Path $Explicit).Path }
        throw "指定的 -InnoPath 不存在：$Explicit"
    }

    # ① 环境变量
    if ($env:INNO_SETUP_HOME) {
        $p = Join-Path $env:INNO_SETUP_HOME 'ISCC.exe'
        if (Test-Path $p) { Write-Host "==> 使用 ISCC：$p"; return $p }
    }

    # ② 常规安装位置
    foreach ($base in @("$env:ProgramFiles\Inno Setup 6", "${env:ProgramFiles(x86)}\Inno Setup 6")) {
        if ($base) {
            $p = Join-Path $base 'ISCC.exe'
            if (Test-Path $p) { Write-Host "==> 使用 ISCC：$p"; return $p }
        }
    }

    # ③ 仓库本地缓存（上次自动下载的）
    $local = Join-Path $RepoRoot '.tools\innosetup\tools\ISCC.exe'
    if (Test-Path $local) { Write-Host "==> 使用本地 ISCC：$local"; return $local }

    # ④ 自动下载 NuGet 上的 Tools.InnoSetup
    Write-Host '==> 本机没有 ISCC，自动获取 Tools.InnoSetup 到 .tools\innosetup'
    $toolDir = Join-Path $RepoRoot '.tools\innosetup'
    New-Item -ItemType Directory -Path $toolDir -Force | Out-Null

    $zip = Join-Path $toolDir '_tools.innosetup.zip'
    $urls = @(
        "https://nuget.azure.cn/v3-flatcontainer/tools.innosetup/$InnoPackageVersion/tools.innosetup.$InnoPackageVersion.nupkg",
        "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$InnoPackageVersion/tools.innosetup.$InnoPackageVersion.nupkg"
    )

    if (-not (Invoke-Download -Urls $urls -OutFile $zip)) {
        throw "无法下载 Tools.InnoSetup $InnoPackageVersion。请手动安装 Inno Setup 6 后用 -InnoPath 指定 ISCC.exe。"
    }

    Expand-Archive -Path $zip -DestinationPath $toolDir -Force
    Remove-Item $zip -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $local)) {
        throw "解压后仍未找到 ISCC.exe：$local"
    }
    Write-Host "==> 已就绪：$local"
    return $local
}

<#
.SYNOPSIS
    确保 Inno 编译器目录里有简体中文语言包（官方包不含中文，需从社区翻译仓库取）。
#>
function Initialize-ChineseLanguage {
    param([Parameter(Mandatory)][string]$IsccPath)

    $toolsDir = Split-Path -Parent $IsccPath
    $langDir = Join-Path $toolsDir 'Languages'
    $isl = Join-Path $langDir 'ChineseSimplified.isl'

    if (Test-Path $isl) {
        Write-Host "==> 简体中文语言包已就位：$isl"
        return $true
    }

    Write-Host '==> 缺少简体中文语言包，尝试下载'
    if (-not (Test-Path $langDir)) { New-Item -ItemType Directory -Path $langDir -Force | Out-Null }

    if (-not (Invoke-Download -Urls $IslUrls -OutFile $isl)) {
        Remove-Item $isl -Force -ErrorAction SilentlyContinue
        Write-Host "[!!] 下载简体中文语言包失败（网络不通？）"
        Write-Host '     安装包的 .iss 引用了 compiler:Languages\ChineseSimplified.isl，缺它无法编译。'
        Write-Host "     手工修法：下载 ChineseSimplified.isl 放到 $langDir"
        foreach ($u in $IslUrls) { Write-Host "       $u" }
        throw '简体中文语言包不可用'
    }

    Write-Host "==> 简体中文语言包已下载：$isl"
    return $true
}

# ═══════════════════════════════════════════════════════════════════════════
# 主体
# ═══════════════════════════════════════════════════════════════════════════

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$issPath = Join-Path $PSScriptRoot 'ControlServer.iss'
$payloadDir = Join-Path $repoRoot 'dist\win-x64-service'
$outDir = Join-Path $repoRoot 'dist\installer'
$payloadExes = @('ControlServer.Server.exe', 'ControlServer.TrayAgent.exe')

# ── 版本号只留一处真源（M6.4）：Directory.Build.props 的 <Version> ──────────
if (-not $AppVersion) {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (Test-Path $propsPath) {
        $m = [regex]::Match((Get-Content $propsPath -Raw), '<Version>\s*([0-9][0-9A-Za-z\.\-]*)\s*</Version>')
        if ($m.Success) { $AppVersion = $m.Groups[1].Value }
    }
    if (-not $AppVersion) {
        throw "未能从 Directory.Build.props 读出 <Version>，请用 -AppVersion 显式指定。"
    }
}

if (-not $BuildStamp) { $BuildStamp = 'build.' + (Get-Date -Format 'yyyyMMdd-HHmm') }

# Inno 的 VersionInfoVersion 必须是数字点（不能带 +build.xxx），所以安装包文件名仍用三段版本号，
# "这一包是哪次构建"由 BUILD-STAMP.txt 与启动自述共同证明。
$fullVersion = "$AppVersion+$BuildStamp"

try {
    Write-Host "==> 仓库根：$repoRoot"
    Write-Host "==> 版本号：$AppVersion（构建标识 $BuildStamp）"

    if (-not (Test-Path $issPath)) { throw "找不到 Inno 脚本：$issPath" }

    # ── 1/4 准备程序产物（payload） ────────────────────────────────
    $missing = @($payloadExes | Where-Object { -not (Test-Path (Join-Path $payloadDir $_)) })

    if ($SkipPayloadBuild) {
        if ($missing.Count -gt 0) {
            throw "dist\win-x64-service 里缺少：$($missing -join ', ')。去掉 -SkipPayloadBuild 让它自动重新发布。"
        }
        Write-Host '==> 1/4 复用现成 payload（-SkipPayloadBuild）'
    }
    elseif ($missing.Count -eq 0) {
        Write-Host '==> 1/4 payload 已存在，跳过重新发布（要强制重发请先删除 dist\win-x64-service）'
    }
    else {
        Write-Host '==> 1/4 发布程序产物（build-portable-exe.ps1）'
        $portable = Join-Path $PSScriptRoot 'build-portable-exe.ps1'

        # ⚠️⚠️ 必须用**哈希表** splat（@{} → 按名字传参），**不能**用数组 splat（@() → 按位置传参）。
        #
        #   数组 splat 是"按声明顺序填充位置参数"，元素**不会被当成参数名**：
        #       @('-DotNetRoot', 'C:\...\dotnet8', ...) → DotNetRoot='-DotNetRoot'
        #   于是被调脚本里 Join-Path '-DotNetRoot' 'dotnet.exe' → "未找到 .NET SDK：-DotNetRoot\dotnet.exe"。
        #
        #   这个坑长期没暴露，是因为安装包历来都是在 payload **已存在**时构建的（走上面
        #   "payload 已存在，跳过重新发布"分支），这行代码从没被执行过 ——
        #   直到 2026-10-09 删掉 dist\win-x64-service 强制重发才第一次踩到。
        #
        #   注意：变量名不能用 $args —— 那是 PowerShell 的自动变量。
        $portableArgs = @{
            DotNetRoot       = $DotNetRoot
            NodeRoot         = $NodeRoot
            OutputDir        = (Join-Path $repoRoot 'dist\win-x64')
            ServiceOutputDir = $payloadDir
            BuildStamp       = $BuildStamp
        }
        if ($SkipWebBuild) { $portableArgs['SkipWebBuild'] = $true }
        & $portable @portableArgs
        if ($LASTEXITCODE -ne 0) { throw "build-portable-exe.ps1 失败（退出码 $LASTEXITCODE）" }
        foreach ($f in $payloadExes) {
            if (-not (Test-Path (Join-Path $payloadDir $f))) { throw "发布后仍缺少 $f" }
        }
    }

    foreach ($f in $payloadExes) {
        $p = Join-Path $payloadDir $f
        Write-Host ("    {0,-34} {1,8:N1} MB" -f $f, ((Get-Item $p).Length / 1MB))
    }

    # ── 2/4 备好 Inno 工具链 ───────────────────────────────────────
    Write-Host '==> 2/4 准备 Inno Setup 编译器'
    $iscc = Resolve-ISCC -Explicit $InnoPath -RepoRoot $repoRoot
    if (-not (Initialize-ChineseLanguage -IsccPath $iscc)) { throw '语言包准备失败' }

    # ── 3/4 编译安装包 ─────────────────────────────────────────────
    Write-Host '==> 3/4 编译安装包（ISCC）'
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue }

    & $iscc "/DAppVersion=$AppVersion" $issPath
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（退出码 $LASTEXITCODE）" }

    $setupExe = Join-Path $outDir "ControlServer-Setup-$AppVersion.exe"
    if (-not (Test-Path $setupExe)) { throw "未生成 $setupExe" }

    # ── 4/4 汇总 ───────────────────────────────────────────────────
    Write-Host '==> 4/4 汇总'
    $hash = (Get-FileHash -Path $setupExe -Algorithm SHA256).Hash
    $sizeMb = (Get-Item $setupExe).Length / 1MB

    # 构建标识落成文件（M6.4）：安装包文件名只有三段版本号，两次构建会重名（还会互相覆盖）。
    # 这个文件就是"手里这个 setup.exe 到底装的是哪一次构建"的凭证 —— 升级/回滚时拿它对启动自述。
    $stampFile = Join-Path $outDir 'BUILD-STAMP.txt'
    @(
        "安装包      : $(Split-Path -Leaf $setupExe)"
        "版本        : $AppVersion"
        "构建标识    : $BuildStamp"
        "完整版本    : $fullVersion"
        "构建时间    : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
        "SHA256      : $hash"
        "核对方法    : 装好后看 logs\server-<日期>.log 第一行的「程序版本：」，应为 $fullVersion"
    ) | Set-Content -Path $stampFile -Encoding UTF8

    Write-Host ''
    Write-Host '──────────── 安装包 ────────────'
    Write-Host ("  {0}" -f (Split-Path -Leaf $setupExe))
    Write-Host ("  大小    : {0:N1} MB" -f $sizeMb)
    Write-Host ("  SHA256  : {0}" -f $hash)
    Write-Host ("  构建标识: {0}（见 BUILD-STAMP.txt）" -f $BuildStamp)
    Write-Host '────────────────────────────────'
    Write-Host ''
    Write-Host '用法：'
    Write-Host "  · 桌面测试：右键「以管理员身份运行」$([IO.Path]::GetFileName($setupExe))"
    Write-Host ("  · 静默安装：{0} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -f (Split-Path -Leaf $setupExe))
    Write-Host '  · 装完打开：http://localhost:8899   默认账号 admin / admin123'
    Write-Host ''
    Write-Host '完成。'
    exit 0
}
catch {
    Write-Host ''
    Write-Host "[!!] 构建安装包失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
