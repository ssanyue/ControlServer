#Requires -Version 5.1
<#
.SYNOPSIS
    客户端Server —— 一键生成 Windows 安装包，并（可选）直接投放到服务端的 downloads 目录。

.DESCRIPTION
    流程：发布 2 个 exe → 备 Inno 工具链 → 编译安装包 → 投放到服务端下载目录 → 打印摘要。

    产物：dist\agent-setup\ControlServer-Agent-Setup-<版本>.exe

    ⚠️ 文件名**必须**遵循 `ControlServer-Agent-Setup-<版本>.exe` 这个约定 ——
       服务端 AgentInstallerLocator 靠这个前缀解析版本号，并据此提示
       "安装包与服务端主次版本不一致"。改了名，网页右上角那个下载按钮
       就会退化成"随便找一个 exe"。

    ⚠️ 投放目标默认是 %ProgramData%\ControlServer\downloads（服务端数据目录下的下载区）。
       **不要**把它放进 dist\win-x64\ —— 那个目录"恰好 1 个文件"是绿色版的承诺，
       build-portable-exe.ps1 有硬校验，塞进去必然报错。

    🔓 关于准入（2026-10-13 变更）：客户端注册**不再需要任何凭据** ——
       安装包无需烘焙令牌或加入码，装完即自动发现并注册上线。
       安全边界因此落在"谁能访问中心服务端"上：请确保 8899 只在局域网内可达。
       分发安装包 = 允许对方接入，无需额外告知凭据。

.NOTES
    沿用本目录其它脚本的约定：**不用** $ErrorActionPreference='Stop'
    （原生工具警告走 stderr 会被误判成致命错误），统一 $LASTEXITCODE + try/catch 收口；
    用户可见输出**禁用** ✓/✗/⚠ 符号（不在系统 GBK 代码页里，控制台会显示成问号）。

.PARAMETER AppVersion
    写进安装包的版本号。默认读 dist\agent-version.txt（build-agent.ps1 留的便签），
    读不到再退回 Directory.Build.props 的 <Version>。

.PARAMETER Server
    烘焙进安装包的服务端地址（可选）。留空 = 靠 UDP 广播自动发现。

.PARAMETER PublishTo
    安装包投放目录。默认 %ProgramData%\ControlServer\downloads。

.PARAMETER PayloadDir
    两个 exe 的目录。默认 dist\agent。

    🔥 **为什么要留这个口子**：某些受限环境（CI 容器、被安全策略盯上的目录、
    或者本机 dist 目录被别的进程/策略占着）里，`dotnet publish` 的单文件打包
    会在**最后一步**失败 —— 报的是 `MSB4018 GenerateBundle 意外失败` +
    `UnauthorizedAccessException: Access to the path ... is denied`。
    那一步是 HostWriter 把 exe 自己打开做内存映射、回填单文件 header
    （`AdjustHeadersForBundle`），**不是**目录不可写：同一目录里新建文件是成功的，
    只有"打开已有 exe 原地写"被拒。

    ⇒ 先 `build-agent.ps1 -OutputDir <可写目录>`，再用
    `-SkipPayloadBuild -PayloadDir <可写目录>` 打安装包。

.PARAMETER SkipPayloadBuild
    跳过 build-agent.ps1，直接复用 PayloadDir（默认 dist\agent）里现成的两个 exe。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-agent-installer.ps1

.EXAMPLE
    # 批量部署：把服务端地址烘焙进安装包
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-agent-installer.ps1 `
        -Server http://10.0.0.5:8899

.EXAMPLE
    # 只编译，不投放到服务端下载目录
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-agent-installer.ps1 -PublishTo ''
#>
param(
    [string]$DotNetRoot = 'C:\Users\IYLONG\.workbuddy\binaries\dotnet8',
    [string]$AppVersion,
    [string]$Server = '',
    [string]$PublishTo,
    [string]$PayloadDir,
    [string]$InnoPath,
    [switch]$SkipPayloadBuild,
    [switch]$NoCompression
)

$ErrorActionPreference = 'Continue'

$InnoPackageVersion = '6.7.3'
$IslUrls = @(
    'https://raw.githubusercontent.com/kira-96/Inno-Setup-Chinese-Simplified-Translation/main/ChineseSimplified.isl',
    'https://raw.githubusercontent.com/kira-96/Inno-Setup-Chinese-Simplified-Translation/master/ChineseSimplified.isl'
)

# ═══════════════════════════════════════════════════════════════════════════
# 函数（PowerShell 不支持前向引用，必须全部前置）
#
# 下面三个工具函数与 build-installer.ps1 里的同名函数是同一套实现 ——
# 刻意各自内联而不是共享模块：这两个脚本都要能在现场**单独复制走就能跑**。
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
        Write-Host '[!!] 下载简体中文语言包失败（网络不通？）'
        Write-Host '     安装包的 .iss 引用了 compiler:Languages\ChineseSimplified.isl，缺它无法编译。'
        Write-Host "     手工修法：下载 ChineseSimplified.isl 放到 $langDir"
        foreach ($u in $IslUrls) { Write-Host "       $u" }
        throw '简体中文语言包不可用'
    }

    Write-Host "==> 简体中文语言包已下载：$isl"
    return $true
}

<#
.SYNOPSIS
    解析要写进安装包的版本号：显式参数 > dist\agent-version.txt > Directory.Build.props。
#>
function Resolve-AppVersion {
    param(
        [string]$Explicit,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    if ($Explicit) { return $Explicit.Trim() }

    $stamp = Join-Path $RepoRoot 'dist\agent-version.txt'
    if (Test-Path $stamp) {
        $v = (Get-Content -Path $stamp -Raw -ErrorAction SilentlyContinue)
        if ($v -and $v.Trim()) { return $v.Trim() }
    }

    $props = Join-Path $RepoRoot 'Directory.Build.props'
    if (Test-Path $props) {
        $m = [regex]::Match((Get-Content -Path $props -Raw), '<Version>\s*([^<\s]+)\s*</Version>')
        if ($m.Success) { return $m.Groups[1].Value }
    }

    return '0.1.0'
}

# ═══════════════════════════════════════════════════════════════════════════
# 主体
# ═══════════════════════════════════════════════════════════════════════════

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$issPath = Join-Path $PSScriptRoot 'ControlServer.Agent.iss'
$payloadDir = if ($PayloadDir) {
    if ([System.IO.Path]::IsPathRooted($PayloadDir)) { $PayloadDir } else { Join-Path $repoRoot $PayloadDir }
} else { Join-Path $repoRoot 'dist\agent' }
$outDir = Join-Path $repoRoot 'dist\agent-setup'
$payloadExes = @('ControlServer.Agent.Service.exe', 'ControlServer.Agent.Tray.exe')

try {
    $AppVersion = Resolve-AppVersion -Explicit $AppVersion -RepoRoot $repoRoot

    # ⚠️ 必须用 $PSBoundParameters 判断"传没传"，不能靠 `-not $PublishTo`：
    #    PowerShell 会把**未绑定**的 [string] 参数初始化成空字符串（而不是 $null），
    #    所以「没传」和「显式传 -PublishTo ''（=只编译不投放）」在值上完全一样。
    #    第一版就是这么写的 —— 结果默认投放**从未生效**，却打印"跳过投放"。
    if (-not $PSBoundParameters.ContainsKey('PublishTo')) {
        $PublishTo = Join-Path $env:ProgramData 'ControlServer\downloads'
    }

    Write-Host "==> 仓库根：$repoRoot"
    Write-Host "==> 版本号：$AppVersion"
    if ($Server) { Write-Host "==> 烘焙服务端地址：$Server" }

    if (-not (Test-Path $issPath)) { throw "找不到 Inno 脚本：$issPath" }

    # ── 1/5 准备程序产物（payload） ────────────────────────────────
    $missing = @($payloadExes | Where-Object { -not (Test-Path (Join-Path $payloadDir $_)) })

    if ($SkipPayloadBuild) {
        if ($missing.Count -gt 0) {
            throw "$payloadDir 里缺少：$($missing -join ', ')。去掉 -SkipPayloadBuild 让它自动重新发布。"
        }
        Write-Host '==> 1/5 复用现成 payload（-SkipPayloadBuild）'
    }
    else {
        Write-Host '==> 1/5 发布客户端Server（build-agent.ps1）'

        # ⚠️ 必须用**哈希表** splat（按名字传参），不能用数组 splat（按位置传参）——
        #    数组 splat 的元素不会被当成参数名，被调脚本会拿到 DotNetRoot='-DotNetRoot'。
        #    （build-installer.ps1 里踩过这个坑，见那边的长注释。）
        $agentArgs = @{
            DotNetRoot = $DotNetRoot
            OutputDir  = $payloadDir
        }
        if ($NoCompression) { $agentArgs['NoCompression'] = $true }

        & (Join-Path $PSScriptRoot 'build-agent.ps1') @agentArgs
        if ($LASTEXITCODE -ne 0) { throw "build-agent.ps1 失败（退出码 $LASTEXITCODE）" }

        foreach ($f in $payloadExes) {
            if (-not (Test-Path (Join-Path $payloadDir $f))) { throw "发布后仍缺少 $f" }
        }
    }

    foreach ($f in $payloadExes) {
        $p = Join-Path $payloadDir $f
        Write-Host ("    {0,-34} {1,8:N1} MB" -f $f, ((Get-Item $p).Length / 1MB))
    }

    # ── 2/5 备好 Inno 工具链 ───────────────────────────────────────
    Write-Host '==> 2/5 准备 Inno Setup 编译器'
    $iscc = Resolve-ISCC -Explicit $InnoPath -RepoRoot $repoRoot
    if (-not (Initialize-ChineseLanguage -IsccPath $iscc)) { throw '语言包准备失败' }

    # ── 3/5 编译安装包 ─────────────────────────────────────────────
    Write-Host '==> 3/5 编译安装包（ISCC）'
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue }

    # ⚠️ /D 的值里不能有裸引号；Inno 的 #define 是"文本替换"，我们只在双方都是
    #    非空时才传，空值就让它走 .iss 里的默认 #ifndef。
    $isccArgs = @("/DAppVersion=$AppVersion", $issPath)
    if ($Server) { $isccArgs = @("/DEmbeddedServer=$Server") + $isccArgs }

    # ⚠️ 只在显式传了 -PayloadDir 时才覆盖 .iss 里的默认值：默认路径仍由 .iss 自己
    #    用相对路径算（跟着 .iss 走，整个仓库挪盘也不会错）。这里传的是绝对路径。
    if ($PayloadDir) { $isccArgs = @("/DPayloadDir=$payloadDir") + $isccArgs }

    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（退出码 $LASTEXITCODE）" }

    $setupExe = Join-Path $outDir "ControlServer-Agent-Setup-$AppVersion.exe"
    if (-not (Test-Path $setupExe)) { throw "未生成 $setupExe" }

    # ── 4/5 投放到服务端下载目录 ───────────────────────────────────
    if ($PublishTo) {
        Write-Host "==> 4/5 投放到服务端下载目录 $PublishTo"

        # 数据目录不存在 = 本机还没装服务端。不报错，只提示 —— 这台机器可能只是"构建机"。
        $dataRoot = Split-Path -Parent $PublishTo
        if (Test-Path $dataRoot) {
            New-Item -ItemType Directory -Path $PublishTo -Force | Out-Null

            # 同一主次版本只留一个：否则现场会看到一堆 Setup-0.1.0/0.1.1/0.1.2，
            # 而 AgentInstallerLocator 是"按写入时间取最新" —— 留旧文件只会制造困惑。
            $old = @(Get-ChildItem -Path $PublishTo -Filter 'ControlServer-Agent-Setup-*.exe' -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -ne $setupExe })
            foreach ($f in $old) {
                Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
                Write-Host "    已移除旧安装包：$($f.Name)"
            }

            Copy-Item $setupExe $PublishTo -Force
            Write-Host '    [OK] 已投放，网页右上角「客户端安装包」按钮即刻可下载。'
        }
        else {
            Write-Host "    [--] 服务端数据目录不存在（$dataRoot），跳过投放。"
            Write-Host '         本机装过服务端后再投，或手工复制到该目录。'
        }
    }
    else {
        Write-Host '==> 4/5 跳过投放（-PublishTo 为空）'
    }

    # ── 5/5 汇总 ───────────────────────────────────────────────────
    Write-Host '==> 5/5 汇总'
    $hash = (Get-FileHash -Path $setupExe -Algorithm SHA256).Hash
    $sizeMb = (Get-Item $setupExe).Length / 1MB

    Write-Host ''
    Write-Host '──────────── 客户端安装包 ────────────'
    Write-Host ("  {0}" -f (Split-Path -Leaf $setupExe))
    Write-Host ("  大小    : {0:N1} MB" -f $sizeMb)
    Write-Host ("  SHA256  : {0}" -f $hash)
    Write-Host '──────────────────────────────────────'
    Write-Host ''
    Write-Host '用法：'
    Write-Host "  · 图形安装：右键「以管理员身份运行」$(Split-Path -Leaf $setupExe)"
    Write-Host ("  · 静默安装：{0} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -f (Split-Path -Leaf $setupExe))
    if (-not $Server) {
        Write-Host '  · 静默时还可临时指定：/SERVER=http://10.0.0.5:8899'
    }
    Write-Host '  · 装完：客户机会自动发现局域网内的中心服务端并直接注册，出现在网页「客户端」页（无需任何令牌/加入码）'
    Write-Host ''
    Write-Host '完成。'

    exit 0
}
catch {
    Write-Host ''
    Write-Host "[!!] 客户端安装包构建失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
