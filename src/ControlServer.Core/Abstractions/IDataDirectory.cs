namespace ControlServer.Core.Abstractions;

/// <summary>
/// 服务数据目录（跨平台）。Windows 默认 %ProgramData%\ControlServer，Linux 默认 /var/lib/controlserver。
/// 全项目禁止硬编码路径，一律经由此接口。
/// </summary>
public interface IDataDirectory
{
    string Root { get; }

    string DatabaseFile { get; }

    string ConfigDirectory { get; }

    string LogDirectory { get; }

    string FilesDirectory { get; }

    string ImagesDirectory { get; }

    /// <summary>
    /// HTTPS 证书目录（本地根 CA + 服务器证书 + PFX 口令）。
    ///
    /// <para>
    /// 🔥 <b>它放的是"私钥"，不是普通数据</b> —— 这个目录的访问权限就是
    /// 整条链路的安全边界（拿到根 CA 私钥的人可以给任何观看端签一张它认的证书，
    /// 从而冒充中控服务）。所以它<b>不许</b>被放进备份 / 分发 / 日志导出里。
    /// </para>
    /// <para>
    /// ⚠️ 它<b>不是</b>授权文件（<see cref="LicenseFile"/>）—— 那个是"卖了多少"，
    /// 这个是"你连的是不是真的那台机器"，两件事的失效后果完全不同。
    /// </para>
    /// </summary>
    string CertificateDirectory { get; }

    string LicenseFile { get; }

    string TimeStampFile { get; }

    /// <summary>创建目录结构（幂等）。</summary>
    void EnsureCreated();
}
