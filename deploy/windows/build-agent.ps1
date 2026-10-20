#Requires -Version 5.1
<#
.SYNOPSIS
    客户端Server —— 发布自包含单文件的「服务侧 + 托盘侧」两个 exe，并组装成 dist\agent\。

.DESCRIPTION
    产出 <OutputDir>\ 下**恰好 2 个文件**：

      ControlServer.Agent.Service.exe   客户端服务（控制台子系统；SCM 拉起，也可前台排障）
      ControlServer.Agent.Tray.exe      托盘伴侣（WinExe；由当前登录用户会话启动）

    为什么必须两个进程：客户端服务跑在 **Session 0**，那里没有 explorer、没有通知区域，
    服务进程自己**不可能**显示托盘图标（这是 Windows 会话模型，不是实现问题）。

    ⚠️ 与绿色版服务端的关键差异：**客户端不开入站端口**，
       所有通信都是它连出去（UDP 广播发现 + SignalR 长连接）。
       唯一的例外是 **M4.5「UDP 指令通道」**（远程关机 / 重启 / 音量），
       它由中心下发端口（默认 8090，M4.6 起；旧版为 8080），安装脚本按**程序**放行一条入站 UDP 规则。
       除此之外安装时**不需要放行任何防火墙端口** —— 这是"客户机零配置"的实现基础。

    发布策略与 build-portable-exe.ps1 保持一致：
      --self-contained true -p:PublishSingleFile=true
      -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
    目标机**不需要**预装 .NET 运行时。

.NOTES
    本脚本刻意【不】使用 $ErrorActionPreference='Stop'：dotnet 的 stderr 警告
    不该被当成致命错误。统一用 $LASTEXITCODE + try/catch 收口。

    ⚠️ 函数必须定义在调用点之前 —— PowerShell 不支持前向引用，
       而 Continue 模式会把 CommandNotFoundException 静默吞掉（脚本"成功"却少做了事）。

.PARAMETER DotNetRoot
    本地 .NET 8 SDK 目录。本机默认：C:\Users\IYLONG\.workbuddy\binaries\dotnet8

.PARAMETER OutputDir
    发布输出目录。默认 <仓库根>\dist\agent

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-agent.ps1
.EXAMPLE
    # 不压缩（体积大但启动略快，排障时用）
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-agent.ps1 -NoCompression
#>
param(
    [string]$DotNetRoot = 'C:\Users\IYLONG\.workbuddy\binaries\dotnet8',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [switch]$NoCompression
)

# 见 .NOTES：不要用 Stop。
$ErrorActionPreference = 'Continue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$serviceProj = Join-Path $repoRoot 'agents\ControlServer.Agent.Service\ControlServer.Agent.Service.csproj'
$trayProj = Join-Path $repoRoot 'agents\ControlServer.Agent.Tray\ControlServer.Agent.Tray.csproj'

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "dist\agent" }

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

    foreach ($proj in @($serviceProj, $trayProj)) {
        if (-not (Test-Path $proj)) { throw "未找到工程文件：$proj" }
    }

    Write-Host "==> 仓库根：$repoRoot"
    Write-Host "==> SDK   ：$(& $dotnetExe --version)"

    # 版本号与服务端同源（Directory.Build.props 的 <Version>），安装包名要靠它。
    $version = (& $dotnetExe msbuild $serviceProj -getProperty:Version -nologo 2>$null | Where-Object { $_ -and $_.Trim() } | Select-Object -First 1)
    if (-not $version) { $version = '0.0.0' }
    $version = $version.Trim()
    Write-Host "==> 版本  ：$version"

    $singleFileProps = @(
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    if (-not $NoCompression) { $singleFileProps += '-p:EnableCompressionInSingleFile=true' }

    # ── 1. 清理输出目录 ──────────────────────────────────────
    Write-Host "==> 1/3 准备输出目录 $OutputDir"
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force -ErrorAction Stop }
    New-Item -ItemType Directory -Path $OutputDir -Force -ErrorAction Stop | Out-Null

    # ── 2. 发布两个 exe ──────────────────────────────────────
    Write-Host '==> 2/3 发布客户端服务（ControlServer.Agent.Service）'
    $serviceArgs = @('publish', $serviceProj, '-c', 'Release', '-r', $Runtime) + $singleFileProps + @('-o', $OutputDir)
    & $dotnetExe @serviceArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish（Agent.Service）失败（退出码 $LASTEXITCODE）" }

    Write-Host '==> 2/3 发布托盘伴侣（ControlServer.Agent.Tray）'
    $trayArgs = @('publish', $trayProj, '-c', 'Release', '-r', $Runtime) + $singleFileProps + @('-o', $OutputDir)
    & $dotnetExe @trayArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish（Agent.Tray）失败（退出码 $LASTEXITCODE）" }

    # ── 3. 校验产物 ──────────────────────────────────────────
    Write-Host '==> 3/3 校验产物'
    $files = @(Get-ChildItem -Path $OutputDir -Recurse -File)

    Write-Host ''
    Write-Host '──────────── 客户端Server（期望 2 文件）────────────'
    foreach ($f in $files) {
        Write-Host ("  {0,-34} {1,8:N1} MB" -f $f.Name, ($f.Length / 1MB))
    }
    Write-Host '────────────────────────────────────────────────────'

    $svcExe = Join-Path $OutputDir 'ControlServer.Agent.Service.exe'
    $trayExe = Join-Path $OutputDir 'ControlServer.Agent.Tray.exe'

    if (-not (Test-Path $svcExe)) { throw "未生成 $svcExe" }
    if (-not (Test-Path $trayExe)) { throw "未生成 $trayExe" }

    # 单文件模式被破坏时会散落 pdb / 原生 dll —— 那正是"手敲 dotnet publish"的典型产物。
    $unexpected = @($files | Where-Object {
            $_.Name -ne 'ControlServer.Agent.Service.exe' -and $_.Name -ne 'ControlServer.Agent.Tray.exe'
        })

    if ($unexpected.Count -gt 0) {
        Write-Warning "目录里有多余文件（$($unexpected.Count) 个）：$($unexpected.Name -join ', ')"
        Write-Warning '单文件模式可能被破坏，请检查 csproj 配置。'
        exit 1
    }

    Write-Host '[OK] 客户端Server 产物正常：2 个 exe，单文件模式未被破坏。'

    # 版本号落一个便签文件给 build-agent-installer.ps1 复用（免得两处各算一遍、还算出两个值）。
    # ⚠️ 刻意放在 dist\ 而不是 dist\agent\ 里 —— "恰好 2 个文件"是交付约定，不能掺别的。
    $versionFile = Join-Path (Split-Path $OutputDir -Parent) 'agent-version.txt'
    Set-Content -Path $versionFile -Value $version -Encoding ASCII -ErrorAction SilentlyContinue

    Write-Host ''
    Write-Host '── 下一步 ──────────────────────────────────────'
    Write-Host "  装到本机（管理员）：powershell -ExecutionPolicy Bypass -File deploy\windows\install-agent.ps1"
    Write-Host '  打成安装包：        powershell -ExecutionPolicy Bypass -File deploy\windows\build-agent-installer.ps1'
    Write-Host ''
    Write-Host '完成。'

    exit 0
}
catch {
    Write-Host ''
    Write-Host "[!!] 客户端Server 打包失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
