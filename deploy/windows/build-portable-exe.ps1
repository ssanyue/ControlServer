#Requires -Version 5.1
<#
.SYNOPSIS
    中控服务版 —— 生成「双击即用」的绿色版单文件 exe，并（可选）一并产出服务版的托盘助手。

.DESCRIPTION
    产出两套产物：

    【绿色版】<OutputDir>\ControlServer.Server.exe —— **严格单文件**
      · 前端产物（wwwroot）已嵌入 exe（见 ControlServer.Server.csproj）
      · 运行时不依赖系统 .NET（self-contained）
      · 双击即启动服务，并自动用默认浏览器打开管理界面
      · 右下角托盘图标（服务自持）：右键菜单可「打开管理界面 / 退出服务」
      · 停服务 = 托盘右键「退出服务」；**关掉浏览器/控制台不会停服务**
      · 默认开启单文件压缩，体积约 49MB

    【服务版】<ServiceOutputDir>\ —— 2 个文件
      · ControlServer.Server.exe（与绿色版同一个 exe，直接复制）
      · ControlServer.TrayAgent.exe（托盘助手，约 34MB）
      · 服务跑在 Session 0 没有通知区域，所以托盘必须由用户在会话里的助手进程提供。
        安装脚本 install-service.ps1 会部署它并注册 HKCU 启动项。

    默认流程：构建前端 → 发布绿色版单文件 → 发布托盘助手 → 校验两个目录的文件数。

.NOTES
    本脚本刻意【不】使用 $ErrorActionPreference='Stop'：
    vite / rollup 会把正常警告写到 stderr（如 /*#__PURE__*/ 注解提示），
    在 Stop 模式下会被当成致命错误，导致脚本"明明成功却报失败"。
    改为统一用 $LASTEXITCODE 判断原生命令成败，并用 try/catch 统一收口。

.PARAMETER DotNetRoot
    本地 .NET 8 SDK 目录（本机系统只有运行时，没有 SDK）。
    本机默认：C:\Users\IYLONG\.workbuddy\binaries\dotnet8

.PARAMETER NodeRoot
    本地 Node 目录（用于构建前端）。传空字符串可跳过前端构建。

.PARAMETER OutputDir
    绿色版发布输出目录。默认 <仓库根>\dist\win-x64

.PARAMETER ServiceOutputDir
    服务版发布输出目录。默认 <仓库根>\dist\win-x64-service

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-portable-exe.ps1
.EXAMPLE
    # 只重新发布，不重跑前端构建
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-portable-exe.ps1 -SkipWebBuild
.EXAMPLE
    # 只要绿色版单文件（跳过托盘助手与服务版目录）
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-portable-exe.ps1 -SkipServiceOutput

.NOTES
    -BuildStamp（M6.4）：注入 -p:SourceRevisionId，SDK 会把它拼成
    InformationalVersion = <props 里的 Version>+<BuildStamp>，启动自述打印"程序版本：0.1.0+build.…"。
    没有它，升级前后日志里那一行完全一样，等于没有版本号。
#>
param(
    [string]$DotNetRoot = 'C:\Users\IYLONG\.workbuddy\binaries\dotnet8',
    [string]$NodeRoot = 'C:\Users\IYLONG\.workbuddy\binaries\node\versions\22.22.2-6',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [string]$ServiceOutputDir,
    [string]$BuildStamp,
    [switch]$SkipWebBuild,
    [switch]$SkipServiceOutput,
    [switch]$NoCompression
)

# 见 .NOTES：不要用 Stop，否则原生工具的 stderr 警告会误杀脚本。
$ErrorActionPreference = 'Continue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$serverProj = Join-Path $repoRoot 'src\ControlServer.Server\ControlServer.Server.csproj'
$trayProj = Join-Path $repoRoot 'src\ControlServer.TrayAgent\ControlServer.TrayAgent.csproj'
$webDir = Join-Path $repoRoot 'web'
$wwwrootDir = Join-Path $repoRoot 'src\ControlServer.Server\wwwroot'

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "dist\$Runtime" }
if (-not $ServiceOutputDir) { $ServiceOutputDir = Join-Path $repoRoot "dist\$Runtime-service" }

# 构建标识（M6.4）：升级/回滚后靠它区分"跑的是哪个包"。
# 格式里【不放空格与冒号】—— 它会被拼进文件名、命令行参数与日志，冒号在 -p: 里是分隔符。
if (-not $BuildStamp) { $BuildStamp = 'build.' + (Get-Date -Format 'yyyyMMdd-HHmm') }

try {
    # ── 0. 环境检查 ──────────────────────────────────────────
    $dotnetExe = Join-Path $DotNetRoot 'dotnet.exe'
    if (-not (Test-Path $dotnetExe)) {
        throw "未找到 .NET SDK：$dotnetExe。请用 -DotNetRoot 指定 SDK 目录。"
    }
    $env:DOTNET_ROOT = $DotNetRoot
    $env:PATH = "$DotNetRoot;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    Write-Host "==> 仓库根：$repoRoot"
    Write-Host "==> SDK   ：$(& $dotnetExe --version)"

    # ── 1. 构建前端 ──────────────────────────────────────────
    $canBuildWeb = (-not $SkipWebBuild) -and $NodeRoot -and (Test-Path (Join-Path $NodeRoot 'npm.cmd'))

    if ($canBuildWeb) {
        Write-Host '==> 1/4 构建前端（Vite → wwwroot）'
        $env:PATH = "$NodeRoot;$env:PATH"
        Push-Location $webDir
        try {
            if (-not (Test-Path (Join-Path $webDir 'node_modules'))) {
                Write-Host '    安装前端依赖（--ignore-scripts：跳过 esbuild postinstall，避免 EBUSY）'
                & npm install --ignore-scripts --no-audit --no-fund
                if ($LASTEXITCODE -ne 0) { throw "npm install 失败（退出码 $LASTEXITCODE）" }
            }
            & npm run build
            if ($LASTEXITCODE -ne 0) { throw "npm run build 失败（退出码 $LASTEXITCODE）" }
        }
        finally {
            Pop-Location
        }
    }
    elseif ($SkipWebBuild) {
        Write-Host '==> 1/4 跳过前端构建（-SkipWebBuild）'
    }
    else {
        Write-Warning "未找到 npm（$NodeRoot\npm.cmd），跳过前端构建。"
        Write-Host '==> 1/4 跳过前端构建（无 npm）'
    }

    # 前端产物必须存在，否则嵌入的 wwwroot 是空的 —— 启动后首页 404。
    $indexHtml = Join-Path $wwwrootDir 'index.html'
    if (-not (Test-Path $indexHtml)) {
        throw "前端产物缺失：$indexHtml 未生成。请先执行前端构建（去掉 -SkipWebBuild）。"
    }
    Write-Host "    前端产物就绪：$wwwrootDir"

    # 发布参数（Server / TrayAgent 共用同一套单文件策略）
    # PublishTrimmed=false：SignalR / EF Core 反射多，裁剪易踩坑；TrayAgent 也用同一策略保持一致。
    $singleFileProps = @(
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        # SDK 会拼成 InformationalVersion = <Directory.Build.props 的 Version>+<BuildStamp>。
        # 版本号仍只有 Directory.Build.props 一处真源，这里只往它后面挂"这一包是什么时候打的"。
        "-p:SourceRevisionId=$BuildStamp"
    )
    Write-Host "==> 构建标识：$BuildStamp（启动自述打印成 <版本>+$BuildStamp）"

    # ── 2. 发布绿色版单文件 ──────────────────────────────────
    Write-Host "==> 2/4 发布自包含单文件 exe → $OutputDir"
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force -ErrorAction Stop }
    New-Item -ItemType Directory -Path $OutputDir -Force -ErrorAction Stop | Out-Null

    $publishArgs = @('publish', $serverProj, '-c', 'Release', '-r', $Runtime) + $singleFileProps + @('-o', $OutputDir)
    if (-not $NoCompression) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }

    & $dotnetExe @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish（Server）失败（退出码 $LASTEXITCODE）" }

    # ── 3. 发布托盘助手 + 组装服务版目录 ─────────────────────
    if (-not $SkipServiceOutput) {
        Write-Host "==> 3/4 发布托盘助手并组装服务版目录 → $ServiceOutputDir"

        if (Test-Path $ServiceOutputDir) { Remove-Item $ServiceOutputDir -Recurse -Force -ErrorAction Stop }
        New-Item -ItemType Directory -Path $ServiceOutputDir -Force -ErrorAction Stop | Out-Null

        $trayArgs = @('publish', $trayProj, '-c', 'Release', '-r', $Runtime) + $singleFileProps + @('-o', $ServiceOutputDir)
        if (-not $NoCompression) { $trayArgs += '-p:EnableCompressionInSingleFile=true' }

        & $dotnetExe @trayArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish（TrayAgent）失败（退出码 $LASTEXITCODE）" }

        # 服务版里的 Server.exe 直接用绿色版那一个 —— 保证两者字节一致，不会出现"两个版本"。
        Copy-Item (Join-Path $OutputDir 'ControlServer.Server.exe') $ServiceOutputDir -Force
        Write-Host '    已复制 ControlServer.Server.exe（与绿色版同一份产物）'
    }
    else {
        Write-Host '==> 3/4 跳过托盘助手与服务版目录（-SkipServiceOutput）'
    }

    # ── 4. 校验产物 ──────────────────────────────────────────
    Write-Host '==> 4/4 校验产物'
    $files = @(Get-ChildItem -Path $OutputDir -Recurse -File)
    $exe = Join-Path $OutputDir 'ControlServer.Server.exe'

    Write-Host ''
    Write-Host '──────────── 绿色版（单文件）────────────'
    foreach ($f in $files) {
        Write-Host ("  {0,-32} {1,8:N1} MB" -f $f.Name, ($f.Length / 1MB))
    }
    Write-Host '────────────────────────────────────────'

    if (-not (Test-Path $exe)) { throw "未生成 $exe" }

    if ($files.Count -gt 1) {
        Write-Warning "绿色版目录含 $($files.Count) 个文件 —— 单文件模式被破坏。请检查 csproj 的 Content / EmbeddedResource 配置。"
    }
    else {
        Write-Host '[OK] 绿色版单文件模式正常：一个 exe 即可分发。'
    }

    if (-not $SkipServiceOutput) {
        $svcFiles = @(Get-ChildItem -Path $ServiceOutputDir -Recurse -File)
        Write-Host ''
        Write-Host '──────────── 服务版（2 文件）────────────'
        foreach ($f in $svcFiles) {
            Write-Host ("  {0,-32} {1,8:N1} MB" -f $f.Name, ($f.Length / 1MB))
        }
        Write-Host '────────────────────────────────────────'

        $trayExe = Join-Path $ServiceOutputDir 'ControlServer.TrayAgent.exe'
        if (-not (Test-Path $trayExe)) { throw "未生成 $trayExe" }

        # 只该有 2 个文件：Server.exe（含 wwwroot/pdb 已内嵌）+ TrayAgent.exe。
        $unexpected = @($svcFiles | Where-Object {
                $_.Name -ne 'ControlServer.Server.exe' -and $_.Name -ne 'ControlServer.TrayAgent.exe'
            })
        if ($unexpected.Count -gt 0) {
            Write-Warning "服务版目录含意外文件（$($unexpected.Count) 个）：$($unexpected.Name -join ', ')"
        }
        elseif ($svcFiles.Count -eq 2) {
            Write-Host '[OK] 服务版目录正常：2 个 exe（Server + 托盘助手）。'
        }
    }

    Write-Host ''
    Write-Host '── 用法 ────────────────────────────────────────'
    Write-Host "【绿色版 · 双击即用】$exe"
    Write-Host '  启动后自动打开浏览器，默认 http://localhost:8899 ，账号 admin / admin123'
    Write-Host '  右下角托盘图标：右键 →「打开管理界面」/「退出服务」'
    Write-Host '  停止服务 = 托盘右键「退出服务」（关掉网页不会停服务）'
    Write-Host '  改端口/账号：在 exe 同目录放一个 appsettings.json（模板见仓库根 appsettings.sample.json）'
    Write-Host ''
    if (-not $SkipServiceOutput) {
        Write-Host '【服务版 · 装成开机自启的 Windows 服务】'
        Write-Host "  1) 以管理员执行：powershell -ExecutionPolicy Bypass -File deploy\windows\install-service.ps1"
        Write-Host '  2) 托盘助手会自动部署并注册为当前用户的开机启动项'
        Write-Host '  3) 停止服务 = 托盘右键「停止服务」'
        Write-Host ''
    }
    Write-Host '完成。'

    exit 0
}
catch {
    Write-Host ''
    Write-Host "[!!] 打包失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
