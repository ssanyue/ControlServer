<#
.SYNOPSIS
  M6.3 压测：起一个**临时实例**的中心，跑 N 台协议级假 Agent，出一份曲线报告。

.DESCRIPTION
  它做四件事，顺序不能换：
    1. 挑一个**当时空闲**的端口（铁律 12：开发机上装着开机自启的 Agent，固定端口必冲突）；
    2. 用**系统临时目录**当数据目录起服务（绝不碰现场库 —— 100 台假机器会留在在线表里）；
    3. 等 /healthz 通了再压（服务没起来就压，得到的是一堆注册失败）；
    4. 跑完停服务、删临时目录，**只留报告**。

.PARAMETER Agents
  假 Agent 台数。

.PARAMETER Minutes
  压测时长（分钟，可带小数）。

.PARAMETER Tag
  报告文件名标签（例 100）。不给就按台数命名。

.EXAMPLE
  .\run-loadtest.ps1 -Agents 100 -Minutes 30 -Ramp 10 -Tag 100

.EXAMPLE
  .\run-loadtest.ps1 -Agents 20 -Minutes 1 -CommandsPerMin 60 -Streams 1 -Tag smoke
#>
[CmdletBinding()]
param(
    [int]$Agents = 20,
    [double]$Minutes = 5,
    [double]$Ramp = 5,
    [int]$CommandsPerMin = 0,
    [int]$Streams = 0,
    [string]$Tag = "",
    [string]$Out = "reports",
    [string]$ServerExe = "",
    [string]$Dotnet = "",
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = "$Agents" }

function Write-Step([string]$text) { Write-Host "[*] $text" -ForegroundColor Cyan }

# ── 找 dotnet 与产物 ──────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($Dotnet)) {
    $candidates = @(
        "C:\Users\$env:USERNAME\.workbuddy\binaries\dotnet8\dotnet.exe",
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe")
    )

    $Dotnet = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $Dotnet) {
        throw "找不到 dotnet.exe —— 用 -Dotnet 指定一个。"
    }
}

if ([string]::IsNullOrWhiteSpace($ServerExe)) {
    $ServerExe = Join-Path $repoRoot "src\ControlServer.Server\bin\Debug\net8.0\ControlServer.Server.exe"
}

if (-not (Test-Path $ServerExe)) {
    throw "找不到中心产物：$ServerExe（先 dotnet build src\ControlServer.Server）"
}

$loadTestProj = Join-Path $repoRoot "deploy\loadtest\ControlServer.LoadTest\ControlServer.LoadTest.csproj"
$loadTestDll = Join-Path $repoRoot "deploy\loadtest\ControlServer.LoadTest\bin\Debug\net8.0\ControlServer.LoadTest.dll"

if (-not (Test-Path $loadTestDll)) {
    Write-Step "编译压测工具……"
    & $Dotnet build $loadTestProj -m:1 -v:q --nologo
    if ($LASTEXITCODE -ne 0) { throw "压测工具编译失败。" }
}

# ── 挑空闲端口（绑 0 让系统给，立刻释放；有竞态，但比固定端口强得多）──
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dataDir = Join-Path $env:TEMP "controlserver-loadtest-$stamp"
$outDir = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $PSScriptRoot $Out }

New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$logStdout = Join-Path $dataDir "server-stdout.log"
$logStderr = Join-Path $dataDir "server-stderr.log"

Write-Step "仓库：$repoRoot"
Write-Step "端口：$port（空闲）"
Write-Step "数据目录：$dataDir（跑完删）"

# ── 起服务 ──────────────────────────────────────────────────────
# 这些环境变量只在**本脚本进程**里存在，退出时清掉（脚本是子进程，不会污染用户的 shell）。
$env:Server__Storage__DataDir              = $dataDir
$env:Server__Http__Urls                    = "http://127.0.0.1:$port"
$env:Server__Https__Enabled                = "false"
$env:Server__Startup__AutoOpenBrowser      = "false"
$env:Server__Tray__Enabled                 = "false"
$env:Server__Startup__SingleInstance       = "false"
$env:Server__Startup__AttachConsole        = "true"
$env:Server__Time__AutoSync                = "false"
$env:Server__Timer__Enabled                = "false"
$env:Server__Discovery__Port               = "0"
$env:Server__Agent__MaxHosts               = [Math]::Max(500, $Agents + 50).ToString()
# 压测不该被登录限速挡住：整轮只登一次，但连跑多轮时会累积。
$env:Server__Auth__LoginRateLimit__PermitLimit = "1000"

$server = $null

try {
    Write-Step "启动中心（PID 稍后回填）……"

    $server = Start-Process -FilePath $ServerExe `
        -PassThru -NoNewWindow `
        -RedirectStandardOutput $logStdout `
        -RedirectStandardError $logStderr

    $deadline = (Get-Date).AddSeconds(60)
    $ready = $false

    while ((Get-Date) -lt $deadline) {
        if ($server.HasExited) { break }

        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port/healthz" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        }
        catch {
            Start-Sleep -Milliseconds 300
        }
    }

    if (-not $ready) {
        Write-Host "[x] 中心没起来。服务端输出：" -ForegroundColor Red
        if (Test-Path $logStdout) { Get-Content $logStdout -Tail 40 }
        if (Test-Path $logStderr) { Get-Content $logStderr -Tail 20 }
        throw "中心启动失败（端口 $port）。"
    }

    Write-Step "中心就绪（PID $($server.Id)），开始压测"

    $toolArgs = @(
        $loadTestDll,
        "--server", "http://127.0.0.1:$port",
        "--agents", $Agents,
        "--minutes", $Minutes,
        "--ramp", $Ramp,
        "--commands-per-min", $CommandsPerMin,
        "--streams", $Streams,
        "--server-pid", $server.Id,
        "--out", $outDir,
        "--tag", $Tag
    )

    & $Dotnet @toolArgs

    if ($LASTEXITCODE -ne 0) { throw "压测工具退出码 $LASTEXITCODE。" }
}
finally {
    if ($server -and -not $server.HasExited) {
        Write-Step "停止中心……"
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        $server.WaitForExit(10000) | Out-Null
    }

    # 环境变量清理（脚本进程退出也会消失，这里是为了"同一个会话里连跑两轮"）。
    @(
        "Server__Storage__DataDir", "Server__Http__Urls", "Server__Https__Enabled",
        "Server__Startup__AutoOpenBrowser", "Server__Tray__Enabled", "Server__Startup__SingleInstance",
        "Server__Startup__AttachConsole", "Server__Time__AutoSync", "Server__Timer__Enabled",
        "Server__Discovery__Port", "Server__Agent__MaxHosts",
        "Server__Auth__LoginRateLimit__PermitLimit"
    ) | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }

    if ($KeepData) {
        Write-Step "保留数据目录（-KeepData）：$dataDir"
    }
    else {
        try { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}
