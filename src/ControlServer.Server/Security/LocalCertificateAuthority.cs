using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;

namespace ControlServer.Server.Security;

/// <summary>
/// <b>本机自建的一套证书</b>（M5.3）：一棵长期不变的本地根 CA + 一张按本机当前 IP 签发的服务器证书。
///
/// <para>
/// 🔥 <b>为什么非要有它 —— 这不是"顺手加个 https"</b>：
/// 浏览器把 <c>https</c> 与 <c>http://localhost</c> 之外的一切都判为<b>非安全上下文</b>，
/// 而在非安全上下文里 <c>VideoDecoder</c> / <c>AudioDecoder</c> / <c>AudioWorklet</c>
/// <b>压根不存在</b>（<c>typeof AudioDecoder === "undefined"</c>，Chrome 94 起、141 复验）。
/// 而本系统的观看端正是"局域网里另一台电脑按 IP 访问 http://10.x.x.x:8899" ——
/// 也就是说：<b>不做这一步，M5.3 的音频与 M5.4 的硬解在一开始就注定跑不起来</b>，
/// 而且失败形态是"音频区域一句错误都没有、就是没声音"。
/// </para>
///
/// <para>
/// <b>为什么是"根 CA + 签发"而不是直接自签一张证书</b>：自签一张的话，
/// 观看端信任的是<b>那张证书本身</b>，而它的 SAN 里写死了当时的 IP ——
/// 换网段 / 加网卡 / 证书到期换了新的一张，每一台观看端都要<b>重新导入一次</b>。
/// 换成根 CA 之后，信任的是 CA，服务器证书可以随便重签（本类就是按"IP 变了就自动重签"实现的），
/// 观看端一辈子只导一次。
/// </para>
///
/// <para>
/// ⚠️ <b>这套东西的作用边界要说清楚，别当成"加密就安全了"</b>：
/// 它解决的是"让浏览器愿意启用那几个 API"，顺带把页面传输从明文变成加密；
/// 它<b>不</b>解决"谁能看别人的屏幕"（那是 <c>[Authorize]</c> 与 AdminOnly 的事），
/// 也<b>不</b>解决"要不要把根证书装到一台不受控的机器上"（那是运维的决定）。
/// 而一旦有人把 CA 私钥拷走，他就能给任何机器签一张你的观看端认的证书 ——
/// 所以 <c>CertificateDirectory</c> 的访问权限就是这条链路的真实安全边界。
/// </para>
///
/// <para>
/// <b>平台</b>：只用 .NET 的 <c>X509Certificate2</c> / <c>CertificateRequest</c>，Windows 与 Linux 通用。
/// </para>
/// </summary>
public sealed class LocalCertificateAuthority
{
    /// <summary>根 CA 的有效期。<b>长</b>是刻意的：观看端只导一次，换证书不该换信任。</summary>
    private const int CaValidityYears = 20;

    /// <summary>服务器证书的有效期。短一点无所谓 —— IP 一变它本来就要重签。</summary>
    private const int ServerValidityYears = 3;

    /// <summary>服务器证书离到期还有这么久就提前重签，免得现场在到期当口才发现。</summary>
    private const int RenewAheadDays = 30;

    private const string CaFileName = "ca.pfx";
    private const string CaPublicFileName = "controlserver-ca.crt";
    private const string ServerFileName = "server.pfx";
    private const string SecretFileName = "pfx.password";

    private readonly string _directory;

    private LocalCertificateAuthority(string directory, X509Certificate2 server, byte[] caDer, string caFingerprint)
    {
        _directory = directory;
        ServerCertificate = server;
        CaDer = caDer;
        CaFingerprint = caFingerprint;
    }

    /// <summary>
    /// 证书目录（日志里说得清"装错了该去哪看"）。
    ///
    /// ⚠️ 名字**必须带 Path 后缀**：叫 <c>Directory</c> 的话，本类里所有
    /// <c>Directory.CreateDirectory(...)</c> 都会解析到这个实例属性上，
    /// 编译期报一句"对象引用对于非静态字段是必需的" —— 错得完全不指向真实原因。
    /// </summary>
    public string DirectoryPath => _directory;

    /// <summary>给 Kestrel 用的服务器证书（<b>含私钥</b>）。</summary>
    public X509Certificate2 ServerCertificate { get; }

    /// <summary>根 CA 的 <b>DER</b> 字节（<b>只有公钥</b>，可以直接给观看端安装）。</summary>
    public byte[] CaDer { get; }

    /// <summary>根 CA 的 SHA-1 指纹（大写十六进制、冒号分隔）—— 用于人工核对"装的是不是这一张"。</summary>
    public string CaFingerprint { get; }

    /// <summary>服务器证书覆盖的 IP（用于日志与"装完还是报红"时的排查）。</summary>
    public IReadOnlyList<string> CoveredAddresses { get; private init; } = Array.Empty<string>();

    /// <summary>根证书文件的建议文件名（下载时给浏览器用）。</summary>
    public const string CaDownloadFileName = CaPublicFileName;

    /// <summary>
    /// 载入或生成。幂等：正常启动走的是"读盘"这条快路（几毫秒）。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>启动时绝不因为"证书目录不可写"就让服务起不来</b>：
    /// 那会把一个"音频用不了"的问题升级成"整个中控连不上"。
    /// 失败一律降级为 <c>null</c> 返回值，由调用方决定"就只跑 http"。
    /// </remarks>
    public static LocalCertificateAuthority? TryLoadOrCreate(IDataDirectory dataDirectory, LogService log)
    {
        try
        {
            return LoadOrCreate(dataDirectory, log);
        }
        catch (Exception ex)
        {
            log.Warn(
                "Security",
                $"本地 HTTPS 证书不可用（{ex.GetType().Name}：{ex.Message}）。"
                + $"服务将继续只提供 http —— 局域网内的浏览器会落入非安全上下文，"
                + $"远控音频与硬解在那些观看端上不可用。");

            return null;
        }
    }

    private static LocalCertificateAuthority LoadOrCreate(IDataDirectory dataDirectory, LogService log)
    {
        var directory = dataDirectory.CertificateDirectory;

        Directory.CreateDirectory(directory);

        var passwordPath = Path.Combine(directory, SecretFileName);
        var caPath = Path.Combine(directory, CaFileName);
        var serverPath = Path.Combine(directory, ServerFileName);

        var password = File.Exists(passwordPath)
            ? ReadSecret(passwordPath)
            : CreateSecret(passwordPath);

        var addresses = DiscoverLocalAddresses();

        X509Certificate2 ca;

        if (File.Exists(caPath))
        {
            ca = LoadCertificate(caPath, password);

            log.Info("Security", $"已载入本地根 CA（指纹 {Fingerprint(ca)}，到期 {ca.NotAfter:yyyy-MM-dd}）。");
        }
        else
        {
            ca = CreateRoot(caPath, password);

            log.Info(
                "Security",
                $"已生成本地根 CA（指纹 {Fingerprint(ca)}，到期 {ca.NotAfter:yyyy-MM-dd}）。"
                + "观看端需要导入一次根证书，之后不必再导。");
        }

        // 🔥 每次启动都检查"这张服务器证书还covering不covering当前的本机地址"。
        //    不检查的话，改网段之后的表现是——**服务一切正常、浏览器一律报证书错误**，
        //    而日志里一个字都不说。
        var server = NeedsReissue(serverPath, password, addresses) is { } reason
            ? IssueServerCertificate(ca, serverPath, password, addresses, reason, log)
            : LoadCertificate(serverPath, password);

        // 把公钥单独落一份：观看端要下载的就是它（**只有公钥**，可以从 http 端口随便给）。
        File.WriteAllBytes(Path.Combine(directory, CaPublicFileName), ca.Export(X509ContentType.Cert));

        var caDer = ca.Export(X509ContentType.Cert);

        return new LocalCertificateAuthority(directory, server, caDer, Fingerprint(ca))
        {
            CoveredAddresses = addresses.Select(a => a.ToString()).ToArray(),
        };
    }

    /// <summary>这张已存在的服务器证书是否需要重签；返回 <c>null</c> = 不用。</summary>
    private static string? NeedsReissue(string serverPath, string password, IReadOnlyList<IPAddress> addresses)
    {
        if (!File.Exists(serverPath)) return "还没有服务器证书";

        X509Certificate2 existing;

        try
        {
            existing = LoadCertificate(serverPath, password);
        }
        catch
        {
            // 文件在但读不出来（被换成了别的东西 / 口令变了）：重签是最省事的收场。
            return "已有的服务器证书读不出来";
        }

        if (existing.NotAfter < DateTimeOffset.Now.AddDays(RenewAheadDays))
        {
            return $"已有的服务器证书将在 {existing.NotAfter:yyyy-MM-dd} 到期";
        }

        var covered = ReadSanValues(existing);

        foreach (var address in addresses)
        {
            if (!covered.Contains(address.ToString()))
            {
                return $"证书里没有 {address}（本机地址变了）";
            }
        }

        return null;
    }

    /// <summary>读证书 SAN 里已覆盖的 IP 文本集合。</summary>
    /// <remarks>
    /// ⚠️ <b>不能用 <c>X509SubjectAlternativeNameExtension</c> 的强类型访问器</b>
    /// （那是 .NET 7 才加的，且对"我们自己写进去的 IP"这种编码并不总能解析干净）。
    /// 直接从原始字节里找 IPv4 的四字节序列 —— 粗糙但**判据是我们自己写进去的东西**，
    /// 判错的最坏后果只是"多签一张证书"，而漏判的后果是"浏览器一律报错"。
    /// </remarks>
    private static HashSet<string> ReadSanValues(X509Certificate2 certificate)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var san = certificate.Extensions
            .OfType<X509Extension>()
            .FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");

        if (san is null) return result;

        var raw = san.RawData;

        for (var i = 0; i + 5 <= raw.Length; i++)
        {
            // SAN 里 IPv4 的编码是 [0x87][4 字节]（context tag 7 = IP 地址，长度 4）。
            // 只要出现这一段，后面那四个字节就是它 covering 的一个 IPv4。
            if (raw[i] != 0x87) continue;

            result.Add(new IPAddress(raw.AsSpan(i + 1, 4)).ToString());
        }

        return result;
    }

    private static X509Certificate2 CreateRoot(string caPath, string password)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=ControlServer 本地根证书, O=ControlServer",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // 基本约束：它是 CA，而且下面的路径长度不设限（我们只签一层，但不必把它写死）。
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;

        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(CaValidityYears));

        // ⚠️ 落盘要**带着私钥**（下次启动要拿它签新证书）；但导出的"给观看端的那份"绝不能带。
        File.WriteAllBytes(caPath, created.Export(X509ContentType.Pfx, password));

        return LoadCertificate(caPath, password);
    }

    private static X509Certificate2 IssueServerCertificate(
        X509Certificate2 ca,
        string serverPath,
        string password,
        IReadOnlyList<IPAddress> addresses,
        string reason,
        LogService log)
    {
        using var key = RSA.Create(2048);

        var hostName = ResolveHostName();
        var subject = $"CN={hostName}, O=ControlServer";

        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // 它是叶子证书，**不是** CA —— 这一位不写对，某些客户端会把它当中间 CA 处理而校验失败。
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },  // serverAuth
            false));

        var san = new SubjectAlternativeNameBuilder();

        // 🔥 **必须有 SAN**：现代浏览器完全不看 CN 了（Chrome 58 起），
        //    只写 CN 的证书会被判为"名称不匹配"，而错误信息看起来像"证书无效"。
        san.AddDnsName(hostName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);

        foreach (var address in addresses)
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;

        using var issued = request.Create(ca, now.AddDays(-1), now.AddYears(ServerValidityYears), Serial());

        // ⚠️ ⚠️ `Create()` 返回的证书**不带私钥**。不 `CopyWithPrivateKey` 就直接导出，
        //    得到的 PFX 里没有私钥，Kestrel 会在启动时报一句很难读懂的话
        //    （"服务器证书没有私钥"），或者更糟：起得来但 TLS 握手全失败。
        using var withKey = issued.CopyWithPrivateKey(key);

        File.WriteAllBytes(serverPath, withKey.Export(X509ContentType.Pfx, password));

        log.Info(
            "Security",
            $"已签发服务器证书：{subject}（覆盖 {string.Join("、", addresses)}；原因：{reason}）。");

        return LoadCertificate(serverPath, password);
    }

    private static X509Certificate2 LoadCertificate(string path, string password)
    {
        // ⚠️ 刻意**不用** EphemeralKeySet：那种密钥不落密钥容器，而 Windows 的 SChannel
        //    （.NET 在 Windows 上做 TLS 走的就是它）要求密钥在容器里，
        //    否则握手时才报"密钥不适用于指定状态" —— 一个启动看不出来、一连就失败的坑。
        //
        // 先试机器密钥容器（Windows 服务以 SYSTEM 身份跑，这是标准做法）；
        // 拿不到就回落到 .NET 默认（用户密钥容器）—— 因为**源码态 `run.cmd` 是普通用户身份跑的**，
        // 而机器容器要管理员。不做这个回落的表现是"装成服务能用、源码态调试时 https 用不了"。
        Exception? machineFailure = null;

        try
        {
            return new X509Certificate2(path, password, X509KeyStorageFlags.MachineKeySet);
        }
        catch (Exception ex)
        {
            machineFailure = ex;
        }

        try
        {
            return new X509Certificate2(path, password);
        }
        catch
        {
            // 两次都失败时**抛第一次的异常**：用户容器失败往往只是"权限不足"这类连带现象，
            // 真正的原因（口令不对 / 文件损坏）在第一次那个异常里。
            throw machineFailure;
        }
    }

    /// <summary>生成一个人类不可记的口令，存进数据目录（<b>与私钥同目录同权限</b>）。</summary>
    /// <remarks>
    /// ⚠️ 如实说明它的作用：口令与 PFX 放在一起，<b>它挡不住"能读这个目录的人"</b>。
    /// 它挡的是"有人只拷走了其中一个文件"（比如把 pfx 发到群里排查问题）。
    /// 真正的边界是目录权限本身 —— 这一点写在类注释里，不假装它是一道强保护。
    ///
    /// 🔥 <b>这个文件里**只能有口令这一行**，说明文字写进另一个文件</b> ——
    /// 这条纪律是踩出来的：原来把说明当第二行写在同一个文件里、读取时 <c>Trim()</c> 一下，
    /// 而 <c>Trim()</c> 只去首尾空白，于是"口令"实际变成了
    /// <c>"3pjZ…c=\n# 这是 https 目录下…"</c> 一整个多行串。后果非常隐蔽：
    /// <b>当次启动一切正常</b>（口令是内存里刚生成的那一份），
    /// <b>下一次启动 PFX 就解不开了</b> —— 表现为"重启之后 https 突然不可用"，
    /// 而日志里只写着"证书建立失败"，看起来像文件被谁动过。
    /// </remarks>
    private static string CreateSecret(string path)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        File.WriteAllText(path, secret + Environment.NewLine);

        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(path)!, "说明.txt"),
            "这个目录里放的是中控服务自己的 HTTPS 证书（M5.3）。" + Environment.NewLine
            + Environment.NewLine
            + "  ca.pfx              本地根 CA（含私钥）—— 观看端信任的就是它签发的东西" + Environment.NewLine
            + "  controlserver-ca.crt  根 CA 的公钥，**只有这个可以分发出去**" + Environment.NewLine
            + "  server.pfx          服务器证书（含私钥），给 Kestrel 用；IP 变了会自动重签" + Environment.NewLine
            + "  pfx.password        上面两个 pfx 的口令（**这个文件里只有口令这一行，别加注释**）" + Environment.NewLine
            + Environment.NewLine
            + "⚠️ ca.pfx 的私钥 = 整条链路的安全边界：拿到它的人可以签出观看端会认的任意证书。" + Environment.NewLine
            + "   因此：不要备份它、不要分发它、不要连同这个目录一起打包。" + Environment.NewLine
            + "   口令与私钥放在同一个目录里，它挡不住能读这个目录的人 ——" + Environment.NewLine
            + "   它挡的是「有人只拷走了其中一个文件」这类事故。" + Environment.NewLine);

        return secret;
    }

    /// <summary>读口令：**只取第一行**，免得有人（或过去版本的自己）在文件里加了说明就失效。</summary>
    private static string ReadSecret(string path)
    {
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();

            // 跳过空行与注释行（`#` 开头）—— 与上面 CreateSecret 的纪律一致：
            // 口令文件被手工加了一行说明就整台机器 https 不可用，代价太大。
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            return trimmed;
        }

        throw new InvalidOperationException($"口令文件 {path} 里没有有效的口令行。");
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);

        // RFC 5280：序列号必须是非负的（首字节最高位为 1 时会被当成负数，某些客户端直接拒）。
        serial[0] &= 0x7F;

        return serial;
    }

    private static string ResolveHostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch
        {
            return "controlserver";
        }
    }

    /// <summary>本机所有可作为访问入口的地址（IPv4 + 非链路本地 IPv6）。</summary>
    /// <remarks>
    /// ⚠️ <b>刻意排除回环之外的虚拟网卡吗？不排除</b> —— 现场可能真的有"通过 Hyper-V 虚拟网段访问"
    /// 的用法，把地址漏掉的表现是"那台机器就是打不开 https"，而没有任何日志说得出为什么。
    /// 多covering几个地址的代价只是证书里多几行。
    /// </remarks>
    private static IReadOnlyList<IPAddress> DiscoverLocalAddresses()
    {
        var result = new List<IPAddress>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;

                    if (IPAddress.IsLoopback(address)) continue;

                    if (address.AddressFamily == AddressFamily.InterNetworkV6
                        && (address.IsIPv6LinkLocal || address.IsIPv6Multicast))
                    {
                        continue;
                    }

                    if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    {
                        continue;
                    }

                    if (!result.Contains(address)) result.Add(address);
                }
            }
        }
        catch
        {
            // 拿不到网卡信息（权限 / 平台上不支持）：回落到"只covering localhost"，
            // 由调用方的日志把这件事说出来。
        }

        return result;
    }

    private static string Fingerprint(X509Certificate2 certificate)
        => BitConverter.ToString(certificate.GetCertHash()).Replace('-', ':');
}
