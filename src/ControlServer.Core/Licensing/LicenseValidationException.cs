namespace ControlServer.Core.Licensing;

/// <summary>
/// 授权文件不可用（结构非法 / 签名不符 / 字段自相矛盾等）。
/// <para>
/// 这是<b>预期内的失败</b>而不是程序缺陷：现场完全可能把授权文本粘错、把文件发错版本。
/// 因此调用方应当捕获它并向用户展示 <see cref="Exception.Message"/>（文案已经是人话），
/// 而不是让它冒到顶层变成 500。
/// </para>
/// </summary>
public sealed class LicenseValidationException : Exception
{
    public LicenseValidationException(string message)
        : base(message)
    {
    }

    public LicenseValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
