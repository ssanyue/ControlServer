using System.Text;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 数据编码内核（**纯函数，无 IO**）。
/// <para>
/// 覆盖数据准备链路上的四个纯函数：
/// <c>PrepareData</c> / <c>ParseHex</c> / <c>ParseMacAddress</c> / <c>GetPreview</c>。
/// 这里是 M2 方案 §11.3「行为等价性测试向量」的主战场，必须能被测试逐个断言。
/// </para>
/// <para>
/// <b>跨平台 ANSI（方案 §10.4 的坑）</b>：
/// .NET 在非 Windows 上默认<b>不带</b>代码页编码，<c>Encoding.GetEncoding("GBK")</c> 会抛
/// <see cref="NotSupportedException"/>。旧版（Windows MAUI）因此恰好能用；
/// 服务版要跑 Linux，必须注册 <see cref="CodePagesEncodingProvider.Instance"/>。
/// 本类在静态构造里<b>无条件注册</b>，取不到 GBK 时回退 UTF-8 并记下诊断信息
/// （<see cref="AnsiIsExact"/> / <see cref="AnsiEncodingName"/>），<b>绝不抛异常</b>。
/// </para>
/// </summary>
public static class DataEncoder
{
    /// <summary>中文 Windows 的 ANSI 代码页号。</summary>
    public const int GbkCodePage = 936;

    private static readonly Encoding EffectiveAnsi;
    private static readonly bool RegisteredProvider;
    private static readonly string? RegistrationError;

    static DataEncoder()
    {
        // ── 无条件注册代码页提供程序 ──
        // 幂等，重复调用没有副作用；已注册过时不会抛。
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            RegisteredProvider = true;
        }
        catch (Exception ex)
        {
            // 极端环境（被裁剪的运行时）下可能失败 —— 记住原因，继续走回退路径。
            RegisteredProvider = false;
            RegistrationError = ex.Message;
        }

        // ── 取 GBK；失败回退 UTF-8 ──
        try
        {
            EffectiveAnsi = Encoding.GetEncoding(GbkCodePage);
            AnsiIsExact = true;
        }
        catch (Exception ex)
        {
            EffectiveAnsi = Encoding.UTF8;
            AnsiIsExact = false;
            RegistrationError ??= ex.Message;
        }

        AnsiEncodingName = EffectiveAnsi.WebName;
    }

    /// <summary>
    /// 当前生效的 ANSI 编码名（正常应为 <c>gb2312</c> / <c>gbk</c>）。
    /// 启动自检会把它打进日志 —— 便于 Linux 上一眼看出有没有静默乱码（方案 §13 风险缓解）。
    /// </summary>
    public static string AnsiEncodingName { get; }

    /// <summary>ANSI 是否真的拿到了 GBK。<c>false</c> 表示已回退 UTF-8，中文将按 UTF-8 发送。</summary>
    public static bool AnsiIsExact { get; }

    /// <summary>是否成功注册了代码页提供程序。</summary>
    public static bool HasCodePagesProvider => RegisteredProvider;

    /// <summary>注册 / 取码页过程中的错误（无错为 null），仅用于诊断输出。</summary>
    public static string? DiagnosticError => RegistrationError;

    /// <summary>供启动自检使用的一行诊断文本。</summary>
    public static string DescribeAnsiEncoding()
        => AnsiIsExact
            ? $"ANSI 编码：{AnsiEncodingName}（CP{GbkCodePage}，精确匹配历史格式）"
            : $"ANSI 编码：{AnsiEncodingName}（⚠️ 未取到 CP{GbkCodePage}，已回退 UTF-8；原因：{RegistrationError ?? "未知"}）";

    // ═══════════════════════════════════════════════════════════
    // 编码
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 按数据类型把文本编码为待发送字节。
    /// <para>
    /// 分支顺序与旧版 <c>PrepareData</c> <b>完全一致</b>：
    /// <c>Hex</c> → <c>ANSI</c> → <c>ASCII</c> → <c>UTF-8</c> → 其它（含 <c>String</c>、<c>指令库</c> 残留）一律 UTF-8。
    /// </para>
    /// </summary>
    /// <exception cref="FormatException">
    /// <paramref name="dataType"/> 为 Hex 且内容非法时抛出（<b>与旧版一致</b>：奇数长度、非法字符都抛）。
    /// </exception>
    public static byte[] Encode(string? dataType, string? content)
    {
        if (IsHex(dataType)) return ParseHex(content);
        if (IsAnsi(dataType)) return EffectiveAnsi.GetBytes(content ?? string.Empty);
        if (IsAscii(dataType)) return Encoding.ASCII.GetBytes(content ?? string.Empty);
        if (IsUtf8(dataType)) return Encoding.UTF8.GetBytes(content ?? string.Empty);
        return Encoding.UTF8.GetBytes(content ?? string.Empty);
    }

    /// <summary>
    /// 非抛出版编码（供 API 预览 / 校验使用）。异常转为 <c>error</c> 文本。
    /// </summary>
    public static bool TryEncode(string? dataType, string? content, out byte[] bytes, out string? error)
    {
        try
        {
            bytes = Encode(dataType, content);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            bytes = Array.Empty<byte>();
            error = ex.Message;
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Hex
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 十六进制字符串 → 字节数组。
    /// <para>与旧版逐行一致：先剥离空格 / 换行 / 制表符 / <c>0x</c>，再两两成字节。</para>
    /// </summary>
    /// <exception cref="FormatException">长度为奇数或含非法字符。</exception>
    public static byte[] ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();

        var cleaned = hex
            .Replace(" ", string.Empty)
            .Replace("\n", string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\t", string.Empty)
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (cleaned.Length % 2 != 0)
        {
            throw new FormatException("十六进制数据长度必须为偶数");
        }

        var bytes = new byte[cleaned.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            // Convert.ToByte(..., 16) 对非法字符抛 FormatException —— 与旧版一致，不吞掉。
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    /// <summary>字节数组 → 大写、无分隔的十六进制串（对接码 Hex 比较用）。</summary>
    public static string ToHexString(byte[] bytes)
        => bytes is null || bytes.Length == 0 ? string.Empty : Convert.ToHexString(bytes);

    /// <summary>规范化十六进制对接码：去掉所有空白并转大写（与旧版 <c>NormalizeHex</c> 一致）。</summary>
    public static string NormalizeHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return string.Empty;

        var sb = new StringBuilder(hex.Length);
        foreach (var c in hex)
        {
            if (!char.IsWhiteSpace(c)) sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════
    // MAC / Wake-on-LAN
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 解析 MAC 地址为 6 字节。支持 <c>AA:BB:CC:DD:EE:FF</c> / <c>AABBCCDDEEFF</c> /
    /// <c>aa-bb-cc-dd-ee-ff</c> / 带点或空格的分隔形式。
    /// </summary>
    /// <returns>合法返回 6 字节；长度非 12 位或含非法字符返回 <c>null</c>（与旧版一致，不抛）。</returns>
    public static byte[]? TryParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;

        var cleaned = mac
            .Replace(":", string.Empty)
            .Replace("-", string.Empty)
            .Replace(".", string.Empty)
            .Replace(" ", string.Empty)
            .Trim();

        if (cleaned.Length != 12) return null;

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(
                    cleaned.AsSpan(i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    provider: null,
                    out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    /// <summary>
    /// 构造网络唤醒魔术包：6 字节 <c>0xFF</c> + 16 次 MAC（共 102 字节）。
    /// </summary>
    public static byte[] BuildMagicPacket(byte[] mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        if (mac.Length != 6) throw new ArgumentException("MAC 必须为 6 字节", nameof(mac));

        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    /// <summary>WoL 默认端口（旧版语义：动作端口为 0 时用 9）。</summary>
    public const int DefaultWolPort = 9;

    /// <summary>
    /// 归一化 WoL 目标地址：空或 <c>0.0.0.0</c> 一律视为广播 <c>255.255.255.255</c>（与旧版一致）。
    /// </summary>
    public static string NormalizeWolHost(string? host)
        => string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" ? "255.255.255.255" : host;

    // ═══════════════════════════════════════════════════════════
    // 预览
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把「已编码字节」还原为可读预览，避免 ANSI 中文在日志里显示成乱码。
    /// 规则与旧版 <c>GetPreview</c> 一致：Hex 直接回显原文；空则空串；解码失败回退 <c>AA-BB-CC</c>。
    /// </summary>
    public static string Preview(string? dataType, byte[]? data, string? rawContent)
    {
        if (IsHex(dataType)) return rawContent ?? string.Empty;
        if (data is null || data.Length == 0) return string.Empty;

        try
        {
            if (IsAnsi(dataType)) return EffectiveAnsi.GetString(data);
            if (IsAscii(dataType)) return Encoding.ASCII.GetString(data);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return BitConverter.ToString(data);
        }
    }

    /// <summary>字节数组 → <c>41 42 43</c> 形式的十六进制可读串（预览 / 日志用）。</summary>
    public static string ToSpacedHex(byte[]? data)
        => data is null || data.Length == 0 ? string.Empty : BitConverter.ToString(data).Replace('-', ' ');

    // ═══════════════════════════════════════════════════════════
    // 类型判定（大小写不敏感，与旧版 OrdinalIgnoreCase 一致）
    // ═══════════════════════════════════════════════════════════

    public static bool IsHex(string? dataType) => Eq(dataType, DataTypes.Hex);

    public static bool IsAnsi(string? dataType) => Eq(dataType, DataTypes.Ansi);

    public static bool IsAscii(string? dataType) => Eq(dataType, DataTypes.Ascii);

    public static bool IsUtf8(string? dataType) => Eq(dataType, DataTypes.Utf8);

    public static bool IsCommandLibrary(string? dataType) => Eq(dataType, DataTypes.CommandLibrary);

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
