using System.Text;

namespace ControlServer.LicenseTool;

/// <summary>
/// 文件读写。
///
/// <para>
/// <b>统一 UTF-8 且不带 BOM</b>。这不是风格问题：授权文本是 Base64，
/// 而 <c>U+FEFF</c>（BOM）在 .NET 里<b>不被视为空白</b>，所以带 BOM 的文件会让
/// <c>Convert.FromBase64String</c> 直接抛格式错误 —— 症状是"文件看起来完全正常，
/// 服务端就是说不合法"。（服务端读取时会做 BOM 检测，但工具不该依赖对方兜底。）
/// </para>
/// </summary>
internal static class FileIo
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteTextNoBom(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, content, Utf8NoBom);
    }

    public static Task<string> ReadTextAsync(string path)
        => File.ReadAllTextAsync(path, Encoding.UTF8);

    /// <summary>读取必填的文本文件；不存在或读不了时打印原因并终止。</summary>
    public static async Task<string> ReadRequiredAsync(string path, string what)
    {
        if (!File.Exists(path))
        {
            ConsoleOut.Error($"找不到{what}：{path}");
            throw new ToolAbortException();
        }

        try
        {
            return await ReadTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleOut.Error($"读取{what}失败：{ex.Message}");
            throw new ToolAbortException();
        }
    }
}
