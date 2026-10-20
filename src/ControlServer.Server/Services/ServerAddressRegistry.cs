using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ControlServer.Server.Services;

/// <summary>
/// 监听地址的**运行时真相**（M2-E5）。
///
/// <para>
/// 热切换之后，配置里的 <c>Server:Http:Urls</c> 与"实际在听哪里"会不一致。
/// 界面（首页状态卡 / 系统信息页）必须显示<b>实际</b>值，否则会出现
/// "首页说 8899、实际连 9000"这种最难排查的不一致 —— 因此这里成了唯一的读点，
/// <see cref="StatusProvider"/> 不再直接读配置。
/// </para>
/// <para>
/// 装配时序：Kestrel 启动 → 工厂 <c>BindAsync</c> → 创建 listener → <c>Attach</c>。
/// 因此在这个单例被首次使用前它<b>可能还是空的</b>，读取方要能吃住（见 <see cref="HasListener"/>）。
/// </para>
/// </summary>
public sealed class ServerAddressRegistry
{
    private readonly object _lock = new();
    private readonly List<SwitchableConnectionListener> _listeners = new();
    private readonly Security.HttpsSetup _https;

    public ServerAddressRegistry(IConfiguration configuration)
        => _https = Security.HttpsSetup.Resolve(configuration, configuration["Server:Http:Urls"]);

    internal void Attach(SwitchableConnectionListener listener)
    {
        lock (_lock)
        {
            _listeners.Add(listener);
        }
    }

    /// <summary>是否已经有被接管的监听器（Kestrel 启动后为 true）。</summary>
    public bool HasListener
    {
        get
        {
            lock (_lock)
            {
                return _listeners.Count > 0;
            }
        }
    }

    /// <summary>监听器个数（&gt;1 表示配置了多个地址，热切换只作用于主地址）。</summary>
    public int ListenerCount
    {
        get
        {
            lock (_lock)
            {
                return _listeners.Count;
            }
        }
    }

    /// <summary>
    /// 主监听器 —— 接收连接的那一个，也是端口热切换的作用对象。
    ///
    /// <para>
    /// ⚠️ <b>它必须是 http 那一个</b>：热切换的语义是"改运维平时访问的那个端口"，
    /// 而 https 端口的证书是按 IP 签的、改端口对它没有任何意义。
    /// 用 <see cref="Urls"/> 的口径（http 在前）来对齐，见那个属性的说明。
    /// </para>
    /// </summary>
    public SwitchableConnectionListener? Primary
    {
        get
        {
            lock (_lock)
            {
                if (_listeners.Count == 0) return null;

                return Ordered().First();
            }
        }
    }

    /// <summary>当前实际监听的 URL 列表；监听器尚未装配时返回空。</summary>
    /// <remarks>
    /// 🔥 <b>http 端点在前、https 在后</b>，两条理由：
    /// <list type="number">
    ///   <item>下游 <c>ServerAddressService</c> 用 <c>[0]</c> 去推断"热切换该改哪个"，
    ///         而它只该改 http 那一个（见 <see cref="Primary"/>）；</item>
    ///   <item>状态页把运维平时访问的那个地址显示在最前面，符合直觉。</item>
    /// </list>
    /// ⚠️ 因此这个<b>不再等于 Kestrel 的绑定时序</b>。需要时序的地方不存在 ——
    /// 唯一在乎"主监听器"的地方已经经由 <see cref="Primary"/> 走同一个口径。
    /// </remarks>
    public IReadOnlyList<string> Urls
    {
        get
        {
            lock (_lock)
            {
                return Ordered()
                    .Select(l =>
                    {
                        var scheme = _https.IsHttpsPort(l.Port) ? "https" : "http";

                        return $"{scheme}://{l.DisplayHost}:{l.Port}";
                    })
                    .ToArray();
            }
        }
    }

    /// <summary>本机 https 端点的端口（未启用为 0）。</summary>
    public int HttpsPort => _https.Enabled ? _https.Port : 0;

    /// <summary>
    /// 把"主机 + 端口"渲染成完整的 URL。<b>scheme 由 <see cref="Security.HttpsSetup"/> 决定</b>。
    /// </summary>
    /// <remarks>
    /// 🔥 这个方法是给**日志**用的，而它值得单独存在：监听器自己只知道一个 <c>IPEndPoint</c>，
    /// 不知道上面套没套 TLS（TLS 是 Kestrel 在监听器之上加的）。于是接管日志曾经对
    /// https 端点也写"已接管监听地址 http://0.0.0.0:8443" ——
    /// 一条**写着错 scheme 的正常日志**，够让人去查半天"为什么 https 端口在讲 http"。
    /// </remarks>
    public string DescribeUrl(string host, int port)
        => $"{(_https.IsHttpsPort(port) ? "https" : "http")}://{host}:{port}";

    /// <summary>按"http 在前"排序（LINQ 的 OrderBy 是稳定排序，同组内保持发现顺序）。</summary>
    private IEnumerable<SwitchableConnectionListener> Ordered()
        => _listeners.OrderBy(l => _https.IsHttpsPort(l.Port) ? 1 : 0);

    /// <summary>当前实际监听端口；监听器尚未装配时为 0。</summary>
    public int Port => Primary?.Port ?? 0;
}

/// <summary>
/// 把默认的 socket 监听器包一层，使其支持运行期换端口（M2-E5）。
///
/// <para>
/// <b>装配方式</b>：用 <c>ServiceCollection.Insert(0, …)</c> 插到所有
/// <see cref="IConnectionListenerFactory"/> 注册的最前面 —— Kestrel 按注册顺序选择要用的工厂，
/// 插队即可生效，同时<b>不必 RemoveAll</b>（RemoveAll 会连带干掉 NamedPipe transport 的注册）。
/// </para>
/// <para>
/// <b>内层工厂为什么是"延迟解析"而不是构造注入</b>：构造我的时候如果去解析
/// <c>IEnumerable&lt;IConnectionListenerFactory&gt;</c>，会连带构造我自己 ⇒ <b>DI 递归</b>。
/// 把解析推迟到第一次 <c>BindAsync</c>（那时单例已经建好、进了缓存），
/// 再从列表里挑出"不是我的那一个"就安全了。
/// </para>
/// </summary>
public sealed class SwitchableConnectionListenerFactory : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    private readonly Func<IConnectionListenerFactory> _innerAccessor;
    private readonly ServerAddressRegistry _registry;
    private readonly ILogger<SwitchableConnectionListenerFactory> _logger;
    private readonly object _innerGate = new();

    private IConnectionListenerFactory? _inner;

    public SwitchableConnectionListenerFactory(
        Func<IConnectionListenerFactory> innerAccessor,
        ServerAddressRegistry registry,
        ILogger<SwitchableConnectionListenerFactory> logger)
    {
        _innerAccessor = innerAccessor;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// <b>这个方法是必须的，不是可选的</b> —— Kestrel 只从"实现了
    /// <see cref="IConnectionListenerFactorySelector"/> 且 <c>CanBind</c> 命中"的工厂里挑。
    /// </summary>
    public bool CanBind(EndPoint endpoint) => endpoint is IPEndPoint;

    public async ValueTask<IConnectionListener> BindAsync(
        EndPoint endpoint,
        CancellationToken cancellationToken = default)
    {
        // 非 IP 型端点（命名管道 / Unix socket）交回内层工厂原样处理 —— 本服务用不到，
        // 但保留这条路径可以避免"我插队导致其它 transport 全废"。
        if (endpoint is not IPEndPoint ip)
        {
            return await InnerFactory().BindAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }

        var listener = new SwitchableConnectionListener(
            InnerFactory(),
            ip.Address,
            DescribeHost(ip.Address),
            ip.Port,
            _logger);

        // Kestrel 期望 BindAsync 返回时已经在监听，所以首次绑定必须在这里完成（不能推迟到 AcceptAsync）。
        await listener.BindInitialAsync(cancellationToken).ConfigureAwait(false);

        _registry.Attach(listener);
        _logger.LogInformation(
            "已接管监听地址 {Url}（支持运行期换端口）。",
            _registry.DescribeUrl(listener.DisplayHost, ip.Port));

        return listener;
    }

    private IConnectionListenerFactory InnerFactory()
    {
        if (_inner is not null)
        {
            return _inner;
        }

        lock (_innerGate)
        {
            return _inner ??= _innerAccessor();
        }
    }

    /// <summary>把 <see cref="IPAddress"/> 还原成写进 URL 的 host 文本。</summary>
    internal static string DescribeHost(IPAddress address)
    {
        if (address.Equals(IPAddress.Any))
        {
            return "0.0.0.0";
        }

        if (address.Equals(IPAddress.IPv6Any))
        {
            return "[::]";
        }

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }
}
