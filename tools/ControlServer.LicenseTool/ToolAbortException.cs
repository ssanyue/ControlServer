namespace ControlServer.LicenseTool;

/// <summary>
/// 命令已经自行向用户说明了原因，需要立刻终止。
/// <para>
/// 用它把"打印 + 退出"收在同一个地方：命令内部最清楚上下文（哪个文件、哪一步），
/// 顶层只负责设置退出码，不再重复打印 —— 否则同一个错会冒出两条 <c>[error]</c>。
/// </para>
/// </summary>
internal sealed class ToolAbortException : Exception
{
    public ToolAbortException(int exitCode = 1)
        : base("命令已终止。")
        => ExitCode = exitCode;

    public int ExitCode { get; }
}
