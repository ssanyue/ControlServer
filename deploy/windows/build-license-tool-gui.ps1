#Requires -Version 5.1
<#
.SYNOPSIS
    中控服务版 —— 生成厂商侧的「授权签发工具（图形界面）」绿色版单文件 exe。

.DESCRIPTION
    产出 <OutputDir>\ControlServer.LicenseTool.Gui.exe —— **严格单文件**、自包含运行时，
    拷到任何一台 Windows 机器上**双击即可用**，不需要装 .NET，也不需要会敲命令。

    与命令行版（deploy\windows\build-license-tool.ps1）的关系：
      · 命令行版给脚本/CI 用，参数化；
      · 本图形界面给"销售 / 现场支持"用，双击就能操作。
    两者都把规则交给 ControlServer.Core，因此"签出来的东西"完全一致。

    ⚠️ 这个 exe 是【厂商内部工具】，**绝不可**随服务端一起交付给客户：
       · 厂商私钥已【内嵌】进这个 exe —— 它本身就是签发权，必须当机密保管；
       · 把它送到客户手上 = 把"签发能力"送到客户手上：拿到它的人可以签出
         「任意机器、任意期限」的授权，整套授权体系当场归零。

    默认流程：发布单文件 → 校验目录里只有一个文件 → 冒烟启动一次确认不崩。

.PARAMETER DotNetRoot
    本地 .NET 8 SDK 目录（本机系统只有运行时，没有 SDK）。
    本机默认：C:\Users\IYLONG\.workbuddy\binaries\dotnet8

.PARAMETER Runtime
    目标运行时标识。默认 win-x64

.PARAMETER OutputDir
    输出目录。默认 <仓库根>\dist\license-tool-gui

.PARAMETER NoCompression
    关闭单文件压缩（体积更大，换来发布速度快很多）。调试期反复发布时用它。

.PARAMETER SkipSmoke
    跳过"启动一次确认不崩"的冒烟（例如在无人值守的机器上跑构建）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-license-tool-gui.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-license-tool-gui.ps1 -NoCompression -SkipSmoke
#>
param(
    [string]$DotNetRoot = 'C:\Users\IYLONG\.workbuddy\binaries\dotnet8',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [switch]$NoCompression,
    [switch]$SkipSmoke
)

# 与其它打包脚本一致：不要用 Stop，原生工具的 stderr 警告会误杀脚本。
$ErrorActionPreference = 'Continue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$toolProj = Join-Path $repoRoot 'tools\ControlServer.LicenseTool.Gui\ControlServer.LicenseTool.Gui.csproj'
$exeName = 'ControlServer.LicenseTool.Gui.exe'

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist\license-tool-gui' }

try {
    # ── 0. 环境检查 ──────────────────────────────────────────
    $dotnetExe = Join-Path $DotNetRoot 'dotnet.exe'
    if (-not (Test-Path $dotnetExe)) {
        throw "未找到 .NET SDK：$dotnetExe。请用 -DotNetRoot 指定 SDK 目录。"
    }
    if (-not (Test-Path $toolProj)) {
        throw "未找到图形界面项目：$toolProj"
    }

    $env:DOTNET_ROOT = $DotNetRoot
    $env:PATH = "$DotNetRoot;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    Write-Host "==> 仓库根：$repoRoot"
    Write-Host "==> SDK   ：$(& $dotnetExe --version)"

    # ── 1. 发布单文件 ────────────────────────────────────────
    Write-Host "==> 1/3 发布自包含单文件 exe -> $OutputDir"

    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force -ErrorAction Stop }
    New-Item -ItemType Directory -Path $OutputDir -Force -ErrorAction Stop | Out-Null

    # DebugType=None + DebugSymbols=false：不加这两个，pdb 会散落在产物目录里，破坏单文件形态。
    # PublishTrimmed=false：**WPF 不支持裁剪**，且与 Server / TrayAgent 保持同一策略。
    # IncludeNativeLibrariesForSelfExtract=true：WPF 带原生组件（wpfgfx_cor3.dll 等），
    #   不把它们收进自解压包的话，单文件在干净机器上会起不来。
    $publishArgs = @(
        'publish', $toolProj, '-c', 'Release', '-r', $Runtime,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $OutputDir
    )

    if (-not $NoCompression) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }

    & $dotnetExe @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish（LicenseTool.Gui）失败（退出码 $LASTEXITCODE）" }

    # ── 2. 校验产物 ──────────────────────────────────────────
    Write-Host '==> 2/3 校验产物'

    $files = @(Get-ChildItem -Path $OutputDir -Recurse -File)
    $exe = Join-Path $OutputDir $exeName

    Write-Host ''
    Write-Host '──────────── 签发工具·图形界面（单文件）────────────'
    foreach ($f in $files) {
        Write-Host ("  {0,-40} {1,8:N1} MB" -f $f.Name, ($f.Length / 1MB))
    }
    Write-Host '──────────────────────────────────────────────────'

    if (-not (Test-Path $exe)) { throw "未生成 $exe" }

    if ($files.Count -gt 1) {
        Write-Warning "产物目录含 $($files.Count) 个文件 —— 单文件模式被破坏。请检查 WPF 的发布参数。"
    }
    else {
        Write-Host '[OK] 单文件模式正常：一个 exe 即可分发。'
    }

    # ── 3. 冒烟：真的能起来 ──────────────────────────────────
    if ($SkipSmoke) {
        Write-Host '==> 3/3 冒烟：已按 -SkipSmoke 跳过'
    }
    else {
        Write-Host '==> 3/3 冒烟：启动一次，确认不会立刻退出'

        # 图形界面没有 --help 可跑，能证明"发布没缺组件"的唯一办法就是真启动一次：
        # 缺运行时/WPF 原生库时进程会在 1 秒内退出。
        $proc = Start-Process -FilePath $exe -PassThru
        Start-Sleep -Seconds 4

        if ($proc.HasExited) {
            throw "进程启动后立刻退出（退出码 $($proc.ExitCode)）—— 多半是单文件发布漏了 WPF 原生组件。"
        }

        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Write-Host '    [ok] 窗口进程保持运行，未崩溃。'
    }

    $global:LASTEXITCODE = 0

    Write-Host ''
    Write-Host '── 用法 ─────────────────────────────────────────'
    Write-Host "【图形界面】$exe"
    Write-Host '  双击即可打开，只有一个页面：填表 → 点「签发」。'
    Write-Host '  默认到期日是 3 天后，旁边一排「快捷到期日」可一键设成 1/2/3/6 个月或 1 年。'
    Write-Host '  通用授权的「使用截止」默认只到当天 23:59:59，要更长的导入窗口请自己改日期。'
    Write-Host ''
    Write-Host '  无需任何密钥文件：厂商密钥已内嵌，打开就能签。'
    Write-Host '  换钥属于厂商侧一次性维护，走命令行版的 new-key（见 docs\授权操作手册.md）。'
    Write-Host ''
    Write-Host '  需要命令行的场合（脚本、CI）仍用命令行版：'
    Write-Host '    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-license-tool.ps1'
    Write-Host ''
    Write-Host '[!!] 这个 exe 是厂商内部工具，不要随服务端交付给客户。'
    Write-Host ''
    Write-Host '完成。'

    exit 0
}
catch {
    Write-Host ''
    Write-Host "[!!] 打包失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
