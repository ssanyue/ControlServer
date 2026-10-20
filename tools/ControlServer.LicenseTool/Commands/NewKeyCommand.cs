using ControlServer.Core.Licensing;

namespace ControlServer.LicenseTool.Commands;

/// <summary>
/// <c>new-key</c> —— 生成厂商密钥对。
///
/// <para>
/// <b>为什么默认拒绝覆盖</b>：私钥是授权的唯一根。覆盖它不会"更新"任何东西，
/// 只会让此前用它签发的所有授权<b>全部失效</b>（服务端内嵌的还是旧公钥）。
/// 这个动作一旦误触，代价是所有已交付客户重新走一遍签发流程 ——
/// 所以必须显式加 <c>--force</c> 才能覆盖，而不是默认静默替换。
/// </para>
/// </summary>
internal static class NewKeyCommand
{
    public const string Name = "new-key";

    private const string DefaultPrivatePath = "vendor-private.pem";

    public const string Help = """
        用法：
          ControlServer.LicenseTool new-key [--out <私钥文件>] [--public-out <公钥文件>] [--force]

        参数：
          --out         私钥输出路径（默认 vendor-private.pem）
          --public-out  公钥输出路径（默认为私钥路径里的 private 换成 public）
          --force       允许覆盖已存在的私钥文件（危险，见下）

        说明：
          生成 ECDSA P-256 密钥对，并打印可直接粘贴进
          src/ControlServer.Core/Licensing/VendorPublicKey.cs 的公钥与指纹。

          私钥文件绝不可入库、不可随产品交付、不可放进任何共享目录。
          一旦泄漏，任何人拿到它都能签出"任意机器、永久有效"的授权。
        """;

    public static Task<int> RunAsync(string[] args)
    {
        var parsed = Arguments.Parse(args);

        if (parsed.Has("help") || parsed.Has("h"))
        {
            ConsoleOut.Line(Help);
            return Task.FromResult(0);
        }

        parsed.RejectUnknown(Name, "out", "public-out", "force", "help", "h");

        if (parsed.Positional.Count > 0)
        {
            throw new UsageException(Name, $"多余的位置参数「{parsed.Positional[0]}」。本命令不需要位置参数。");
        }

        var privatePath = Path.GetFullPath(parsed.ValueOr("out", DefaultPrivatePath));
        var publicPath = Path.GetFullPath(parsed.Value("public-out") ?? DerivePublicPath(privatePath));

        if (File.Exists(privatePath) && !parsed.Has("force"))
        {
            ConsoleOut.Error($"私钥文件已存在，已停止：{privatePath}");
            ConsoleOut.Hint("覆盖它会让此前用旧私钥签发的**所有授权失效** —— 服务端内嵌的仍是旧公钥，");
            ConsoleOut.Hint("新私钥签出来的文件在那里一律验不过，必须连服务端一起重新交付。");
            ConsoleOut.Hint("确实要换新密钥，请加 --force 重跑（那意味着你还要重新签发全部客户授权）。");
            throw new ToolAbortException();
        }

        var (privatePem, publicPem, fingerprint) = LicenseSigner.GenerateKeyPair();

        FileIo.WriteTextNoBom(privatePath, ConsoleOut.NormalizePem(privatePem));
        FileIo.WriteTextNoBom(publicPath, ConsoleOut.NormalizePem(publicPem));

        ConsoleOut.Section("已生成密钥对");
        ConsoleOut.Field("私钥", privatePath);
        ConsoleOut.Field("公钥", publicPath);
        ConsoleOut.Field("公钥指纹", fingerprint);
        ConsoleOut.Field("服务端当前", VendorPublicKey.Fingerprint);

        ConsoleOut.Line();
        ConsoleOut.Warn("把私钥文件保管好：它等于整套授权的签发权。");
        ConsoleOut.Hint("不要提交到版本库、不要随产品交付、不要放进共享/同步目录。");

        ConsoleOut.Section("下一步：把公钥替换进服务端");
        ConsoleOut.Line($"  编辑 src/ControlServer.Core/Licensing/VendorPublicKey.cs，用下面这段替换原有内容：");
        ConsoleOut.Line();

        Console.WriteLine($"    public const string Fingerprint = \"{fingerprint}\";");
        Console.WriteLine();
        Console.WriteLine("    public const string Pem = \"\"\"");

        // 原文逐行输出（不做任何缩进）：粘进 C# 原生字符串字面量后，
        // 与项目里已有的 VendorPublicKey.cs 应逐字节一致。
        Console.Write(ConsoleOut.NormalizePem(publicPem));

        Console.WriteLine("\"\"\";");

        ConsoleOut.Line();
        ConsoleOut.Warn("换公钥必须重编并重新交付服务端，否则新授权在客户那里一律验不过。");
        ConsoleOut.Hint("同时需要通知所有客户改用新私钥重新签发的授权文件。");

        return Task.FromResult(0);
    }

    /// <summary>
    /// 由私钥路径推默认公钥路径：<c>vendor-private.pem</c> → <c>vendor-public.pem</c>。
    /// 名字里没有 <c>private</c> 时退化成 <c>xxx.public.pem</c>，宁可多一层后缀也不覆盖用户的文件。
    /// </summary>
    internal static string DerivePublicPath(string privatePath)
    {
        var directory = Path.GetDirectoryName(privatePath);
        var name = Path.GetFileNameWithoutExtension(privatePath);
        var extension = Path.GetExtension(privatePath);

        var stem = name.Contains("private", StringComparison.OrdinalIgnoreCase)
            ? ReplaceOrdinalIgnoreCase(name, "private", "public")
            : name + ".public";

        return string.IsNullOrEmpty(directory)
            ? stem + extension
            : Path.Combine(directory, stem + extension);
    }

    /// <summary>大小写不敏感的一次替换（用于把路径里的 private 换成 public）。</summary>
    private static string ReplaceOrdinalIgnoreCase(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);

        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }
}
