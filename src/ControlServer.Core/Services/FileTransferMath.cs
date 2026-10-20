using System.Text;
using ControlServer.Contracts;

namespace ControlServer.Core.Services;

/// <summary>
/// 文件传输相关的<b>纯函数</b>（M4.4）：进度、文件名安全化、路径穿越防护、任务状态汇总。
/// <para>
/// 全部无状态、无 IO —— 因此能在 Core 单测里被穷举验证，
/// 也能被服务端与客户端（Agent）<b>共用同一份实现</b>。
/// 路径白名单这种东西只要有两份实现，就一定会有一份是错的。
/// </para>
/// </summary>
public static class FileTransferMath
{
    /// <summary>
    /// 传输进度百分比（0–100，保留一位小数由调用方决定）。
    /// <para>
    /// <b>总大小未知时返回 0 而不是 100</b>：回传任务在客户端量出源文件大小之前
    /// <c>total</c> 就是 0，此时显示"0%"表示"还不知道"，比显示"100%"（会被读成"传完了"）安全得多。
    /// </para>
    /// </summary>
    public static double ProgressPercent(long receivedBytes, long totalBytes)
    {
        if (totalBytes <= 0) return 0d;

        var pct = receivedBytes * 100d / totalBytes;

        if (pct < 0) return 0d;
        return pct > 100d ? 100d : pct;
    }

    /// <summary>
    /// 把用户/客户端给的原始文件名清洗成一个<b>安全的单段文件名</b>。
    /// <para>
    /// 做三件事：① 只取最后一段（<c>..\..\x</c> → <c>x</c>）；
    /// ② 去掉非法字符与首尾的点；③ 限制长度。空/全是非法字符时回退为 <c>file</c>。
    /// </para>
    /// <para>
    /// ⚠️ 即使清洗过，<b>也绝不拿它当磁盘路径</b> —— 落盘名一律由服务端生成
    /// （见 <see cref="GenerateStoredName"/>）。本函数只服务"展示名"与"取扩展名"。
    /// </para>
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";

        // 统一分隔符后取最后一段 —— 无论对方用 \ 还是 /，都只留文件名部分。
        var last = name.Replace('\\', '/');
        var slash = last.LastIndexOf('/');
        if (slash >= 0) last = last[(slash + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(last.Length);

        foreach (var ch in last)
        {
            if (ch is '\r' or '\n' or '\t') continue;
            if (Array.IndexOf(invalid, ch) >= 0) continue;
            sb.Append(ch);
        }

        // 去掉首尾空白与点：Windows 上以点结尾的目录项本身就是非法的，
        // 而 "." / ".." 更是目录穿越的原料。
        var cleaned = sb.ToString().Trim().Trim('.');

        if (cleaned.Length == 0) return "file";
        if (cleaned.Length > 180) cleaned = cleaned[..180];

        return cleaned;
    }

    /// <summary>
    /// 从（已清洗的）文件名里取出一个安全的扩展名（含点，小写）。
    /// <para>拿不到安全扩展名时返回空串 —— 调用方回退到 <c>.bin</c>，而不是硬塞一个猜测的扩展名。</para>
    /// </summary>
    public static string SafeExtension(string? name)
    {
        var ext = Path.GetExtension(SanitizeFileName(name));

        if (string.IsNullOrEmpty(ext) || ext.Length > 12) return string.Empty;

        // 只认"点 + 字母数字"：挡住 ".p n g"、".b/../" 这类怪异输入。
        for (var i = 1; i < ext.Length; i++)
        {
            if (!char.IsLetterOrDigit(ext[i])) return string.Empty;
        }

        return ext.ToLowerInvariant();
    }

    /// <summary>
    /// 生成磁盘上的实际文件名（<b>唯一来源</b>）。形如 <c>f_{guid}.ext</c>，只含一段。
    /// <para>用 GUID 是为了杜绝覆盖与猜测；保留扩展名只是为了让运维在资源管理器里能看出类型。</para>
    /// </summary>
    public static string GenerateStoredName(string? originalName)
    {
        var ext = SafeExtension(originalName);
        if (ext.Length == 0) ext = ".bin";

        return $"f_{Guid.NewGuid():N}{ext}";
    }

    /// <summary>
    /// 目标路径是否确实落在 <paramref name="rootDirectory"/> 之内（防路径穿越的<b>唯一</b>实现）。
    /// <para>
    /// ⚠️ 比较时<b>必须带末尾目录分隔符</b>：否则 <c>C:\data\files-evil</c> 会被
    /// <c>StartsWith("C:\data\files")</c> 判为"在目录内"（本项目已在图片目录上踩过一次）。
    /// </para>
    /// <para>
    /// ⚠️ <b>但根目录自己结尾就带分隔符，且不能再补一个</b>
    /// （<see cref="Path.TrimEndingDirectorySeparator"/> 刻意不裁根，<c>C:\</c> 原样留下）。
    /// 补成 <c>C:\\</c> 会让盘根之下的一切**全部**判成"不在目录内" ——
    /// 用户看到的是"白名单里明明写了盘根、却一律拒收"（2026-10-15 修）。
    /// </para>
    /// </summary>
    public static bool IsInside(string rootDirectory, string candidateFullPath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(candidateFullPath))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string root, path;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateFullPath));
        }
        catch
        {
            // 非法路径（含 NUL、非法字符）一律判不通过 —— 不去猜它"也许在目录里"。
            return false;
        }

        if (string.Equals(path, root, comparison)) return true;

        // 只有 **根** 才会在 Trim 之后仍以分隔符结尾（`C:\` / `/`）：
        // 此时它本身就是完整前缀，补分隔符会变成 `C:\\`。
        var prefix = HasTrailingSeparator(root) ? root : root + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, comparison);
    }

    /// <summary>路径是否以目录分隔符结尾（<c>C:\</c> / <c>/</c>）。</summary>
    private static bool HasTrailingSeparator(string path)
        => path.Length > 0
           && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 把候选路径解析到根目录之下，若解析结果越界则失败。
    /// <para>
    /// 用以把"用户/客户机给的相对路径"安全地映射成磁盘绝对路径。
    /// 返回 <c>false</c> 时 <paramref name="resolved"/> 为 <c>null</c>。
    /// </para>
    /// </summary>
    public static bool TryResolveInside(string rootDirectory, string? candidate, out string? resolved)
    {
        resolved = null;

        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(candidate)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(rootDirectory, candidate));
        }
        catch
        {
            return false;
        }

        if (!IsInside(rootDirectory, full)) return false;

        resolved = full;
        return true;
    }

    /// <summary>
    /// 归一化一组白名单目录：去空白、去尾分隔符、去重（忽略大小写，Windows）。
    /// <para>配置来自人手，重复与末尾斜杠是常态 —— 不归一化会让"同一个目录"在比较时不等。</para>
    /// </summary>
    public static IReadOnlyList<string> NormalizeDirectories(IEnumerable<string>? directories)
    {
        if (directories is null) return Array.Empty<string>();

        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        var result = new List<string>();

        foreach (var raw in directories)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim()));
            }
            catch
            {
                continue;
            }

            if (seen.Add(full)) result.Add(full);
        }

        return result;
    }

    /// <summary>
    /// 由明细状态汇总出任务的总状态（<b>唯一实现</b>，避免"总状态"与明细打架）。
    /// <list type="bullet">
    ///   <item>有任意明细在传 → <c>running</c>；</item>
    ///   <item>否则有任意明细在等 → <c>pending</c>（还有活没干完）；</item>
    ///   <item>全都终结了：全成功 → <c>completed</c>；全失败 → <c>failed</c>；全取消 → <c>canceled</c>；混合 → <c>partial</c>。</item>
    /// </list>
    /// </summary>
    public static string SummarizeTaskState(IReadOnlyList<string> itemStates)
    {
        if (itemStates.Count == 0) return DistributionStates.Pending;

        var running = 0;
        var pending = 0;
        var completed = 0;
        var failed = 0;
        var canceled = 0;

        foreach (var state in itemStates)
        {
            switch (state)
            {
                case DistributionItemStates.Running: running++; break;
                case DistributionItemStates.Pending: pending++; break;
                case DistributionItemStates.Completed: completed++; break;
                case DistributionItemStates.Failed: failed++; break;
                case DistributionItemStates.Canceled: canceled++; break;
                default: pending++; break;
            }
        }

        if (running > 0) return DistributionStates.Running;
        if (pending > 0) return DistributionStates.Pending;

        if (completed > 0 && failed == 0 && canceled == 0) return DistributionStates.Completed;
        if (failed > 0 && completed == 0 && canceled == 0) return DistributionStates.Failed;
        if (canceled > 0 && completed == 0 && failed == 0) return DistributionStates.Canceled;

        return DistributionStates.Partial;
    }
}
