using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

// 测试内通过环境变量注入配置，而环境变量是进程级的；
// 串行执行才能保证"同一时刻只有一个宿主在跑、只在用自己那个端口"。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 集成测试宿主工厂。
///
/// 为什么用环境变量而不是 <c>ConfigureAppConfiguration</c>：
///   <c>Program</c> 在 <c>WebApplication.CreateBuilder</c> 之后【立刻】读取配置
///   （数据目录决定日志路径，必须早于 DI）。而测试侧注入的配置源要到
///   <c>builder.Build()</c> 才生效 —— 对这几个键来说太晚了。
///   环境变量则在 <c>CreateBuilder</c> 那一刻就在配置链里，顺序正确。
///
/// 每个实例：唯一数据目录 + 唯一 UDP 端口，互不干扰。
/// </summary>
public class ControlServerFactory : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory;
    private readonly bool _ownsDataDirectory;

    public ControlServerFactory()
        : this(null)
    {
    }

    /// <param name="dataDirectory">
    /// 指定数据目录（用于"预置一个旧库再启动"这类迁移测试）。
    /// 传 null 则自动生成一个唯一临时目录，并在释放时清理。
    /// </param>
    public ControlServerFactory(string? dataDirectory)
        : this(dataDirectory, enforcePasswordChange: false)
    {
    }

    /// <param name="enforcePasswordChange">
    /// 是否开启"必须先改密"闸门（<c>Server:Auth:EnforcePasswordChange</c>），默认 <b>false</b>。
    /// <para>
    /// 播种的默认管理员带 <c>MustChangePassword = true</c>，而闸门默认是开的 ——
    /// 若测试环境不关它，143 个既有用例的每一次业务断言都会先撞上 403，
    /// 于是它们会被迫集体退化成"先改密再测"的流程测试，真正的业务覆盖反而消失。
    /// </para>
    /// <para>闸门自身由 <c>UserRbacTests</c> 显式传 <c>true</c> 覆盖，不是"没测"。</para>
    /// </param>
    public ControlServerFactory(string? dataDirectory, bool enforcePasswordChange)
        : this(dataDirectory, enforcePasswordChange, licenseEnforcement: null, licenseAutoTrial: false)
    {
    }

    /// <param name="licenseEnforcement">
    /// 授权闸门档位（<c>Server:License:Enforcement</c>）。传 <c>null</c> 用默认值 <c>Warn</c>。
    /// <para>闸门矩阵由 <c>LicenseApiTests</c> 用三种档位各自显式覆盖。</para>
    /// </param>
    /// <param name="licenseAutoTrial">
    /// 是否允许"首次启动自动生成本机试用"，默认 <b>false</b>。
    /// <para>
    /// ⚠️ 测试环境必须关掉：生成试用要取一次网络时间（SNTP 逐服务器尝试），
    /// 每个测试宿主都真去联网既慢又不稳定 —— 与 <c>Server__Time__AutoSync=false</c> 同一个理由。
    /// 自动试用自身的正确性由 <c>LicenseApiTests</c> 显式传 <c>true</c> 覆盖，不是"没测"。
    /// </para>
    /// </param>
    /// <param name="agentMaxHosts">
    /// 客户端在线表容量上限（<c>Server:Agent:MaxHosts</c>），默认 <b>500</b>（即生产默认值）。
    /// <para>
    /// 存在的意义是让"在线表已满 ⇒ 注册被拒（409 / toomany）"这条分支<b>可测</b> ——
    /// 免凭据准入（2026-10-13）后它是仅剩的两类注册失败之一，而按生产默认值要真灌 500 台才撞得到。
    /// </para>
    /// <para>
    /// ⚠️ 必须<b>无条件赋值</b>：环境变量是进程级的，若只在"非默认值"时设置，
    /// 某个用例设成的上限会一直留在进程环境里，把之后所有注册多台机器的用例一起打成 409。
    /// 详见本文件里 <c>Server__License__Enforcement</c> 上那段同源注释。
    /// </para>
    /// </param>
    /// <param name="allowedCidrs">
    /// 来源网段白名单（<c>Server:Security:AllowedCidrs</c>，逗号分隔）。默认 <c>null</c> = 清空（不限制）。
    /// <para>
    /// ⚠️ 与 <c>licenseEnforcement</c> 同一条纪律：<b>必须无条件赋值</b>。
    /// 而且这里**必须**默认清空 —— TestServer 的 <c>RemoteIpAddress</c> 常常是 <c>null</c>，
    /// 一旦有残留的白名单，后续宿主的每一个请求都会 403。
    /// </para>
    /// </param>
    public ControlServerFactory(
        string? dataDirectory,
        bool enforcePasswordChange,
        string? licenseEnforcement,
        bool licenseAutoTrial,
        int agentMaxHosts = 500,
        int loginPermitLimit = 1_000_000,
        string? allowedCidrs = null)
    {
        _ownsDataDirectory = dataDirectory is null;
        _dataDirectory = dataDirectory
            ?? Path.Combine(Path.GetTempPath(), "controlserver-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_dataDirectory);
        PairingPort = ReserveFreeUdpPort();
        DiscoveryPort = ReserveFreeUdpPort();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Server__Storage__DataDir", _dataDirectory);
        Environment.SetEnvironmentVariable("Server__Listener__PairingPort", PairingPort.ToString());

        // ⚠️ 发现端口必须每个宿主一个：默认的 9998 在开发机上常常已经**真的**被
        //    一个跑着的服务端占着（UDP 不像 TCP，第二个 bind 会直接失败）。虽然发现监听
        //    失败只是记一条日志、不影响其它功能，但那样一来"发现"这条链路在测试里
        //    从未被真正执行过，而它恰恰是最容易因为一个字符出错的地方。
        Environment.SetEnvironmentVariable("Server__Discovery__Port", DiscoveryPort.ToString());

        // 测试进程里绝不能弹浏览器。
        Environment.SetEnvironmentVariable("Server__Startup__AutoOpenBrowser", "false");

        // ⚠️ 必须关掉托盘图标：否则**每个**测试宿主的 ApplicationStarted 都会真的建一个
        //    Shell_NotifyIcon 托盘图标（集成测试有 170+ 个宿主），既会在跑测试时刷屏任务栏，
        //    也可能拖慢甚至干扰进程退出。托盘自身的正确性由 TrayIconTests 单独守门。
        Environment.SetEnvironmentVariable("Server__Tray__Enabled", "false");

        // ⚠️ 必须关掉单实例互斥：互斥体是**进程级**的，而若干用例会在同一进程里
        //    反复新建/重启宿主（"survives_host_restart"）。不关的话第二个宿主会被判成
        //    "已有实例在运行"，Program 提前 return 而不建 IHost，测试直接报
        //    "The entry point exited without ever building an IHost"。
        Environment.SetEnvironmentVariable("Server__Startup__SingleInstance", "false");

        // ⚠️ 必须让宿主"保留控制台"，绝不能让测试宿主去 FreeConsole 自己。
        //    背景：`Program` 默认会主动脱离启动它的控制台（"关闭 CMD 窗口不能停止服务"，
        //    见 WindowsInteractiveHost.ConfigureConsoleOutput）。而集成测试是在**测试进程内**
        //    跑 `Program` —— 若在用户自己的终端里跑 `dotnet test`（输出没被重定向），
        //    就会把测试宿主自己的控制台给退掉，xunit 的输出随之丢失。
        //    设成 true 后走"保留"分支：输出被重定向时它本来就什么都不做，
        //    真有控制台时也只是尝试附着（已在控制台上会失败，无害）。
        Environment.SetEnvironmentVariable("Server__Startup__AttachConsole", "true");

        // 见 enforcePasswordChange 参数说明。
        Environment.SetEnvironmentVariable(
            "Server__Auth__EnforcePasswordChange",
            enforcePasswordChange ? "true" : "false");

        // ⚠️ 必须关掉自动时钟校准：否则每个测试宿主启动时都会真的去联网查 NTP，
        //    既慢（3 个服务器 × 700ms）又不稳定。关掉后 BeijingTime 退化为
        //    "系统钟 + 固定 UTC+8"，即引入校正之前的行为，所有既有时间断言不受影响。
        Environment.SetEnvironmentVariable("Server__Time__AutoSync", "false");

        // 同理关掉定时调度循环：测试不需要一个每秒 tick 的后台任务，
        // 而且能排除"某条测试刚好写了一个等于当前秒的定时点"带来的不确定行为。
        // 端点仍能正常回显与手动触发 —— 手动触发是显式调用，不依赖后台循环。
        Environment.SetEnvironmentVariable("Server__Timer__Enabled", "false");

        // 见 licenseAutoTrial 参数说明。
        Environment.SetEnvironmentVariable(
            "Server__License__AutoTrial",
            licenseAutoTrial ? "true" : "false");

        // 授权闸门档位：null = 走配置默认（Warn）。
        //
        // ⚠️ 必须**无条件赋值**（传 null 也要赋值，等于"删除该变量"），不能只在非空时设置。
        //    环境变量是**进程级**的：闸门矩阵用例会把它设成 Enforce，若这里在 null 时跳过，
        //    那个 Enforce 会一直留在进程环境里，之后每一个宿主都被误判成"Enforce + 无授权"
        //    ⇒ 所有要求动作执行的用例集体收到 403。
        //    症状是"换一台机器/换一次运行顺序，若干毫不相关的用例突然红了" ——
        //    本文件顶部对 NTP 服务器列表的注释已经踩过同一个坑。
        Environment.SetEnvironmentVariable("Server__License__Enforcement", licenseEnforcement);

        // NTP 超时压到 100ms：授权模块在有需要时会主动取一次网络时间
        // （LicenseTimeSource → TimeSyncService.SyncAsync，逐服务器尝试）。
        // 默认 700ms × 3 个服务器 = 2 秒/次，测试里纯属浪费；压小后即使全部超时也只有 0.3 秒。
        Environment.SetEnvironmentVariable("Server__Time__NtpTimeoutMs", "100");

        // 见 agentMaxHosts 参数说明 —— 必须无条件赋值（含默认值），否则会跨用例泄漏。
        Environment.SetEnvironmentVariable("Server__Agent__MaxHosts", agentMaxHosts.ToString());

        // 登录限速（M6.2）：测试里必须抬到实际上无限。
        //
        // ⚠️ 两个理由，任一条都足以让默认值 20/5min 把测试打红：
        //   ① TestServer 下 RemoteIpAddress 常常是 null ⇒ 所有用例落进同一个 "unknown" 分区，
        //      共享 20 次额度，跑到第 21 次登录就集体收到 429；
        //   ② 用例之间共享进程环境变量，额度还会跨宿主累积。
        // 限速**自身**的行为由 AuthRateLimitTests 用显式的小额度覆盖，不是"没测"。
        Environment.SetEnvironmentVariable(
            "Server__Auth__LoginRateLimit__PermitLimit",
            loginPermitLimit.ToString());

        // 同源网段白名单：默认必须清空 —— 它是 M6.2 新接线的配置，
        // 任何一条残留都会让后续宿主的请求全部 403（TestServer 的 RemoteIp 是 null ⇒ 按拒绝处理）。
        Environment.SetEnvironmentVariable("Server__Security__AllowedCidrs", allowedCidrs);
    }

    /// <summary>本实例正在使用的对接码监听端口（已确保启动时可用）。</summary>
    public int PairingPort { get; }

    /// <summary>本实例正在使用的客户端发现端口（M4，同理由测试独占）。</summary>
    public int DiscoveryPort { get; }

    public string DataDirectory => _dataDirectory;

    /// <summary>数据库文件完整路径（<c>&lt;DataDir&gt;/control.db</c>）。</summary>
    public string DatabaseFile => Path.Combine(_dataDirectory, "control.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }

    /// <summary>
    /// 允许派生类在应用自身的服务注册之后再做替换（如授权测试换掉验签公钥）。
    /// <para>
    /// ⚠️ 必须走 <c>ConfigureTestServices</c>（它在应用 <c>ConfigureServices</c> <b>之后</b>运行），
    /// 而不是在 <see cref="ConfigureWebHost"/> 里直接 <c>services.Add…</c>：后者的注册会被应用
    /// 自己的注册覆盖掉，表现为"我明明换了实现，跑的还是原来那个"，且没有任何提示。
    /// </para>
    /// </summary>
    protected static void ReplaceServices(IWebHostBuilder builder, Action<IServiceCollection> configure)
        => builder.ConfigureTestServices(configure);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || !_ownsDataDirectory) return;

        try
        {
            if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结论。
        }
    }

    private static int ReserveFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
