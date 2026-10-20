#Requires -Version 5.1
<#
.SYNOPSIS
    中控服务版 —— 生成厂商侧的「授权签发工具」绿色版单文件 exe。

.DESCRIPTION
    产出 <OutputDir>\ControlServer.LicenseTool.exe —— **严格单文件**，自包含运行时，
    拷到任何一台 Windows 机器上双击/命令行即可用，不需要装 .NET。

    ⚠️ 这个 exe 是【厂商内部工具】，**绝不可**随服务端一起交付给客户：
       · 厂商私钥已【内嵌】进这个 exe —— 它本身就是签发权，必须当机密保管；
       · 把它送到客户手上 = 把"签发能力"送到客户手上，
         等于告诉客户"你也能自己造授权"。

    默认流程：发布单文件 → 校验目录里只有一个文件 → 冒烟跑一次 --version。

.PARAMETER DotNetRoot
    本地 .NET 8 SDK 目录（本机系统只有运行时，没有 SDK）。
    本机默认：C:\Users\IYLONG\.workbuddy\binaries\dotnet8

.PARAMETER Runtime
    目标运行时标识。默认 win-x64

.PARAMETER OutputDir
    输出目录。默认 <仓库根>\dist\license-tool

.PARAMETER NoCompression
    关闭单文件压缩（体积约为压缩后的 1.4 倍，换来发布速度快很多）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-license-tool.ps1
.EXAMPLE
    # 调试期反复发布时用它，省掉压缩的等待
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-license-tool.ps1 -NoCompression
#>
param(
    [string]$DotNetRoot = 'C:\Users\IYLONG\.workbuddy\binaries\dotnet8',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [switch]$NoCompression
)

# 与 build-portable-exe.ps1 一致：不要用 Stop，原生工具的 stderr 警告会误杀脚本。
$ErrorActionPreference = 'Continue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$toolProj = Join-Path $repoRoot 'tools\ControlServer.LicenseTool\ControlServer.LicenseTool.csproj'

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "dist\license-tool" }

try {
    # ── 0. 环境检查 ──────────────────────────────────────────
    $dotnetExe = Join-Path $DotNetRoot 'dotnet.exe'
    if (-not (Test-Path $dotnetExe)) {
        throw "未找到 .NET SDK：$dotnetExe。请用 -DotNetRoot 指定 SDK 目录。"
    }
    if (-not (Test-Path $toolProj)) {
        throw "未找到签发工具项目：$toolProj"
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
    # PublishTrimmed=false：与 Server / TrayAgent 保持同一策略，避免两边行为分叉。
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
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish（LicenseTool）失败（退出码 $LASTEXITCODE）" }

    # ── 2. 校验产物 ──────────────────────────────────────────
    Write-Host '==> 2/3 校验产物'

    $files = @(Get-ChildItem -Path $OutputDir -Recurse -File)
    $exe = Join-Path $OutputDir 'ControlServer.LicenseTool.exe'

    Write-Host ''
    Write-Host '──────────── 签发工具（单文件）────────────'
    foreach ($f in $files) {
        Write-Host ("  {0,-32} {1,8:N1} MB" -f $f.Name, ($f.Length / 1MB))
    }
    Write-Host '─────────────────────────────────────────'

    if (-not (Test-Path $exe)) { throw "未生成 $exe" }

    if ($files.Count -gt 1) {
        Write-Warning "产物目录含 $($files.Count) 个文件 —— 单文件模式被破坏。请检查工具 csproj 的输出配置。"
    }
    else {
        Write-Host '[OK] 单文件模式正常：一个 exe 即可分发。'
    }

    # ── 3. 冒烟：真的能跑起来 ────────────────────────────────
    Write-Host '==> 3/3 冒烟：跑一次 --version 与 --help'
    $env:PATH = "$OutputDir;$env:PATH"

    $version = & $exe --version
    if ($LASTEXITCODE -ne 0) { throw "--version 退出码异常（$LASTEXITCODE）" }
    Write-Host "    版本：$version"

    # --help 的输出量足够说明"程序能正常启动、能打印中文"，且不受环境变量影响。
    $null = & $exe --help
    if ($LASTEXITCODE -ne 0) { throw "--help 退出码异常（$LASTEXITCODE）" }

    # 默认的 $LASTEXITCODE 会污染后续判断，显式清零。
    $global:LASTEXITCODE = 0

    Write-Host ''
    Write-Host '── 用法 ─────────────────────────────────────────'
    Write-Host "【签发工具】$exe"
    Write-Host ''
    Write-Host '  日常签发（无需密钥文件：厂商密钥已内嵌）'
    Write-Host "     $exe gen --type full --bind AAAA-BBBB-CCCC-DDDD --expire 2027-10-08 --customer ""XX展厅"""
    Write-Host '  客户报问题时，先解读一下那份授权'
    Write-Host "     $exe inspect 授权_XX展厅.lic"
    Write-Host ''
    Write-Host '  换钥（厂商侧一次性维护，正常不会用到）：生成新密钥对并把公钥替换进'
    Write-Host '  src\ControlServer.Core\Licensing\VendorPublicKey.cs，然后重编服务端与签发工具。'
    Write-Host "     $exe new-key --force"
    Write-Host ''
    Write-Host '  完整的参数说明：'
    Write-Host "     $exe gen --help"
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
