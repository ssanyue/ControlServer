using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;

namespace ControlServer.Server.Security;

/// <summary>
/// <see cref="LocalCertificateAuthority"/> 的<b>懒加载持有者</b>（M5.3）。
///
/// <para>
/// 🔥 <b>为什么不直接把 CA 注册成单例</b>：它的构造会（首次）生成密钥对并落盘 ——
/// 那是一次几十到上百毫秒的操作，而它<b>无论如何都要早于第一次 TLS 握手</b>。
/// 直接注册单例意味着"启动时必做"，而生成失败时 DI 无法表达"这个单例是空的"
/// （<c>GetRequiredService</c> 会对 factory 返回的 <c>null</c> 抛异常，
/// 那会把一个"音频用不了"的问题升级成"整个中控起不来"）。
/// </para>
///
/// <para>
/// <b>所以</b>：这里持一个 <see cref="Lazy{T}"/>，取值时机交给"真正需要证书的那一刻"
/// （Kestrel 握手 / 启动自检 / 有人来下载根证书）。失败就是 <c>null</c>，
/// 由调用方各自决定怎么降级 —— 服务本身照常跑 http。
/// </para>
/// </summary>
public sealed class HttpsCertificateProvider
{
    private readonly Lazy<LocalCertificateAuthority?> _lazy;

    public HttpsCertificateProvider(IDataDirectory dataDirectory, LogService log)
    {
        _lazy = new Lazy<LocalCertificateAuthority?>(
            () => LocalCertificateAuthority.TryLoadOrCreate(dataDirectory, log),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>取证书机构；不可用（数据目录不可写 / 生成失败）时为 <c>null</c>。</summary>
    /// <remarks>
    /// ⚠️ Kestrel 的服务证书选择器会调它。返回 <c>null</c> 时 Kestrel 会在握手时抛
    /// "没有可用的服务器证书" —— 那是<b>对的</b>行为：宁可 https 明确不可用，
    /// 也不能悄悄降级成明文（那正是这个功能要避免的事情）。
    /// </remarks>
    public LocalCertificateAuthority? Current => _lazy.Value;

    /// <summary>给 Kestrel 用的服务器证书（含私钥）；不可用时 <c>null</c>。</summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate => Current?.ServerCertificate;
}
