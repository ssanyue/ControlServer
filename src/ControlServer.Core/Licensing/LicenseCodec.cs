using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ControlServer.Core.Licensing;

/// <summary>
/// 授权文件的编解码：<b>载荷 JSON → 签名 → Base64 文本</b>，以及反向解析。
///
/// <para>
/// <b>本类型是全项目唯一允许拼授权载荷 JSON 的地方</b>。原因：
/// 签名是对"载荷字节"做的，只要有两个地方各自拼过一遍 JSON，
/// 就迟早会因为字段序、空白、数字格式、转义策略的细微差别而对不上，
/// 表现出来是<b>"自己签的自己验不过"</b> —— 而且这种错在签发那一刻是看不出来的，
/// 要等到客户现场导入才炸。
/// </para>
///
/// <para>
/// <b>为什么手写序列化而不用 <see cref="JsonSerializer"/></b>：
/// 序列化器的字段顺序取决于类型的属性声明顺序与运行时反射结果，
/// 是"实现细节"而非"契约"。一旦有人给 <see cref="LicenseDocument"/>
/// 换了个属性顺序（纯重构、不改语义），已签发的授权会<b>集体失效</b>，
/// 而且编译器不会提醒。手写一张固定字段表，这个顺序就成了显式契约，
/// 改它必须是有意识的动作（见 <see cref="LicenseDocument"/> 上的警告）。
/// </para>
///
/// <para>
/// 文本形态 = <c>Base64(UTF-8(JSON))</c>。选 Base64 而不是裸 JSON 的理由与参照实现一致：
/// 授权文本会被邮件/微信转发、被复制粘贴进输入框，裸 JSON 里的引号与换行在中间环节极易被"美化"掉；
/// Base64 是单一字母数字串，怎么转都不会变形。解码时<b>会忽略所有空白</b>，因此换行包裹也无妨。
/// </para>
/// </summary>
public static class LicenseCodec
{
    /// <summary>签名在载荷里的字段名。</summary>
    public const string SignatureField = "sig";

    /// <summary>Base64 正文的折行宽度（仅影响写出到文件的观感，解码时空白一律忽略）。</summary>
    private const int WrapWidth = 76;

    // ═══════════════════════════════════════════════════════════
    // 编码
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 构造<b>待签名载荷</b>（不含 <c>sig</c> 字段的确定性 JSON）。
    /// <para>字段序、数字格式、转义规则都是契约的一部分，改动即为不兼容变更。</para>
    /// </summary>
    public static string BuildPayload(LicenseDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder(320);
        builder.Append('{');

        var first = true;

        void Field(string name, string rawJson)
        {
            if (!first) builder.Append(',');
            first = false;
            builder.Append('"').Append(name).Append("\":").Append(rawJson);
        }

        void Text(string name, string value) => Field(name, Quote(value));

        void Number(string name, long value) =>
            Field(name, value.ToString(CultureInfo.InvariantCulture));

        // ⚠️ 顺序即契约。新增字段只能追加到末尾（且必须同步改两边实现）。
        Number("v", document.V);
        Text("mc", document.Mc);
        Text("lt", document.Lt);
        Text("id", document.Id);
        Text("ed", document.Ed);
        Number("et", document.Et);
        Number("md", document.Md);
        Text("sn", document.Sn);
        Text("vi", document.Vi);
        Text("hn", document.Hn);
        Number("ub", document.Ub);
        Number("ud", document.Ud);
        Number("bt", document.Bt);
        Number("ivt", document.Ivt);

        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>构造<b>完整文件载荷</b>（待签名载荷 + 追加在末尾的 <c>sig</c> 字段）。</summary>
    public static string BuildSignedPayload(LicenseDocument document)
    {
        var payload = BuildPayload(document);

        // payload 一定以 '}' 收尾；把 sig 插在它之前，保证 sig 是最后一个字段。
        return payload[..^1] + ",\"" + SignatureField + "\":" + Quote(document.Sig) + "}";
    }

    /// <summary>把授权文档编码成可分发/可粘贴的 Base64 文本（按 <see cref="WrapWidth"/> 折行）。</summary>
    public static string Encode(LicenseDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var raw = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(BuildSignedPayload(document)),
            Base64FormattingOptions.InsertLineBreaks);

        return Wrap(raw, WrapWidth);
    }

    /// <summary>把一串字节编码成 Base64（不折行）——签名等短数据的统一形态。</summary>
    public static string ToBase64(byte[] bytes) => Convert.ToBase64String(bytes);

    // ═══════════════════════════════════════════════════════════
    // 解码
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 解析授权文本。失败一律抛 <see cref="LicenseValidationException"/>（附可读原因）。
    /// <para>
    /// ⚠️ 本方法<b>只做结构解析，不做验签</b> —— 解析成功不代表授权有效。
    /// 顺序必须是"先解析拿字段、再按字段重算载荷、最后验签"，且<b>只能</b>用重算出来的载荷验签
    /// （而不是用原文里的那段 JSON 字节），否则攻击者可以在 JSON 里塞重复字段绕过校验。
    /// </para>
    /// </summary>
    public static LicenseDocument Decode(string? text)
    {
        if (!TryDecode(text, out var document, out var error))
        {
            throw new LicenseValidationException(error!);
        }

        return document!;
    }

    /// <summary>宽容版解析：失败返回 <c>false</c> 与可读原因，不抛异常。</summary>
    public static bool TryDecode(string? text, out LicenseDocument? document, out string? error)
    {
        document = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "授权文本为空。";
            return false;
        }

        byte[] utf8;
        try
        {
            utf8 = DecodeBase64(text);
        }
        catch (FormatException)
        {
            error = "授权文件不是有效的 Base64 文本（可能在转发过程中被截断或改动过）。";
            return false;
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(utf8);
        }
        catch (JsonException)
        {
            error = "授权文件内容不是合法的 JSON。";
            return false;
        }

        using (json)
        {
            var root = json.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "授权文件内容不是 JSON 对象。";
                return false;
            }

            var parsed = new LicenseDocument
            {
                V = ReadInt(root, "v"),
                Mc = ReadText(root, "mc"),
                Lt = ReadText(root, "lt"),
                Id = ReadText(root, "id"),
                Ed = ReadText(root, "ed"),
                Et = ReadLong(root, "et"),
                Md = ReadInt(root, "md"),
                Sn = ReadText(root, "sn"),
                Vi = ReadText(root, "vi"),
                Hn = ReadText(root, "hn"),
                Ub = ReadInt(root, "ub"),
                Ud = ReadLong(root, "ud"),
                Bt = ReadLong(root, "bt"),
                Ivt = ReadLong(root, "ivt"),
                Sig = ReadText(root, SignatureField),
            };

            if (string.IsNullOrWhiteSpace(parsed.Sig))
            {
                error = "授权文件缺少签名（sig）。";
                return false;
            }

            document = parsed;
            return true;
        }
    }

    /// <summary>解码 Base64，忽略全部空白；同时接受标准与 URL-safe 两种字母表。</summary>
    public static byte[] DecodeBase64(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch == '-') { builder.Append('+'); continue; }
            if (ch == '_') { builder.Append('/'); continue; }
            builder.Append(ch);
        }

        var normalized = builder.ToString();

        // 补齐 padding：粘贴过程中尾部 '=' 很容易被吞掉。
        var remainder = normalized.Length % 4;
        if (remainder == 2) normalized += "==";
        else if (remainder == 3) normalized += "=";
        else if (remainder == 1) throw new FormatException("Base64 长度非法。");

        return Convert.FromBase64String(normalized);
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// JSON 字符串字面量（含引号）。<b>手写转义</b>而不是借 JsonEncodedText，
    /// 是因为要保证"任何 .NET 版本、任何编码器配置下，同样的输入产出同样的字节"。
    /// 非 ASCII 字符原样输出（UTF-8 编码后即为多字节），不做 \uXXXX 转义 ——
    /// 这样中文客户名在授权文件里仍然可读。
    /// </summary>
    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>按固定宽度折行（只影响观感；解码侧会忽略空白，因此旧文件无需重排）。</summary>
    private static string Wrap(string text, int width)
    {
        if (text.Length <= width) return text;

        var builder = new StringBuilder(text.Length + (text.Length / width) + 1);

        for (var offset = 0; offset < text.Length; offset += width)
        {
            if (offset > 0) builder.Append('\n');
            builder.Append(text, offset, Math.Min(width, text.Length - offset));
        }

        return builder.ToString();
    }

    private static string ReadText(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static long ReadLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0L;
}
