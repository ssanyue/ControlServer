using System.Security.Cryptography;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>一个「盘上有、库里没有」的仓库文件（孤儿）。</summary>
/// <param name="Name">磁盘上的文件名（即 <c>StoredName</c>，单段名，无路径）。</param>
/// <param name="Bytes">文件长度。</param>
/// <param name="LastWriteUtc">最后写入时刻（UTC）。判龄用它，不用本地时间。</param>
public sealed record OrphanFile(string Name, long Bytes, DateTimeOffset LastWriteUtc);

/// <summary>
/// 对账所需的最小仓库记录投影。
/// <para>
/// 它存在的理由是<b>判据只能有一份</b>：后台任务从 <see cref="IFileRepositoryStore"/> 拿记录，
/// 而 <c>--health</c> 巡检命令不经过 DI、直接只读查库。两边都投影成这个形状，
/// 再走同一个 <see cref="RepositoryGroomer.Scan"/> —— 否则"后台说没孤儿、巡检说有"
/// 这种事迟早会发生，而且没人能判断谁对。
/// </para>
/// </summary>
public sealed record RepoEntry(int Id, string Name, string StoredName, long Size, string? Sha256 = null);

/// <summary>一条「库里有、盘上没有」的仓库记录（断链）。</summary>
/// <param name="Id">记录 id。</param>
/// <param name="Name">用户看到的文件名。</param>
/// <param name="StoredName">磁盘上应当存在的文件名。</param>
/// <param name="Size">记录声明的大小。</param>
public sealed record LostItem(int Id, string Name, string StoredName, long Size);

/// <summary>
/// 一次对账的结论。<b>只读</b> —— 这里不删任何东西。
/// </summary>
/// <param name="DbItems"><c>file_repo_items</c> 行数。</param>
/// <param name="DbBytes">记录声明的总字节（可能大于盘上实际，断链时就是）。</param>
/// <param name="RepoFiles"><c>repo/</c> 下的文件数（只数顶层，不递归）。</param>
/// <param name="RepoBytes"><c>repo/</c> 下的实际字节。</param>
/// <param name="Orphans">可回收的孤儿（<b>已按宽限期过滤</b>）。</param>
/// <param name="Lost">断链记录（<b>永不自动删</b>）。</param>
/// <param name="StaleParts"><c>uploads/</c> 里没有会话主人的 <c>.part</c>（已按宽限期过滤）。</param>
/// <param name="PartFiles"><c>uploads/</c> 下的 <c>.part</c> 总数（含正在传的）。</param>
/// <param name="PartBytes"><c>uploads/</c> 下的总字节。</param>
/// <param name="LiveSessions">会话表里的记录数（用来解释 <paramref name="PartFiles"/>）。</param>
/// <param name="UnexpectedDirectories"><c>repo/</c> 下出现的子目录名（<b>不碰</b>，只报）。</param>
public sealed record RepositorySurvey(
    int DbItems,
    long DbBytes,
    int RepoFiles,
    long RepoBytes,
    IReadOnlyList<OrphanFile> Orphans,
    IReadOnlyList<LostItem> Lost,
    IReadOnlyList<OrphanFile> StaleParts,
    int PartFiles,
    long PartBytes,
    int LiveSessions,
    IReadOnlyList<string> UnexpectedDirectories)
{
    /// <summary>孤儿合计字节（"能腾出多少"）。</summary>
    public long OrphanBytes => Orphans.Sum(o => o.Bytes);

    /// <summary>无主 <c>.part</c> 合计字节。</summary>
    public long StalePartBytes => StaleParts.Sum(p => p.Bytes);

    /// <summary>有没有任何值得动手/值得报警的东西。</summary>
    public bool HasFindings => Orphans.Count > 0 || Lost.Count > 0 || StaleParts.Count > 0
                               || UnexpectedDirectories.Count > 0;
}

/// <summary>回收动作的结果。</summary>
/// <param name="DeletedFiles">真的删掉的文件数。</param>
/// <param name="DeletedBytes">真的腾出的字节数。</param>
/// <param name="Failures">删不掉的（文件名 + 原因）。删不掉不该让整轮失败。</param>
public sealed record RepositoryGroomingResult(int DeletedFiles, long DeletedBytes, IReadOnlyList<string> Failures)
{
    public bool AnyDeleted => DeletedFiles > 0;
}

/// <summary>
/// 一条「盘上文件在、但内容与库里记的 SHA-256 对不上」的仓库记录。
/// <para>
/// 它与 <see cref="LostItem"/> 是<b>两种不同的坏</b>，处置也完全不同：
/// 断链是"文件没了"（去找备份），不一致是"文件被换了 / 写坏了"（这份备份同样不可信，
/// 得从源头重传）。把两者混成一个计数，现场会按错误的方式去修。
/// </para>
/// </summary>
/// <param name="Id">记录 id。</param>
/// <param name="Name">用户看到的文件名。</param>
/// <param name="StoredName">磁盘上的文件名。</param>
/// <param name="Size">库里声明的字节数。</param>
/// <param name="ExpectedSha256">库里记的摘要。</param>
/// <param name="Reason">人话原因（大小不符 / 摘要不符 / 读不出来）。</param>
public sealed record ContentMismatch(
    int Id,
    string Name,
    string StoredName,
    long Size,
    string ExpectedSha256,
    string Reason);

/// <summary>
/// 一次内容校验的结论。
/// </summary>
/// <param name="Checked">真正读了全量并算过摘要的文件数。</param>
/// <param name="CheckedBytes">真正读掉的字节数（用来解释"巡检为什么跑了几秒"）。</param>
/// <param name="Mismatched">内容对不上的条数（= <see cref="Mismatches"/> 的长度）。</param>
/// <param name="Skipped">
/// 没校验的条数。**必须报出来**：预算用尽时它会是几百，
/// 而这个数字就是"这轮结论只覆盖了一部分"的凭据 —— 不报等于假装全验过了。
/// </param>
/// <param name="Mismatches">对不上的明细。</param>
/// <param name="Failures">读文件失败（被占用 / 权限）。不算不一致，但也不能当没发生。</param>
public sealed record ContentVerificationResult(
    int Checked,
    long CheckedBytes,
    int Mismatched,
    int Skipped,
    IReadOnlyList<ContentMismatch> Mismatches,
    IReadOnlyList<string> Failures)
{
    /// <summary>什么都没校验（没有记录 / 都没有摘要 / 预算为 0）。</summary>
    public static readonly ContentVerificationResult None = new(0, 0, 0, 0, [], []);

    public bool AnyChecked => Checked > 0;
}

/// <summary>
/// 文件仓库的<b>存储对账</b>（M7.1）：把「库里的记录」与「<c>files/</c> 下的文件」对上，
/// 找出<b>孤儿</b>（盘有库无）与<b>断链</b>（库有盘无），按宽限期回收孤儿。
///
/// <para>
/// <b>为什么需要它</b>：<c>FileRepositoryService.DeleteAsync</c> 的注释写着
/// 「磁盘上那个孤儿文件由清理任务兜底」，而全仓从来<b>没有</b>这个任务
/// （<c>FileTransferHostedService</c> 只回收过期会话与陈旧分发任务）。
/// 孤儿会从三条路产生：
/// </para>
/// <list type="number">
/// <item>删元数据成功、删磁盘文件失败（文件被占用 / 权限）—— 代码里只留一条 Warn；</item>
/// <item>上传落位成功、落库失败、且回滚 <c>Move</c> 也失败 —— 同样只有 Warn；</item>
/// <item>换机器 / 恢复备份时只带库不带 <c>files/</c>，或反过来。</item>
/// </list>
/// <para>
/// <b>断链（<see cref="RepositorySurvey.Lost"/>）绝不自动处理</b>：那是"用户的文件真的丢了"，
/// 删掉记录等于替用户决定放弃，而且会让"文件丢了"这件事从此<b>看不见</b>。
/// 它的正确处置是<b>报警 + 人去找备份</b>。
/// </para>
/// <para>
/// 🔴 <b>一切判龄都走 UTC</b>：<c>lastWriteTimeUtc</c> 与 <c>BeijingTime.Now.UtcDateTime</c> 比。
/// 用本地时间（<c>LastWriteTime</c> / <c>BeijingTime.Now</c>）比，在机器时区不是 +08:00 的
/// 部署上会整体偏几个小时 —— 偏早会误删、偏晚会让回收永远不触发，两种都不报错。
/// </para>
/// </summary>
public sealed class RepositoryGroomer
{
    private readonly IFileRepositoryStore _store;
    private readonly IDataDirectory _dataDir;
    private readonly FileRepositoryOptions _options;

    public RepositoryGroomer(IFileRepositoryStore store, IDataDirectory dataDir, FileRepositoryOptions options)
    {
        _store = store;
        _dataDir = dataDir;
        _options = options;
    }

    /// <summary>仓库文件的实际存放目录（与 <see cref="FileRepositoryService.RepositoryDirectory"/> 同一处）。</summary>
    public string RepositoryDirectory => Path.Combine(_dataDir.FilesDirectory, "repo");

    /// <summary>上传中途的 <c>.part</c> 暂存目录。</summary>
    public string StagingDirectory => Path.Combine(_dataDir.FilesDirectory, "uploads");

    // ═══════════════════════════════════════════════════════════
    // 对账（只读）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 扫一遍，给出结论。<b>不改任何东西</b>（连一条日志都不写）——
    /// 这样它可以被巡检命令、被测试、被人手工调用，而不用担心副作用。
    /// </summary>
    public async Task<RepositorySurvey> SurveyAsync(CancellationToken cancellationToken = default)
    {
        var items = await _store.ListAsync(null, cancellationToken).ConfigureAwait(false);
        var sessions = await _store.ListSessionsAsync(cancellationToken).ConfigureAwait(false);

        var entries = items
            .Select(i => new RepoEntry(i.Id, i.Name, i.StoredName, i.Size, i.Sha256))
            .ToList();

        var sessionNames = sessions.Select(s => s.StoredName).ToList();

        return Scan(
            entries,
            sessionNames,
            RepositoryDirectory,
            StagingDirectory,
            BeijingTime.Now.UtcDateTime,
            _options.OrphanGracePeriod);
    }

    /// <summary>
    /// 对账的<b>纯函数核心</b>：给定「库里的记录」「会话的 StoredName」「两个目录」，算出结论。
    /// <para>
    /// 抽成静态是为了让不经过 DI 的调用方（<c>--health</c> 巡检，它只拿得到一条只读 SQLite 连接）
    /// 用<b>完全同一套判据</b>。两套判据会各自演化，然后互相打脸。
    /// </para>
    /// </summary>
    /// <param name="items">库里的仓库记录。</param>
    /// <param name="sessionStoredNames">全部会话的 <c>StoredName</c>（<b>含活跃会话</b>，别只喂僵尸的）。</param>
    /// <param name="repositoryDirectory"><c>files/repo</c>。</param>
    /// <param name="stagingDirectory"><c>files/uploads</c>。</param>
    /// <param name="nowUtc">判龄基准，<b>必须是 UTC</b>。</param>
    /// <param name="gracePeriod">宽限期：比它更新的文件一律不碰。</param>
    public static RepositorySurvey Scan(
        IReadOnlyList<RepoEntry> items,
        IReadOnlyList<string> sessionStoredNames,
        string repositoryDirectory,
        string stagingDirectory,
        DateTime nowUtc,
        TimeSpan gracePeriod)
    {
        var cutoffUtc = nowUtc - gracePeriod;

        // ── repo/ ──
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long dbBytes = 0;
        foreach (var item in items)
        {
            knownNames.Add(item.StoredName);
            dbBytes += item.Size;
        }

        var onDiskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orphans = new List<OrphanFile>();
        var unexpectedDirs = new List<string>();

        long repoBytes = 0;
        var repoFiles = 0;

        foreach (var file in EnumerateTopLevelFiles(repositoryDirectory))
        {
            repoFiles++;
            repoBytes += file.Length;
            onDiskNames.Add(file.Name);

            if (knownNames.Contains(file.Name)) continue;

            // 🔴 太新的不碰：它可能正处在 Move→落库 的窗口里（见 OrphanGraceHours 的注释）。
            if (file.LastWriteTimeUtc > cutoffUtc) continue;

            orphans.Add(new OrphanFile(file.Name, file.Length, AsUtc(file.LastWriteTimeUtc)));
        }

        foreach (var dir in EnumerateTopLevelDirectories(repositoryDirectory))
        {
            unexpectedDirs.Add(dir.Name);
        }

        // ── 断链：库里有、盘上没有 ──
        var lost = new List<LostItem>();
        foreach (var item in items)
        {
            if (onDiskNames.Contains(item.StoredName)) continue;

            lost.Add(new LostItem(item.Id, item.Name, item.StoredName, item.Size));
        }

        // ── uploads/*.part ──
        // 有主人的名集合 = 会话的 StoredName（活跃会话与僵尸会话都算：
        // 僵尸的 .part 由 PurgeExpiredSessionsAsync 负责，不归这里管，抢着删只会两边打架）。
        var ownedPartNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var storedName in sessionStoredNames)
        {
            ownedPartNames.Add(storedName + ".part");
        }

        var staleParts = new List<OrphanFile>();
        long partBytes = 0;
        var partFiles = 0;

        foreach (var file in EnumerateTopLevelFiles(stagingDirectory))
        {
            partFiles++;
            partBytes += file.Length;

            if (ownedPartNames.Contains(file.Name)) continue;
            if (file.LastWriteTimeUtc > cutoffUtc) continue;

            staleParts.Add(new OrphanFile(file.Name, file.Length, AsUtc(file.LastWriteTimeUtc)));
        }

        return new RepositorySurvey(
            DbItems: items.Count,
            DbBytes: dbBytes,
            RepoFiles: repoFiles,
            RepoBytes: repoBytes,
            Orphans: orphans,
            Lost: lost,
            StaleParts: staleParts,
            PartFiles: partFiles,
            PartBytes: partBytes,
            LiveSessions: sessionStoredNames.Count,
            UnexpectedDirectories: unexpectedDirs);
    }

    // ═══════════════════════════════════════════════════════════
    // 回收（有副作用）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 按一份已经算好的对账结论删掉孤儿与无主 <c>.part</c>。
    /// <para>
    /// 只删 <paramref name="survey"/> 里<b>已经列出来</b>的东西 —— 不再重新判断一遍：
    /// 重判会引入"两次扫描之间状态变了"的窗口，而删文件这件事不该有第二次机会。
    /// </para>
    /// <para><b>断链记录一律不动</b>（见类注释）。</para>
    /// </summary>
    public Task<RepositoryGroomingResult> CollectAsync(
        RepositorySurvey survey,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Collect(survey, RepositoryDirectory, StagingDirectory));

    /// <summary>回收动作的纯函数核心（与 <see cref="Scan"/> 配对，供巡检/测试复用）。</summary>
    public static RepositoryGroomingResult Collect(
        RepositorySurvey survey,
        string repositoryDirectory,
        string stagingDirectory)
    {
        var failures = new List<string>();
        var deleted = 0;
        long bytes = 0;

        foreach (var orphan in survey.Orphans)
        {
            if (TryDelete(Path.Combine(repositoryDirectory, orphan.Name), orphan.Bytes, failures))
            {
                deleted++;
                bytes += orphan.Bytes;
            }
        }

        foreach (var part in survey.StaleParts)
        {
            if (TryDelete(Path.Combine(stagingDirectory, part.Name), part.Bytes, failures))
            {
                deleted++;
                bytes += part.Bytes;
            }
        }

        return new RepositoryGroomingResult(deleted, bytes, failures);
    }

    // ═══════════════════════════════════════════════════════════
    // 内容校验（重算 SHA-256）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 重算盘上文件的 SHA-256，与 <c>file_repo_items.Sha256</c> 比对。
    /// <para>
    /// <b>为什么它不跟 30 分钟那轮对账一起跑</b>：对账只 <c>stat</c> 每个文件（几乎零成本），
    /// 而这个要把字节全读一遍。让一个每 30 分钟的后台任务持续读几 GB 盘，
    /// 换来的只是"上周被人手工改掉的那个文件"，代价与收益完全不匹配。
    /// 所以它挂在 <c>--health</c>（周巡检）与 <c>--files-verify</c>（要用时手动跑），
    /// 并且带预算。
    /// </para>
    /// <para>
    /// <b>预算怎么用</b>：<paramref name="maxFiles"/> 与 <paramref name="maxBytes"/> 同时生效，
    /// 先到先停，剩下的计入 <see cref="ContentVerificationResult.Skipped"/>。
    /// 按 <c>Id</c> 升序取 —— 顺序固定，才能说"这一轮验的是最老的那 200 个"。
    /// </para>
    /// <para>
    /// ⚠️ <b>盘上没有的文件不在这里报</b>（那是断链，<see cref="RepositorySurvey.Lost"/> 的活）。
    /// 一条记录只会落在三种结论里的一种：断链 / 不一致 / 跳过 —— 不会既报断链又报不一致。
    /// </para>
    /// </summary>
    /// <param name="items">库里的仓库记录（必须带 <see cref="RepoEntry.Sha256"/>）。</param>
    /// <param name="repositoryDirectory"><c>files/repo</c>。</param>
    /// <param name="maxFiles">本轮最多校验几个文件；≤0 表示不限（不推荐）。</param>
    /// <param name="maxBytes">本轮最多读多少字节；≤0 表示不限。</param>
    public static async Task<ContentVerificationResult> VerifyContentsAsync(
        IReadOnlyList<RepoEntry> items,
        string repositoryDirectory,
        int maxFiles,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var mismatches = new List<ContentMismatch>();
        var failures = new List<string>();

        var checkedCount = 0;
        long checkedBytes = 0;
        var skipped = 0;

        foreach (var item in items.OrderBy(i => i.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 🔴 只认单段文件名。内容校验会**真的去读**这个路径，
            //    一个被人改过的库里如果出现 `../../https/server.pfx`，读出来再算摘要就是"探测任意文件"。
            if (item.StoredName.Length == 0
                || !string.Equals(item.StoredName, Path.GetFileName(item.StoredName), StringComparison.Ordinal)
                || item.StoredName.Contains("..", StringComparison.Ordinal))
            {
                failures.Add($"{item.StoredName}（id={item.Id}）：文件名不是单段名，拒绝按它读盘");
                skipped++;
                continue;
            }

            var path = Path.Combine(repositoryDirectory, item.StoredName);

            if (!File.Exists(path))
            {
                // 断链：由 Lost 那条线报，这里不重复。
                skipped++;
                continue;
            }

            if (item.Sha256 is not { Length: > 0 })
            {
                failures.Add($"{item.Name}（id={item.Id}）：库里没有摘要，无法校验内容（老数据？）");
                skipped++;
                continue;
            }

            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                failures.Add($"{item.Name}（id={item.Id}）：读不到文件长度（{ex.GetType().Name}: {ex.Message}）");
                skipped++;
                continue;
            }

            // 大小先比：不一致就不必读几个 GB 去证明它不一致。
            if (length != item.Size)
            {
                mismatches.Add(new ContentMismatch(
                    item.Id, item.Name, item.StoredName, item.Size, item.Sha256,
                    $"大小对不上（盘上 {length} 字节，库里 {item.Size} 字节）—— 这个文件被替换或写了一半"));
                continue;
            }

            // 预算：任一先到就停。⚠️ 不能"超了也验完这一个" —— 一个 4 GB 的文件
            // 足以让周巡检从 3 秒变成几分钟，而现场根本等不起。
            if ((maxFiles > 0 && checkedCount >= maxFiles)
                || (maxBytes > 0 && checkedBytes + length > maxBytes))
            {
                skipped++;
                continue;
            }

            string actual;
            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024, useAsync: true);

                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                failures.Add($"{item.Name}（id={item.Id}）：读文件失败（{ex.GetType().Name}: {ex.Message}）");
                skipped++;
                continue;
            }

            checkedCount++;
            checkedBytes += length;

            if (!string.Equals(actual, item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(new ContentMismatch(
                    item.Id, item.Name, item.StoredName, item.Size, item.Sha256,
                    $"摘要不符（盘上 {actual}，库里 {item.Sha256}）—— 文件被改过或写坏了"));
            }
        }

        return new ContentVerificationResult(
            Checked: checkedCount,
            CheckedBytes: checkedBytes,
            Mismatched: mismatches.Count,
            Skipped: skipped,
            Mismatches: mismatches,
            Failures: failures);
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 删一个文件，失败只记一条（不抛）。
    /// <para>
    /// 🔥 <b>为什么是 <c>internal</c> 而不是 <c>private</c></b>：图片对账
    /// （<see cref="ImageRepositoryGroomer"/>）用的是<b>完全相同</b>的删除语义
    /// （删不掉要进 <c>Failures</c>、不能被当成"已清理"）。各写一份迟早会漂移，
    /// 而漂移的表现是"一处说删了、另一处说没删"，两边都没错，只是判据不同。
    /// </para>
    /// </summary>
    internal static bool TryDelete(string path, long expectedBytes, List<string> failures)
    {
        try
        {
            if (!File.Exists(path)) return false;

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            // 删不掉（被占用 / 权限 / 只读）不该让整轮停摆，也不该被当成"已清理"。
            failures.Add($"{Path.GetFileName(path)}（{expectedBytes} 字节）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 只枚举顶层的<b>文件</b>。<c>repo/</c> 与 <c>uploads/</c> 本来就该是平铺的
    /// （<c>StoredName</c> 是服务端生成的单段名），出现子目录说明有人手工动过 ——
    /// 那种情况<b>不递归、不删</b>，只在报告里点名。
    /// </summary>
    private static IEnumerable<FileInfo> EnumerateTopLevelFiles(string path)
    {
        if (!Directory.Exists(path)) yield break;

        FileInfo[] found;
        try
        {
            var dir = new DirectoryInfo(path);
            found = dir.GetFiles("*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            // 目录读不了（ACL / 正在被替换）：当作"这一轮什么都没看到"。
            // 比抛出去好 —— 抛出去会让整个巡检命令失败，而巡检恰恰是在出问题时才跑。
            yield break;
        }

        foreach (var file in found) yield return file;
    }

    private static IEnumerable<DirectoryInfo> EnumerateTopLevelDirectories(string path)
    {
        if (!Directory.Exists(path)) yield break;

        DirectoryInfo[] found;
        try
        {
            found = new DirectoryInfo(path).GetDirectories("*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in found) yield return dir;
    }

    private static DateTimeOffset AsUtc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
