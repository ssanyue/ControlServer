using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ControlServer.LicenseTool;

/// <summary>
/// 极简命令行解析：<c>--name value</c> / <c>--flag</c>，其余为位置参数。
///
/// <para>
/// <b>为什么不用第三方库</b>：签发工具要能在任何一台干净机器上"解压即用"，
/// 多一个包就多一份"客户/同事这边还原不出来"的概率。这里需要的能力只有两项
/// （取值参数 + 开关），两百行内足够，且行为完全可预期。
/// </para>
///
/// <para>
/// <b>判别规则</b>：<c>--name</c> 后面若跟着一个<b>不以 <c>--</c> 开头</b>的 token，
/// 它就是该参数的值；否则视为开关（值为 <c>null</c>）。
/// 少数情况（比如 <c>--customer --sn X</c>）会被判成"customer 是个空开关"，
/// 但那种写法本身有歧义，最终会被"缺少必需参数"拦下，不会静默出错。
/// </para>
/// </summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = new();

    public static Arguments Parse(IEnumerable<string> args)
    {
        var result = new Arguments();
        var list = args.ToList();

        for (var i = 0; i < list.Count; i++)
        {
            var token = list[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                result._positional.Add(token);
                continue;
            }

            string? value = null;

            if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = list[++i];
            }

            result._options[Canonical(token)] = value;
        }

        return result;
    }

    /// <summary>位置参数（不含 <c>--</c> 开头的 token）。</summary>
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>是否出现过该参数（开关与取值参数都算）。</summary>
    public bool Has(string name) => _options.ContainsKey(Canonical(name));

    /// <summary>取值；未提供或作为空开关出现时为 <c>null</c>。</summary>
    public string? Value(string name)
        => _options.TryGetValue(Canonical(name), out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    /// <summary>取值，缺省时回退。</summary>
    public string ValueOr(string name, string fallback) => Value(name) ?? fallback;

    /// <summary>必填取值。</summary>
    public string Require(string command, string name)
        => Value(name) ?? throw new UsageException(command, $"缺少必需参数 --{name}。");

    /// <summary>必填整数。</summary>
    public int RequireInt(string command, string name)
    {
        var text = Require(command, name);

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new UsageException(command, $"--{name} 需要一个整数，但收到「{text}」。");
    }

    /// <summary>可选整数。</summary>
    public int? OptionalInt(string command, string name)
    {
        var text = Value(name);
        if (text is null) return null;

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new UsageException(command, $"--{name} 需要一个整数，但收到「{text}」。");
    }

    /// <summary>
    /// 拒绝任何未在 <paramref name="allowed"/> 里声明过的参数。
    /// <para>
    /// <b>必须显式调用</b>。否则把 <c>--exipre</c> 拼错时会静默变成"没有指定有效期"，
    /// 结果签出一份永久授权 —— 一个错字换来的后果是"授权永不过期"，
    /// 这正是最需要被拦下的那类错误。
    /// </para>
    /// </summary>
    public void RejectUnknown(string command, params string[] allowed)
    {
        var known = new HashSet<string>(allowed.Select(Canonical), StringComparer.OrdinalIgnoreCase);

        foreach (var name in _options.Keys)
        {
            if (!known.Contains(name))
            {
                throw new UsageException(command, $"无法识别的参数 --{name}。");
            }
        }
    }

    /// <summary>互斥参数：最多只能出现一个。</summary>
    public void RejectTogether(string command, params string[] names)
    {
        var present = names.Where(Has).ToList();

        if (present.Count > 1)
        {
            throw new UsageException(
                command, $"参数 {string.Join(" 与 ", present.Select(n => "--" + n))} 不能同时使用。");
        }
    }

    private static string Canonical(string name)
        => name.TrimStart('-').Trim().ToLowerInvariant();
}
