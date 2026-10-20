using ControlServer.Core.Licensing;

namespace ControlServer.Core.Abstractions;

/// <summary>
/// 授权在设备上的完整落盘形态。
/// </summary>
/// <param name="Exists">是否存在授权文件。</param>
/// <param name="RawText">授权文件原文（Base64 文本，已去掉首尾空白）。</param>
/// <param name="Document">解析出的文档；解析失败为 <c>null</c>。</param>
/// <param name="Binding">通用授权的绑定侧车；没有为 <c>null</c>。</param>
/// <param name="ParseError">解析失败原因；成功为 <c>null</c>。</param>
public sealed record LicenseSnapshot(
    bool Exists,
    string? RawText,
    LicenseDocument? Document,
    LicenseBinding? Binding,
    string? ParseError)
{
    /// <summary>文件不存在。</summary>
    public static LicenseSnapshot Missing() => new(false, null, null, null, null);
}

/// <summary>
/// 授权文件存储端口。
/// <para>
/// 两个文件：<c>{DataDir}\license.lic</c>（签发时的原文，<b>原样保存</b>）与
/// <c>{DataDir}\license.binding.json</c>（通用授权的首次绑定记录）。
/// 为什么不把绑定写进授权文件本身，见 <see cref="LicenseBinding"/> 的注释。
/// </para>
/// <para>
/// <b>授权文件不参与配置导入导出</b>（与指令库同理）：它是设备级凭证，跟着机器走；
/// 若能被配置备份带跑，"把 A 机器的配置恢复到 B 机器"就会顺带把授权也带过去，
/// 而那份授权绑的是 A 的机器码，结果是 B 机器上一片"已授权但全部功能不可用"的混乱状态。
/// </para>
/// </summary>
public interface ILicenseStore
{
    /// <summary>读取当前授权（含绑定侧车）。不存在时返回 <see cref="LicenseSnapshot.Missing"/>。</summary>
    Task<LicenseSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子写入授权文件与绑定侧车。
    /// </summary>
    /// <param name="licenseText">授权文件原文（会原样写入，不做任何加工）。</param>
    /// <param name="binding">绑定记录；<c>null</c> 表示删除既有侧车。</param>
    Task WriteAsync(string licenseText, LicenseBinding? binding, CancellationToken cancellationToken = default);

    /// <summary>移除授权文件与绑定侧车（回到未授权状态）。</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取本机自动试用记录；不存在或无法解析为 <c>null</c>。
    /// <para>没有单独返回"解析失败"的通道：一份损坏的试用记录，其效果就该是"没有试用"。</para>
    /// </summary>
    Task<LocalTrialRecord?> ReadTrialAsync(CancellationToken cancellationToken = default);

    /// <summary>写入本机自动试用记录（首次生成时调用一次）。</summary>
    Task WriteTrialAsync(LocalTrialRecord record, CancellationToken cancellationToken = default);
}
