#Requires -Version 5.1
<#
.SYNOPSIS
    把中控服务（ControlServer）自己的根证书装进本机「受信任的根证书颁发机构」。

.DESCRIPTION
    🔥 这个脚本解决的是「浏览器打开 https://<中心>:8443 时地址栏报不安全」这件事。

    报不安全**不是证书做错了**，而是这台观看端**还没有信任签发它的那张本地根 CA**。
    中控的 https 用的是自己建的一棵根 CA（见 LocalCertificateAuthority），
    浏览器默认只认公共 CA，所以必须人工把这张根证书装进去一次。

    装一次即可长期有效：
      - 根 CA 有效期 20 年，服务器证书按本机 IP 自动重签，观看端不用重复导入；
      - 中心换网段 / 加网卡时服务器证书会重签，但签发者不变，信任关系不断。

    ⚠️ 本机需要管理员权限（要写 LocalMachine 的根证书存储）。
      脚本会自己弹一次 UAC，点「是」即可，不需要手工打开证书向导。

.PARAMETER CertificatePath
    根证书文件路径。默认取本机中控安装目录下的那一份。
    在**观看端**（不是中心）运行时，请先下载：
        http://<中心地址>:8899/trust/controlserver-ca.crt
    然后 -CertificatePath 指向下载下来的文件。

.PARAMETER ServerAddress
    装完之后用它做一次真实握手验证。默认 localhost。
    想验证别的地址就传进来，例如 -ServerAddress 192.168.101.57。

.PARAMETER Uninstall
    从信任存储里移除这张根证书（排查用，不是常规操作）。

.EXAMPLE
    .\trust-ca.ps1

.EXAMPLE
    .\trust-ca.ps1 -CertificatePath .\controlserver-ca.crt -ServerAddress 192.168.101.57
#>
[CmdletBinding()]
param(
    [string]$CertificatePath = 'C:\ProgramData\ControlServer\https\controlserver-ca.crt',
    [string]$ServerAddress = 'localhost',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$StoreLocation = [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
$StoreName = [System.Security.Cryptography.X509Certificates.StoreName]::Root

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-Elevated {
    # ⚠️ 用 ProcessStartInfo 而不是 Start-Process：Verb=runas 要求 UseShellExecute=true，
    #    而 Start-Process 在某些带代理变量的环境里会直接把子进程参数拼坏（本项目踩过）。
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $psi.UseShellExecute = $true
    $psi.Verb = 'runas'

    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $arguments += " -CertificatePath `"$CertificatePath`" -ServerAddress `"$ServerAddress`""
    if ($Uninstall) { $arguments += ' -Uninstall' }

    $psi.Arguments = $arguments

    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.WaitForExit()
        return $proc.ExitCode
    }
    catch {
        Write-Host ''
        Write-Host '  [失败] 没能取得管理员权限（UAC 被取消或不允许提权）。' -ForegroundColor Red
        Write-Host '         请用「以管理员身份运行」打开 PowerShell 后重跑本脚本。' -ForegroundColor Yellow
        return 1
    }
}

function Find-Existing($thumbprint) {
    $found = @()
    foreach ($location in @('LocalMachine', 'CurrentUser')) {
        $path = "Cert:\$location\Root"
        $hit = Get-ChildItem $path -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $thumbprint }
        if ($hit) { $found += $location }
    }
    return $found
}

Write-Host ''
Write-Host '=== 中控服务 根证书信任安装 ===' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path $CertificatePath)) {
    Write-Host "  [失败] 找不到根证书文件：$CertificatePath" -ForegroundColor Red
    Write-Host ''
    Write-Host '  如果你是在【观看端】运行本脚本，请先下载中心的根证书：' -ForegroundColor Yellow
    Write-Host '      http://<中心地址>:8899/trust/controlserver-ca.crt' -ForegroundColor Yellow
    Write-Host '  然后指定路径重跑：' -ForegroundColor Yellow
    Write-Host '      .\trust-ca.ps1 -CertificatePath .\controlserver-ca.crt' -ForegroundColor Yellow
    exit 2
}

$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2((Resolve-Path $CertificatePath).Path)
$thumbprint = $certificate.Thumbprint
$existing = Find-Existing $thumbprint

Write-Host "  证书文件 : $CertificatePath"
Write-Host "  主体     : $($certificate.Subject)"
Write-Host "  有效期至 : $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  指纹     : $thumbprint"
Write-Host ''

# ── 卸载分支 ──
if ($Uninstall) {
    if (-not (Test-Administrator)) {
        exit (Restart-Elevated)
    }

    if ($existing.Count -eq 0) {
        Write-Host '  这张证书当前没有被信任，无需移除。' -ForegroundColor Yellow
        exit 0
    }

    foreach ($location in $existing) {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
            $StoreName, [System.Security.Cryptography.X509Certificates.StoreLocation]::$location)
        $store.Open('ReadWrite')
        $target = $store.Certificates | Where-Object { $_.Thumbprint -eq $thumbprint }
        foreach ($item in $target) { $store.Remove($item) }
        $store.Close()
        Write-Host "  已从 $location\Root 移除。" -ForegroundColor Green
    }

    Write-Host ''
    Write-Host '  移除完成。浏览器下次访问 https 时会重新报「不安全」。' -ForegroundColor Yellow
    exit 0
}

# ── 安装分支 ──
if ($existing -contains 'LocalMachine') {
    Write-Host '  本机已信任这张根证书，跳过安装。' -ForegroundColor Green
}
else {
    if (-not (Test-Administrator)) {
        Write-Host '  需要管理员权限写入根证书存储，正在请求提权……' -ForegroundColor Yellow
        Write-Host ''
        exit (Restart-Elevated)
    }

    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($StoreName, $StoreLocation)
    $store.Open('ReadWrite')
    try {
        $store.Add($certificate)
    }
    finally {
        $store.Close()
    }

    Write-Host '  已安装到 LocalMachine\Root（受信任的根证书颁发机构）。' -ForegroundColor Green
}

# ── 验证：不只看"装进去了"，要看"握手真的过了" ──
# 🔥 判据必须是真实 TLS 握手。只看存储里有没有这张证书是**假验证** ——
#    证书装错位置（比如进了"个人"而不是"根"）、或者商店名写错，都会显示"已安装"但浏览器照旧报红。
Write-Host ''
Write-Host '  验证 TLS 握手……'

$probeHost = $ServerAddress
$probePort = 8443
$url = "https://${probeHost}:${probePort}/"

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(8)

$verifyOk = $false
$verifyNote = ''

try {
    $response = $client.GetAsync($url).GetAwaiter().GetResult()
    $verifyOk = $true
    $verifyNote = "HTTP $([int]$response.StatusCode)"
}
catch {
    $inner = $_.Exception
    while ($inner.InnerException) { $inner = $inner.InnerException }
    $verifyNote = $inner.Message
}

$client.Dispose()
$handler.Dispose()

if ($verifyOk) {
    Write-Host "  [通过] $url -> $verifyNote" -ForegroundColor Green
    Write-Host ''
    Write-Host '  现在用浏览器打开下面任一个地址，地址栏应当显示「连接安全」：' -ForegroundColor Green
    Write-Host "      https://localhost:$probePort/" -ForegroundColor White
    Write-Host "      https://<本机IP>:$probePort/" -ForegroundColor White
    Write-Host "      https://<计算机名>:$probePort/" -ForegroundColor White
    Write-Host ''
    Write-Host '  ⚠️ 不要访问 https://0.0.0.0:8443 —— 0.0.0.0 是「监听所有网卡」的意思，' -ForegroundColor Yellow
    Write-Host '     它不是能被访问的地址，浏览器必然连不上或报名称不匹配。' -ForegroundColor Yellow
    exit 0
}

Write-Host "  [未通过] $url -> $verifyNote" -ForegroundColor Red
Write-Host ''
Write-Host '  证书已安装但握手未通过，请按顺序排查：' -ForegroundColor Yellow
Write-Host '    1) 中心服务没在跑，或 8443 没有监听      -> netstat -ano | findstr 8443' -ForegroundColor Yellow
Write-Host "    2) 你用的地址不在证书 SAN 里               -> 换 https://localhost:8443 再试" -ForegroundColor Yellow
Write-Host '    3) 证书是本机新生成的、和下载那份不是同一张 -> 重新下载 controlserver-ca.crt' -ForegroundColor Yellow
Write-Host '    4) 浏览器有独立证书缓存（Firefox 用自己的存储，需在设置里单独导入）' -ForegroundColor Yellow
exit 3
