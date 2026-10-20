using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Transfer;

/// <summary>
/// 客户机侧的<b>目录浏览</b>（选文件存放路径 / 回传源路径用，2026-10-13）。
///
/// <para>
/// <b>它像一个"远程资源管理器"</b>：顶层列出这台机器的<b>全部盘符</b>，
/// 之后任意目录都能逐级进去 —— 供中心渲染成可点的列表，
/// 免得运维手输路径（<b>手输是现场最常见的失败源</b>：盘符写错、目录名大小写错、
/// 多一个空格、中文路径被输入法带进全角字符，而这些错要等到"传输失败"才暴露）。
/// </para>
///
/// <para>
/// 🔥 <b>2026-10-13 起从"只列白名单内"放开为"整盘浏览"</b>（产品决策）。
/// 这一改动的安全含义要说清：
/// </para>
/// <list type="bullet">
///   <item><b>浏览会暴露整台机器的目录结构</b>（含目录名与文件名）给中心网页。
///         拿到 Cookie 的账号因此能看到客户机上有什么 —— 这是为了"让人能挑到正确的目录"付的代价。</item>
///   <item><b>浏览不是写权限</b>：真正落地 / 读取的边界<b>仍然是客户机的白名单</b>
///         （见 <see cref="FileTransferClient"/> 与 <see cref="TransferPaths"/>）。
///         选到白名单外的目录照样会被拒，只是拒绝发生在"传输"那一步而不是"浏览"这一步。</item>
/// </list>
///
/// <para>⚠️ <b>它依旧是只读的</b>：不创建、不删除、不写入任何东西。这条要一直成立 ——
/// 它是"看一眼好填个路径"的能力，任何写动作都超出了这个能力该有的权限。</para>
/// </summary>
public sealed class TransferBrowseService
{
    /// <summary>
    /// 一次最多返回多少条。
    /// <para>
    /// <b>这不是分页</b>：一个列表里 300 条已经远超一屏，再多的部分对"挑一个目录"没有帮助，
    /// 却会把 Hub 消息撑大（目录名可以长到 255 字符）。截断时用
    /// <see cref="AgentBrowseResultDto.Truncated"/> 明说，别让用户以为"那东西不存在"。
    /// </para>
    /// </summary>
    public const int MaxEntries = 300;

    /// <summary>
    /// 顶层盘符列表（含卷标 / 就绪状态）的缓存时长。
    /// <para>
    /// 盘符集合与卷标是分钟级都不会变的量，而读它们要为每个盘做两次可能阻塞的调用
    /// （断开的网络盘上那就是 SMB 超时）。缓存 30 秒足以让"连续点开几次浏览"只付一次代价，
    /// 又短到"刚插上 U 盘"下一分钟就能看见。
    /// </para>
    /// </summary>
    private static readonly TimeSpan DriveCacheTtl = TimeSpan.FromSeconds(30);

    private readonly TransferOptions _options;
    private readonly LogService _log;

    /// <summary>顶层结果缓存（见 <see cref="DriveCacheTtl"/>）。</summary>
    private readonly object _driveGate = new();

    private (DateTimeOffset At, IReadOnlyList<string> Roots, IReadOnlyList<AgentBrowseEntryDto> Entries)? _driveCache;

    public TransferBrowseService(TransferOptions options, LogService log)
    {
        _options = options;
        _log = log;
    }

    /// <summary>列出请求的目录；<c>Path</c> 为空时列出<b>本机全部盘符</b>（"此电脑"）。</summary>
    public AgentBrowseResultDto Browse(AgentBrowseRequestDto? request)
    {
        if (!_options.Enabled)
        {
            return AgentBrowseResultDto.Fail(
                AgentBrowseStatuses.Disabled, "这台机器已停用文件传输，没有可浏览的目录。");
        }

        var roots = DriveRoots();

        // ── 顶层：这台机器有几个盘 ──
        if (string.IsNullOrWhiteSpace(request?.Path)) return Top();

        var requested = request!.Path!.Trim();
        var resolved = Resolve(requested);

        if (resolved is null)
        {
            _log.Warn("Files", $"浏览请求被拒绝（非法路径）：{requested}");

            return AgentBrowseResultDto.Fail(
                AgentBrowseStatuses.NotFound, $"「{requested}」不是一个合法的本机路径。");
        }

        if (File.Exists(resolved))
        {
            return AgentBrowseResultDto.Fail(
                AgentBrowseStatuses.NotDirectory, $"「{resolved}」是一个文件，不是目录。");
        }

        if (!Directory.Exists(resolved))
        {
            return AgentBrowseResultDto.Fail(
                AgentBrowseStatuses.NotFound, $"目录不存在或无权访问：{resolved}");
        }

        var (entries, truncated) = List(resolved);

        return new AgentBrowseResultDto(
            Ok: true,
            Status: AgentBrowseStatuses.Ok,
            Message: null,
            RequestedPath: resolved,
            ParentPath: Parent(resolved),
            Separator: Path.DirectorySeparatorChar.ToString(),
            Roots: roots,
            Entries: entries,
            Truncated: truncated);
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 本机全部盘符（"此电脑"那一层）。<b>结果有短时缓存</b>（见 <see cref="_driveCache"/>）。
    /// <para>
    /// 🔥 枚举本身在 <see cref="TransferPaths.DriveRoots"/> —— <b>与回传的可读范围同一份实现</b>。
    /// 「浏览里看得见」与「回传读得到」必须是同一个集合，否则就会出现
    /// "在这儿挑好了文件、建单后必然被拒"（2026-10-15 修的那次事故）。
    /// </para>
    /// <para>
    /// ⚠️ <b>就绪判断要单独包一层 try</b>：光驱 / 可移动盘 / 已断开的网络盘在枚举与
    /// 读 <c>IsReady</c> / <c>VolumeLabel</c> 时都可能抛（介质不在、句柄失效）。
    /// 一个盘读不出来不该让整次浏览失败 —— 其它盘仍然是有用的信息。
    /// </para>
    /// </summary>
    private IReadOnlyList<string> DriveRoots()
        => TransferPaths.DriveRoots(message => _log.Warn("Files", message));

    /// <summary>
    /// 顶层：盘符各占一行。<b>带短时缓存</b>。
    /// <para>
    /// 为什么缓存：这一层要为每个盘读 <c>IsReady</c> 与 <c>VolumeLabel</c>，
    /// 而一个<b>已断开的网络盘</b>会让那两次读各自等上一次 SMB 超时 ——
    /// 那样"点一下浏览"就要好几秒。盘符集合与卷标是分钟级都不会变的量，
    /// 缓存 <see cref="DriveCacheTtl"/> 完全够用。
    /// </para>
    /// <para>
    /// ⚠️ 缓存里<b>只有盘符列表</b>（结果对象每次新建）：它要被序列化回传给中心，
    /// 共享同一个实例意味着调用方任何一次误改都会污染后面所有人。
    /// </para>
    /// </summary>
    private AgentBrowseResultDto Top()
    {
        var now = DateTimeOffset.Now;

        lock (_driveGate)
        {
            if (_driveCache is { } cached && now - cached.At < DriveCacheTtl)
            {
                return BuildTop(cached.Roots, cached.Entries);
            }
        }

        var roots = DriveRoots();
        var entries = roots
            .Select(root => new AgentBrowseEntryDto(Name: DescribeDrive(root), Path: root, IsDirectory: true))
            .ToArray();

        lock (_driveGate)
        {
            _driveCache = (now, roots, entries);
        }

        return BuildTop(roots, entries);
    }

    private static AgentBrowseResultDto BuildTop(
        IReadOnlyList<string> roots,
        IReadOnlyList<AgentBrowseEntryDto> entries)
        => new(
            Ok: true,
            Status: AgentBrowseStatuses.Ok,
            Message: null,
            RequestedPath: null,
            ParentPath: null,
            Separator: Path.DirectorySeparatorChar.ToString(),
            Roots: roots,
            Entries: entries,
            Truncated: false);

    /// <summary>
    /// 盘符的显示名：`C:\（系统）（固定）` 这样。
    /// <para>为什么不直接用 <c>C:\</c>：现场同一台机器上 C 与 D 一眼分不出哪个是数据盘，
    /// 而卷标（"数据"/"备份"）正是运维自己起的名，比盘符有用得多。</para>
    /// </summary>
    private static string DescribeDrive(string path)
    {
        try
        {
            var drive = new DriveInfo(path);

            var label = drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? $"（{drive.VolumeLabel.Trim()}）"
                : string.Empty;

            var type = drive.DriveType switch
            {
                DriveType.Fixed => "固定",
                DriveType.Removable => "可移动",
                DriveType.Network => "网络",
                DriveType.CDRom => "光驱",
                DriveType.Ram => "内存盘",
                _ => "其它",
            };

            var ready = drive.IsReady ? string.Empty : "·未就绪";

            return $"{path}{label}（{type}{ready}）";
        }
        catch
        {
            // 读不到属性（介质不在 / 权限）就退回盘符本身 —— 它仍然能点进去试试。
            return path;
        }
    }

    /// <summary>
    /// 把请求路径解析成绝对路径；非法时返回 null。
    /// <para>
    /// 🔥 <b>这里不再比对白名单</b>（2026-10-13 起整盘可浏览）。
    /// 解析失败（非法字符 / 盘符不存在 / 超长路径）返回 null，由调用方给一句人话。
    /// </para>
    /// </summary>
    private static string? Resolve(string raw)
    {
        try
        {
            return Path.GetFullPath(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 上一级目录。<b>盘根（如 <c>C:\</c>）没有上一级 ⇒ 返回 null</b> ——
    /// 界面上要能区分"再往上一级"与"已经没有上一级了（回盘符列表）"。
    /// </summary>
    private static string? Parent(string resolved)
    {
        var parent = Path.GetDirectoryName(resolved);

        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    /// <summary>
    /// 列出目录下的条目（目录在前，同组按名排序）。
    ///
    /// <para>
    /// 🔥 <b>2026-10-13 重写：单次枚举（这是"打开一个盘特别慢"的修复）</b>。
    /// </para>
    /// <para><b>旧写法为什么慢</b>：它对每个条目再造一个 <c>FileInfo</c> / <c>DirectoryInfo</c>
    /// 并读 <c>Exists</c> / <c>Length</c> / <c>LastWriteTimeUtc</c> ——
    /// 每个条目 2~3 次额外的文件系统调用。300 个条目就是近千次；
    /// 在机械盘 / 网络盘上每次几毫秒，"打开一个盘"于是变成好几秒，
    /// 而中心的浏览超时是 8 秒 ⇒ 表现成"半天不出来"。
    /// </para>
    /// <para><b>新写法</b>：<see cref="DirectoryInfo.EnumerateFileSystemInfos"/> 只走一次
    /// <c>FindFirstFile/FindNextFile</c>，返回的对象<b>已经带上了</b>名字 / 大小 / 修改时间
    /// （由枚举本身填充），因此读这些属性<b>不再产生系统调用</b>。
    /// 顺带把"目录一遍 + 文件一遍"两次遍历合成了一次。
    /// </para>
    /// <para>
    /// ⚠️ <see cref="FileSystemInfo.Attributes"/> 对枚举出来的对象是<b>已缓存</b>的，
    /// 但保险起见仍包在 try 里：目录在枚举中途被删、权限不足都会抛。
    /// </para>
    /// </summary>
    private (IReadOnlyList<AgentBrowseEntryDto> Entries, bool Truncated) List(string directory)
    {
        var entries = new List<AgentBrowseEntryDto>(MaxEntries);
        var truncated = false;

        try
        {
            var directories = new List<FileSystemInfo>();
            var files = new List<FileSystemInfo>();

            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (!TryIsDirectory(item, out var isDirectory)) continue;

                (isDirectory ? directories : files).Add(item);
            }

            // 排序在**枚举出来的那一批**上做（枚举顺序由文件系统定，不稳定；中心那边
            // 是按名字找东西，所以必须给一个稳定顺序）。
            directories.Sort(ByName);
            files.Sort(ByName);

            truncated = Collect(directories, isDirectory: true, entries);

            if (!truncated) truncated = Collect(files, isDirectory: false, entries);
        }
        catch (Exception ex)
        {
            // 权限不足 / 目录在枚举中途被删 —— 都归到"读不到"这一种处置。
            _log.Warn("Files", $"浏览目录失败（{directory}）：{ex.Message}");

            return (Array.Empty<AgentBrowseEntryDto>(), false);
        }

        return (entries, truncated);

        // 局部函数：先取够再加上限判断，避免"目录刚好多一条就整批丢掉"。
        bool Collect(List<FileSystemInfo> items, bool isDirectory, List<AgentBrowseEntryDto> target)
        {
            foreach (var item in items)
            {
                if (target.Count >= MaxEntries) return true;

                target.Add(Describe(item, isDirectory));
            }

            return false;
        }

        static int ByName(FileSystemInfo a, FileSystemInfo b)
            => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读"这个条目是不是目录"。<b>读不到属性时返回 false 并跳过</b> ——
    /// 属性读失败（竞态删除 / 权限）不该让整次浏览失败，但也别把一个读不出属性的东西
    /// 当成文件塞进列表（那会让用户点它并期待能选中）。
    /// </summary>
    private static bool TryIsDirectory(FileSystemInfo item, out bool isDirectory)
    {
        isDirectory = false;

        try
        {
            isDirectory = (item.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把一个枚举出来的条目转成回传形状。<b>属性读失败不致命</b>：名字与"是不是目录"
    /// 仍然是有用的信息，照样列出来。
    /// <para>
    /// ⚠️ 只对文件读 <see cref="FileSystemInfo"/> 的尺寸 —— 对目录读 <c>Length</c> 会抛。
    /// </para>
    /// </summary>
    private static AgentBrowseEntryDto Describe(FileSystemInfo item, bool isDirectory)
    {
        long size = 0;
        DateTimeOffset? modified = null;

        try
        {
            if (!isDirectory && item is FileInfo file) size = file.Length;

            // ⚠️ `LastWriteTimeUtc` 在**本地时间无效**的场合（例如 1601 年之前的时间戳）会抛，
            //    这在某些损坏的移动盘上真出现过 —— 所以它和 Length 一样要各自容错。
            modified = BeijingTime.ToBeijing(item.LastWriteTimeUtc);
        }
        catch
        {
            // 见方法注释。
        }

        // 空名字只可能来自"以分隔符结尾的输入"，而盘根就是这么来的 ⇒ 退回用完整路径。
        var name = item.Name;
        if (string.IsNullOrEmpty(name)) name = item.FullName;

        return new AgentBrowseEntryDto(name, item.FullName, isDirectory, size, modified);
    }
}
