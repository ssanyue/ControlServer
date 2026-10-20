namespace ControlServer.LicenseTool;

/// <summary>
/// 命令行用法错误（参数缺失、互相冲突、无法识别、文件已存在等）。
/// <para>
/// 与 <c>LicenseValidationException</c> 分开：那个表示"这份授权文件不行"，
/// 这个表示"你这条命令本身不对"。前者提示用户去换文件，后者提示用户改命令。
/// </para>
/// </summary>
internal sealed class UsageException : Exception
{
    public UsageException(string command, string message)
        : base(message)
        => Command = command;

    /// <summary>出错时正在执行的子命令，用于给出针对该命令的用法提示。</summary>
    public string Command { get; }
}
